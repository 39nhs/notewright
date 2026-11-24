"""Craft checks: the shape of a song per section, and the habits that make machine-written songs monotonous — one vocal rhythm in
every bar, a line that stops at every bar line, a melody that walks the scale or circles the tonic, a bass that only plays roots,
one drum bar for the whole song, stepwise and stock progressions. The thresholds are the ones skills/compose-song/reference/
songwriting.md explains; they are measurements to compare with the plan, not a grade. Bars are 1-based bars of the song."""
import bisect
import re
from collections import Counter

from . import theory
from .notes import name as note_name

EPS = 1e-6
# section kind from its name; pre is tested before chorus ("prechorus")
KINDS = (("pre", r"^pre|build|lift|riser|climb"), ("chorus", r"chorus|hook|refrain|drop|sabi|climax"),
         ("verse", r"verse|a-?melo|^a\d*$|^v\d*$"), ("bridge", r"bridge|break|c-?melo|middle|solo|interlude"),
         ("intro", r"intro|lead-?in|^drone$"), ("outro", r"outro|ending|^end|coda|close|fade|^tag"))
LEAD_NAMES = r"vox|vocal|voice|sing|lead|melod|tune|topline|fiddle"
DOMINANTS = {"7", "9", "7b9", "7sus4"}
DEGREES = {0: 1, 2: 2, 4: 3, 5: 4, 7: 5, 9: 6, 11: 7}
CLICHES = {"1-2-5": [(1, 2, 5)], "4-3": [(4, 3)], "3-2": [(3, 2)], "4-5-3-6": [(4, 5, 3, 6)],
           "1-5-6-4": [(1, 5, 6, 4), (5, 6, 4, 1), (6, 4, 1, 5), (4, 1, 5, 6)],
           "1-6-4-5": [(1, 6, 4, 5), (6, 4, 5, 1), (4, 5, 1, 6), (5, 1, 6, 4)]}


def kind(name):
    n = str(name).lower()
    return next((k for k, rx in KINDS if re.search(rx, n)), "other")


def _lead(meta):
    tracks = [t for t in meta["tracks"] if not t["drums"] and not t.get("mute") and t["notes"]]
    if meta.get("lead"):
        return next((t for t in tracks if t["name"] == meta["lead"]), None)

    def mono(t):
        onsets = Counter(round(n[0], 3) for n in t["notes"])
        return sum(1 for c in onsets.values() if c == 1) / len(onsets)
    melody = [t for t in tracks if t.get("layer") == "Melody" and mono(t) >= .8]
    named = [t for t in melody if re.search(LEAD_NAMES, t["name"].lower())]
    return max(named or melody, key=lambda t: len(t["notes"]), default=None)


def _line(notes):
    """The top note at each onset, in time order: [beat, length, key, velocity]."""
    top = {}
    for b, d, k, v in notes:
        at = round(b, 4)
        if at not in top or k > top[at][2]:
            top[at] = [b, d, k, v]
    return sorted(top.values())


def _tied(line):
    """Same-key notes that touch count as one note (a tie)."""
    out = []
    for n in line:
        if out and out[-1][2] == n[2] and abs(out[-1][0] + out[-1][1] - n[0]) < 1e-3:
            out[-1] = [out[-1][0], n[0] + n[1] - out[-1][0], n[2], out[-1][3]]
        else:
            out.append(list(n))
    return out


def _sounding(line, beat):
    return any(n[0] <= beat + EPS and n[0] + n[1] > beat + EPS for n in line)


class _Song:
    def __init__(self, meta):
        self.meta, self.meter = meta, meta["meter"]
        self.end = meta.get("loop") or max((n[0] + n[1] for t in meta["tracks"] for n in t["notes"]), default=0)
        self.starts = list(meta.get("barStarts") or [])
        if not self.starts:
            self.starts = [i * self.meter for i in range(int(self.end / self.meter + .999))]
        self.bounds = self.starts + [self.end]
        self.sections = [dict(s, kind=kind(s["name"]), end=s["beat"] + s["beats"]) for s in meta["sections"]]
        # keys: the song's key (declared, else detected) and its changes
        key = None
        if meta.get("keyName"):
            key = theory.Key.parse(meta["keyName"])
        else:
            pitched = [n for t in meta["tracks"] if not t["drums"] and not t.get("mute") for n in t["notes"]]
            detected, confidence = theory.detect_key(pitched)
            key = detected if confidence > .5 else None
        self.keys = [(0.0, key)]
        for change in sorted(meta.get("keys") or [], key=lambda k: k["beat"]):
            try:
                k = theory.Key.parse(change["key"])
            except theory.TheoryError:
                continue
            self.keys = [(b, x) for b, x in self.keys if b < change["beat"] - EPS] + [(change["beat"], k)]
        self.chords = []
        raw = meta.get("chords") or []
        for i, c in enumerate(raw):
            chord = theory.Chord.parse_name(c["chord"])
            if chord:
                self.chords.append((c["beat"], raw[i + 1]["beat"] if i + 1 < len(raw) else self.end, chord, c["chord"]))
        reference = meta.get("reference") or {}
        borrowed = reference.get("borrowed", "all" if reference else "none")
        self.borrowed = borrowed if isinstance(borrowed, str) else {str(b).lower() for b in borrowed}

    def bar(self, beat):
        return max(0, bisect.bisect_right(self.starts, beat + EPS) - 1)

    def bars_of(self, s):
        return [i for i, b in enumerate(self.starts) if s["beat"] - EPS <= b < s["end"] - EPS]

    def key_at(self, beat):
        return [k for b, k in self.keys if b <= beat + EPS][-1]

    def chord_at(self, beat):
        for b, e, c, _ in self.chords:
            if b <= beat + EPS < e:
                return c
        return None

    def is_borrowed(self, s):
        if self.borrowed == "all":
            return True
        if self.borrowed == "none":
            return False
        return s["name"].lower() in self.borrowed or s["kind"] in self.borrowed

    def where(self, beat):
        i = self.bar(beat)
        inside = beat - self.starts[i] if i < len(self.starts) else 0
        return f"bar {i + 1}" + (f" beat {inside + 1:g}" if inside > 1e-3 else "")


def _in(notes, s):
    return [n for n in notes if s["beat"] - EPS <= n[0] < s["end"] - EPS]


def _mode(values):
    if not values:
        return None, 0
    value, count = Counter(values).most_common(1)[0]
    return value, count / len(values)


def check(meta):
    """{"lead": track name or None, "shape": [row per section], "craft": [{"check", "level", "message"}]}."""
    song = _Song(meta)
    out, shape = [], []
    warn = lambda code, message, level="warn": out.append({"check": code, "level": level, "message": message})
    lead = _lead(meta)
    line = _line(lead["notes"]) if lead else []
    live = [t for t in meta["tracks"] if not t.get("mute") and t["notes"]]
    band = [t for t in live if t is not lead]
    drums = [t for t in live if t["drums"]]
    bass = [n for t in live if not t["drums"] and t.get("layer") == "Bass" for n in t["notes"]]

    # --- shape: one row per section ---
    for s in song.sections:
        bars = song.bars_of(s) or [song.bar(s["beat"])]
        n_bars = len(bars)
        onsets = {(t["name"], round(n[0], 3)) for t in band for n in _in(t["notes"], s)}
        notes = [n for t in live for n in _in(t["notes"], s)]
        row = {"section": s["name"], "kind": s["kind"], "bar": bars[0] + 1, "bars": n_bars,
               "band": round(len(onsets) / n_bars, 1), "parts": sum(1 for t in band if _in(t["notes"], s)),
               "velocity": round(sum(n[3] for n in notes) / len(notes), 2) if notes else None}
        sung = _in(line, s)
        if sung:
            starts = []
            for i in bars:
                inside = [n for n in sung if song.bar(n[0]) == i]
                if inside:
                    starts.append("tied" if _sounding(line, song.starts[i]) and inside[0][0] > song.starts[i] + EPS
                                  else round(inside[0][0] - song.starts[i], 3))
            start, share = _mode(starts)
            covered = sum(min(n[0] + n[1], s["end"]) - n[0] for n in sung)
            row["vocal"] = {"range": f"{note_name(min(n[2] for n in sung))}-{note_name(max(n[2] for n in sung))}",
                            "barsStart": (f"beat {start + 1:g}" if isinstance(start, float) else start) + f" ({share:.0%})",
                            "notesPerBar": round(len(sung) / n_bars, 1),
                            "longest": round(max(n[1] for n in _tied(sung)), 2),
                            "space": round(max(0.0, 1 - covered / s["beats"]), 2)}
        if song.chords:
            changes = [c for c in song.chords if s["beat"] - EPS <= c[0] < s["end"] - EPS]
            row["chordsPerBar"] = round(len(changes) / n_bars, 2)
            here = _in(bass, s)
            if here:
                on_root = sum(1 for n in here if (c := song.chord_at(n[0])) and n[2] % 12 in (c.root, c.bass))
                row["bassOnRoot"] = round(on_root / len(here), 2)
        shape.append(row)

    sung_sections = [s for s, r in zip(song.sections, shape) if "vocal" in r and len(_in(line, s)) >= 4]
    by_row = dict(zip((id(s) for s in song.sections), shape))

    # --- arrangement ---
    singing = [s for s in sung_sections if s["kind"] not in ("intro", "outro")]
    densities = [by_row[id(s)]["band"] for s in singing]
    if len(singing) >= 2 and min(densities) > 0 and max(densities) < 1.35 * min(densities):
        warn("energy-flat", f"the band plays about as much under every sung section ({min(densities):g}-{max(densities):g} onsets a bar, "
                            "less than 1.35x apart): take parts out of the verses and bring them back in the chorus")
    first_chorus = next((s for s in song.sections if s["kind"] == "chorus"), None)
    verses = [s for s in song.sections if s["kind"] == "verse" and first_chorus and s["beat"] < first_chorus["beat"]]

    def palette(s):
        names = {t["name"] for t in band if not t["drums"] and _in(t["notes"], s)}
        return names | {("drum", n[2]) for t in drums for n in _in(t["notes"], s)}
    if first_chorus and verses:
        if not palette(first_chorus) - set().union(*(palette(v) for v in verses)):
            warn("arrangement-no-lift", f"the first chorus ('{first_chorus['name']}') brings in no part or drum sound the verse did not have: "
                                        "leave something out of the verse and let it arrive with the chorus")
        if line:
            ch, vs = _in(line, first_chorus), [n for v in verses for n in _in(line, v)]
            if ch and vs and max(n[2] for n in ch) <= max(n[2] for n in vs) and \
                    sum(n[2] for n in ch) / len(ch) <= sum(n[2] for n in vs) / len(vs) + 1:
                warn("melody-no-peak", f"the first chorus sings no higher than the verse (top {note_name(max(n[2] for n in ch))} against "
                                       f"{note_name(max(n[2] for n in vs))}): put the chorus's long notes a third or more above the verse")

    # --- the lead line ---
    if line:
        sung_bars = {song.bar(n[0]) for n in line}
        moves = [(a, b) for a, b in zip(line, line[1:]) if b[2] != a[2]]
        leaps = [m for m in moves if abs(m[1][2] - m[0][2]) >= 5]
        for s in sung_sections:
            if s["kind"] in ("intro", "outro"):
                continue
            sung = _in(line, s)
            sigs = []
            for i in song.bars_of(s):
                inside = [n for n in sung if song.bar(n[0]) == i]
                if inside:
                    sigs.append(tuple(round((n[0] - song.starts[i]) * 12) for n in inside))
            if len(sigs) >= 6:
                _, share = _mode(sigs)
                if share > .7:
                    warn("melody-rhythm-loop", f"'{s['name']}': {share:.0%} of its sung bars have the same rhythm: develop the motif "
                                               "(sequence, truncate, augment, push a note) instead of repeating the bar")
        if len(line) >= 40:
            pushed = [n for n in line if EPS < n[0] % 1 < 1 - EPS and n[0] + n[1] > int(n[0]) + 1 + .1]
            if len(pushed) / len(line) < .03:
                warn("melody-no-push", f"only {len(pushed)} of {len(line)} lead notes start before a beat and hold across it: start about "
                                       "one stressed syllable in four an eighth early and tie it over the beat")
        if len(moves) >= 40 and len(leaps) / len(moves) < .08:
            warn("melody-small-intervals", f"{len(leaps)} of {len(moves)} melodic moves are a fourth or wider ({len(leaps) / len(moves):.0%}): "
                                           "give each line one leap on a stressed word, then step back the other way")
        for s in sung_sections:
            sm = [(a, b) for a, b in zip(_in(line, s), _in(line, s)[1:]) if b[2] != a[2]]
            if len(sm) > 12 and sum(1 for a, b in sm if abs(b[2] - a[2]) >= 5) / len(sm) < .10:
                warn("melody-small-intervals", f"'{s['name']}': under 10% of its {len(sm)} moves are a fourth or wider", "info")
        for s in sung_sections:
            sung = _in(line, s)
            if len(sung) < 16 or not song.key_at(s["beat"]):
                continue
            tonic = song.key_at(s["beat"]).tonic
            orbit = sum(1 for n in sung if (n[2] - tonic) % 12 in (10, 11, 0, 1, 2)) / len(sung)
            sm = [(a, b) for a, b in zip(sung, sung[1:]) if b[2] != a[2]]
            jumps = sum(1 for a, b in sm if abs(b[2] - a[2]) >= 5) / (len(sm) or 1)
            if orbit >= .55 and jumps < .15:
                warn("melody-tonic-orbit", f"'{s['name']}': {orbit:.0%} of the notes sit on degrees 7, 1 and 2 with few leaps: save the tonic "
                                           "for line ends and build the line around 3, 5 and 6")
        cadences = []
        for a, b, c in zip(line, line[1:], line[2:]):
            k = song.key_at(c[0])
            if k and a[2] - b[2] in (1, 2) and b[2] - c[2] in (1, 2) and (c[2] - k.tonic) % 12 == 0:
                cadences.append(c)
        if len(cadences) >= 3 and len(cadences) >= len(sung_bars) / 10:
            warn("melody-scale-cadence", f"{len(cadences)} lines step down onto the tonic (3-2-1 and the like), e.g. at "
                                         f"{song.where(cadences[0][0])}: end some lines open (on 5, 2 or 6) or land by a leap")
        boxed = []
        for s in sung_sections:
            sung = _in(line, s)
            lines_ = [song.starts[i] for i in song.bars_of(s)
                      if sung[0][0] + EPS < song.starts[i] < sung[-1][0] + sung[-1][1] - EPS]
            if len(lines_) >= 5:
                silent = sum(1 for b in lines_ if not _sounding(line, b))
                if silent / len(lines_) >= .8:
                    boxed.append(f"'{s['name']}' {silent}/{len(lines_)}")
        if boxed:
            warn("melody-bar-boxes", f"the lead is silent on most bar lines ({', '.join(boxed)}): it stops and restarts in every bar. "
                                     "Write 2-4 bar lines that cross bar lines (tie over, or pick up into the downbeat)")
        runs, run = [], [line[0]]
        for a, b in zip(line, line[1:]):
            if b[0] - (a[0] + a[1]) >= .5 - EPS:
                runs.append(run)
                run = []
            run.append(b)
        runs.append(run)
        if len(runs) >= 16:
            notes_per = sum(len(r) for r in runs) / len(runs)
            beats_per = sum(r[-1][0] + r[-1][1] - r[0][0] for r in runs) / len(runs)
            if notes_per < 4.5 and beats_per < song.meter:
                warn("melody-short-lines", f"the lead sings in {len(runs)} short bursts of {notes_per:.1f} notes ({beats_per:.1f} beats) "
                                           "between rests: let a line run 2-4 bars and gather the rests at its end")
        lead_bars = sorted({song.bar(n[0]) for n in line} | {i for i in range(len(song.starts)) if _sounding(line, song.starts[i])})
        if len(lead_bars) >= 16:
            restarts = []
            for i in lead_bars:
                if not _sounding(line, song.starts[i]):
                    first = next((n for n in line if song.bar(n[0]) == i), None)
                    if first:
                        restarts.append(round(first[0] - song.starts[i], 3))
            offset, _ = _mode(restarts)
            count = restarts.count(offset) if restarts else 0
            if count >= len(lead_bars) / 2:
                warn("melody-same-start", f"{count} of {len(lead_bars)} sung bars restart at beat {offset + 1:g} after a silent bar line: "
                                          "move where lines enter (on 1, after 1, a pickup from the bar before, after two beats of rest)")
        for s in sung_sections:
            sung, shapes = _in(line, s), []
            for i in song.bars_of(s):
                inside = [n for n in sung if song.bar(n[0]) == i][:3]
                if len(inside) == 3:
                    d1, d2 = inside[1][2] - inside[0][2], inside[2][2] - inside[1][2]
                    shapes.append("up" if d1 > 0 and d2 > 0 else "down" if d1 < 0 and d2 < 0 else "other")
            if len(shapes) >= 6:
                ups, downs = shapes.count("up"), shapes.count("down")
                if max(ups, downs) / len(shapes) >= .6:
                    warn("melody-contour-loop", f"'{s['name']}': {max(ups, downs)} of {len(shapes)} bars open with the same "
                                                f"{'climb' if ups >= downs else 'fall'} of three notes (an arpeggio, not a vocal line): "
                                                "stay on two or three neighbouring notes, repeat a note, answer a rise with a fall")
        tied = _tied(line)
        if len(tied) >= 40:
            seconds = 60.0 / float(meta.get("bpm") or 120)
            long = sum(1 for n in tied if n[1] >= 2 - EPS or n[1] * seconds >= 1 - EPS)
            if long / len(tied) < .04:
                warn("melody-no-long-note", f"only {long} of {len(tied)} lead notes last 2 beats (or 1 s): hold the last syllable of a line "
                                            "and the hook's top note")
        verse = next((s for s in sung_sections if s["kind"] == "verse"), None)
        chorus = next((s for s in sung_sections if s["kind"] == "chorus"), None)
        if verse and chorus:
            rv, rc = by_row[id(verse)]["vocal"], by_row[id(chorus)]["vocal"]
            mean = lambda s: sum(n[1] for n in _in(line, s)) / len(_in(line, s))
            close = lambda a, b: abs(a - b) <= .15 * max(a, b, 1e-9)
            if rv["barsStart"].split(" (")[0] == rc["barsStart"].split(" (")[0] and close(rv["notesPerBar"], rc["notesPerBar"]) \
                    and close(mean(verse), mean(chorus)):
                warn("melody-verse-chorus-rhythm", f"'{verse['name']}' and '{chorus['name']}' start lines at the same place with the same "
                                                   "number and length of notes: flip one of them (note length, rests, line length, entry)",
                     "info")

    # --- bass ---
    if len(bass) >= 16 and song.chords:
        roots = [n for n in bass if (c := song.chord_at(n[0])) and n[2] % 12 in (c.root, c.bass)]
        length, _ = _mode([round(n[1], 3) for n in roots])
        same = sum(1 for n in roots if round(n[1], 3) == length)
        if same / len(bass) >= .85:
            warn("bass-roots-only", f"{same / len(bass):.0%} of the bass notes are the chord root at one length: root on the chord change, "
                                    "then fifths, octaves and an approach note into the next root")

    # --- harmony ---
    if len(song.chords) >= 16:
        lengths = {round(e - b, 2) for b, e, _, _ in song.chords[:-1]}
        if len(lengths) == 1:
            warn("harmony-rhythm-flat", f"all {len(song.chords)} chords last {lengths.pop():g} beats: change the harmonic rhythm "
                                        "between sections (two a bar, one held over two bars, a push into the chorus)")
    if song.chords:
        _progressions(song, warn)

    # --- drums ---
    if drums:
        _drums(song, drums, warn)

    # --- guitar strokes ---
    for t in live:
        if t["drums"] or not re.search(r"guitar|gtr|기타", t["name"].lower()):
            continue
        groups, group = [], []
        for n in sorted(t["notes"]):
            if group and n[0] - group[0][0] > .1:
                groups.append(group)
                group = []
            group.append(n)
        groups.append(group)
        strokes = [g for g in groups if len(g) >= 3]
        ups = [g for g in strokes if g[-1][0] - g[0][0] > .002 and g[0][2] > g[-1][2]]
        if len(strokes) >= 8 and not ups:
            warn("guitar-all-down", f"track '{t['name']}': {len(strokes)} strums, none an upstroke: write upstrokes on the off-beats "
                                    "(top strings first, 3-4 strings, softer)")
    return {"lead": lead["name"] if lead else None, "shape": shape, "craft": out}


def _progressions(song, warn):
    sections = []
    for s in song.sections:
        seq = [c for c in song.chords if s["beat"] - EPS <= c[0] < s["end"] - EPS]
        dedup = []
        for c in seq:
            if not dedup or dedup[-1][3] != c[3]:
                dedup.append(c)
        if len(dedup) >= 2:
            sections.append((s, dedup))
    if not sections:
        return
    own = [(s, seq) for s, seq in sections if not song.is_borrowed(s)]
    level = lambda: "warn" if own else "info"
    scope = own or sections

    def diatonic(c, at):
        k = song.key_at(at)
        return bool(k) and all(k.contains(p) for p in c.pitch_classes)

    # stepwise: three diatonic chords (or four of any colour) stepping by seconds in one direction
    stairs = []
    for s, seq in scope:
        signed = [((b[2].root - a[2].root + 6) % 12) - 6 for a, b in zip(seq, seq[1:])]
        same_way = lambda xs: all(1 <= abs(x) <= 2 for x in xs) and (all(x > 0 for x in xs) or all(x < 0 for x in xs))
        i = 0
        while i < len(seq) - 2:
            k = song.key_at(seq[i][0])
            four, trio = seq[i:i + 4], seq[i:i + 3]
            if len(four) == 4 and same_way(signed[i:i + 3]) and not any(c[2].suffix in ("dim", "dim7") for c in four[1:3]):
                stairs.append((s, four))
                i += 3
                continue
            passing = trio[1][2].suffix in ("dim", "dim7")
            aeolian = k and [(c[2].root - k.tonic) % 12 for c in trio] == [8, 10, 0]          # bVI-bVII-I
            if same_way(signed[i:i + 2]) and not passing and not aeolian and all(diatonic(c[2], c[0]) for c in trio):
                stairs.append((s, trio))
                i += 2
                continue
            i += 1
    if stairs:
        s, run = stairs[0]
        warn("progression-stepwise", f"{len(stairs)} chord runs climb or fall by step ({' '.join(c[3] for c in run)} in '{s['name']}'): "
                                     "make the middle chord a fourth or fifth away, slide by a semitone (bII7-I, a passing dim7), "
                                     "borrow bVI-bVII-I, or keep the chord and walk only the bass (slash chords)", level())

    # root motion: leaning moves (fourths, fifths, tritones, a dominant falling a semitone) against seconds and thirds
    def lean(a, b):
        d = (b[2].root - a[2].root) % 12
        return d in (5, 6, 7) or (d == 11 and a[2].suffix in DOMINANTS)
    small, total, flat = 0, 0, []
    for s, seq in scope:
        pairs = [(a, b) for a, b in zip(seq, seq[1:]) if a[2].root != b[2].root]
        total += len(pairs)
        small += sum(1 for a, b in pairs if not lean(a, b))
        if len(pairs) >= 4 and not any(lean(a, b) for a, b in pairs):
            flat.append(s["name"])
    if flat:
        warn("progression-small-moves", f"no fourth, fifth or tritone root move in {', '.join(repr(n) for n in flat)}: add a secondary "
                                        "dominant or a circle-of-fifths run so the harmony leans somewhere", level())
    elif total >= 10 and small / total >= .7:
        warn("progression-small-moves", f"{small / total:.0%} of the root moves are seconds and thirds: give at least three moves in ten "
                                        "a fourth, fifth or dominant pull", level())

    # the same loop in sections of different kinds
    loops = {}
    for s, seq in scope:
        if s["kind"] in ("intro", "outro"):
            continue
        names = [c[3] for c in seq]
        period = next(p for p in range(1, len(names) + 1) if all(names[i] == names[i % p] for i in range(len(names))))
        if period < 2:
            continue
        loop = names[:period]
        canon = min(tuple(loop[i:] + loop[:i]) for i in range(period))
        loops.setdefault(canon, []).append(s)
    for canon, where in loops.items():
        kinds = {s["kind"] if s["kind"] != "other" else s["name"] for s in where}
        if len(kinds) >= 2:
            names = sorted({s["name"] for s in where}, key=[s["name"] for s in where].index)
            warn("progression-same-loop", f"{', '.join(repr(n) for n in names)} turn the same chord loop ({' '.join(canon)}): give each "
                                          "kind of section its own progression family", level())

    # no chord outside the key
    changes = sum(len(seq) - 1 for _, seq in sections)
    if len(sections) >= 3 and changes >= 12 and all(diatonic(c[2], c[0]) for _, seq in sections for c in seq):
        warn("progression-diatonic", f"{changes} chord changes and not one chord from outside the key: try a secondary dominant, a borrowed "
                                     "iv or bVI, or a chromatic mediant in the chorus or bridge", level())

    # stock progressions, counted from the relative major
    found = {}
    for s, seq in scope:
        k = song.key_at(seq[0][0])
        if not k:
            continue
        tonic = _major_tonic(k)
        degrees = tuple(DEGREES.get((c[2].root - tonic) % 12) for c in seq)
        for name, shapes in CLICHES.items():
            if any(degrees[i:i + len(p)] == p for p in shapes for i in range(len(degrees) - len(p) + 1)):
                found.setdefault(name, []).append(s["name"])
    with_any = len({n for names in found.values() for n in names})
    for name, names in found.items():
        if len(names) >= 3 or (with_any * 2 >= len(scope) and len(names) >= 2):
            warn("progression-cliche", f"the stock progression {name} turns up in {', '.join(repr(n) for n in names)}: keep it to one "
                                       "section at most (or the borrowed one) and give the others another family", level())


def _major_tonic(key):
    if key.mode in ("major", "major pentatonic", "blues"):         # a blues counts its I7 IV7 V7 from its own tonic
        return key.tonic
    scale = {(key.tonic + i) % 12 for i in key.scale}
    for t in range(12):
        if {(t + i) % 12 for i in theory.SCALES["major"]} == scale:
            return t
    return (key.tonic + 3) % 12


def _drums(song, drums, warn):
    hits = sorted((n for t in drums for n in t["notes"]), key=lambda n: n[0])
    bars = {}
    for n in hits:
        i = song.bar(n[0])
        bars.setdefault(i, []).append(n)
    grid = lambda i, vel: tuple(sorted((round(n[0] - song.starts[i], 3), n[2]) + ((round(n[3], 1),) if vel else ()) for n in bars.get(i, [])))
    mains, transitions = [], []
    for s in song.sections:
        idx = [i for i in song.bars_of(s) if i in bars]
        if not idx:
            continue
        if len(idx) > 4 and len(idx) == len(song.bars_of(s)) and len({grid(i, True) for i in idx}) == 1:
            warn("drums-loop", f"'{s['name']}': every one of its {len(idx)} drum bars is the same: change the hats (8ths/16ths, closed/open) "
                               "or the kick inside the section, and end it with a fill, a stop or a crash")
        main, _ = _mode([grid(i, False) for i in idx])
        mains.append((s, main))
        last = song.bars_of(s)[-1]
        transitions.append((s, len(idx) >= 2 and last in bars and grid(last, False) == main))
    if len(mains) >= 3:
        main, share = _mode([m for _, m in mains])
        kinds = {s["kind"] for s, m in mains if m == main}
        if share >= .75 and len({s["kind"] for s, _ in mains}) >= 2 and len(kinds) >= 2:
            warn("drums-one-groove", f"{share:.0%} of the sections with drums play the same groove: thin it in the verse, fill it in the "
                                     "chorus, give the bridge half time, a ride or toms")
    ends = [plain for (s, plain), nxt in zip(transitions, transitions[1:])]
    if len(ends) >= 3 and sum(ends) / len(ends) >= .75:
        warn("drums-no-fills", f"{sum(ends)} of {len(ends)} section changes go by with the drums playing their usual bar: mark them with "
                               "a tom run, a snare roll, a one-beat stop, a kick pickup or a band hit, a different one each time")
    for t in drums:
        if len(t["notes"]) >= 16 and len({round(n[3], 2) for n in t["notes"]}) == 1:
            warn("drums-flat", f"track '{t['name']}': all {len(t['notes'])} hits at one velocity: accent the beats, ghost the snare "
                               "(0.3-0.5), soften off-beat hats")
