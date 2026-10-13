"""Reads a Standard MIDI File: tempo, meter, key, tracks and the chord of every bar — a reference song Claude can read — and turns it
into a spec to edit (notes become free notes at absolute beats)."""
import re
import struct
from pathlib import Path

from . import theory
from .notes import name as note_name

FAMILIES = ("piano", "chromatic percussion", "organ", "guitar", "bass", "strings", "ensemble", "brass", "reed", "pipe", "synth lead",
            "synth pad", "synth effects", "ethnic", "percussive", "sound effects")
FIND = {"piano": "piano", "chromatic percussion": "bell", "organ": "organ", "guitar": "guitar", "bass": "bass", "strings": "violin",
        "ensemble": "strings", "brass": "trumpet", "reed": "clarinet", "pipe": "flute", "synth lead": "lead", "synth pad": "pad",
        "synth effects": "pad", "ethnic": "pluck", "percussive": "bell", "sound effects": "pad"}


class MidiError(ValueError):
    pass


def _varlen(data, at):
    value = 0
    for _ in range(4):
        byte = data[at]
        at += 1
        value = (value << 7) | (byte & 0x7F)
        if not byte & 0x80:
            return value, at
    raise MidiError("bad variable-length number")


def read(path):
    """{division, tempo [(beat, bpm)], meter [(beat, n, d)], keySignature, tracks [{name, channel, program, drums, notes}]};
    notes are [beat, duration, key, velocity 0-1]."""
    data = Path(path).read_bytes()
    if data[:4] != b"MThd":
        raise MidiError(f"{path}: not a MIDI file")
    _, fmt, count, division = struct.unpack(">IHHH", data[4:14])
    if division & 0x8000:
        raise MidiError("SMPTE-timed MIDI files are not supported (export with beats/ticks per quarter note)")
    at, tracks, tempo, meter, key_signature = 8 + struct.unpack(">I", data[4:8])[0], [], [], [], None
    for _ in range(count):
        if data[at:at + 4] != b"MTrk":
            raise MidiError("damaged track chunk")
        length = struct.unpack(">I", data[at + 4:at + 8])[0]
        pos, end, tick, status = at + 8, at + 8 + length, 0, 0
        name, programs, open_notes, notes, channels = "", {}, {}, [], set()
        while pos < end:
            delta, pos = _varlen(data, pos)
            tick += delta
            byte = data[pos]
            if byte & 0x80:
                status = byte
                pos += 1
            kind = status & 0xF0
            if status == 0xFF:
                meta, length2 = data[pos], None
                length2, pos = _varlen(data, pos + 1)
                body = data[pos:pos + length2]
                pos += length2
                if meta == 0x51 and length2 == 3:
                    tempo.append((tick / division, round(60000000 / int.from_bytes(body, "big"), 3)))
                elif meta == 0x58 and length2 >= 2:
                    meter.append((tick / division, body[0], 2 ** body[1]))
                elif meta == 0x59 and length2 == 2 and key_signature is None:
                    sharps = struct.unpack("b", body[:1])[0]
                    tonic = (sharps * 7) % 12 if body[1] == 0 else (sharps * 7 + 9) % 12
                    key_signature = f"{theory.PITCH_NAMES[tonic]} {'minor' if body[1] else 'major'}"
                elif meta == 0x03 and not name:
                    name = body.decode("latin-1").strip()
            elif status in (0xF0, 0xF7):
                length2, pos = _varlen(data, pos)
                pos += length2
            elif kind in (0x80, 0x90):
                key, velocity = data[pos], data[pos + 1]
                pos += 2
                channel = status & 0x0F
                channels.add(channel)
                if kind == 0x90 and velocity > 0:
                    open_notes.setdefault((channel, key), []).append((tick, velocity))
                elif open_notes.get((channel, key)):
                    start, vel = open_notes[(channel, key)].pop(0)
                    notes.append((channel, [start / division, max(1, tick - start) / division, key, round(vel / 127, 3)]))
            elif kind in (0xA0, 0xB0, 0xE0):
                pos += 2
            elif kind in (0xC0, 0xD0):
                if kind == 0xC0:
                    programs.setdefault(status & 0x0F, data[pos])
                pos += 1
            else:
                raise MidiError("damaged MIDI event")
        at = end
        for channel in sorted({c for c, _ in notes}):
            mine = sorted(n for c, n in notes if c == channel)
            label = name or f"track {len(tracks) + 1}"
            if len({c for c, _ in notes}) > 1:
                label += f" ch{channel + 1}"
            tracks.append({"name": label, "channel": channel + 1, "program": programs.get(channel, 0), "drums": channel == 9,
                           "notes": [[round(b, 4), round(d, 4), k, v] for b, d, k, v in mine]})
    return {"format": fmt, "division": division, "tempo": sorted(tempo) or [(0.0, 120.0)], "meter": sorted(meter) or [(0.0, 4, 4)],
            "keySignature": key_signature, "tracks": tracks}


def chords_per_bar(song, beats_per_bar):
    """The chord heard in each bar (pitched notes weighted by time and velocity, lowest note as the bass hint)."""
    pitched = [n for t in song["tracks"] if not t["drums"] for n in t["notes"]]
    if not pitched:
        return []
    end = max(n[0] + n[1] for n in pitched)
    out, bar = [], 0
    while bar * beats_per_bar < end - 1e-9:
        a, b = bar * beats_per_bar, (bar + 1) * beats_per_bar
        weights, lowest = {}, None
        for start, duration, key, velocity in pitched:
            overlap = min(b, start + duration) - max(a, start)
            if overlap > 0:      # short notes (melody runs, passing tones) count half: the chord is what is held
                weights[key % 12] = weights.get(key % 12, 0) + overlap * velocity * (1 if duration >= 1 else .5)
                lowest = key if lowest is None or key < lowest else lowest
        chord = theory.name_chord(weights, lowest) if weights else None
        out.append(str(chord) if chord else ("—" if not weights else "?"))
        bar += 1
    return out


def progression_text(chords):
    """['Am', 'Am', 'F', 'G'] → 'Am:2 F G' (bars)."""
    parts, i = [], 0
    while i < len(chords):
        j = i
        while j + 1 < len(chords) and chords[j + 1] == chords[i]:
            j += 1
        parts.append(chords[i] + (f":{j - i + 1}" if j > i else ""))
        i = j + 1
    return " ".join(parts)


def summary(path):
    song = read(path)
    n, d = song["meter"][0][1], song["meter"][0][2]
    beats_per_bar = n * 4 / d
    pitched = [x for t in song["tracks"] if not t["drums"] for x in t["notes"]]
    detected, confidence = theory.detect_key(pitched)
    chords = chords_per_bar(song, beats_per_bar)
    tracks = []
    for t in song["tracks"]:
        keys = [x[2] for x in t["notes"]]
        tracks.append({"name": t["name"], "channel": t["channel"], "drums": t["drums"],
                       "instrument": "drums" if t["drums"] else f"{FAMILIES[t['program'] // 8]} (GM {t['program']})", "notes": len(keys),
                       "range": f"{note_name(min(keys))}-{note_name(max(keys))}" if keys else None,
                       "bars": [int(t["notes"][0][0] // beats_per_bar) + 1, int(t["notes"][-1][0] // beats_per_bar) + 1] if keys else None})
    return {"file": str(Path(path).resolve()), "bpm": song["tempo"][0][1],
            "tempoChanges": [{"beat": round(b, 3), "bpm": v} for b, v in song["tempo"][1:]],
            "meter": f"{n}/{d}", "meterChanges": [{"beat": round(b, 3), "meter": f"{a}/{c}"} for b, a, c in song["meter"][1:]],
            "keySignature": song["keySignature"], "detectedKey": str(detected) if detected else None, "keyConfidence": round(confidence, 2),
            "bars": len(chords), "chordsPerBar": chords, "progression": progression_text(chords), "tracks": tracks}


def to_spec(path, title=None):
    """A spec with every MIDI track as a track playing its notes (free notes at absolute beats); instruments are guessed from the GM
    program (edit them), the bar chords go to the chord track."""
    song = read(path)
    n, d = song["meter"][0][1], song["meter"][0][2]
    if d != 4:
        raise MidiError(f"the MIDI file is in {n}/{d}; the spec's main meter counts quarter notes (convert by hand with meterChanges)")
    detected, _ = theory.detect_key([x for t in song["tracks"] if not t["drums"] for x in t["notes"]])
    instruments, tracks, notes, used = {}, {}, {}, set()
    for t in song["tracks"]:
        if not t["notes"]:
            continue
        name = re.sub(r"[^\w\- ]+", "", t["name"]).strip() or f"track{t['channel']}"
        while name in used:
            name += "_"
        used.add(name)
        family = "drums" if t["drums"] else FAMILIES[t["program"] // 8]
        alias = re.sub(r"\W+", "-", family)
        instruments.setdefault(alias, "synth:drum-kit" if t["drums"] else f"find:{FIND[family]}")
        median = sorted(x[2] for x in t["notes"])[len(t["notes"]) // 2]
        layer = "Drums" if t["drums"] else "Bass" if family == "bass" or median < 48 else "Melody"
        tracks[name] = {"instrument": alias, "layer": layer, "program": t["program"]}
        notes[name] = " | ".join(f"{b:g} {dur:g} {note_name(k) if not t['drums'] else k} @{v:g}" for b, dur, k, v in t["notes"])
    chords = chords_per_bar(song, n)
    spec = {"title": title or Path(path).stem, "bpm": song["tempo"][0][1], "meter": n, "key": song["keySignature"] or (str(detected) if detected else None),
            "instruments": instruments, "tracks": tracks, "notes": notes,
            "chords": [{"bar": i + 1, "chord": c} for i, c in enumerate(chords) if c not in ("—", "?") and (i == 0 or chords[i - 1] != c)]}
    if len(song["tempo"]) > 1:
        spec["tempo"] = [{"beat": round(b, 4), "bpm": v} for b, v in song["tempo"][1:]]
    if not spec["key"]:
        del spec["key"]
    return spec
