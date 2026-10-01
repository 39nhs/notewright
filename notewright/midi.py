"""Standard MIDI File export (type 1, 480 PPQ): tempo map (ramps written as small steps), time signature, section markers,
one track per spec track (drums on channel 10) with a General MIDI program guessed from the instrument."""
import struct

PPQ = 480
# (words in instrument id/tags, GM program 0-based)
PROGRAMS = [(("upright", "piano"), 0), (("epiano", "electric"), 4), (("organ",), 19), (("harp",), 46), (("celesta",), 8),
            (("glockenspiel",), 9), (("marimba",), 12), (("xylophone",), 13), (("vibraphone",), 11), (("bell", "chimes"), 14),
            (("violin",), 40), (("viola",), 41), (("cello",), 42), (("contrabass",), 43), (("pizz", "pizzicato"), 45),
            (("trem",), 44), (("strings",), 48), (("trumpet",), 56), (("trombone",), 57), (("tuba",), 58), (("horn",), 60),
            (("brass",), 61), (("flute",), 73), (("piccolo",), 72), (("oboe",), 68), (("clarinet",), 71), (("bassoon",), 70),
            (("recorder",), 74), (("saxophone", "sax"), 65), (("guitar",), 24), (("pluck",), 45), (("sub", "808"), 38),
            (("saw-bass", "bass"), 38), (("square",), 80), (("saw", "lead"), 81), (("pad",), 89), (("synth",), 81)]


def guess_program(instrument_id, tags):
    words = " ".join([instrument_id.lower()] + [t.lower() for t in tags])
    for keys, program in PROGRAMS:
        if any(k in words for k in keys):
            return program
    return 0


def _vlq(value):
    out = [value & 0x7F]
    value >>= 7
    while value:
        out.append(0x80 | (value & 0x7F))
        value >>= 7
    return bytes(reversed(out))


def _track(events):
    """events: (tick, order, bytes). Returns an MTrk chunk."""
    data, last = bytearray(), 0
    for tick, _, payload in sorted(events, key=lambda e: (e[0], e[1])):
        data += _vlq(tick - last) + payload
        last = tick
    data += _vlq(0) + b"\xFF\x2F\x00"
    return b"MTrk" + struct.pack(">I", len(data)) + bytes(data)


def _meta(kind, payload):
    return bytes([0xFF, kind]) + _vlq(len(payload)) + payload


def _tick(beat):
    return int(round(beat * PPQ))


def _tempo_events(bpm, changes):
    events = [(0, 0, _meta(0x51, struct.pack(">I", int(round(60e6 / bpm)))[1:]))]
    current = bpm
    for c in sorted(changes, key=lambda c: c["Beat"]):
        ramp = c.get("RampBeats", 0)
        if ramp > 0:
            steps = max(1, int(ramp * 4))
            for i in range(steps):
                at = c["Beat"] + ramp * i / steps
                value = current + (c["Bpm"] - current) * (i + .5) / steps
                events.append((_tick(at), 0, _meta(0x51, struct.pack(">I", int(round(60e6 / value)))[1:])))
        events.append((_tick(c["Beat"] + ramp), 0, _meta(0x51, struct.pack(">I", int(round(60e6 / c["Bpm"])))[1:])))
        current = c["Bpm"]
    return events


def write(path, meta):
    meter = meta["meter"]
    conductor = _tempo_events(meta["bpm"], meta["tempo"])
    conductor.append((0, 0, _meta(0x03, meta["title"].encode("utf-8"))))
    conductor.append((0, 0, _meta(0x58, bytes([meter, 2, 24, 8]))))
    for s in meta["sections"]:
        conductor.append((_tick(s["beat"]), 1, _meta(0x06, s["name"].encode("utf-8"))))
    chunks = [_track(conductor)]
    channels = iter([c for c in range(16) if c != 9] * 4)
    for t in meta["tracks"]:
        if not t["notes"]:
            continue
        channel = 9 if t["drums"] else next(channels)
        program = t.get("program")
        if program is None:
            program = guess_program(t["instrument"], t["tags"])
        events = [(0, 0, _meta(0x03, t["name"].encode("utf-8")))]
        if not t["drums"]:
            events.append((0, 1, bytes([0xC0 | channel, int(program) & 0x7F])))
        events.append((0, 1, bytes([0xB0 | channel, 10, max(0, min(127, int(round(64 + t["pan"] * 63))))])))
        for beat, dur, key, vel in t["notes"]:
            if t["mute"]:
                break
            start, end = _tick(beat), max(_tick(beat) + 1, _tick(beat + dur))
            events.append((start, 3, bytes([0x90 | channel, key, max(1, min(127, int(round(vel * 127))))])))
            events.append((end, 2, bytes([0x80 | channel, key, 0])))
        chunks.append(_track(events))
    header = b"MThd" + struct.pack(">IHHH", 6, 1, len(chunks), PPQ)
    with open(path, "wb") as f:
        f.write(header + b"".join(chunks))
    return path
