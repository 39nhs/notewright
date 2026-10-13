"""Music checks from the notes, without rendering: key, notes outside the key, semitone clashes against held notes, melody against the
chords, ranges, kick/bass alignment, leaps and sections that repeat exactly. Claude cannot hear the song; these are the things a listener
would notice first. Positions are 1-based bars of the song's main meter."""
import bisect

from . import theory
from .notes import name as note_name

KICKS = {35, 36}


def _clash(a, b):
    """A minor second, minor ninth or two-octave minor second (a major seventh is a color, not a clash)."""
    d = abs(a - b)
    return d % 12 == 1 and d <= 25


def _where(beat, meter, starts=None):
    """'bar 5 beat 2.5' (1-based), following meter changes when the bar starts are known."""
    if starts:
        i = max(0, bisect.bisect_right(starts, beat + 1e-9) - 1)
        bar, inside = i, beat - starts[i]
    else:
        bar, inside = divmod(beat, meter)
    return f"bar {int(bar) + 1}" + (f" beat {inside + 1:g}" if inside > 1e-6 else "")


def _pitched(meta):
    return [t for t in meta["tracks"] if not t["drums"] and not t.get("mute") and t["notes"]]


def _sounding(notes, beat):
    return [n for n in notes if n[0] <= beat + 1e-6 and n[0] + n[1] > beat + 1e-6]


def check(meta):
    """{"key": {...}, "issues": [...], "notes": [...], "stats": {...}} for compiled spec metadata (spec.compile's meta)."""
    meter, issues, notes_out = meta["meter"], [], []
    starts = meta.get("barStarts")
    where = lambda beat: _where(beat, meter, starts)
    pitched = _pitched(meta)
    everything = [n for t in pitched for n in t["notes"]]
    stats = {}

    # key
    detected, confidence = theory.detect_key(everything)
    declared = None
    if meta.get("keyName"):
        declared = theory.Key.parse(meta["keyName"])
    key_info = {"declared": str(declared) if declared else None, "detected": str(detected) if detected else None,
                "confidence": round(confidence, 2)}
    if declared and detected and confidence > .6 and set(declared.scale) != set() and \
            {(declared.tonic + i) % 12 for i in declared.scale} != {(detected.tonic + i) % 12 for i in detected.scale}:
        issues.append(f"the spec says {declared} but the notes read as {detected} (confidence {confidence:.2f}): fix 'key' or the notes")
    key = declared or (detected if confidence > .5 else None)

    # notes outside the key, per track
    if key:
        for t in pitched:
            outside = [n for n in t["notes"] if not key.contains(n[2])]
            share = len(outside) / len(t["notes"])
            if share > .15 and len(outside) >= 3:
                examples = ", ".join(f"{note_name(n[2])} at {where(n[0])}" for n in outside[:3])
                issues.append(f"track '{t['name']}': {len(outside)} of {len(t['notes'])} notes are outside {key} ({examples}…); "
                              "chromatic passing notes are fine, held ones sound wrong")

    # semitone clashes: a stressed melody note against a note held in another track
    melody = [t for t in pitched if t.get("layer") == "Melody"]
    others = {t["name"]: t["notes"] for t in pitched}
    clashes, seen = [], set()
    for t in melody:
        for n in t["notes"]:
            stressed = abs(n[0] - round(n[0])) < 1e-6 or n[1] >= 1
            if not stressed:
                continue
            for other, held in others.items():
                if other == t["name"]:
                    continue
                for h in _sounding(held, n[0]):
                    overlap = min(n[0] + n[1], h[0] + h[1]) - n[0]
                    pair = (round(n[0], 4), frozenset(((t["name"], n[2]), (other, h[2]))))
                    if _clash(n[2], h[2]) and overlap >= min(.5, n[1]) and pair not in seen:
                        seen.add(pair)
                        clashes.append((n[0], t["name"], n[2], other, h[2]))
    if clashes:
        examples = "; ".join(f"{where(b)}: {note_name(k)} ({a}) against {note_name(hk)} ({o})" for b, a, k, o, hk in clashes[:4])
        issues.append(f"{len(clashes)} semitone clashes between a stressed melody note and a held note ({examples}): move one of the notes "
                      "to a chord tone or shorten the held note")
    stats["clashes"] = len(clashes)

    # melody against the chord track
    chords = []
    for i, c in enumerate(meta.get("chords") or []):
        chord = theory.Chord.parse_name(c["chord"])
        if chord:
            end = meta["chords"][i + 1]["beat"] if i + 1 < len(meta["chords"]) else meta["loop"]
            chords.append((c["beat"], end, chord))
    if chords:
        for t in melody:
            strong = [n for n in t["notes"] if abs(n[0] % 1) < 1e-6 and n[1] >= 1]
            against = [n for n in strong for b, e, c in chords if b <= n[0] < e and n[2] % 12 not in c.pitch_classes]
            if strong and len(against) / len(strong) > .35:
                issues.append(f"track '{t['name']}': {len(against)} of {len(strong)} long notes on the beat are not in the chord under them "
                              f"(e.g. {note_name(against[0][2])} at {where(against[0][0])}): land phrases on chord tones")

    # ranges
    for t in pitched:
        low, high = min(n[2] for n in t["notes"]), max(n[2] for n in t["notes"])
        if low < 24 or high > 108:
            issues.append(f"track '{t['name']}': notes from {note_name(low)} to {note_name(high)} go outside the useful range (C1-C8)")
        if t.get("range"):
            lo, hi = t["range"]
            far = [n for n in t["notes"] if n[2] < lo - 5 or n[2] > hi + 5]
            if far:
                issues.append(f"track '{t['name']}': {len(far)} notes are more than a fourth outside the instrument's samples "
                              f"({note_name(lo)}-{note_name(hi)}), they will sound stretched; transpose the part or pick another instrument")
        stats.setdefault("ranges", {})[t["name"]] = f"{note_name(low)}-{note_name(high)}"
        if t.get("layer") == "Melody":
            leaps = sum(1 for a, b in zip(t["notes"], t["notes"][1:]) if abs(b[2] - a[2]) > 12 and b[0] > a[0])
            if leaps > 2:
                notes_out.append(f"track '{t['name']}': {leaps} leaps wider than an octave; fine for synth leads, hard to sing")

    # kick and bass
    kicks = sorted(n[0] for t in meta["tracks"] if t["drums"] and not t.get("mute") for n in t["notes"] if n[2] in KICKS)
    bass = sorted(n[0] for t in pitched if t.get("layer") == "Bass" for n in t["notes"])
    if len(kicks) >= 8 and len(bass) >= 8:
        together = sum(1 for k in kicks if any(abs(k - b) <= .125 for b in bass)) / len(kicks)
        stats["kickWithBass"] = round(together, 2)
        if together < .25:
            notes_out.append(f"only {together:.0%} of kicks land with a bass note: for a tight groove, start bass notes on the kicks")

    # sections that repeat exactly (different names, same notes in every track)
    signatures = {}
    for s in meta["sections"]:
        sig = tuple(sorted((t["name"], round(n[0] - s["beat"], 4), round(n[1], 4), n[2]) for t in meta["tracks"] if not t.get("mute")
                           for n in t["notes"] if s["beat"] <= n[0] < s["beat"] + s["beats"]))
        if sig:
            signatures.setdefault((sig, s["beats"]), []).append(s["name"])
    for names in signatures.values():
        distinct = sorted(set(names), key=names.index)
        if len(distinct) > 1:
            notes_out.append(f"sections {', '.join(distinct)} have exactly the same notes: vary the melody, the drums or the voicing")

    # density per section
    density = {}
    for s in meta["sections"]:
        bars = (sum(1 for b in starts if s["beat"] - 1e-9 <= b < s["beat"] + s["beats"] - 1e-9) if starts else 0) or s["beats"] / meter
        row = {t["name"]: round(sum(1 for n in t["notes"] if s["beat"] <= n[0] < s["beat"] + s["beats"]) / bars, 1)
               for t in meta["tracks"] if not t.get("mute")}
        density.setdefault(s["name"], row)
    stats["notesPerBar"] = density
    if meta.get("polymeter"):
        stats["polymeter"] = [f"{p['where']}: a {p['cycle']:g}-beat cycle against {p['bar']:g}-beat bars meets the downbeat again every "
                              f"{p['realignBeats']:g} beats" for p in meta["polymeter"]]
    return {"key": key_info, "issues": issues, "notes": notes_out, "stats": stats}
