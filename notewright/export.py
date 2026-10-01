"""Exports besides the WAV: a Unity-ready folder for com.graze.music's SongJsonBuilder, and PDF/MuseScore conversion of the
MusicXML score when MuseScore is installed."""
import copy
import json
import os
import re
import shutil
import subprocess
from pathlib import Path


def unity_folder(song, meta, folder: Path, name: str):
    """<folder>/<name>.song.json with sample names relative to <folder>/Samples/, the samples copied there, and CREDITS.md.
    In Unity, SongJsonBuilder.Build(folder, name, ...) turns it into a MusicCueDefinition asset."""
    folder.mkdir(parents=True, exist_ok=True)
    samples = folder / "Samples"
    samples.mkdir(exist_ok=True)
    out = copy.deepcopy(song)
    names = {}
    for part, info in zip(out["parts"], meta["parts"]):
        source = Path(part["sample"])
        if source not in names:
            prefix = re.sub(r"[^A-Za-z0-9]+", "-", info["instrument"]).strip("-")
            target = f"{prefix}-{source.stem}.wav"
            shutil.copyfile(source, samples / target)
            names[source] = target
        part["sample"] = names[source]
        part.pop("stem", None)                                  # renderer-only key
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
