"""Command line: python notewright.py <command> ... (run with -h for help). Every command prints JSON on stdout so Claude can
read results; progress and downloads go to stderr."""
import argparse
import json
import math
import os
import re
import sys
from pathlib import Path

from . import __version__, catalog, engine, export, midi, midiread, mml, musiccheck, musicxml, paths, spec as specs, synthkit
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
    if paths.vst3_supported():
        check("vst3", "ok" if paths.PLUGIN_HOST.exists() else "action", "VST3 instruments and effects available (`notewright plugins` lists them)"
              if paths.PLUGIN_HOST.exists() else f"the plugin host is missing: {paths.PLUGIN_HOST}", None if paths.PLUGIN_HOST.exists()
              else "reinstall or update the plugin")
    else:
        check("vst3", "info", "not available here: VST3 plugins work only on Windows x64 (use the built-in synth, samples and effects)")
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


def cmd_plugins(args):
    """Installed VST3 plugins (Windows x64 only), or one plugin's parameters and state with --info."""
    if not paths.vst3_supported():
        _print({"ok": False, "error": paths.VST3_UNAVAILABLE})
        return 1
    if args.info:
        info = engine.plugin_info(args.info, preset=args.preset, roots=args.roots)
        if not args.state:
            info["state"] = f"{len(info['state'])} characters (add --state to print it)"
        _print(info)
    else:
        found = engine.plugins(rescan=args.rescan, roots=args.roots)
        kinds = [k for k in ("instrument", "effect") if not args.kind or args.kind == k]
        _print({"ok": True, "worker": f"protocol {found['worker']}", "roots": found["roots"], "errors": found["errors"],
                "plugins": [{k: p[k] for k in ("name", "vendor", "version", "kind", "classId")} for p in found["plugins"] if p["kind"] in kinds]})
    return 0


def cmd_midi(args):
    """A MIDI file as a reference: tempo, meter, key, tracks and the chord of every bar; --spec writes it as an editable spec."""
    info = midiread.summary(args.file)
    if args.spec:
        target = Path(args.spec)
        if target.exists() and not args.force:
            raise SystemExit(f"{target} exists (use --force to overwrite)")
        target.write_text(json.dumps(midiread.to_spec(args.file), indent=1, ensure_ascii=False), encoding="utf-8")
        info["spec"] = str(target.resolve())
    _print(dict(info, ok=True))
    return 0


def cmd_tempo(args):
    """Tempo and beats of a recording, and how to line it up in a spec."""
    report = engine.tempo(Path(args.file).resolve(), args.beats_per_bar, args.min, args.max)
    beats = report.pop("beatSeconds")
    report["firstBeats"] = beats[:16]
    if report["confidence"] < .3:
        report["note"] = "low confidence: the recording has no steady pulse the detector can follow; check the tempo by ear or tap it"
    report["spec"] = {"bpm": report["bpm"], "audio clip": {"file": args.file, "bar": 1, "sourceStart": report["downbeatSeconds"],
                                                         "sourceBpm": report["bpm"], "warp": "Tempo"}}
    if report["tempoChanges"]:
        report["spec"]["tempo"] = [{"beat": c["beat"], "bpm": c["bpm"]} for c in report["tempoChanges"]]
    _print(report)
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


def _bar_of(beat, meta):
    """1-based bar number at a beat (following meter changes); fractional inside a bar."""
    import bisect
    starts = meta.get("barStarts") or []
    if not starts:
        return beat / meta["meter"] + 1
    i = max(0, bisect.bisect_right(starts, beat + 1e-9) - 1)
    length = (starts[i + 1] if i + 1 < len(starts) else meta["loop"]) - starts[i]
    return i + 1 + ((beat - starts[i]) / length if length > 0 else 0)


def _summary(song, meta):
    seconds = _seconds_at(meta["loop"], meta["bpm"], meta["tempo"])
    return {"title": meta["title"], "bpm": meta["bpm"], "meter": meta["meter"], "bars": len(meta.get("barStarts") or []) or meta["loop"] / meta["meter"],
            "seconds": round(seconds, 2), "playbackVersion": song["playbackVersion"],
            "sections": [{"name": s["name"], "bar": _bar_of(s["beat"], meta), "bars": _bar_of(s["beat"] + s["beats"], meta) - _bar_of(s["beat"], meta)}
                         for s in meta["sections"]],
            "tracks": [{"name": t["name"], "instrument": t["instrument"], "notes": len(t["notes"]),
                        "parts": sum(1 for p in meta["parts"] if p["track"] == t["name"])} for t in meta["tracks"]],
            "engineParts": len(song["parts"]), "regions": len(song["regions"]), "warnings": meta["warnings"]}


def cmd_check(args):
    song, meta = _compile(args.spec, auto_fetch=not args.no_fetch)
    _print(dict(_summary(song, meta), ok=True, music=musiccheck.check(meta)))
    return 0


BAND_RANGES = {"low": "20-120 Hz", "lowMid": "120-500 Hz", "mid": "500 Hz-2 kHz", "highMid": "2-6 kHz", "high": "above 6 kHz"}


def _power_mean(values):
    values = [v for v in values if v is not None and v > -150]
    return round(10 * math.log10(sum(10 ** (v / 10) for v in values) / len(values)), 1) if values else None


def _spectrum_advice(report, parts):
    """Tips from the frequency bands, the stereo width, loudness and masking between stems (render analysis)."""
    tips = []
    bands = report.get("bands") or {}
    if bands and bands.get("mid", -180) > -150:
        mid = bands["mid"]
        if bands["low"] - mid > 14:
            tips.append(f"the low end (20-120 Hz) is {bands['low'] - mid:.0f} dB above the mids: boomy; lower the bass/kick or high-pass "
                        "pads and keys (HighPassHz 150-250)")
        elif bands["low"] - mid < -4:
            tips.append("little low end (20-120 Hz is below the mids): fine for acoustic or orchestral music; for pop, rock or electronic "
                        "it sounds thin: give the bass more level or a sub, or a fuller kick")
        if bands["lowMid"] - mid > 6:
            tips.append(f"120-500 Hz is {bands['lowMid'] - mid:.0f} dB above 500 Hz-2 kHz: muddy; high-pass or EQ-cut pads, keys and "
                        "guitars around 250-400 Hz (Eq EqLowDb/EqMidDb)")
        if bands["high"] - mid < -26:
            tips.append("very little above 6 kHz: dark (fine for a soft or orchestral piece); for more air: brighter hats/cymbals, a "
                        "brighter synth (CutoffHz) or EqHighDb +3 on the master")
        elif bands["high"] - mid > -2:
            tips.append("strong above 6 kHz: harsh or hissy; lower hats/cymbals or EqHighDb, or DeEsser on the master")
    widths = [w for w in report.get("widthPerSecond") or [] if w is not None]
    if widths:
        width = sum(widths) / len(widths)
        if width < .05:
            tips.append(f"the mix is almost mono (width {width:.2f}): pan tracks apart (keys, pads, doubles left/right), add Chorus or Reverb")
        elif width > .9:
            tips.append(f"the mix is very wide (width {width:.2f}): out-of-phase sounds vanish in mono; narrow StereoWidth or pans")
    lufs, peak = report.get("lufs"), report.get("truePeakDb")
    if lufs is not None and not report.get("targetLufs") and lufs < -20 and not report.get("gainDb"):
        tips.append(f"integrated loudness {lufs:.1f} LUFS is quiet next to streamed music (about -14): set \"targetLufs\": -14 in the spec")
    if peak is not None and peak + (report.get("gainDb") or 0) > -.5:
        tips.append(f"true peak {peak + (report.get('gainDb') or 0):.1f} dBTP after normalizing: keep it under -1 (master Limiter, "
                    "LimiterCeilingDb -1) so encoders don't clip")
    # masking: two stems whose main band is the same, both carrying it at a similar level for a good part of the song
    mix_seconds = report.get("bandsPerSecond") or []
    tracks = [p for p in parts if p.get("kind", "track") == "track" and p.get("bandsPerSecond")]
    names = report.get("bandNames") or list(BAND_RANGES)
    main = {p["name"]: max(names, key=lambda n: p["bands"].get(n, -180)) for p in tracks}
    masking = []
    for b, band in enumerate(names):
        if band in ("highMid", "high"):
            continue
        for i, a in enumerate(tracks):
            for c in tracks[i + 1:]:
                if main[a["name"]] != band or main[c["name"]] != band:
                    continue
                both = shared = 0
                for second, mix in enumerate(mix_seconds):
                    if second >= len(a["bandsPerSecond"]) or second >= len(c["bandsPerSecond"]):
                        break
                    x, y, m = a["bandsPerSecond"][second][b], c["bandsPerSecond"][second][b], mix[b]
                    if x > m - 6 and y > m - 6:
                        both += 1
                        if abs(x - y) < 4:
                            shared += 1
                if shared >= 8 and shared >= .5 * both:
                    quieter = a if a["bands"][band] < c["bands"][band] else c
                    other = c if quieter is a else a
                    fix = ("high-pass it (HighPassHz 120-200) so the bass owns the lows" if band == "low" else
                           "EQ-cut it there (Eq: EqLowDb/EqMidDb -3..-6), move it an octave, or give each its own section")
                    masking.append((shared, f"'{a['name']}' and '{c['name']}' both sit mainly in {BAND_RANGES[band]} at the same level for "
                                            f"{shared} s: they mask each other; for '{quieter['name']}' {fix}, or pan it away from '{other['name']}'"))
    tips += [text for _, text in sorted(masking, reverse=True)[:3]]
    return tips


def _changes(old, new, sections, advice):
    """What moved since the previous render of the same range (levels in dB; only moves of 0.5 dB or more)."""
    out = {}

    def delta(label, a, b):
        if isinstance(a, (int, float)) and isinstance(b, (int, float)) and abs(b - a) >= .5:
            out[label] = f"{a:g} → {b:g} ({b - a:+.1f})"
    for key in ("outputRmsDb", "outputPeakDb", "lufs"):
        delta(key, old.get(key), new.get(key))
    for band in (new.get("bands") or {}):
        delta(f"bands.{band}", (old.get("bands") or {}).get(band), new["bands"][band])
    before = {s["name"]: s for s in old.get("sections") or []}
    for s in sections:
        if s["name"] in before:
            delta(f"section {s['name']}", before[s["name"]].get("rmsDb"), s.get("rmsDb"))
    stems = {p["name"]: p for p in old.get("parts") or []}
    for p in new.get("parts") or []:
        if p["name"] in stems:
            delta(f"stem {p['name']}", stems[p["name"]].get("rmsDb"), p.get("rmsDb"))
    same = lambda text: re.sub(r"-?\d+(\.\d+)?", "#", text)          # the same tip with other numbers is not new
    before_tips, after_tips = {same(a) for a in old.get("advice") or []}, {same(a) for a in advice}
    gone = [a for a in old.get("advice") or [] if same(a) not in after_tips]
    added = [a for a in advice if same(a) not in before_tips]
    if gone:
        out["adviceResolved"] = gone
    if added:
        out["adviceNew"] = added
    return out or {"note": "no level moved by 0.5 dB or more since the previous render of this range"}


def _advice(report, meta, sections, per_second=None, tail=0.0):
    """Mixing tips from the report. per_second (full renders only) also checks the start and end for a lead-in and a ring-out."""
    tips = _spectrum_advice(report, report.get("parts") or [])
    for warning in report.get("pluginWarnings") or []:
        tips.append(f"VST3: {warning} — the track played its fallback (or the insert was bypassed); check the plugin and its settings")
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
    if meta["vst3"] and not args.score_only and not paths.vst3_supported():
        raise specs.SpecError(f"this spec uses VST3 plugins ({', '.join(meta['vst3'])}). {paths.VST3_UNAVAILABLE} "
                              "--score-only still writes MIDI and MusicXML.")
    if not args.score_only:
        if args.preview:                     # quick listen while iterating: half the sample rate (about twice as fast), 16-bit
            args.rate, args.bits, args.no_score = args.rate or 24000, args.bits or 16, True
        wav = out_dir / f"{slug}{'-' + _slug(args.section) if args.section else ''}{'-bars-' + args.bars if args.bars else ''}" \
                        f"{'-preview' if args.preview else ''}.wav"
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
                entry = {"name": s["name"], "rmsDb": round(10 * math.log10(power), 1) if power > 0 else None, "peakDb": max(p for _, p in window)}
                spectrum = (report.get("bandsPerSecond") or [])[max(0, int(a)):max(0, int(math.ceil(b)))]
                if spectrum:
                    entry["bands"] = {name: _power_mean([row[i] for row in spectrum]) for i, name in enumerate(report["bandNames"])}
                widths = (report.get("widthPerSecond") or [])[max(0, int(a)):max(0, int(math.ceil(b)))]
                if widths:
                    entry["width"] = round(sum(widths) / len(widths), 2)
                sections.append(entry)
        range_key = {"section": args.section, "bars": args.bars, "preview": bool(args.preview)}
        previous = None
        try:
            previous = json.loads((out_dir / "report.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            pass
        result["files"]["wav"] = report["out"]
        result["render"] = {k: report[k] for k in ("seconds", "rate", "bits", "playbackVersion", "peakVoices", "voiceLimit", "droppedNotes",
                                                    "peakDb", "rmsDb", "gainDb", "outputPeakDb", "outputRmsDb", "nearFullScale", "lufs",
                                                    "truePeakDb", "bands") if k in report}
        result["sections"] = sections
        if report.get("plugins"):
            result["plugins"] = report["plugins"]
        if report.get("pluginWarnings"):
            result["pluginWarnings"] = report["pluginWarnings"]
        if report.get("parts"):
            result["stems"] = [{k: p[k] for k in ("name", "kind", "file", "notes", "rmsDb", "peakDb", "lufs", "bands") if k in p} for p in report["parts"]]
        full = start is None and length is None
        result["advice"] = _advice(report, meta, sections, per_second if full else None,
                                   args.tail if args.tail is not None else output.get("tail", 2.0))
        if previous and previous.get("range") == range_key:
            result["changes"] = _changes(previous, report, sections, result["advice"])
        (out_dir / "report.json").write_text(json.dumps(dict(report, perSecond=per_second, sections=sections, range=range_key,
                                                             advice=result["advice"]), indent=1), encoding="utf-8")
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
    if args.mml:
        result["mml"] = _write_mml(meta, args.mml, args.mml_dynamics, out_dir, slug, result["files"])
    if args.unity:
        result["files"]["unity"] = str(export.unity_folder(song, meta, out_dir / "unity", slug))
    result["files"]["songJson"] = str(song_path)
    result["summary"] = _summary(song, meta)
    result["music"] = musiccheck.check(meta)
    _print(result)
    return 0


HINTS = [
    (r"expected 'beat duration pitch", '"notes": "0 1 C4 | 1 0.5 E4,G4 @0.8 | 2 2 kick"  (beat, duration, pitch[,pitch], optional @velocity)'),
    (r"is not a note|is not a pitch", '"C4", "F#3", "Bb2", "60", or a drum name like "kick", "snare", "hat"'),
    (r"a point is|expected a list of points", '[{"bar": 1, "value": -30}, {"bar": 3, "value": 0, "curve": "smooth"}]  or  [[0, -30], [8, 0, "linear"]]'),
    (r"give 'bars' \(or 'beats'\)", '{"name": "verse", "bars": 8, "play": {"drums": "beat", "bass": "bassline"}}'),
    (r"unknown chord", '"chords": "Am F C G7"  or numerals in the song key: "i VI III VII", "I V vi IV"; ":2" = two slots, "-" holds'),
    (r"unknown pattern", '"patterns": {"riff": {"melody": "A4:0.5 C5 E5 r:0.5"}}  then play it with "riff"'),
    (r"'groove'", '{"groove": "rock", "fill": 4}  on a drum track; grooves: rock, fourOnFloor, halfTime, breakbeat, metal, shuffle'),
    (r"unknown key '", '"key": "A minor"  (or "F# dorian", "Bb", "C#m")'),
    (r"unknown keys \[", "check the spelling: valid keys are listed in the message"),
]


def _hint(message):
    for pattern, example in HINTS:
        if re.search(pattern, message):
            return example
    return None


def _write_mml(meta, games, dynamics, out_dir, slug, files):
    """Writes <slug>.<game>.mml.txt per game; returns a summary per game (scores, character counts, warnings)."""
    summary = {}
    for game in (list(mml.GAMES) if "all" in games else games):
        result = mml.score(meta, game, dynamics)
        path = out_dir / f"{slug}.{game}.mml.txt"
        path.write_text(mml.text_file(result, meta), encoding="utf-8")
        files[f"mml-{game}"] = str(path)
        summary[game] = {"scores": [{k: s[k] for k in ("track", "part", "of", "bars", "instrument", "chars", "notes", "fits")}
                                    for s in result["scores"]], "warnings": result["warnings"], "about": result["about"]}
    return summary


def cmd_mml(args):
    """Game MML from a spec (no renderer needed): Mabinogi, Mabinogi Mobile or Lost Ark."""
    spec_path = Path(args.spec).resolve()
    song, meta = _compile(spec_path)
    slug = _slug(meta["title"])
    out_dir = Path(args.out).resolve() if args.out else spec_path.parent / "out" / slug
    out_dir.mkdir(parents=True, exist_ok=True)
    files = {}
    summary = _write_mml(meta, args.game, args.dynamics, out_dir, slug, files)
    _print({"ok": True, "files": files, "mml": summary})
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
    p.add_argument("--preview", action="store_true", help="quick listen: 24 kHz 16-bit, no score, about twice as fast (<title>-preview.wav)")
    p.add_argument("--bars", help="render only bars A-B (1-based, inclusive)")
    p.add_argument("--no-score", action="store_true", help="skip the MIDI and MusicXML files")
    p.add_argument("--score-only", action="store_true", help="skip audio; write MIDI and MusicXML")
    p.add_argument("--pdf", action="store_true", help="also convert the score to PDF with MuseScore")
    p.add_argument("--unity", action="store_true", help="also write a Unity SongJsonBuilder folder (song.json + Samples + CREDITS)")
    p.add_argument("--mml", nargs="+", choices=list(mml.GAMES) + ["all"], help="also write game MML: mabinogi, mabinogi-mobile, lostark or all")
    p.add_argument("--mml-dynamics", choices=mml.DYNAMICS, default="coarse", help="MML volume changes: coarse (default, fewer characters), full, none")
    p = sub.add_parser("midi", help="read a MIDI file: tempo, meter, key, tracks, chords per bar; --spec writes an editable spec")
    p.add_argument("file")
    p.add_argument("--spec", help="also write the MIDI file as a spec to this path")
    p.add_argument("--force", action="store_true")
    p = sub.add_parser("tempo", help="tempo, beats and first downbeat of a WAV recording")
    p.add_argument("file")
    p.add_argument("--beats-per-bar", type=int, default=4)
    p.add_argument("--min", type=float, default=60, help="lowest BPM to consider")
    p.add_argument("--max", type=float, default=200, help="highest BPM to consider")
    p = sub.add_parser("mml", help="game MML (Mabinogi, Mabinogi Mobile, Lost Ark) from a spec; no renderer needed")
    p.add_argument("spec")
    p.add_argument("--game", nargs="+", choices=list(mml.GAMES) + ["all"], default=["all"])
    p.add_argument("--dynamics", choices=mml.DYNAMICS, default="coarse", help="volume changes: coarse (default), full, none")
    p.add_argument("--out", help="output folder (default <spec folder>/out/<title>)")
    p = sub.add_parser("plugins", help="installed VST3 instruments and effects (Windows x64 only); --info NAME lists a plugin's parameters")
    p.add_argument("--info", metavar="NAME", help="load this plugin (name or class ID): parameter keys, values 0-1, latency, state")
    p.add_argument("--preset", help="with --info: read this .vstpreset into the state (for 'state' in the spec)")
    p.add_argument("--state", action="store_true", help="with --info: print the whole state (base64)")
    p.add_argument("--kind", choices=["instrument", "effect"])
    p.add_argument("--rescan", action="store_true", help="scan every plugin again instead of using the cached scan")
    p.add_argument("--roots", help="VST3 folders to scan, ';'-separated (default: the standard folders, or NOTEWRIGHT_VST3_PATH)")
    p = sub.add_parser("convert", help="convert a score with MuseScore (e.g. song.musicxml song.pdf)")
    p.add_argument("source")
    p.add_argument("target")
    for stream in (sys.stdout, sys.stderr):   # JSON is UTF-8 even where the console code page is not (cp949, cp1252)
        if hasattr(stream, "reconfigure") and (stream.encoding or "").lower().replace("-", "") != "utf8":
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")
    args = parser.parse_args(argv)
    handlers = {"doctor": cmd_doctor, "setup": cmd_setup, "instruments": cmd_instruments, "new": cmd_new, "check": cmd_check, "render": cmd_render,
                "convert": cmd_convert, "plugins": cmd_plugins, "midi": cmd_midi, "tempo": cmd_tempo, "mml": cmd_mml}
    try:
        return handlers[args.command](args)
    except (specs.SpecError, catalog.CatalogError, engine.EngineError, mml.MmlError, RuntimeError, FileNotFoundError, ValueError) as error:
        out = {"ok": False, "error": str(error)}
        hint = _hint(str(error))
        if hint:
            out["example"] = hint
        _print(out)
        return 2
