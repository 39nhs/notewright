"""Song spec (what Claude writes) → engine song.json (what the com.graze.music runtime plays).

A spec describes instruments, tracks, reusable patterns and an arrangement of sections; see skills/compose-song/reference/spec.md.
compile() resolves instruments (downloading registry instruments on demand), expands the arrangement into notes, splits each
track into engine parts (the engine plays one sample per part: one part per used key zone, one per drum sound) and returns the
song.json dict plus metadata used by the MIDI / MusicXML / Unity exports."""
import json
import math
import re
from pathlib import Path

from . import catalog, paths
from .notes import DRUMS, name as note_name, note_number, number

MAX_PARTS = 32                       # MusicCue.MaxParts: instrument parts + additive noise regions
LAYERS = ("Melody", "Bass", "Harmony", "Drums")
DRIVE_MODES = ("Soft", "Hard", "Fold", "Fuzz")                      # MusicDriveMode order
NOISE_KINDS = ("White", "Pink", "Brown", "Sample", "SlowingGlitch", "AcceleratingGlitch", "PitchGlitch", "StutterGlitch",
               "BitcrushGlitch", "GateGlitch")                       # MusicNoiseKind order
MIXER_V1 = {"OutputDb", "LowPassHz", "HighPassHz", "Compressor", "ThresholdDb", "Ratio", "AttackMs", "ReleaseMs", "EchoWet",
            "EchoSeconds", "EchoFeedback", "CrushBits", "CrushDownsample", "CrushMix", "NoiseGate", "NoiseGateThresholdDb",
            "NoiseGateRangeDb", "NoiseGateHoldMs", "NoiseGateReleaseMs", "Gate", "GateBeats", "GateOpen", "GateDepth", "GatePattern"}
MIXER_V2 = {"Drive", "DriveMode", "DriveDb", "DriveMix", "DriveToneHz", "DriveKeepBassHz", "DriveTrimDb",
            "Amp", "AmpModel", "AmpGainDb", "AmpTightHz", "AmpBassDb", "AmpMidDb", "AmpTrebleDb", "AmpPresenceDb", "AmpLevelDb",
            "Cabinet", "CabinetMix", "Eq", "EqLowDb", "EqLowHz", "EqMidDb", "EqMidHz", "EqMidQ", "EqHighDb", "EqHighHz",
            "Chorus", "ChorusMix", "ChorusRateHz", "ChorusDepthMs", "ChorusDelayMs",
            "Reverb", "ReverbMix", "ReverbSize", "ReverbDamping", "ReverbWidth", "ReverbPreDelayMs"}
MIXER_V3 = {"Limiter", "LimiterInputDb", "LimiterCeilingDb", "LimiterReleaseMs", "SidechainTrack", "EchoBeats", "Stutter", "StutterBeats",
            "CabinetImpulseMix", "CabinetImpulseGainDb", "CabinetImpulseNormalize", "Color"}
MIXER_FIELDS = MIXER_V1 | MIXER_V2 | MIXER_V3          # MusicMixerSettings (C# names; a test checks the list against the engine)
AUTOMATION_TARGETS = ("volume", "pan", "lowpass", "highpass", "echo", "chorus", "reverb", "pitch", "modulation", "brightness", "gate",
                      "crush", "drive", "driveMix", "stutter", "width", "speed", "rotary")   # MusicAutomation.JsonNames
TRACK_ONLY_TARGETS = {"pan", "pitch", "modulation", "brightness"}
CURVES = ("linear", "step", "smooth")                               # MusicAutomationCurve order
SYNTH_PRESETS = ("Init", "Saw lead", "Square lead", "Pluck", "Warm pad", "Sub bass", "Acid bass", "Brass", "Bell keys", "Noise sweep",
                 "Jazz organ", "Jazz organ perc", "Rock organ", "Gospel organ", "FM e-piano", "FM bell", "FM bass", "Wavetable pad",
                 "Wavetable vox")                                   # MusicSynthPresets.Names
REGION_FIELDS = {"Name", "Enabled", "Kind", "StartBeat", "DurationBeats", "Gain", "FadeInBeats", "FadeOutBeats", "Seed", "LoopSource",
                 "RepeatWithCue", "MasterFilter", "Wet", "GlitchRate", "PitchDepth"}
TRACK_KEYS = {"instrument", "layer", "gain", "pan", "attack", "release", "glide", "drive", "driveMode", "effects", "transpose",
              "velocity", "splits", "mute", "playbackVersion", "program", "clef", "score",
              # playback V3
              "automation", "modulators", "output", "sends", "sampleShape", "articulations", "keySwitches", "vibratoRate", "ignoreSwing",
              "stretchBpm", "stretchGrainMs", "loop", "impulse", "expression", "audio", "zones",
              "plugin"}                                             # VST3 instrument settings (Windows x64 only)
V3_TRACK_KEYS = TRACK_KEYS - {"instrument", "layer", "gain", "pan", "attack", "release", "glide", "drive", "driveMode", "effects", "transpose",
                              "velocity", "splits", "mute", "playbackVersion", "program", "clef", "score", "zones"}
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


def _mixer(fields, where, meter=4, sections=(), vst3=None):
    """Mixer settings (C# field names) plus 'inserts': VST3 effects after the built-in chain (Windows x64 only)."""
    if fields is None:
        return None
    if not isinstance(fields, dict):
        raise SpecError(f"{where}: expected an object of mixer fields")
    unknown = set(fields) - MIXER_FIELDS - {"inserts"}
    if unknown:
        raise SpecError(f"{where}: unknown mixer fields {sorted(unknown)} (valid: {', '.join(sorted(MIXER_FIELDS))}, inserts)")
    out = dict(fields)
    if "inserts" in out:
        out["Inserts"] = _inserts(out.pop("inserts"), where + ".inserts", meter, sections)
        if vst3 is not None and out["Inserts"]:
            vst3.append(f"{where} inserts")
    if isinstance(out.get("DriveMode"), str):
        out["DriveMode"] = _enum(out["DriveMode"], DRIVE_MODES, where + ".DriveMode")
    return out


# ---------- VST3 plugins (Windows x64 only: the engine hosts them in a Windows worker process) ----------

PLUGIN_TARGET_KEYS = {"state", "parameters", "automation", "gain", "pan", "transpose", "velocityCurve", "voices"}


def _identity(value, where):
    """'vst3': a plugin name as installed (`notewright plugins` lists them) or its 32-hex-digit class ID."""
    if not isinstance(value, str) or not value.strip():
        raise SpecError(f"{where}: 'vst3' is the plugin's name or class ID (`notewright plugins` lists the installed ones)")
    value = value.strip()
    return {"classId": value.upper()} if re.fullmatch(r"[0-9A-Fa-f]{32}", value) else {"name": value}


def _normalized(values, where):
    if not isinstance(values, dict):
        raise SpecError(f"{where}: expected an object of parameter key → value 0-1")
    for key, value in values.items():
        if not isinstance(value, (int, float)) or not 0 <= value <= 1:
            raise SpecError(f"{where}.{key}: plugin parameters are normalized values 0-1 (`notewright plugins --info` lists keys and values)")
    return {k: float(v) for k, v in values.items()}


def _plugin_lanes(spec, where, meter, sections):
    if not isinstance(spec, dict):
        raise SpecError(f"{where}: expected an object of parameter key → points")
    out = {}
    for key, points in spec.items():
        flat = _points(points, f"{where}.{key}", meter, sections)
        if any(not 0 <= v <= 1 for v in flat[1::3]):
            raise SpecError(f"{where}.{key}: plugin parameter values are 0-1")
        out[key] = flat
    return out


def _vst3_instrument(use, where):
    """An instrument that is a VST3 plugin: {"vst3": name or class ID, vendor, version, state, parameters, voices, transpose,
    velocityCurve, gain, pan, fallback (built-in synth preset if the plugin cannot run), drums}."""
    use = _plain(use, where, {"vst3", "vendor", "version", "fallback", "drums", "name"} | PLUGIN_TARGET_KEYS - {"automation"})
    plugin = _identity(use["vst3"], where + ".vst3")
    for key in ("vendor", "version", "state"):
        if use.get(key):
            plugin[key] = str(use[key])
    if "parameters" in use:
        plugin["parameters"] = _normalized(use["parameters"], where + ".parameters")
    for key in ("gain", "pan", "transpose", "velocityCurve"):
        if key in use:
            plugin[key] = use[key]
    if "voices" in use:
        if use["voices"] not in ("mono", "poly"):
            raise SpecError(f"{where}.voices: mono or poly")
        plugin["voices"] = use["voices"]
    fallback = use.get("fallback", "Init")
    match = [n for n in SYNTH_PRESETS if n.lower() == str(fallback).lower()]
    if not match:
        raise SpecError(f"{where}.fallback: unknown synthPreset '{fallback}' (presets: {', '.join(SYNTH_PRESETS)})")
    label = use.get("name") or plugin.get("name") or plugin["classId"]
    return {"id": f"vst3:{label}", "name": label, "kind": "drums" if use.get("drums") else "melodic", "regions": [], "tags": ["vst3"],
            "license": "the user's installed VST3 plugin (its own license)", "source": "VST3 plugin (Windows x64)",
            "vst3": plugin, "fallback": match[0]}


def _inserts(items, where, meter, sections):
    """'inserts': [{"vst3", vendor, version, mix 0-1, bypass, state, parameters, automation}] → MusicPluginInsertDefinition[]."""
    if not isinstance(items, list):
        items = [items]
    if len(items) > 4:
        raise SpecError(f"{where}: at most 4 VST3 inserts per chain")
    out = []
    for i, item in enumerate(items):
        at = f"{where}[{i}]"
        item = _plain(item, at, {"vst3", "vendor", "version", "mix", "bypass", "state", "parameters", "automation"})
        identity = _identity(item.get("vst3"), at + ".vst3")
        mix = float(item.get("mix", 1))
        if not 0 <= mix <= 1:
            raise SpecError(f"{at}.mix: 0 (dry) to 1 (wet)")
        values = _normalized(item.get("parameters") or {}, at + ".parameters")
        lanes = _plugin_lanes(item["automation"], at + ".automation", meter, sections) if item.get("automation") else {}
        parameters = []
        for key in list(values) + [k for k in lanes if k not in values]:
            flat = lanes.get(key, [])
            entry = {"Key": key, "Value": values.get(key, flat[1] if flat else 0.0)}
            if flat:
                entry["Points"] = [{"Beat": flat[k], "Value": flat[k + 1], "Curve": flat[k + 2]} for k in range(0, len(flat), 3)]
            parameters.append(entry)
        out.append({"ClassId": identity.get("classId", ""), "Name": identity.get("name", ""), "Vendor": str(item.get("vendor", "")),
                    "Version": str(item.get("version", "")), "Mix": mix, "Bypass": bool(item.get("bypass")), "State": str(item.get("state", "")),
                    "Parameters": parameters})
    return out


def _track_v3(part, t, name, meter, sections, bus_names, expression, notes, clips):
    """Copies a track's playback V3 settings onto one of its engine parts."""
    where = f"track '{name}'"
    if t.get("automation"):
        part["automation"] = _lanes(t["automation"], where + ".automation", meter, sections)
    if t.get("modulators"):
        mods = t["modulators"] if isinstance(t["modulators"], list) else [t["modulators"]]
        for i, m in enumerate(mods):
            m = _plain(m, f"{where}.modulators[{i}]", {"kind", "target", "amount", "wave", "rateHz", "syncBeats", "phase", "source", "attackMs",
                                                       "releaseMs", "disabled"})
            if m.get("target") not in AUTOMATION_TARGETS:
                raise SpecError(f"{where}.modulators[{i}]: target must be one of {', '.join(AUTOMATION_TARGETS)}")
        part["modulators"] = mods
    if t.get("output"):
        if t["output"] not in bus_names:
            raise SpecError(f"{where}: output '{t['output']}' is not a bus ({', '.join(sorted(bus_names)) or 'no buses'})")
        part["output"] = t["output"]
    if t.get("sends"):
        sends = []
        for i, send in enumerate(t["sends"] if isinstance(t["sends"], list) else [t["sends"]]):
            send = _plain(send, f"{where}.sends[{i}]", {"bus", "db", "preFader", "automation"})
            if send.get("bus") not in bus_names:
                raise SpecError(f"{where}.sends[{i}]: bus '{send.get('bus')}' is not defined ({', '.join(sorted(bus_names)) or 'no buses'})")
            out = {"bus": send["bus"], "db": float(send.get("db", 0))}
            if send.get("preFader"):
                out["preFader"] = True
            if send.get("automation"):
                out["automation"] = {"target": "volume", "points": _points(send["automation"], f"{where}.sends[{i}].automation", meter, sections)}
            sends.append(out)
        part["sends"] = sends
    for key in ("sampleShape", "vibratoRate", "ignoreSwing", "stretchBpm", "stretchGrainMs"):
        if key in t:
            part[key] = _plain(t[key], f"{where}.{key}") if key == "sampleShape" else t[key]
    if t.get("articulations"):
        part["articulations"] = [_plain(a, f"{where}.articulations[{i}]", {"Id", "Name", "StartSeconds", "GainDb", "LengthScale"})
                                 for i, a in enumerate(t["articulations"])]
    if t.get("keySwitches"):
        part["keySwitches"] = [{"Key": _pitch(str(k.get("Key", k.get("key"))), f"{where}.keySwitches[{i}]"),
                                "Articulation": int(k.get("Articulation", k.get("articulation", 0)))} for i, k in enumerate(t["keySwitches"])]
    if t.get("loop"):
        loop = _plain(t["loop"], where + ".loop", {"start", "end", "crossfade"})
        if "zones" in part:
            for zone in part["zones"]:
                zone["loopStart"], zone["loopEnd"] = float(loop.get("start", 0)), float(loop["end"])
        else:
            part["loopStart"], part["loopEnd"] = float(loop.get("start", 0)), float(loop["end"])
        if "crossfade" in loop:
            part["loopCrossfade"] = float(loop["crossfade"])
    mine = [e for e in expression if any(abs(n[0] - e["beat"]) < 1e-6 and n[2] == e["key"] for n in notes)]
    if mine:
        part["expression"] = mine
    if clips:
        part["regions"] = clips


def _engine_synth(use, where):
    """An instrument that is the engine's built-in synth (playback V3+): {"synthPreset": name} or {"synth": {C# fields}}."""
    use = _plain(use, where, {"synth", "synthPreset", "wavetable", "name"})
    if ("synth" in use) == ("synthPreset" in use):
        raise SpecError(f"{where}: give either 'synth' (settings) or 'synthPreset' (one of {', '.join(SYNTH_PRESETS)})")
    out = {"kind": "melodic", "regions": [], "license": "generated by the engine", "source": "built-in synth", "tags": ["synth"]}
    if "synthPreset" in use:
        match = [n for n in SYNTH_PRESETS if n.lower() == str(use["synthPreset"]).lower()]
        if not match:
            raise SpecError(f"{where}: unknown synthPreset '{use['synthPreset']}' (presets: {', '.join(SYNTH_PRESETS)})")
        out.update(id=f"engine-synth:{match[0]}", name=use.get("name", match[0]), engineSynth={"synthPreset": match[0]})
    else:
        synth = dict(_plain(use["synth"], where + ".synth"), Enabled=True)
        out.update(id="engine-synth:custom", name=use.get("name", "built-in synth"), engineSynth={"synth": synth})
    if "wavetable" in use:
        out["wavetable"] = use["wavetable"]
    return out


def _enum(value, names, where):
    if isinstance(value, int):
        return value
    lowered = [n.lower() for n in names]
    if str(value).lower() not in lowered:
        raise SpecError(f"{where}: '{value}' is not one of {', '.join(names)}")
    return lowered.index(str(value).lower())


# ---------- playback V3 data ----------

def _points(points, where, meter, sections):
    """Automation points → flattened beat, value, curve triples. A point is {bar|beat|section(+bar, beat), value, curve} or
    [beat, value(, curve)]; curve is linear (default), step or smooth (the shape from this point to the next)."""
    if not isinstance(points, list) or not points:
        raise SpecError(f"{where}: expected a list of points")
    out = []
    for i, p in enumerate(points):
        at = f"{where}[{i}]"
        if isinstance(p, dict):
            if "value" not in p:
                raise SpecError(f"{at}: a point needs 'value'")
            beat, value, curve = _bar_beat(p, meter, sections, at), p["value"], p.get("curve", "linear")
        elif isinstance(p, list) and len(p) in (2, 3):
            beat, value, curve = p[0], p[1], p[2] if len(p) == 3 else "linear"
        else:
            raise SpecError(f"{at}: a point is {{bar|beat|section, value, curve}} or [beat, value, curve]")
        out += [round(float(beat), 6), float(value), _enum(curve, CURVES, at + ".curve")]
    return out


def _lanes(spec, where, meter, sections, master=False):
    """{"volume": [points], "lowpass": {"points": [...], "disabled": true}, ...} → song.json lanes."""
    if spec is None:
        return []
    if not isinstance(spec, dict):
        raise SpecError(f"{where}: expected an object of target → points")
    lanes = []
    for target, value in spec.items():
        if target not in AUTOMATION_TARGETS:
            raise SpecError(f"{where}: unknown target '{target}' (targets: {', '.join(AUTOMATION_TARGETS)})")
        if master and target in TRACK_ONLY_TARGETS:
            raise SpecError(f"{where}: '{target}' is a track target (the master and buses have no {target} lane)")
        if not master and target == "speed":
            raise SpecError(f"{where}: 'speed' (tape speed) is a master lane only")
        disabled = isinstance(value, dict) and bool(value.get("disabled"))
        points = value.get("points") if isinstance(value, dict) else value
        lane = {"target": target, "points": _points(points, f"{where}.{target}", meter, sections)}
        if disabled:
            lane["disabled"] = True
        lanes.append(lane)
    return lanes


def _file(value, spec_dir, where):
    path = Path(str(value)).expanduser()
    if not path.is_absolute():
        path = (Path(spec_dir) / path).resolve()
    if not path.exists():
        raise SpecError(f"{where}: file not found: {path}")
    return str(path)


def _plain(value, where, allowed=None):
    """An object passed to the engine with C# field names (synth, sampleShape, ...)."""
    if not isinstance(value, dict):
        raise SpecError(f"{where}: expected an object")
    if allowed is not None:
        unknown = set(value) - allowed
        if unknown:
            raise SpecError(f"{where}: unknown keys {sorted(unknown)} (valid: {', '.join(sorted(allowed))})")
    return dict(value)


def _clips(clips, track, spec_dir, meter, sections):
    """Track 'audio': recordings placed on the timeline → song.json part regions."""
    out = []
    keys = {"file", "bar", "beat", "section", "beats", "sourceStart", "gainDb", "fadeIn", "fadeOut", "semitones", "warp", "sourceBpm",
            "markers", "mute"}
    for i, c in enumerate(clips if isinstance(clips, list) else [clips]):
        where = f"track '{track}'.audio[{i}]"
        c = _plain(c, where, keys)
        if "file" not in c or "beats" not in c:
            raise SpecError(f"{where}: give 'file' and 'beats' (length on the timeline)")
        region = {"sample": _file(c["file"], spec_dir, where + ".file"), "start": _bar_beat(c, meter, sections, where), "length": float(c["beats"])}
        for key in ("sourceStart", "gainDb", "fadeIn", "fadeOut", "semitones", "sourceBpm", "mute"):
            if key in c:
                region[key] = c[key]
        if "warp" in c:
            region["warp"] = ("Off", "Tempo", "Markers")[_enum(c["warp"], ("Off", "Tempo", "Markers"), where + ".warp")]
        if "markers" in c:
            region["markers"] = [float(x) for pair in c["markers"] for x in pair]
        out.append(region)
    return out


def _expression(entries, track, meter, sections, notes):
    """Track 'expression': per-note bend, vibrato, articulation and variation, matched to a note by position and sounding pitch."""
    out = []
    fields = {"bendFrom", "bendTo", "bendStart", "bendEnd", "vibrato", "articulation", "skip", "every", "offset"}
    for i, e in enumerate(entries if isinstance(entries, list) else [entries]):
        where = f"track '{track}'.expression[{i}]"
        e = _plain(e, where, fields | {"bar", "beat", "section", "note"})
        if "note" not in e:
            raise SpecError(f"{where}: give 'note' (the pitch as it sounds, e.g. E5) and its position")
        beat, key = round(_bar_beat(e, meter, sections, where), 6), _pitch(str(e["note"]), where)
        if not any(abs(n[0] - beat) < 1e-6 and n[2] == key for n in notes):
            near = sorted(notes, key=lambda n: abs(n[0] - beat))[:3]
            raise SpecError(f"{where}: no {note_name(key)} starts at beat {beat:g} (notes near: "
                            f"{', '.join(f'{note_name(n[2])}@{n[0]:g}' for n in near) or 'none'})")
        item = {"beat": beat, "key": key}
        item.update({k: v for k, v in e.items() if k in fields})
        out.append(item)
    return out


def compile(spec, spec_dir=Path("."), auto_fetch=True, part_budget=None):
    """Returns (song_json_dict, meta). meta: title, meter, bpm, tempo, sections, tracks [{name, instrument, drums, notes, ...}],
    parts [{name, track, sample, ...}], credits, warnings."""
    warnings = []
    known = {"title", "bpm", "meter", "key", "playbackVersion", "tempo", "master", "instruments", "tracks", "patterns", "sections",
             "regions", "output", "notes", "source", "description",
             "swing", "swingGrid", "keys", "chords", "meterChanges", "targetLufs", "variationSeed", "buses", "masterAutomation",
             "masterImpulse"}
    unknown = set(spec) - known
    if unknown:
        raise SpecError(f"unknown top-level keys {sorted(unknown)} (valid: {', '.join(sorted(known))})")
    title = spec.get("title", "Untitled")
    bpm = float(spec.get("bpm", 120))
    meter = int(spec.get("meter", 4))
    version = int(spec.get("playbackVersion", 3))
    if not 10 <= bpm <= 60000:
        raise SpecError("bpm must be 10-60000")

    # instruments
    entries = {}
    for alias, use in (spec.get("instruments") or {}).items():
        if isinstance(use, dict) and "vst3" in use:
            entries[alias] = _vst3_instrument(use, f"instrument '{alias}'")
            continue
        if isinstance(use, dict) and ("synth" in use or "synthPreset" in use) and "use" not in use:
            entries[alias] = _engine_synth(use, f"instrument '{alias}'")
            continue
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
        if "instrument" not in t and "audio" in t and alias not in entries:
            entries[alias] = {"id": "audio", "name": "audio clips", "kind": "melodic", "audioOnly": True, "regions": [], "license": "",
                              "source": "the user's recordings"}
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

    # song-level playback V3 data (key, chords and meter changes are metadata: they never change the sound)
    def needs_v3(what):
        if version < 3:
            raise SpecError(f"{what} needs playbackVersion 3 (this song is V{version})")
    extra = {}
    for key in ("swing", "swingGrid", "targetLufs", "variationSeed"):
        if key in spec:
            extra[key] = float(spec[key]) if key != "variationSeed" else int(spec[key])
    if spec.get("key"):
        extra["key"] = str(spec["key"])
    if spec.get("keys"):
        extra["keys"] = [{"beat": _bar_beat(k, meter, sections, f"keys[{i}]"), "key": str(k["key"])} for i, k in enumerate(spec["keys"])]
    if spec.get("chords"):
        extra["chords"] = [{"beat": _bar_beat(c, meter, sections, f"chords[{i}]"), "chord": str(c["chord"])} for i, c in enumerate(spec["chords"])]
    if spec.get("meterChanges"):
        extra["meter"] = [dict({"beat": _bar_beat(m, meter, sections, f"meterChanges[{i}]"), "numerator": int(m["numerator"]),
                                "denominator": int(m["denominator"])}, **({"grouping": m["grouping"]} if m.get("grouping") else {}))
                          for i, m in enumerate(spec["meterChanges"])]
    vst3_uses = []
    master = _mixer(spec.get("master"), "master", meter, sections, vst3_uses) or {}
    if "targetLufs" in spec:
        needs_v3("targetLufs")
        if not master.get("Limiter"):
            master["Limiter"] = True
            warnings.append("targetLufs: turned on the master Limiter (the renderer reaches the target with its input gain)")
    if spec.get("masterAutomation"):
        needs_v3("masterAutomation")
        extra["masterAutomation"] = _lanes(spec["masterAutomation"], "masterAutomation", meter, sections, master=True)
    if spec.get("masterImpulse"):
        needs_v3("masterImpulse")
        extra["masterImpulse"] = _file(spec["masterImpulse"], spec_dir, "masterImpulse")
        master.setdefault("CabinetImpulseMix", 1)
    buses = []
    for name, b in (spec.get("buses") or {}).items():
        needs_v3(f"bus '{name}'")
        b = _plain(b, f"bus '{name}'", {"effects", "output", "pan", "automation", "impulse"})
        bus = {"name": name, "effects": _mixer(b.get("effects"), f"bus '{name}'.effects", meter, sections, vst3_uses) or {}}
        if b.get("output"):
            bus["output"] = b["output"]
        if "pan" in b:
            bus["pan"] = float(b["pan"])
        if b.get("automation"):
            bus["automation"] = _lanes(b["automation"], f"bus '{name}'.automation", meter, sections, master=True)
        if b.get("impulse"):
            bus["impulse"] = _file(b["impulse"], spec_dir, f"bus '{name}'.impulse")
            bus["effects"].setdefault("CabinetImpulseMix", 1)
        buses.append(bus)
    bus_names = {b["name"] for b in buses}
    for b in buses:
        if b.get("output") and b["output"] not in bus_names:
            raise SpecError(f"bus '{b['name']}': output '{b['output']}' is not a bus ({', '.join(sorted(bus_names))})")
    for where, fields in [("master", master)] + [(f"bus '{b['name']}'", b["effects"]) for b in buses] + \
            [(f"track '{t['name']}'.effects", t.get("effects") or {}) for t in tracks.values()]:
        for group, needed in ((MIXER_V2, 2), (MIXER_V3, 3)):
            used = sorted(set(fields) & group)
            if used and version < needed:
                warnings.append(f"{where}: {', '.join(used)} need playbackVersion {needed}; V{version} ignores them")

    # parts, within the engine budget
    budget = (part_budget or MAX_PARTS) - additive

    def track_version(t):
        return int(t.get("playbackVersion") or version)

    def kind(t):
        if t["entry"].get("engineSynth"):
            return "synth"
        if t["entry"].get("vst3"):
            return "plugin"
        if t["entry"].get("audioOnly"):
            return "audio"
        if track_version(t) >= 3 and t.get("zones", True) and version >= 3:
            return "zones"                   # V3 multi-zone sampler: the whole track is one engine part
        return "split"

    for t in tracks.values():
        used = sorted(set(t) & V3_TRACK_KEYS)
        if used and (version < 3 or track_version(t) < 3):
            raise SpecError(f"track '{t['name']}': {', '.join(used)} need playbackVersion 3")
        if kind(t) in ("synth", "audio", "plugin") and (version < 3 or track_version(t) < 3):
            raise SpecError(f"track '{t['name']}': the built-in synth, VST3 plugins and audio-only tracks need playbackVersion 3")
        if t.get("plugin") and kind(t) != "plugin":
            raise SpecError(f"track '{t['name']}': 'plugin' settings need a VST3 instrument ({{\"vst3\": name}} in 'instruments')")

    def groups_of(name, splits, warn):
        t = tracks[name]
        if kind(t) in ("synth", "audio", "plugin"):
            return [(None, t["notes"])]
        groups = _split(name, t, t["entry"], t["notes"], 10 ** 6 if kind(t) == "zones" else splits, warn)
        return [(groups, [n for _, notes in groups for n in notes])] if kind(t) == "zones" else groups

    plans = {}
    for t in tracks.values():
        if t.get("mute") or (not t["notes"] and not t.get("audio")):
            continue
        plans[t["name"]] = [int(t.get("splits", 8)), None]
    while True:
        total = 0
        for name, plan in plans.items():
            plan[1] = groups_of(name, plan[0], [])
            total += len(plan[1])
        if total <= budget:
            break
        reducible = [(len(p[1]), n) for n, p in plans.items() if kind(tracks[n]) == "split" and not tracks[n]["drums"] and len(p[1]) > 1]
        if not reducible:
            raise SpecError(f"the song needs {total} engine parts but the limit is {budget} (32 minus additive noise regions). "
                            "Use fewer drum sounds or tracks, or merge tracks.")
        _, name = max(reducible)
        plans[name][0] = len(plans[name][1]) - 1
    parts, part_meta = [], []
    for name, (splits, _) in plans.items():
        t = tracks[name]
        groups = groups_of(name, splits, warnings)
        entry = t["entry"]
        base_gain = float(t.get("gain", .5))
        if not 0 <= base_gain <= 1:
            raise SpecError(f"track '{name}': gain must be 0-1")
        expression = _expression(t["expression"], name, meter, sections, t["notes"]) if t.get("expression") else []
        clips = _clips(t["audio"], name, spec_dir, meter, sections) if t.get("audio") else []
        for index, (region, notes) in enumerate(groups):
            k = kind(t)
            boost = 0.0
            if k == "split":
                label = name if len(groups) == 1 else f"{name}-{note_name(region['key']) if not t['drums'] else _drum_label(region['key'])}"
                gain = base_gain * 10 ** (float(region.get("volume", 0)) / 20)
                if gain > 1:                 # quiet sample (SFZ volume > 0): the rest goes to the track's output stage (V2+)
                    boost = 20 * math.log10(gain)
                    gain = 1.0
                    if track_version(t) < 2:
                        warnings.append(f"part '{label}': needs +{boost:.1f} dB but playback V1 has no track output stage; clamped")
                        boost = 0.0
                part = {"name": label, "sample": region["sample"], "root": int(region["key"])}
                sample = region["sample"]
            elif k == "zones":
                label, gain = name, base_gain
                zones = [{"sample": r["sample"], "root": int(r["key"]), "low": int(r["lo"]), "high": int(r["hi"]),
                          "gainDb": round(max(-24.0, min(24.0, float(r.get("volume", 0)))), 3)} for r, _ in region]
                part = {"name": label, "root": zones[0]["root"], "zones": zones}
                sample = zones[0]["sample"]
                notes = sorted(notes)
            elif k == "synth":
                label, gain = name, base_gain
                part = {"name": label, "root": 60}
                part.update(entry["engineSynth"])
                if entry.get("wavetable"):
                    part["wavetable"] = _file(entry["wavetable"], spec_dir, f"instrument of track '{name}'.wavetable")
                sample = ""
            elif k == "plugin":              # one part; the plugin plays its notes (the fallback synth only if it cannot run)
                label, gain = name, base_gain
                plugin = dict(entry["vst3"])
                override = _plain(t.get("plugin") or {}, f"track '{name}'.plugin", PLUGIN_TARGET_KEYS)
                if "parameters" in override:
                    plugin["parameters"] = dict(plugin.get("parameters", {}), **_normalized(override.pop("parameters"), f"track '{name}'.plugin.parameters"))
                if "automation" in override:
                    plugin["automation"] = _plugin_lanes(override.pop("automation"), f"track '{name}'.plugin.automation", meter, sections)
                plugin.update(override)
                part = {"name": label, "root": 60, "synthPreset": entry["fallback"], "plugin": plugin}
                sample = ""
                vst3_uses.append(f"track '{name}'")
            else:                            # audio only: the recordings play through the part's first clip
                if not clips:
                    raise SpecError(f"track '{name}': an audio-only track needs 'audio' clips")
                label, gain = name, base_gain
                part = {"name": label, "root": 60, "sample": clips[0]["sample"]}
                sample = clips[0]["sample"]
            part.update({"layer": t["layer"], "gain": round(gain, 4), "pan": float(t.get("pan", 0)),
                         "attack": float(t.get("attack", entry.get("attack", .005 if k != "synth" else .008))),
                         "release": float(t.get("release", min(float(entry.get("release", .3 if k != "synth" else .12)), 8.0))),
                         "stem": name, "notes": [x for n in notes for x in n]})
            if t.get("playbackVersion"):
                part["playbackVersion"] = int(t["playbackVersion"])
            if t.get("glide"):
                part["glide"] = float(t["glide"])
            if t.get("drive"):
                part["drive"] = float(t["drive"])
                part["driveMode"] = _enum(t.get("driveMode", "Soft"), DRIVE_MODES, f"track '{name}'.driveMode")
            effects = _mixer(t.get("effects"), f"track '{name}'.effects", meter, sections, vst3_uses if index == 0 else None)
            if boost > 0:
                effects = dict(effects or {})
                effects["OutputDb"] = round(min(30.0, float(effects.get("OutputDb", 0)) + boost), 2)
            if t.get("impulse"):
                effects = dict(effects or {})
                effects.setdefault("CabinetImpulseMix", 1)
                part["impulse"] = _file(t["impulse"], spec_dir, f"track '{name}'.impulse")
            if effects:
                part["trackEffects"] = True
                part["effects"] = effects
            _track_v3(part, t, name, meter, sections, bus_names, expression, notes, clips if index == 0 and k != "audio" else
                      clips if k == "audio" else [])
            parts.append(part)
            part_meta.append({"name": label, "track": name, "sample": sample, "instrument": entry["id"]})
    if not parts:
        raise SpecError("no notes to play: add sections with 'play' (or top-level 'notes')")
    if vst3_uses:
        if version < 3:
            raise SpecError(f"VST3 plugins ({', '.join(vst3_uses)}) need playbackVersion 3")
        if not paths.vst3_supported():
            warnings.append(f"VST3 plugins ({', '.join(vst3_uses)}): {paths.VST3_UNAVAILABLE} Rendering this spec fails here; "
                            "--score-only still writes MIDI and MusicXML.")

    song = {"name": title, "bpm": bpm, "beatsPerBar": meter, "loopBeats": loop, "playbackVersion": version,
            "source": spec.get("source", "notewright plugin"), "tempo": tempo,
            "master": master, "regions": regions,
            "sections": [{"name": s["name"], "beat": s["beat"]} for s in sections], "parts": parts}
    song.update(extra)
    if buses:
        song["buses"] = buses
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
            "parts": part_meta, "credits": list(credits.values()), "warnings": warnings, "output": spec.get("output") or {},
            "vst3": vst3_uses}
    return song, meta


def _drum_label(key):
    for name, k in DRUMS.items():
        if k == key and "-" not in name and len(name) > 2:
            return name
    return str(key)
