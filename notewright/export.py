"""Exports besides the WAV: a Unity-ready folder for com.graze.music's SongJsonBuilder, and PDF/MuseScore conversion of the
MusicXML score when MuseScore is installed."""
import copy
import json
import os
import re
import shutil
import subprocess
from pathlib import Path


def _file_refs(song):
    """Every (object, key) of a song.json that names a file: part samples, zones, variants, audio regions, wavetables and IRs."""
    refs = []
    for part in song["parts"]:
        refs += [(part, k) for k in ("sample", "wavetable", "impulse") if part.get(k)]
        for key in ("zones", "variants", "regions"):
            refs += [(item, "sample") for item in part.get(key) or [] if item.get("sample")]
    refs += [(bus, "impulse") for bus in song.get("buses") or [] if bus.get("impulse")]
    if song.get("masterImpulse"):
        refs.append((song, "masterImpulse"))
    return refs


def unity_folder(song, meta, folder: Path, name: str):
    """<folder>/<name>.song.json with file names relative to <folder>/Samples/, the files copied there, and CREDITS.md.
    In Unity, SongJsonBuilder.Build(folder, name, ...) turns it into a MusicCueDefinition asset."""
    folder.mkdir(parents=True, exist_ok=True)
    samples = folder / "Samples"
    samples.mkdir(exist_ok=True)
    out = copy.deepcopy(song)
    instrument = {info["name"]: info["instrument"] for info in meta["parts"]}
    names, taken = {}, set()
    for part in out["parts"]:
        prefix = re.sub(r"[^A-Za-z0-9]+", "-", instrument.get(part["name"], part["name"])).strip("-")
        for owner, key in _file_refs({"parts": [part]}):
            source = Path(owner[key])
            if source not in names:
                target = f"{prefix}-{source.stem}.wav"
                for i in range(2, 10 ** 6):
                    if target not in taken:
                        break
                    target = f"{prefix}-{source.stem}-{i}.wav"
                shutil.copyfile(source, samples / target)
                names[source], _ = target, taken.add(target)
            owner[key] = names[source]
        part.pop("stem", None)                                  # renderer-only key
    song_refs = [(bus, "impulse") for bus in out.get("buses") or [] if bus.get("impulse")] + ([(out, "masterImpulse")] if out.get("masterImpulse") else [])
    for owner, key in song_refs:
        source = Path(owner[key])
        if source not in names:
            target = f"ir-{source.stem}.wav"
            shutil.copyfile(source, samples / target)
            names[source] = target
        owner[key] = names[source]
    path = folder / f"{name}.song.json"
    path.write_text(json.dumps(out, indent=1, ensure_ascii=False), encoding="utf-8")
    lines = [f"# {meta['title']} — instruments", ""]
    for c in meta["credits"]:
        lines.append(f"- {c['instrument']} ({c['source']}): {c['license'] or 'license unknown'}" + (f" — {c['credit']}" if c["credit"] else ""))
    (folder / "CREDITS.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


def musescore():
    """Path of a MuseScore executable, or None. MUSESCORE env var wins."""
    if os.environ.get("MUSESCORE"):
        return os.environ["MUSESCORE"]
    for exe in ("mscore4portable", "MuseScore4", "mscore4", "musescore4", "mscore", "musescore", "MuseScore3", "mscore3", "musescore3"):
        found = shutil.which(exe)
        if found:
            return found
    for candidate in (r"C:\Program Files\MuseScore 4\bin\MuseScore4.exe", r"C:\Program Files\MuseScore 3\bin\MuseScore3.exe",
                      "/Applications/MuseScore 4.app/Contents/MacOS/mscore", "/Applications/MuseScore 3.app/Contents/MacOS/mscore"):
        if Path(candidate).exists():
            return candidate
    return None


def convert_score(source: Path, target: Path):
    """MusicXML/MIDI → PDF, .mscz, PNG… via MuseScore. Raises RuntimeError when it is missing or fails."""
    exe = musescore()
    if not exe:
        raise RuntimeError("MuseScore is not installed (https://musescore.org); open the .musicxml in any notation app instead, "
                           "or set MUSESCORE to its executable")
    env = dict(os.environ, QT_QPA_PLATFORM=os.environ.get("QT_QPA_PLATFORM", "offscreen"))
    result = subprocess.run([exe, "-o", str(target), str(source)], capture_output=True, text=True, env=env, timeout=600)
    if target.exists():
        return target
    pages = sorted(target.parent.glob(f"{target.stem}-*{target.suffix}"))       # PNG/SVG: one file per page
    if pages:
        return pages[0]
    raise RuntimeError(f"MuseScore failed ({result.returncode}): {(result.stderr or result.stdout)[-1500:]}")
