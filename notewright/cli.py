"""Command line: python notewright.py <command> ... (run with -h for help). Every command prints JSON on stdout so Claude can
read results; progress and downloads go to stderr."""
import argparse
import json
import math
import os
import re
import sys
from pathlib import Path

from . import __version__, catalog, engine, export, midi, musicxml, paths, spec as specs, synthkit
from .notes import name as note_name

EXAMPLES = paths.PLUGIN_DIR / "examples"


def _print(value):
    sys.stdout.write(json.dumps(value, indent=1, ensure_ascii=False) + "\n")


def _brief(entry):
    keys = sorted({r["key"] for r in entry.get("regions", [])})
    out = {k: entry.get(k) for k in ("id", "name", "kind", "status", "source", "description") if entry.get(k) is not None}
    out["tags"] = entry.get("tags", [])
    if keys:
        lo, hi = min(r["lo"] for r in entry["regions"]), max(r["hi"] for r in entry["regions"])
        out["range"] = f"{note_name(lo)}-{note_name(hi)} ({lo}-{hi})"
        out["samples"] = len({r["sample"] for r in entry["regions"]})
        if entry.get("kind") == "drums":
            out["keys"] = keys
    if "score" in entry:
        out["score"] = entry["score"]
    return out


def _slug(text):
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-") or "song"


def _seconds_at(beat, bpm, tempo):
    """Integral of 60/BPM with linear ramps (same model as MusicTempoMap)."""
    seconds, cursor, current = 0.0, 0.0, bpm
    for c in sorted(tempo, key=lambda c: c["Beat"]):
        if beat <= c["Beat"]:
            break
        seconds += (c["Beat"] - cursor) * 60 / current
        ramp = c["RampBeats"]
        if ramp > 0:
            span = min(beat, c["Beat"] + ramp) - c["Beat"]
            end_bpm = current + (c["Bpm"] - current) * span / ramp
            seconds += span * 60 * (math.log(end_bpm / current) / (end_bpm - current) if abs(end_bpm - current) > 1e-9 else 1 / current)
            if beat < c["Beat"] + ramp:
                return seconds
            cursor = c["Beat"] + ramp
        else:
            cursor = c["Beat"]
        current = c["Bpm"]
    return seconds + (beat - cursor) * 60 / current


# ---------- commands ----------

def cmd_doctor(args):
    info = engine.doctor()
    info["version"] = __version__
    info["home"] = str(paths.home())
    info["musescore"] = export.musescore()
    info["installedInstruments"] = len(catalog.load()["instruments"])
    info["instrumentDownloads"] = catalog.downloads() or "unavailable: only built-in synths and instruments on this computer"
    if not info["ok"]:
        info["withoutRenderer"] = "render --score-only still writes the MIDI and MusicXML score"
    _print(info)
    return 0 if info["ok"] else 1


def _fingerprint():
    """What setup's result depends on: plugin and engine sources, Python, OS, runtimes on PATH, settings and the data folder."""
    import hashlib
    import shutil
    digest = hashlib.sha256()
    for source in paths.engine_sources():
        digest.update(source.read_bytes())
    keys = {"version": __version__, "engine": digest.hexdigest()[:16], "python": sys.version.split()[0], "platform": sys.platform,
            "dotnet": shutil.which("dotnet"), "mono": shutil.which("mono"), "home": str(paths.home())}
    keys.update({k: os.environ.get(k) for k in ("NOTEWRIGHT_RUNTIME", "NOTEWRIGHT_DOWNLOAD", "DOTNET_ROOT")})
    return keys


def cmd_setup(args):
    """Start-of-work check that fixes what it can: Python, data and temp folders, the renderer (a test render on each installed
    runtime, keeping the fastest that works), built-in instruments, downloads, MuseScore. Cached until the environment changes."""
    import shutil
    import tempfile
    cache = paths.home() / "setup.json"
    fingerprint = _fingerprint()
    if not args.force:
        try:
            saved = json.loads(cache.read_text(encoding="utf-8"))
            if saved.get("fingerprint") == fingerprint and saved["result"].get("ok"):
                _print(dict(saved["result"], cached=True))
                return 0
        except (OSError, ValueError, KeyError):
            pass
    checks, actions = [], []

    def check(name, status, detail, fix=None):
        checks.append(dict({"name": name, "status": status, "detail": detail}, **({"fix": fix} if fix else {})))
        if fix and status in ("action", "warning"):
            actions.append(f"{name}: {fix}")

    check("python", "ok" if sys.version_info >= (3, 9) else "action", sys.version.split()[0],
          None if sys.version_info >= (3, 9) else "install Python 3.9 or newer")
    for name, folder in (("data folder", paths.home()), ("temp folder", Path(tempfile.gettempdir()))):
        try:
            probe = folder / f".notewright-probe-{os.getpid()}"
            probe.write_bytes(b"ok")
            probe.unlink()
            free = shutil.disk_usage(folder).free // (1 << 20)
            if free < 300:
                check(name, "warning", f"{folder}: {free} MB free", "free disk space: renders, stems and instruments need a few hundred MB")
            else:
                check(name, "ok", f"{folder} ({free} MB free)")
        except OSError as error:
            hint = "set NOTEWRIGHT_HOME to a writable folder" if name == "data folder" else "set TMP/TEMP (TMPDIR on Linux/macOS) to a writable folder"
            check(name, "action", f"{folder}: {error}", hint)
    rt = engine.setup_runtime()
    for t in rt["tried"]:
        if t["status"] == "failed":
            check(f"runtime {t['runtime']}", "fixed" if rt["ok"] else "action", "installed but the test render failed: " + t["detail"],
                  None if rt["ok"] else "reinstall it (or remove it so another runtime is used)")
    if rt["ok"]:
        check("renderer", "ok", f"{rt['runtime']}: built and rendered a test bar" + (" (others failed, see above)" if any(
            t["status"] == "failed" for t in rt["tried"]) else ""))
        if rt["kind"] != "dotnet":
            check("speed", "warning", f"rendering with {rt['kind']}; the .NET SDK is about 2x faster with identical output",
                  f"install the .NET 8 SDK ({engine.sdk_hint()}) and run setup again")
    else:
        check("renderer", "action", "no runtime can render here", rt["fix"] + "; until then render --score-only writes MIDI and MusicXML")
    try:
        catalog.resolve("synth:drum-kit", auto_fetch=False)
        check("built-in instruments", "ok", "synth:* generated")
    except Exception as error:  # report, don't crash
        check("built-in instruments", "action", str(error), "check the data folder (NOTEWRIGHT_HOME)")
    downloads = catalog.downloads()
    if downloads:
        check("instrument downloads", "ok", downloads + (" (github.com only: git is used)" if downloads == "git" else ""))
    else:
        check("instrument downloads", "warning", "github.com is not reachable", "use built-in synths, the engine synth and the user's own "
              "instruments; VSCO 2 needs network access to github.com (git or raw.githubusercontent.com)")
    musescore = export.musescore()
    check("musescore", "ok" if musescore else "info", musescore or "not installed: --pdf is unavailable (MusicXML still works)")
    ok = rt["ok"] and not any(c["status"] == "action" for c in checks)
    result = {"ok": ok, "version": __version__, "runtime": rt.get("runtime"), "checks": checks, "actions": actions, "cached": False}
    if ok:
        cache.write_text(json.dumps({"fingerprint": fingerprint, "result": result}, ensure_ascii=False), encoding="utf-8")
    _print(result)
    return 0 if ok else 1


def cmd_instruments(args):
    action = args.action
    if action == "list":
        entries = sorted(catalog.all_entries().values(), key=lambda e: e["id"])
        if args.kind:
            entries = [e for e in entries if e.get("kind") == args.kind]
        if args.installed:
            entries = [e for e in entries if e["status"] == "installed"]
        _print([{"id": e["id"], "kind": e.get("kind"), "status": e["status"], "tags": e.get("tags", [])} for e in entries])
    elif action == "find":
        _print([_brief(e) for e in catalog.find(" ".join(args.terms), kind=args.kind, limit=args.limit)])
    elif action == "fetch":
        _print([_brief(catalog.fetch(i, velocity=args.velocity, all_layers=args.all_layers)) for i in args.terms])
    elif action == "scan":
        found = []
        for folder in args.terms:
            found += catalog.scan(folder, library=args.name)
        _print({"added": len(found), "instruments": [_brief(e) for e in found]})
    elif action == "add":
        if len(args.terms) != 2:
            raise SystemExit("usage: instruments add <id> <file.wav> --root C4")
        _print(_brief(catalog.add_file(args.terms[0], args.terms[1], root=_root(args.root))))
    elif action == "info":
        for i in args.terms:
            entry = catalog.get(i)
            out = _brief(entry)
            out.setdefault("range", f"known after download (instruments fetch {i})")
            out["license"] = entry.get("license") or (catalog.registry()["libraries"].get(entry.get("library"), {}).get("license"))
            out["regions"] = entry.get("regions", "download first (instruments fetch)")
            _print(out)
    elif action == "remove":
        for i in args.terms:
            catalog.remove(i)
        _print({"removed": args.terms})
    return 0


def _root(value):
    from .notes import note_number
    key = note_number(str(value))
    if key is None:
        raise SystemExit(f"--root: '{value}' is not a note")
    return key


def cmd_new(args):
    target = Path(args.path)
    if target.exists() and not args.force:
        raise SystemExit(f"{target} exists (use --force to overwrite)")
    template = EXAMPLES / f"{args.template}.json"
    if not template.exists():
        raise SystemExit(f"unknown template '{args.template}' ({', '.join(p.stem for p in EXAMPLES.glob('*.json'))})")
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(template.read_text(encoding="utf-8"), encoding="utf-8")
    _print({"created": str(target.resolve()), "template": args.template})
    return 0


def _compile(path, auto_fetch=True):
    data = specs.load(path)
    return specs.compile(data, spec_dir=Path(path).resolve().parent, auto_fetch=auto_fetch)


def _summary(song, meta):
    seconds = _seconds_at(meta["loop"], meta["bpm"], meta["tempo"])
    return {"title": meta["title"], "bpm": meta["bpm"], "meter": meta["meter"], "bars": meta["loop"] / meta["meter"],
            "seconds": round(seconds, 2), "playbackVersion": song["playbackVersion"],
            "sections": [{"name": s["name"], "bar": s["beat"] / meta["meter"] + 1, "bars": s["beats"] / meta["meter"]} for s in meta["sections"]],
            "tracks": [{"name": t["name"], "instrument": t["instrument"], "notes": len(t["notes"]),
                        "parts": sum(1 for p in meta["parts"] if p["track"] == t["name"])} for t in meta["tracks"]],
            "engineParts": len(song["parts"]), "regions": len(song["regions"]), "warnings": meta["warnings"]}


def cmd_check(args):
    song, meta = _compile(args.spec, auto_fetch=not args.no_fetch)
    _print(dict(_summary(song, meta), ok=True))
    return 0


def _advice(report, meta, sections, per_second=None, tail=0.0):
    """Mixing tips from the report. per_second (full renders only) also checks the start and end for a lead-in and a ring-out."""
    tips = []
    if report.get("droppedNotes"):
        tips.append(f"{report['droppedNotes']} notes were dropped by the voice limit ({report['voiceLimit']}): shorten releases or thin dense parts")
    peak = report.get("peakDb", -200)
    if report.get("gainDb", 0) == 0 and peak > -0.3:
        tips.append(f"mix peaks at {peak:.1f} dBFS ({report['nearFullScale']} samples ≥ -0.2 dBFS): lower track gains / master OutputDb, "
                    "enable master Compressor, or render with --normalize -1")
    rms = report.get("outputRmsDb", report.get("rmsDb", -200))   # the file's level, after --normalize
    if rms < -26:
        fix = "it is already normalized, so give it more body: louder sustained parts, master Compressor" if report.get("gainDb") \
            else "raise gains or use --normalize -1"
        tips.append(f"overall level is low (output RMS {rms:.1f} dBFS): {fix}")
    parts = [p for p in report.get("parts") or [] if p.get("kind", "track") == "track"]
    audible = [p for p in parts if p["rmsDb"] > -150]
    if len(audible) > 1:
        loudest = max(p["rmsDb"] for p in audible)
        for p in audible:
            if p["rmsDb"] < loudest - 24:
                tips.append(f"track '{p['name']}' is {loudest - p['rmsDb']:.0f} dB below the loudest track; it may be inaudible")
    levels = [s for s in sections if s.get("rmsDb") is not None]
    if len(levels) > 2 and max(s["rmsDb"] for s in levels) - min(s["rmsDb"] for s in levels) < 2:
        tips.append("sections have nearly the same loudness: consider more dynamic contrast (thinner verses, fuller choruses)")
    song = int(report.get("seconds", 0) - tail)                    # whole seconds before the render tail
    if per_second and song >= 4:
        loudest = max(rms for rms, _ in per_second[:song])
        first, last = per_second[0][0], per_second[song - 1][0]
        if first > loudest - 6:
            tips.append(f"the song starts at almost full level (first second {first:.1f} dB, loudest {loudest:.1f} dB): unless the user wants "
                        "a cold open or a seamless loop, give it a 1-2 bar lead-in (a pad or held chord fading in, e.g. masterAutomation "
                        "volume from -30 dB, with drums and melody entering after)")
        if last > loudest - 6:
            tips.append(f"the song ends at almost full level (last second {last:.1f} dB, loudest {loudest:.1f} dB): unless it is a seamless "
                        "loop, let the last 1-2 bars ring out (hold the final chord, start nothing new, fade with masterAutomation volume)")
    return tips


def cmd_render(args):
    spec_path = Path(args.spec).resolve()
    song, meta = _compile(spec_path)
    output = meta["output"]
    slug = _slug(args.name or meta["title"])
    out_dir = Path(args.out).resolve() if args.out else spec_path.parent / "out" / slug
    out_dir.mkdir(parents=True, exist_ok=True)
    song_path = out_dir / f"{slug}.song.json"
    song_path.write_text(json.dumps(song, ensure_ascii=False), encoding="utf-8")
    meter = meta["meter"]
    start = length = None
    if args.section:
        match = [s for s in meta["sections"] if s["name"] == args.section]
        if not match:
            raise SystemExit(f"--section: no section '{args.section}' ({', '.join(s['name'] for s in meta['sections'])})")
        start, length = match[0]["beat"], match[0]["beats"]
    if args.bars:
        first, _, last = args.bars.partition("-")
        start = (float(first) - 1) * meter
        length = ((float(last) if last else float(first)) - float(first) + 1) * meter
    result = {"ok": True, "outDir": str(out_dir), "files": {}}
    if not args.score_only:
        wav = out_dir / f"{slug}{'-' + _slug(args.section) if args.section else ''}{'-bars-' + args.bars if args.bars else ''}.wav"
        normalize = args.normalize if args.normalize is not None else output.get("normalize")
        jobs = args.jobs or min(4, os.cpu_count() or 1)
        if args.stems:
            sounding = any(r.get("Enabled", True) and not r.get("MasterFilter") for r in song["regions"])   # → regions.wav
            stems = len({p.get("stem", p["name"]) for p in song["parts"]}) + sounding
            print(f"rendering the mix, then {stems} stems ({jobs} at a time); each stem takes about as long as the mix", file=sys.stderr)
        report = engine.render(song_path, wav, rate=args.rate or output.get("rate", 48000), bits=args.bits or output.get("bits", 24),
                               start_beat=start, length_beats=length, tail=args.tail if args.tail is not None else output.get("tail", 2.0),
                               normalize=normalize, stems_dir=out_dir / "stems" if args.stems else None, jobs=jobs)
        per_second = report.pop("perSecond", [])
        sections = []
        range_start = start or 0
        range_end = min(meta["loop"], range_start + length) if length else meta["loop"]
        offset = _seconds_at(range_start, meta["bpm"], meta["tempo"])
        for s in meta["sections"]:
            if s["beat"] >= range_end or s["beat"] + s["beats"] <= range_start:
                continue  # outside the rendered range (the tail only rings out; it plays no later section)
            a = _seconds_at(max(s["beat"], range_start), meta["bpm"], meta["tempo"]) - offset
            b = _seconds_at(min(s["beat"] + s["beats"], range_end), meta["bpm"], meta["tempo"]) - offset
            window = per_second[max(0, int(a)):max(0, int(math.ceil(b)))]
            if window:
                power = sum(10 ** (r / 10) for r, _ in window) / len(window)
                sections.append({"name": s["name"], "rmsDb": round(10 * math.log10(power), 1) if power > 0 else None,
                                 "peakDb": max(p for _, p in window)})
        (out_dir / "report.json").write_text(json.dumps(dict(report, perSecond=per_second, sections=sections), indent=1), encoding="utf-8")
        result["files"]["wav"] = report["out"]
        result["render"] = {k: report[k] for k in ("seconds", "rate", "bits", "playbackVersion", "peakVoices", "voiceLimit", "droppedNotes",
                                                    "peakDb", "rmsDb", "gainDb", "outputPeakDb", "outputRmsDb", "nearFullScale")}
        result["sections"] = sections
        if report.get("parts"):
            result["stems"] = [{k: p[k] for k in ("name", "kind", "file", "notes", "rmsDb", "peakDb")} for p in report["parts"]]
        full = start is None and length is None
        result["advice"] = _advice(report, meta, sections, per_second if full else None,
                                   args.tail if args.tail is not None else output.get("tail", 2.0))
        result["files"]["report"] = str(out_dir / "report.json")
    if not args.no_score:
        mid = out_dir / f"{slug}.mid"
        midi.write(mid, meta)
        xml = out_dir / f"{slug}.musicxml"
        meta["warnings"] += musicxml.write(xml, meta)
        result["files"]["midi"] = str(mid)
        result["files"]["musicxml"] = str(xml)
        if args.pdf:
            try:
                result["files"]["pdf"] = str(export.convert_score(xml, out_dir / f"{slug}.pdf"))
            except RuntimeError as error:
                meta["warnings"].append(f"pdf: {error}")
    if args.unity:
        result["files"]["unity"] = str(export.unity_folder(song, meta, out_dir / "unity", slug))
    result["files"]["songJson"] = str(song_path)
    result["summary"] = _summary(song, meta)
    _print(result)
    return 0


def cmd_convert(args):
    _print({"ok": True, "out": str(export.convert_score(Path(args.source).resolve(), Path(args.target).resolve()))})
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(prog="notewright", description=f"notewright {__version__}: compose with the com.graze.music engine")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("doctor", help="check Python, .NET/Mono, the renderer build, instrument downloads and MuseScore")
    p = sub.add_parser("setup", help="start-of-work check that fixes what it can (test render on each runtime, keeps the fastest that works); cached")
    p.add_argument("--force", action="store_true", help="run every check again instead of using the cached result")
    p = sub.add_parser("instruments", help="list / find / fetch / scan / add / info / remove instruments")
    p.add_argument("action", choices=["list", "find", "fetch", "scan", "add", "info", "remove"])
    p.add_argument("terms", nargs="*")
    p.add_argument("--kind", choices=["melodic", "drums"])
    p.add_argument("--installed", action="store_true")
    p.add_argument("--limit", type=int, default=8)
    p.add_argument("--velocity", type=int, default=100, help="fetch: velocity layer to download (1-127)")
    p.add_argument("--all-layers", action="store_true", help="fetch: every velocity layer and round robin")
    p.add_argument("--name", help="scan: library name used in ids (local:<name>/...)")
    p.add_argument("--root", default="C4", help="add: the note the sample plays")
    p = sub.add_parser("new", help="start a spec from a template")
    p.add_argument("path")
    p.add_argument("--template", default="pop")
    p.add_argument("--force", action="store_true")
    p = sub.add_parser("check", help="compile a spec without rendering; prints a summary and warnings")
    p.add_argument("spec")
    p.add_argument("--no-fetch", action="store_true", help="fail instead of downloading missing instruments")
    p = sub.add_parser("render", help="render a spec to WAV (+ MIDI and MusicXML score)")
    p.add_argument("spec")
    p.add_argument("--out", help="output folder (default <spec folder>/out/<title>)")
    p.add_argument("--name", help="file name stem (default: the title)")
    p.add_argument("--rate", type=int)
    p.add_argument("--bits", type=int, choices=[16, 24, 32])
    p.add_argument("--tail", type=float, help="seconds of release tail after the end; nothing new starts in it (default 2)")
    p.add_argument("--normalize", type=float, help="scale the mix so its peak hits this dBFS, e.g. -1")
    p.add_argument("--stems", action="store_true", help="also render each track alone into stems/<track>.wav")
    p.add_argument("--jobs", type=int, help="stems rendered at the same time (default: CPU cores, at most 4)")
    p.add_argument("--section", help="render only this section")
    p.add_argument("--bars", help="render only bars A-B (1-based, inclusive)")
    p.add_argument("--no-score", action="store_true", help="skip the MIDI and MusicXML files")
    p.add_argument("--score-only", action="store_true", help="skip audio; write MIDI and MusicXML")
    p.add_argument("--pdf", action="store_true", help="also convert the score to PDF with MuseScore")
    p.add_argument("--unity", action="store_true", help="also write a Unity SongJsonBuilder folder (song.json + Samples + CREDITS)")
    p = sub.add_parser("convert", help="convert a score with MuseScore (e.g. song.musicxml song.pdf)")
    p.add_argument("source")
    p.add_argument("target")
    for stream in (sys.stdout, sys.stderr):   # JSON is UTF-8 even where the console code page is not (cp949, cp1252)
        if hasattr(stream, "reconfigure") and (stream.encoding or "").lower().replace("-", "") != "utf8":
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")
    args = parser.parse_args(argv)
    handlers = {"doctor": cmd_doctor, "setup": cmd_setup, "instruments": cmd_instruments, "new": cmd_new, "check": cmd_check, "render": cmd_render,
                "convert": cmd_convert}
    try:
        return handlers[args.command](args)
    except (specs.SpecError, catalog.CatalogError, engine.EngineError, RuntimeError, FileNotFoundError, ValueError) as error:
        _print({"ok": False, "error": str(error)})
        return 2
