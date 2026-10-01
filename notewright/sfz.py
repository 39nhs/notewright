"""SFZ subset reader: <control>/<global>/<master>/<group>/<region> with sample, key/lokey/hikey/pitch_keycenter (numbers or
note names), lovel/hivel, volume, tune, transpose, ampeg_attack/release, default_path, seq_length/seq_position,
sw_* key switches (only the default articulation is kept) and trigger (release triggers are skipped)."""
import re
from pathlib import Path

from .notes import note_number

HEADER = re.compile(r"<(\w+)>")
OPCODE = re.compile(r"([A-Za-z0-9_]+)=")


def _strip_comments(text):
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
    return re.sub(r"//[^\n]*", " ", text)


def _opcodes(chunk):
    """Opcode values may contain spaces (sample paths): a value runs until the next 'name=' or line end."""
    found = {}
    for line in chunk.splitlines():
        matches = list(OPCODE.finditer(line))
        for i, m in enumerate(matches):
            end = matches[i + 1].start() if i + 1 < len(matches) else len(line)
            found[m.group(1)] = line[m.end():end].strip()
    return found


def _key(value, default):
    if value is None or value == "":
        return default
    n = note_number(value)
    return default if n is None else n


def parse(path):
    path = Path(path)
    text = _strip_comments(path.read_text(encoding="utf-8", errors="replace"))
    control, scopes, regions = {}, {"global": {}, "master": {}, "group": {}}, []
    pieces = HEADER.split(text)
    for i in range(1, len(pieces), 2):
        header, body = pieces[i].lower(), _opcodes(pieces[i + 1])
        if header == "control":
            control.update(body)
        elif header in ("global", "master", "group"):
            scopes[header] = dict(body)
            if header == "global":
                scopes["master"], scopes["group"] = {}, {}
            elif header == "master":
                scopes["group"] = {}
        elif header == "region":
            merged = {**scopes["global"], **scopes["master"], **scopes["group"], **body}
            regions.append(merged)
    base = path.parent / control.get("default_path", "").replace("\\", "/")
    defaults = [r for r in regions if r.get("sw_default")]
    default_switch = _key(defaults[0].get("sw_default"), None) if defaults else None
    result = []
    for r in regions:
        if "sample" not in r or r.get("trigger", "attack") != "attack":
            continue
        if "sw_last" in r and default_switch is not None and _key(r["sw_last"], None) != default_switch:
            continue
        key = _key(r.get("key"), None)
        lo = _key(r.get("lokey"), key if key is not None else 0)
        hi = _key(r.get("hikey"), key if key is not None else 127)
        center = _key(r.get("pitch_keycenter"), key if key is not None else 60)
        center -= int(float(r.get("transpose", 0) or 0))
        result.append({
            "sample": str((base / r["sample"].replace("\\", "/")).resolve()),
            "lo": lo, "hi": hi, "key": center,
            "lovel": int(float(r.get("lovel", 0))), "hivel": int(float(r.get("hivel", 127))),
            "volume": float(r.get("volume", 0) or 0), "tune": float(r.get("tune", 0) or 0),
            "seq": int(float(r.get("seq_position", 1) or 1)),
            "attack": float(r["ampeg_attack"]) if "ampeg_attack" in r else None,
            "release": float(r["ampeg_release"]) if "ampeg_release" in r else None,
        })
    return result
