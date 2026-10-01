"""MusicXML 4.0 (partwise) score export: one staff per track, chords, up to 4 voices, ties across bar lines, rests, key and
time signature, tempo marks and section rehearsal marks; drums on a percussion staff. MuseScore, Dorico, Finale, Sibelius and
most notation apps open it; convert.py turns it into PDF when MuseScore is installed.

Rhythm is quantized per beat to 32nd notes or, when it fits better, 16th-note triplets. Notes are written as they fall
(no beaming or beat-grouping rules), so an engraving app's 'regroup rhythms' improves readability."""
import math
from xml.sax.saxutils import escape

from . import midi

DIV = 24                                                      # divisions per quarter note (beat)
PLAIN = [(96, "whole", 0), (72, "half", 1), (48, "half", 0), (36, "quarter", 1), (24, "quarter", 0), (18, "eighth", 1),
         (12, "eighth", 0), (9, "16th", 1), (6, "16th", 0), (3, "32nd", 0)]
TRIPLET = [(16, "quarter", 0, True), (12, "eighth", 0, False), (8, "eighth", 0, True), (4, "16th", 0, True)]
SHARPS = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
FLATS = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"]
TONIC_FIFTHS = {"C": 0, "G": 1, "D": 2, "A": 3, "E": 4, "B": 5, "F#": 6, "C#": 7, "G#": 8, "D#": 9, "A#": 10, "E#": 11,
                "F": -1, "Bb": -2, "Eb": -3, "Ab": -4, "Db": -5, "Gb": -6, "Cb": -7, "Fb": -8}
DRUM_DISPLAY = {36: ("F", 4, None), 35: ("E", 4, None), 38: ("C", 5, None), 40: ("C", 5, None), 37: ("C", 5, "x"),
                39: ("C", 5, "x"), 42: ("G", 5, "x"), 44: ("D", 4, "x"), 46: ("G", 5, "circle-x"), 49: ("A", 5, "x"),
                57: ("A", 5, "x"), 51: ("F", 5, "x"), 53: ("F", 5, "diamond"), 50: ("E", 5, None), 48: ("E", 5, None),
                47: ("D", 5, None), 45: ("A", 4, None), 43: ("G", 4, None), 41: ("G", 4, None), 54: ("A", 5, "triangle"),
                56: ("B", 5, "triangle"), 70: ("A", 5, "slash")}


def key_fifths(key):
    """'C minor', 'F# major', 'Bb', 'Am' → circle-of-fifths number (0 when unknown)."""
    if not key:
        return 0
    text = key.strip().replace("♯", "#").replace("♭", "b")
    tonic = text[:2] if len(text) > 1 and text[1] in "#b" else text[:1]
    tonic = tonic[:1].upper() + tonic[1:]
    rest = text[len(tonic):].strip().lower()
    if tonic not in TONIC_FIFTHS:
        return 0
    fifths = TONIC_FIFTHS[tonic] - (3 if rest.startswith("m") and not rest.startswith("maj") else 0)
    return fifths - 12 if fifths > 7 else fifths + 12 if fifths < -7 else fifths


def _grid_per_beat(points, beats):
    """For each beat: 3 (32nd grid) or 4 (16th-triplet grid), whichever quantizes the onsets/offsets inside it better."""
    inside = {}
    for t in points:
        b = int(t)
        if t - b > 1e-6:
            inside.setdefault(b, []).append(t - b)
    grid = {}
    for b, fracs in inside.items():
        err3 = sum(abs(f * 8 - round(f * 8)) / 8 for f in fracs)
        err4 = sum(abs(f * 6 - round(f * 6)) / 6 for f in fracs)
        grid[b] = 4 if err4 + 1e-9 < err3 * .5 else 3
    return grid


def _quantize(t, grid):
    b = int(t)
    g = grid.get(b, 3)
    return b * DIV + int(round((t - b) * DIV / g)) * g


def _pieces(start, length, grid):
    """Splits a span (in divisions) into notatable values: [(units, type, dots, triplet)]."""
    out, p, left = [], start, length
    while left > 0:
        limit = left if p % DIV == 0 else min(left, DIV - p % DIV)
        beat_grid = grid.get(p // DIV, 3)
        choices = TRIPLET if (p % DIV or limit < DIV) and beat_grid == 4 else [(u, t, d, False) for u, t, d in PLAIN]
        unit = next((c for c in choices if c[0] <= limit), None)
        if unit is None:                                       # cannot happen on a consistent grid; fall back to 32nds
            unit = (min(limit, 3), "32nd", 0, False)
        out.append(unit)
        p += unit[0]
        left -= unit[0]
    return out


def _voices(chords, limit=4):
    voices = [[] for _ in range(limit)]
    dropped = 0
    for chord in chords:
        for v in voices:
            if not v or v[-1][1] <= chord[0]:
                v.append(chord)
                break
        else:
            dropped += len(chord[2])
    return [v for v in voices if v], dropped


def _pitch_xml(key, flats):
    names = FLATS if flats else SHARPS
    name = names[key % 12]
    alter = 1 if "#" in name else -1 if "b" in name else 0
    xml = f"<pitch><step>{name[0]}</step>{f'<alter>{alter}</alter>' if alter else ''}<octave>{key // 12 - 1}</octave></pitch>"
    return xml, alter


def _note_xml(units, typ, dots, triplet, *, rest=False, key=None, drums=False, flats=False, chord=False, voice=1,
              tie_start=False, tie_stop=False, velocity=None, accidental=None):
    parts = ["<note>"]
    if velocity is not None and not rest:
        parts = [f'<note dynamics="{velocity * 100 / 0.71:.0f}">']
    if chord:
        parts.append("<chord/>")
    notehead = None
    if rest:
        parts.append("<rest/>")
    elif drums:
        step, octave, notehead = DRUM_DISPLAY.get(key, ("C", 5, "x"))
        parts.append(f"<unpitched><display-step>{step}</display-step><display-octave>{octave}</display-octave></unpitched>")
    else:
        xml, _ = _pitch_xml(key, flats)
        parts.append(xml)
    parts.append(f"<duration>{units}</duration>")
    if tie_stop:
        parts.append('<tie type="stop"/>')
    if tie_start:
        parts.append('<tie type="start"/>')
    parts.append(f"<voice>{voice}</voice><type>{typ}</type>" + "<dot/>" * dots)
    if accidental:
        parts.append(f"<accidental>{accidental}</accidental>")
    if triplet:
        parts.append("<time-modification><actual-notes>3</actual-notes><normal-notes>2</normal-notes></time-modification>")
    if drums and notehead:
        parts.append(f"<notehead>{notehead}</notehead>")
    if tie_start or tie_stop:
        ties = ('<tied type="stop"/>' if tie_stop else "") + ('<tied type="start"/>' if tie_start else "")
        parts.append(f"<notations>{ties}</notations>")
    parts.append("</note>")
    return "".join(parts)


def _drum_rhythm(notes):
    """Drum hits sound for their sample length, so notate each hit (all sounds starting together = one chord) up to the next
    hit, at most to the end of its beat: 'x.x.' reads as eighths instead of sixteenths and rests."""
    starts = sorted({n[0] for n in notes})
    following = {s: (starts[i + 1] if i + 1 < len(starts) else s + 1) for i, s in enumerate(starts)}
    return [[b, min(following[b], math.floor(b + 1e-9) + 1) - b, k, v] for b, d, k, v in notes]


def _clef(track):
    if track["drums"]:
        return "<clef><sign>percussion</sign></clef>"
    choice = track.get("clef")
    if not choice:
        keys = sorted(n[2] for n in track["notes"])
        choice = "bass" if keys and keys[len(keys) // 2] < 57 else "treble"
    if choice == "bass":
        return "<clef><sign>F</sign><line>4</line></clef>"
    if choice == "treble8vb":
        return "<clef><sign>G</sign><line>2</line><clef-octave-change>-1</clef-octave-change></clef>"
    return "<clef><sign>G</sign><line>2</line></clef>"


def write(path, meta):
    meter, loop = meta["meter"], meta["loop"]
    measure_len = meter * DIV
    measures = int(round(loop / meter))
    fifths = key_fifths(meta.get("key"))
    flats = fifths < 0
    tracks = [t for t in meta["tracks"] if t["notes"] and t.get("score", True) and not t["mute"]]
    warnings = []
    xml = ['<?xml version="1.0" encoding="UTF-8"?>',
           '<!DOCTYPE score-partwise PUBLIC "-//Recordare//DTD MusicXML 4.0 Partwise//EN" "http://www.musicxml.org/dtds/partwise.dtd">',
           '<score-partwise version="4.0">',
           f"<work><work-title>{escape(meta['title'])}</work-title></work>",
           "<identification><encoding><software>notewright plugin</software></encoding></identification>", "<part-list>"]
    channels = iter([c for c in range(16) if c != 9] * 4)
    for i, t in enumerate(tracks):
        channel = 10 if t["drums"] else next(channels) + 1
        program = t.get("program")
        if program is None:
            program = midi.guess_program(t["instrument"], t["tags"])
        xml.append(f'<score-part id="P{i + 1}"><part-name>{escape(t["name"])}</part-name>'
                   f'<score-instrument id="P{i + 1}-I1"><instrument-name>{escape(t["instrument"])}</instrument-name></score-instrument>'
                   f'<midi-instrument id="P{i + 1}-I1"><midi-channel>{channel}</midi-channel>'
                   f'{"" if t["drums"] else f"<midi-program>{int(program) + 1}</midi-program>"}</midi-instrument></score-part>')
    xml.append("</part-list>")

    tempo_marks = [(0, meta["bpm"], 0)] + [(c["Beat"], c["Bpm"], c.get("RampBeats", 0)) for c in meta["tempo"]]
    section_marks = {int(round(s["beat"] * DIV)): s["name"] for s in meta["sections"]}

    for i, t in enumerate(tracks):
        notes = _drum_rhythm(t["notes"]) if t["drums"] else t["notes"]
        t = dict(t, notes=notes)
        points = [x for b, d, *_ in t["notes"] for x in (b, b + d)]
        grid = _grid_per_beat(points, loop)
        chords = {}
        for b, d, key, vel in t["notes"]:
            s = _quantize(b, grid)
            e = max(s + grid.get(int(b), 3), _quantize(b + d, grid))
            e = min(e, measures * measure_len)
            if s >= e:
                continue
            chords.setdefault((s, e), {})[key] = vel
        ordered = sorted((s, e, sorted(keys.items())) for (s, e), keys in chords.items())
        voices, dropped = _voices(ordered)
        if dropped:
            warnings.append(f"score: track '{t['name']}' has more than 4 overlapping rhythms; {dropped} notes left out of the score")
        xml.append(f'<part id="P{i + 1}">')
        cursors = [0] * len(voices)
        for m in range(measures):
            m_start, m_end = m * measure_len, (m + 1) * measure_len
            xml.append(f'<measure number="{m + 1}">')
            if m == 0:
                xml.append(f"<attributes><divisions>{DIV}</divisions><key><fifths>{0 if t['drums'] else fifths}</fifths></key>"
                           f"<time><beats>{meter}</beats><beat-type>4</beat-type></time>{_clef(t)}</attributes>")
            if i == 0:
                for at, name in sorted(section_marks.items()):
                    if m_start <= at < m_end:
                        xml.append(f'<direction placement="above"><direction-type><rehearsal>{escape(name)}</rehearsal></direction-type>'
                                   f"<offset>{at - m_start}</offset></direction>")
                for beat, bpm, ramp in tempo_marks:
                    at = int(round(beat * DIV))
                    if m_start <= at < m_end:
                        words = f"<direction-type><words>{'accel.' if ramp and bpm > meta['bpm'] else 'rit.'}</words></direction-type>" if ramp else ""
                        xml.append(f'<direction placement="above">{words}<direction-type><metronome><beat-unit>quarter</beat-unit>'
                                   f"<per-minute>{bpm:g}</per-minute></metronome></direction-type><offset>{at - m_start}</offset>"
                                   f'<sound tempo="{bpm:g}"/></direction>')
            for v, chords_in_voice in enumerate(voices):
                if v > 0:
                    if not any(s < m_end and e > m_start for s, e, _ in chords_in_voice):
                        continue
                    xml.append(f"<backup><duration>{measure_len}</duration></backup>")
                pos = m_start
                for s, e, keys in chords_in_voice:
                    if e <= m_start or s >= m_end:
                        continue
                    seg_start, seg_end = max(s, m_start), min(e, m_end)
                    if seg_start > pos:
                        for units, typ, dots, trip in _pieces(pos, seg_start - pos, grid):
                            xml.append(_note_xml(units, typ, dots, trip, rest=True, voice=v + 1))
                    pieces = _pieces(seg_start, seg_end - seg_start, grid)
                    for k, (units, typ, dots, trip) in enumerate(pieces):
                        tie_stop = k > 0 or s < m_start
                        tie_start = k < len(pieces) - 1 or e > m_end
                        for c, (key, vel) in enumerate(keys):
                            xml.append(_note_xml(units, typ, dots, trip, key=key, drums=t["drums"], flats=flats, chord=c > 0,
                                                 voice=v + 1, tie_start=tie_start, tie_stop=tie_stop, velocity=vel))
                    pos = seg_end
                if pos < m_end and (v == 0 or pos > m_start):
                    if v == 0 and pos == m_start:
                        xml.append(f'<note><rest measure="yes"/><duration>{measure_len}</duration><voice>1</voice></note>')
                    else:
                        for units, typ, dots, trip in _pieces(pos, m_end - pos, grid):
                            xml.append(_note_xml(units, typ, dots, trip, rest=True, voice=v + 1))
            if not voices:
                xml.append(f'<note><rest measure="yes"/><duration>{measure_len}</duration><voice>1</voice></note>')
            if m == measures - 1:
                xml.append('<barline location="right"><bar-style>light-heavy</bar-style></barline>')
            xml.append("</measure>")
        xml.append("</part>")
    xml.append("</score-partwise>")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(xml))
    return warnings
