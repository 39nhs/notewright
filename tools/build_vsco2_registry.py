#!/usr/bin/env python3
"""Regenerates data/registry.json entries for VSCO 2 Community Edition (CC0) from a clone's SFZ branch file list.
usage: python tools/build_vsco2_registry.py <path to a vsco-2-ce clone with the SFZ branch fetched> <SFZ commit>"""
import json
import re
import subprocess
import sys
from pathlib import Path

FAMILY = [("SViolin", ["violin", "solo", "strings"]), ("ViolinEns", ["violin", "section", "ensemble", "strings"]),
          ("ViolaEns", ["viola", "section", "ensemble", "strings"]), ("CelloEns", ["cello", "section", "ensemble", "strings"]),
          ("Contrabass", ["contrabass", "double bass", "bass", "strings"]), ("Piccolo", ["piccolo", "flute", "woodwind"]),
          ("Flute", ["flute", "woodwind"]), ("Oboe", ["oboe", "woodwind", "reed"]), ("Clarinet", ["clarinet", "woodwind", "reed"]),
          ("Bassoon", ["bassoon", "woodwind", "reed", "low"]), ("Trumpet", ["trumpet", "brass"]), ("Trombone", ["trombone", "brass"]),
          ("Tuba", ["tuba", "brass", "low"]), ("FHorn", ["french horn", "horn", "brass"]), ("UprightPiano", ["piano", "upright", "keys", "acoustic"]),
          ("VSUpright1", ["piano", "upright", "keys", "acoustic"]), ("Organ", ["organ", "pipe organ", "keys", "church"]),
          ("Harp", ["harp", "plucked", "strings"]), ("Glockenspiel", ["glockenspiel", "mallet", "bell", "percussion"]),
          ("Marimba", ["marimba", "mallet", "percussion", "wood"]), ("Xylophone", ["xylophone", "mallet", "percussion"]),
          ("TubularBells", ["tubular bells", "chimes", "bell", "percussion"]), ("TimpaniRolls", ["timpani", "roll", "percussion", "orchestral"]),
          ("Timpani", ["timpani", "percussion", "orchestral"]), ("GM-StylePerc", ["drums", "kit", "orchestral percussion", "acoustic"])]
STYLE = [("SusVib", ["sustained", "vibrato", "legato"]), ("SusNV", ["sustained", "no vibrato"]), ("SusVB", ["sustained", "vibrato"]),
         ("ExpVib", ["sustained", "expressive", "vibrato"]), ("Sus", ["sustained", "legato"]), ("Vib", ["sustained", "vibrato"]),
         ("Stac", ["staccato", "short"]), ("Spic", ["spiccato", "short"]), ("Pizz", ["pizzicato", "short", "plucked"]),
         ("Trem", ["tremolo"]), ("HarmonMute", ["muted", "harmon"]), ("StraightMute", ["muted"]), ("Mute", ["muted"]),
         ("Quiet", ["soft", "quiet"]), ("Loud", ["loud"]), ("Pedal", ["pedal", "low"])]


RENAME = {"SViolin": "SoloViolin", "VSUpright1": "UprightPiano2", "FHorn": "FrenchHorn", "GM-StylePerc": "OrchestralKit"}


def kebab(name):
    for old, new in RENAME.items():
        name = name.replace(old, new)
    return re.sub(r"(?<=[a-z0-9])(?=[A-Z])", "-", name.replace("-KS", "")).replace("--", "-").lower()


def main(clone, commit):
    names = [n for n in subprocess.run(["git", "-C", clone, "ls-tree", "--name-only", commit], capture_output=True, text=True,
                                       check=True).stdout.splitlines() if n.lower().endswith(".sfz") and "-KS" not in n]
    entries = []
    for file in sorted(names):
        stem = file[:-4]
        tags = ["orchestral", "acoustic", "vsco2"]
        for key, extra in FAMILY:
            if stem.startswith(key) or key in stem:
                tags += extra
                break
        for key, extra in STYLE:
            if key in stem:
                tags += extra
        entries.append({"id": "vsco2:" + kebab(stem), "name": stem, "library": "vsco2", "sfz": file,
                        "kind": "drums" if stem == "GM-StylePerc" else "melodic", "tags": sorted(set(tags)),
                        "description": f"VSCO 2 Community Edition — {stem}"})
    registry = {"libraries": {"vsco2": {
        "name": "VS Chamber Orchestra: Community Edition (VSCO 2 CE)", "license": "CC0-1.0",
        "credit": "Versilian Studios; recorded by Sam Gossner & Simon Dalzell (credit encouraged, not required)",
        "homepage": "https://github.com/sgossner/VSCO-2-CE",
        "raw": f"https://raw.githubusercontent.com/sgossner/VSCO-2-CE/{commit}/"}}, "instruments": entries}
    out = Path(__file__).resolve().parent.parent / "data" / "registry.json"
    out.write_text(json.dumps(registry, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(entries)} instruments → {out}")


if __name__ == "__main__":
    main(*sys.argv[1:3])
