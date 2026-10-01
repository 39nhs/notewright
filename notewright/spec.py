"""Song spec (what Claude writes) → engine song.json (what the com.graze.music runtime plays).

A spec describes instruments, tracks, reusable patterns and an arrangement of sections; see skills/compose-song/reference/spec.md.
compile() resolves instruments (downloading registry instruments on demand), expands the arrangement into notes, splits each
track into engine parts (the engine plays one sample per part: one part per used key zone, one per drum sound) and returns the
song.json dict plus metadata used by the MIDI / MusicXML / Unity exports."""
import json
import math
import re
from pathlib import Path

from . import catalog
from .notes import DRUMS, name as note_name, note_number, number

MAX_PARTS = 32                       # MusicCue.MaxParts: instrument parts + additive noise regions
LAYERS = ("Melody", "Bass", "Harmony", "Drums")
DRIVE_MODES = ("Soft", "Hard", "Fold", "Fuzz")                      # MusicDriveMode order
NOISE_KINDS = ("White", "Pink", "Brown", "Sample", "SlowingGlitch", "AcceleratingGlitch", "PitchGlitch", "StutterGlitch",
               "BitcrushGlitch", "GateGlitch")                       # MusicNoiseKind order
MIXER_FIELDS = {"OutputDb", "LowPassHz", "HighPassHz", "Compressor", "ThresholdDb", "Ratio", "AttackMs", "ReleaseMs", "EchoWet",
                "EchoSeconds", "EchoFeedback", "CrushBits", "CrushDownsample", "CrushMix", "NoiseGate", "NoiseGateThresholdDb",
                "NoiseGateRangeDb", "NoiseGateHoldMs", "NoiseGateReleaseMs", "Gate", "GateBeats", "GateOpen", "GateDepth", "GatePattern",
                "Drive", "DriveMode", "DriveDb", "DriveMix", "DriveToneHz", "DriveKeepBassHz", "DriveTrimDb"}
REGION_FIELDS = {"Name", "Enabled", "Kind", "StartBeat", "DurationBeats", "Gain", "FadeInBeats", "FadeOutBeats", "Seed", "LoopSource",
                 "RepeatWithCue", "MasterFilter", "Wet", "GlitchRate", "PitchDepth"}
TRACK_KEYS = {"instrument", "layer", "gain", "pan", "attack", "release", "glide", "drive", "driveMode", "effects", "transpose",
              "velocity", "splits", "mute", "playbackVersion", "program", "clef", "score"}
# Kit keys missing from a drum instrument fall back to a close sound before failing.
DRUM_FALLBACK = {35: 36, 40: 38, 37: 38, 39: 38, 44: 42, 46: 42, 52: 49, 55: 49, 57: 49, 53: 51, 59: 51, 41: 45, 43: 45, 48: 47,
                 45: 47, 50: 47, 54: 42, 70: 42, 69: 42, 56: 37, 81: 51}
GRID = {"X": 1.0, "x": .85, "o": .45, "g": .3}                     # drum grid hits; '.', '-', '_' rest; '|' and spaces ignored


class SpecError(ValueError):
    pass


def load(path):
    """Reads a spec file: JSON with // and /* */ comments and trailing commas allowed."""
    text = Path(path).read_text(encoding="utf-8")
    out, i, in_string = [], 0, False
    while i < len(text):
        c = text[i]
        if in_string:
            out.append(c)
            if c == "\\":
                out.append(text[i + 1]); i += 1
            elif c == '"':
                in_string = False
        elif c == '"':
            in_string = True; out.append(c)
        elif text.startswith("//", i):
            while i < len(text) and text[i] != "\n":
                i += 1
            continue
        elif text.startswith("/*", i):
            i = text.index("*/", i) + 2
            continue
        else:
            out.append(c)
        i += 1
    cleaned = re.sub(r",(\s*[}\]])", r"\1", "".join(out))
    try:
        return json.loads(cleaned)
    except json.JSONDecodeError as error:
        raise SpecError(f"{path}: JSON error line {error.lineno}: {error.msg}") from None


# ---------- pitches, velocities, durations ----------

def _pitch(token, where):
    key = note_number(token)
    if key is None or not 0 <= key <= 127:
        raise SpecError(f"{where}: '{token}' is not a note (C4 = 60, F#3, Bb2, 0-127) or drum name ({', '.join(sorted(DRUMS)[:8])}, ...)")
    return key


def _velocity(token, where):
    try:
        v = float(token)
    except (TypeError, ValueError):
        raise SpecError(f"{where}: velocity '{token}' is not a number (0-1 or 1-127, e.g. @0.7)") from None
    if v > 1:
        v /= 127.0
    if not 0 < v <= 1:
        raise SpecError(f"{where}: velocity must be 0-1 (or 1-127)")
    return v


def _num(token, where):
    try:
        return number(token)
    except (ValueError, ZeroDivisionError):
        raise SpecError(f"{where}: '{token}' is not a number (1.5, 3/4 and 1+1/3 work)") from None


# ---------- patterns ----------

def _events(text, where):
    """'beat dur pitch[,pitch…] [@vel]' events separated by newlines, '|' or ';'."""
    notes = []
    for i, event in enumerate(e.strip() for e in re.split(r"[\n|;]", text)):
        if not event:
            continue
        parts = event.split()
        vel = 1.0
        if parts[-1].startswith("@"):
            vel = _velocity(parts.pop()[1:], f"{where} event {i + 1}")
        if len(parts) != 3:
            raise SpecError(f"{where} event {i + 1} '{event}': expected 'beat duration pitch[,pitch] [@velocity]'")
        beat, dur = _num(parts[0], where), _num(parts[1], where)
        if dur <= 0:
            raise SpecError(f"{where} event {i + 1}: duration must be > 0")
        for p in parts[2].split(","):
            notes.append([beat, dur, _pitch(p, where), vel])
    return notes


def _melody(text, where):
    """Sequential tokens 'pitch:dur' (chord 'C4+E4+G4:2', rest 'r:1'); a bare pitch reuses the last duration. A token '@0.7'
    on its own sets the velocity of the notes after it; a suffix 'E5:1@0.7' sets it for that note only. '|' bar lines are
    ignored."""
    notes, beat, dur, level = [], 0.0, 1.0, 1.0
    for token in text.replace("|", " ").split():
        if token.startswith("@"):
            level = _velocity(token[1:], f"{where} '{token}'")
            continue
        vel = level
        if "@" in token:
            token, v = token.split("@", 1)
            vel = _velocity(v, f"{where} '{token}@{v}'")
        if ":" in token:
            token, d = token.split(":", 1)
            dur = _num(d, where)
        if token.lower() not in ("r", "rest", "_"):
            for p in token.split("+"):
                notes.append([beat, dur, _pitch(p, where), vel])
        beat += dur
    return notes, beat


def _grid(lanes, step, where):
    notes, length = [], 0.0
    for lane, line in lanes.items():
        key = _pitch(lane, f"{where} lane")
        cells = [c for c in line if c not in " |"]
        for i, c in enumerate(cells):
            if c in GRID:
                notes.append([i * step, step, key, GRID[c]])
            elif c not in ".-_":
                raise SpecError(f"{where} lane '{lane}': unknown step '{c}' (X x o g hit, . - _ rest)")
        length = max(length, len(cells) * step)
    return notes, length


def _pattern(spec, where, meter):
    if isinstance(spec, str):
        spec = {"notes": spec} if re.match(r"^\s*[\d./+]+\s+[\d./+]+\s+\S", spec) else {"melody": spec}
    notes, natural = [], 0.0
    if "notes" in spec:
        events = _events(spec["notes"], where) if isinstance(spec["notes"], str) else \
            [[float(n[0]), float(n[1]), _pitch(str(n[2]), where), _velocity(n[3], where) if len(n) > 3 else 1.0] for n in spec["notes"]]
        notes += events
        natural = max([natural] + [n[0] + n[1] for n in events])
    if "melody" in spec:
        m, length = _melody(spec["melody"], where)
        notes += m
        natural = max(natural, length)
    if "drums" in spec:
        g, length = _grid(spec["drums"], _num(str(spec.get("step", .25)), where), where)
        notes += g
        natural = max(natural, length)
    unknown = set(spec) - {"notes", "melody", "drums", "step", "beats", "bars"}
    if unknown:
        raise SpecError(f"{where}: unknown keys {sorted(unknown)} (use notes / melody / drums, step, beats or bars)")
    if "beats" in spec:
        beats = float(spec["beats"])
    elif "bars" in spec:
        beats = float(spec["bars"]) * meter
    else:
        beats = max(meter, math.ceil(natural / meter - 1e-9) * meter)
    return {"notes": notes, "beats": beats}


# ---------- arrangement ----------

def _place(play, patterns, track, where, start, length, meter):
    """Notes for one track in one section. play: pattern name, list of names (played in order, then repeated), inline
    pattern dict/string, or {pattern|patterns, transpose, octave, velocity, offset, repeat}."""
    options = {}
    if isinstance(play, dict) and ("pattern" in play or "patterns" in play):
        options = play
        play = play.get("pattern", play.get("patterns"))
    sequence = play if isinstance(play, list) else [play]
    resolved = []
    for i, item in enumerate(sequence):
        if isinstance(item, str) and item in patterns:
            resolved.append(patterns[item])
        elif isinstance(item, (dict, str)):
            if isinstance(item, str) and re.fullmatch(r"[\w\-]+", item):
                raise SpecError(f"{where}: unknown pattern '{item}' (patterns: {', '.join(sorted(patterns)) or 'none'})")
            resolved.append(_pattern(item, f"{where}[{i}]", meter))
        else:
            raise SpecError(f"{where}: expected a pattern name, inline pattern or list")
    shift = int(options.get("transpose", 0)) + 12 * int(options.get("octave", 0))
    gain = float(options.get("velocity", 1.0))
    offset = float(options.get("offset", 0))
    repeat = options.get("repeat", True)
    notes, cursor, cycle = [], offset, 0
    while cursor < length - 1e-9:
        for p in resolved:
            for beat, dur, key, vel in p["notes"]:
                at = cursor + beat
                if at < length - 1e-9:
                    notes.append([start + at, dur, key + (0 if track["drums"] else shift), min(1.0, vel * gain)])
            cursor += p["beats"]
            if cursor >= length - 1e-9:
                break
        cycle += 1
        if not repeat or all(p["beats"] <= 0 for p in resolved):
            break
    return notes


def _bar_beat(item, meter, sections, where):
    """'beat' (0-based), 'bar' (1-based), or 'section' (+ optional 'bar' inside it, 1-based)."""
    if "section" in item:
        match = [s for s in sections if s["name"] == item["section"]]
        if not match:
            raise SpecError(f"{where}: unknown section '{item['section']}'")
        return match[0]["beat"] + (float(item.get("bar", 1)) - 1) * meter + float(item.get("beat", 0))
    if "bar" in item:
        return (float(item["bar"]) - 1) * meter + float(item.get("beat", 0))
    return float(item.get("beat", 0))


# ---------- engine parts ----------

def _zone_of(regions, key):
    inside = [r for r in regions if r["lo"] <= key <= r["hi"]]
    if inside:
        return min(inside, key=lambda r: abs(r["key"] - key))
    return min(regions, key=lambda r: min(abs(r["lo"] - key), abs(r["hi"] - key)))


def _slug(text):
    return re.sub(r"[^A-Za-z0-9]+", "-", text).strip("-") or "x"


def _split(track_name, track, entry, notes, splits, warnings):
    """Groups a track's notes by the sample that will play them. Returns [(region, notes)]."""
    regions = catalog.choose_regions(entry, int(round(track.get("sampleVelocity", 100))))
    if track["drums"]:
        keys = {r["key"]: r for r in regions if r["lo"] == r["hi"]}
        by_region = {}
        for n in notes:
            key = n[2]
            region = keys.get(key) or keys.get(DRUM_FALLBACK.get(key, -1))
            if region is None and not keys:
                region = _zone_of(regions, key)
            if region is None:
                raise SpecError(f"track '{track_name}': drum {note_name(key)} ({key}) is not in {entry['id']} "
                                f"(has {', '.join(str(k) for k in sorted(keys))})")
            if region["key"] != key and keys:
                warnings.append(f"track '{track_name}': drum key {key} played with {region['key']}")
                n = [n[0], n[1], region["key"], n[3]]
            by_region.setdefault(id(region), (region, []))[1].append(n)
        return list(by_region.values())
    usage = {}
    for n in notes:
        region = _zone_of(regions, n[2])
        usage.setdefault(id(region), [region, 0])[1] += 1
    kept = sorted(usage.values(), key=lambda x: -x[1])[:max(1, splits)]
    kept_regions = [r for r, _ in kept]
    if len(usage) > len(kept_regions):
        warnings.append(f"track '{track_name}': {len(usage)} sample zones used, kept {len(kept_regions)} (raise 'splits' or the part budget "
                        "for a more natural timbre across the range)")
    groups = {}
    for n in notes:
        region = _zone_of(regions, n[2])
        if region not in kept_regions:
            region = min(kept_regions, key=lambda r: abs(r["key"] - n[2]))
        groups.setdefault(id(region), (region, []))[1].append(n)
    return sorted(groups.values(), key=lambda g: g[0]["key"])


def _mixer(fields, where):
    if fields is None:
        return None
    if not isinstance(fields, dict):
        raise SpecError(f"{where}: expected an object of mixer fields")
    unknown = set(fields) - MIXER_FIELDS
    if unknown:
        raise SpecError(f"{where}: unknown mixer fields {sorted(unknown)} (valid: {', '.join(sorted(MIXER_FIELDS))})")
    out = dict(fields)
    if isinstance(out.get("DriveMode"), str):
        out["DriveMode"] = _enum(out["DriveMode"], DRIVE_MODES, where + ".DriveMode")
    return out


def _enum(value, names, where):
    if isinstance(value, int):
        return value
    lowered = [n.lower() for n in names]
    if str(value).lower() not in lowered:
        raise SpecError(f"{where}: '{value}' is not one of {', '.join(names)}")
    return lowered.index(str(value).lower())


def compile(spec, spec_dir=Path("."), auto_fetch=True, part_budget=None):
    """Returns (song_json_dict, meta). meta: title, meter, bpm, tempo, sections, tracks [{name, instrument, drums, notes, ...}],
    parts [{name, track, sample, ...}], credits, warnings."""
    warnings = []
    known = {"title", "bpm", "meter", "key", "playbackVersion", "tempo", "master", "instruments", "tracks", "patterns", "sections",
             "regions", "output", "notes", "source", "description"}
    unknown = set(spec) - known
    if unknown:
        raise SpecError(f"unknown top-level keys {sorted(unknown)} (valid: {', '.join(sorted(known))})")
    title = spec.get("title", "Untitled")
    bpm = float(spec.get("bpm", 120))
    meter = int(spec.get("meter", 4))
    version = int(spec.get("playbackVersion", 2))
    if not 10 <= bpm <= 60000:
        raise SpecError("bpm must be 10-60000")

    # instruments
    entries = {}
    for alias, use in (spec.get("instruments") or {}).items():
        if isinstance(use, str) and not re.match(r"^\w+:", use):
            use = "find:" + use
        if isinstance(use, dict) and "use" in use and isinstance(use["use"], str) and ":" in use["use"]:
            ref = dict(use)
            if ref["use"].startswith(("file:", "sfz:", "dir:")):
                target = ref["use"].split(":", 1)[1]
                if not Path(target).expanduser().is_absolute():
                    ref["use"] = ref["use"].split(":", 1)[0] + ":" + str((Path(spec_dir) / target).resolve())
            use = ref
        elif isinstance(use, str) and use.startswith(("file:", "sfz:", "dir:")):
            prefix, target = use.split(":", 1)
            if not Path(target).expanduser().is_absolute():
                use = prefix + ":" + str((Path(spec_dir) / target).resolve())
        try:
            entries[alias] = catalog.resolve(use, auto_fetch=auto_fetch)
        except (catalog.CatalogError, OSError, ValueError) as error:
            raise SpecError(f"instrument '{alias}': {error}") from None

    # tracks
    tracks = {}
    for name, t in (spec.get("tracks") or {}).items():
        if isinstance(t, str):
            t = {"instrument": t}
        unknown = set(t) - TRACK_KEYS
        if unknown:
            raise SpecError(f"track '{name}': unknown keys {sorted(unknown)} (valid: {', '.join(sorted(TRACK_KEYS))})")
        alias = t.get("instrument", name)
        if alias not in entries:
            if isinstance(alias, str) and ":" in alias:
                entries[alias] = catalog.resolve(alias, auto_fetch=auto_fetch)
            else:
                raise SpecError(f"track '{name}': instrument '{alias}' is not declared in 'instruments'")
        entry = entries[alias]
        drums = entry.get("kind") == "drums"
        layer = t.get("layer") or ("Drums" if drums else "Melody")
        if layer not in LAYERS:
            raise SpecError(f"track '{name}': layer must be one of {', '.join(LAYERS)}")
        tracks[name] = dict(t, name=name, entry=entry, drums=drums, layer=layer, notes=[])
    if not tracks:
        raise SpecError("the spec has no tracks")

    # patterns
    patterns = {}
    for name, p in (spec.get("patterns") or {}).items():
        patterns[name] = _pattern(p, f"pattern '{name}'", meter)

    # sections → notes
    sections, beat = [], 0.0
    for i, s in enumerate(spec.get("sections") or []):
        label = s.get("name", f"section{i + 1}")
        if "bars" not in s and "beats" not in s:
            raise SpecError(f"section '{label}': give 'bars' (or 'beats')")
        length = float(s["beats"]) if "beats" in s else float(s["bars"]) * meter
        unknown = set(s) - {"name", "bars", "beats", "play", "repeat"}
        if unknown:
            raise SpecError(f"section '{label}': unknown keys {sorted(unknown)}")
        for _ in range(int(s.get("repeat", 1))):
            sections.append({"name": label, "beat": beat, "beats": length})
            for track, play in (s.get("play") or {}).items():
                if track not in tracks:
                    raise SpecError(f"section '{label}': unknown track '{track}' (tracks: {', '.join(tracks)})")
                tracks[track]["notes"] += _place(play, patterns, tracks[track], f"section '{label}' track '{track}'", beat, length, meter)
            beat += length
    for track, text in (spec.get("notes") or {}).items():          # free notes at absolute beats
        if track not in tracks:
            raise SpecError(f"notes: unknown track '{track}'")
        tracks[track]["notes"] += _pattern(text, f"notes.{track}", meter)["notes"]
    loop = beat if sections else max([n[0] + n[1] for t in tracks.values() for n in t["notes"]] + [meter])
    loop = math.ceil(loop / meter - 1e-9) * meter
    if not sections:
        sections = [{"name": "song", "beat": 0.0, "beats": loop}]

    # per-track transforms
    for t in tracks.values():
        shift = int(t.get("transpose", 0))
        scale = float(t.get("velocity", 1.0))
        kept = []
        for b, d, k, v in t["notes"]:
            if b >= loop - 1e-9:
                warnings.append(f"track '{t['name']}': note at beat {b:g} is past the end ({loop:g}) and was dropped")
                continue
            key = k if t["drums"] else k + shift
            if not 0 <= key <= 127:
                raise SpecError(f"track '{t['name']}': note {key} at beat {b:g} is outside 0-127")
            kept.append([round(b, 6), round(d, 6), key, round(min(1.0, v * scale), 4)])
        t["notes"] = sorted(kept)

    # tempo
    tempo = []
    for i, c in enumerate(spec.get("tempo") or []):
        at = _bar_beat(c, meter, sections, f"tempo[{i}]")
        tempo.append({"Beat": at, "Bpm": float(c["bpm"]), "RampBeats": float(c.get("rampBeats", c.get("ramp", 0)))})

    # noise / glitch regions
    regions = []
    for i, r in enumerate(spec.get("regions") or []):
        where = f"regions[{i}]"
        out = {k: v for k, v in r.items() if k in REGION_FIELDS}
        extra = set(r) - REGION_FIELDS - {"bar", "beat", "section", "beats", "kind"}
        if extra:
            raise SpecError(f"{where}: unknown keys {sorted(extra)}")
        kind = _enum(r.get("Kind", r.get("kind", "White")), NOISE_KINDS, where + ".Kind")
        if kind == 3:
            raise SpecError(f"{where}: Sample noise needs a Unity AudioClip; use White/Pink/Brown or a glitch kind")
        out["Kind"] = kind
        if "StartBeat" not in out:
            out["StartBeat"] = _bar_beat(r, meter, sections, where)
        if "beats" in r:
            out["DurationBeats"] = float(r["beats"])
        if kind >= 4:
            out.setdefault("MasterFilter", True)
        out.setdefault("Name", f"{NOISE_KINDS[kind]}-{i + 1}")
        regions.append(out)
    additive = sum(1 for r in regions if r.get("Enabled", True) and not r.get("MasterFilter", False) and r["Kind"] not in (8, 9))

    # parts, within the engine budget
    budget = (part_budget or MAX_PARTS) - additive
    plans = {}
    for t in tracks.values():
        if t.get("mute") or not t["notes"]:
            continue
        splits = int(t.get("splits", 8))
        plans[t["name"]] = [splits, None]
    while True:
        total = 0
        for name, plan in plans.items():
            t = tracks[name]
            plan[1] = _split(name, t, t["entry"], t["notes"], plan[0], [])
            total += len(plan[1])
        if total <= budget:
            break
        reducible = [(len(p[1]), n) for n, p in plans.items() if not tracks[n]["drums"] and len(p[1]) > 1]
        if not reducible:
            raise SpecError(f"the song needs {total} engine parts but the limit is {budget} (32 minus additive noise regions). "
                            "Use fewer drum sounds or tracks, or merge tracks.")
        _, name = max(reducible)
        plans[name][0] = len(plans[name][1]) - 1
    parts, part_meta = [], []
    for name, (splits, _) in plans.items():
        t = tracks[name]
        groups = _split(name, t, t["entry"], t["notes"], splits, warnings)
        entry = t["entry"]
        for region, notes in groups:
            label = name if len(groups) == 1 else f"{name}-{note_name(region['key']) if not t['drums'] else _drum_label(region['key'])}"
            base_gain = float(t.get("gain", .5))
            if not 0 <= base_gain <= 1:
                raise SpecError(f"track '{name}': gain must be 0-1")
            gain = base_gain * 10 ** (float(region.get("volume", 0)) / 20)
            boost = 0.0
            if gain > 1:                     # quiet sample (SFZ volume > 0): the rest goes to the track's output stage (V2+)
                boost = 20 * math.log10(gain)
                gain = 1.0
                if (t.get("playbackVersion") or version) < 2:
                    warnings.append(f"part '{label}': needs +{boost:.1f} dB but playback V1 has no track output stage; clamped")
                    boost = 0.0
            part = {"name": label, "sample": region["sample"], "layer": t["layer"], "root": int(region["key"]),
                    "gain": round(gain, 4), "pan": float(t.get("pan", 0)),
                    "attack": float(t.get("attack", entry.get("attack", .005))),
                    "release": float(t.get("release", min(float(entry.get("release", .3)), 8.0))),
                    "stem": name, "notes": [x for n in notes for x in n]}
            if t.get("playbackVersion"):
                part["playbackVersion"] = int(t["playbackVersion"])
            if t.get("glide"):
                part["glide"] = float(t["glide"])
            if t.get("drive"):
                part["drive"] = float(t["drive"])
                part["driveMode"] = _enum(t.get("driveMode", "Soft"), DRIVE_MODES, f"track '{name}'.driveMode")
            effects = _mixer(t.get("effects"), f"track '{name}'.effects")
            if boost > 0:
                effects = dict(effects or {})
                effects["OutputDb"] = round(min(30.0, float(effects.get("OutputDb", 0)) + boost), 2)
            if effects:
                part["trackEffects"] = True
                part["effects"] = effects
            parts.append(part)
            part_meta.append({"name": label, "track": name, "sample": region["sample"], "instrument": entry["id"]})
    if not parts:
        raise SpecError("no notes to play: add sections with 'play' (or top-level 'notes')")

    song = {"name": title, "bpm": bpm, "beatsPerBar": meter, "loopBeats": loop, "playbackVersion": version,
            "source": spec.get("source", "notewright plugin"), "tempo": tempo,
            "master": _mixer(spec.get("master"), "master") or {}, "regions": regions,
            "sections": [{"name": s["name"], "beat": s["beat"]} for s in sections], "parts": parts}
    credits = {}
    for t in tracks.values():
        e = t["entry"]
        credits.setdefault(e["id"], {"instrument": e.get("name", e["id"]), "license": e.get("license", ""), "credit": e.get("credit", ""),
                                     "source": e.get("source", "")})
    meta = {"title": title, "bpm": bpm, "meter": meter, "key": spec.get("key"), "tempo": tempo, "sections": sections, "loop": loop,
            "tracks": [{"name": t["name"], "instrument": t["entry"]["id"], "tags": t["entry"].get("tags", []), "drums": t["drums"],
                        "layer": t["layer"], "notes": t["notes"], "mute": bool(t.get("mute")), "program": t.get("program"),
                        "clef": t.get("clef"), "score": t.get("score", True), "pan": float(t.get("pan", 0)),
                        "gain": float(t.get("gain", .5))} for t in tracks.values()],
            "parts": part_meta, "credits": list(credits.values()), "warnings": warnings, "output": spec.get("output") or {}}
    return song, meta


def _drum_label(key):
    for name, k in DRUMS.items():
        if k == key and "-" not in name and len(name) > 2:
            return name
    return str(key)
