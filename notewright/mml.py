"""MML export for in-game performance in Mabinogi (PC and mobile) and Lost Ark.

Every spec track becomes one score: up to three single-note voices (melody, chord 1, chord 2), written as MML@melody,chord1,chord2;
— a Mabinogi scroll (one player each) or a Lost Ark track. Chords are spread over the voices (top notes first); voices beyond three
go to an extra score for the same track. Scores longer than the game allows are split at bar lines into consecutive parts.

MML: t tempo, l default length, v volume 0-15, o octave, < > octave down/up, c d e f g a b with # (or +) for sharp, r rest, & tie, a
length n = a 1/n note (4/n beats), "." dotted, nN an absolute pitch (o4c = n48 = middle C, MIDI 60). Drums are written as n codes, the
only form both games accept for them (Lost Ark's drum part ignores note letters).

Each game has a profile (GAMES): the note lengths it keeps, the time grid and where tempo goes. Lost Ark drops notes and rests with
non-binary lengths (3, 6, 12, 24, 48: a score entered in the game and copied back lost them), so its profile allows only
1, 2, 4, 8, 16, 32 and 64 (dotted where that stays on the grid) and quantizes every note's absolute start and end to 64ths; triplets
become the nearest 64th positions. Lost Ark plays one BPM for the whole score, so tempo goes into the first track's melody only."""
import math
import re
from fractions import Fraction

TICKS = 48                                      # per beat (quarter note); a whole note = 192
ALL_DENOMINATORS = (1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64)
BINARY_DENOMINATORS = (1, 2, 4, 8, 16, 32, 64)
TEMPO_GRID = 12                                 # tempo changes land on 16ths, which every profile's grid can reach


def _length_table(denominators):
    """[(ticks, token)] for the allowed denominators and their dotted forms (when the dotted value is a whole tick count)."""
    table = [(192 // n, str(n)) for n in denominators]
    table += [(192 // n * 3 // 2, f"{n}.") for n in denominators if (192 // n) % 2 == 0 and (192 // n * 3 // 2) % 3 == 0]
    return sorted(table, reverse=True)


LENGTHS = _length_table(ALL_DENOMINATORS)
NAMES = ("c", "c#", "d", "d#", "e", "f", "f#", "g", "g#", "a", "a#", "b")

# Drum kit positions (white keys F to F, 15 sounds). Lost Ark's drum part plays them two octaves above Mabinogi's drum.
DRUM_SOUNDS = ("kick", "lowTom", "snare", "rimshot", "sidestick", "ghostSnare", "lowMidTom", "highTom", "openHat", "closedHat",
               "pedalHat", "crash", "crashChoke", "ride", "rideBell")
_WHITE = (0, 2, 4, 5, 7, 9, 11, 12, 14, 16, 17, 19, 21, 23, 24)            # F F#… as offsets: F G A B C D E F G A B C D E F
DRUM_N = {"mabinogi": {s: 29 + _WHITE[i] for i, s in enumerate(DRUM_SOUNDS)},
          "lostark": {s: 53 + _WHITE[i] for i, s in enumerate(DRUM_SOUNDS)}}
# General MIDI drum keys → kit sounds (sounds the kits do not have take the closest one).
GM_DRUMS = {35: "kick", 36: "kick", 37: "sidestick", 38: "snare", 39: "rimshot", 40: "snare", 41: "lowTom", 43: "lowTom", 45: "lowTom",
            47: "lowMidTom", 48: "lowMidTom", 50: "highTom", 42: "closedHat", 44: "pedalHat", 46: "openHat", 49: "crash", 52: "crash",
            55: "crashChoke", 57: "crash", 51: "ride", 59: "ride", 53: "rideBell", 54: "closedHat", 56: "rideBell", 69: "closedHat",
            70: "closedHat", 81: "rideBell", 75: "sidestick", 76: "sidestick", 77: "sidestick"}

GAMES = {
    # parts: character limits (melody, chord 1, chord 2) or total: one limit for all three; notes: note limit per score (counted
    # conservatively: every tied piece and every t/v command); denominators: note lengths the game keeps; triplets: whether a track may
    # use the 48th grid; tempo: "every" voice or only the "first" track's melody (one BPM for the whole score); sharp: how sharps are
    # written (the game's own form).
    "mabinogi": {"title": "Mabinogi", "parts": (1200, 800, 500), "total": None, "notes": None, "denominators": ALL_DENOMINATORS,
                 "triplets": True, "tempo": "every", "sharp": "#",
                 "about": "one scroll per player (composition rank 1: melody 1200, chord 1 800, chord 2 500 characters); everyone starts "
                          "together in an ensemble"},
    "mabinogi-mobile": {"title": "Mabinogi Mobile", "parts": (2400, 2400, 2400), "total": None, "notes": None,
                        "denominators": ALL_DENOMINATORS, "triplets": True, "tempo": "every", "sharp": "#",
                        "about": "one score per player, 2400 characters per part"},
    "lostark": {"title": "Lost Ark", "parts": None, "total": 5000, "notes": 1000, "denominators": BINARY_DENOMINATORS,
                "triplets": False, "tempo": "first", "sharp": "+",
                "about": "one track each (4 tracks, up to 10 with crystals); per track 5000 characters (melody + chord 1 + chord 2) and "
                         "1000 notes; at most 10 minutes; one BPM for the whole score (written in track 1's melody); note lengths "
                         "1-64 in powers of two only, so triplets are placed on the nearest 64th"},
}
INSTRUMENTS = {
    "mabinogi": [(("drum",), "drum"), (("piano", "keys", "epiano", "organ"), "piano"), (("harp", "pluck"), "harp"),
                 (("violin", "strings", "viola"), "violin"), (("cello", "contrabass", "bass"), "cello"), (("tuba", "trombone", "horn", "brass"), "tuba"),
                 (("flute", "piccolo", "recorder", "whistle"), "flute"), (("clarinet", "oboe", "bassoon", "sax"), "chalumeau"),
                 (("guitar", "lead", "saw", "square"), "electric guitar"), (("bell", "celesta", "glockenspiel", "xylophone", "marimba"), "xylophone"),
                 (("pad", "synth"), "lute")],
    "lostark": [(("drum",), "드럼"), (("piano", "keys", "epiano", "organ"), "피아노"), (("harp", "pluck"), "하프"),
                (("violin", "strings", "viola"), "바이올린"), (("cello", "contrabass"), "첼로"), (("tuba", "trombone", "bass"), "튜바"),
                (("horn", "brass", "trumpet"), "뿔피리"), (("flute", "piccolo", "recorder"), "플루트"), (("clarinet", "oboe", "bassoon", "sax"), "클라리넷"),
                (("lead", "saw", "square", "guitar"), "일렉트로닉 기타"), (("pad", "synth", "bell"), "어쿠스틱 기타")],
}


class MmlError(ValueError):
    pass


def suggest_instrument(game, track):
    words = " ".join([track["instrument"].lower()] + [t.lower() for t in track.get("tags", [])] + ["drum" if track["drums"] else ""])
    for keys, name in INSTRUMENTS["lostark" if game == "lostark" else "mabinogi"]:
        if any(k in words for k in keys):
            return name
    return "piano" if game != "lostark" else "피아노"


def _tick(beat):
    return int(round(beat * TICKS))


def _grid(notes, triplets=True):
    """3 ticks (a 64th) or, where the game keeps triplet lengths, 4 ticks (a 48th) when that fits the track's note times better."""
    def error(step):
        return sum(abs(x * TICKS / step - round(x * TICKS / step)) for b, d, _, _ in notes for x in (b, b + d))
    return 4 if triplets and notes and error(4) < error(3) * .5 else 3


def _tempo_points(bpm, changes):
    """[(tick, bpm)]: the song's tempo map with ramps as one step per beat."""
    points, current = [(0, bpm)], bpm
    for c in sorted(changes, key=lambda c: c["Beat"]):
        ramp = c.get("RampBeats", 0) or 0
        steps = max(1, int(math.ceil(ramp))) if ramp > 0 else 0
        for i in range(steps):
            points.append((_tick(c["Beat"] + ramp * i / steps), current + (c["Bpm"] - current) * (i + .5) / steps))
        points.append((_tick(c["Beat"] + ramp), c["Bpm"]))
        current = c["Bpm"]
    merged = {}
    for tick, value in points:
        merged[int(round(tick / TEMPO_GRID)) * TEMPO_GRID] = int(round(min(255, max(32, value))))
    out = []
    for tick in sorted(merged):
        if not out or out[-1][1] != merged[tick]:
            out.append((tick, merged[tick]))
    return out


def voices(notes, grid, drums=False):
    """Single-note voices from a track's notes (quantized to the grid in ticks): top notes first, a note that overlaps the next one in
    its voice is shortened, and a short gap before the next note is closed (a release gap costs characters and is not heard in the
    game; drum hits last until the next hit). Returns [[(start, end, key, velocity)]] sorted by start."""
    events = []
    for beat, duration, key, velocity in notes:
        start = int(round(beat * TICKS / grid)) * grid
        end = max(start + grid, int(round((beat + duration) * TICKS / grid)) * grid)
        events.append((start, end, key, velocity))
    events.sort(key=lambda e: (e[0], -e[2]))
    out = []
    for start, end, key, velocity in events:
        for voice in out:
            last = voice[-1]
            if last[0] == start:
                continue                                            # this voice already has a note starting here (a chord tone)
            if last[1] <= start:
                voice.append((start, end, key, velocity))
                break
            if last[1] - start <= (last[1] - last[0]) // 4:          # a small legato overlap: shorten the earlier note
                voice[-1] = (last[0], start, last[2], last[3])
                voice.append((start, end, key, velocity))
                break
        else:
            out.append([(start, end, key, velocity)])
    for voice in out:
        for i in range(len(voice) - 1):
            start, end, key, velocity = voice[i]
            gap = voice[i + 1][0] - end
            if 0 < gap and (drums and voice[i + 1][0] - start <= 192 or gap <= max(grid, (end - start) // 8)):
                voice[i] = (start, voice[i + 1][0], key, velocity)
    return out


_TABLES = {}
_BEST = {}


def _lengths(ticks, denominators=ALL_DENOMINATORS):
    """Length tokens adding up to `ticks` using only the allowed denominators, as few characters as possible (whole notes first for
    long values). Each set of denominators has its own table and cache, so games never share a length policy."""
    key = tuple(denominators)
    if key not in _TABLES:
        _TABLES[key] = _length_table(key)
        _BEST[key] = {0: []}
    table, best_of = _TABLES[key], _BEST[key]
    head = []
    while ticks > 384:
        head.append("1")
        ticks -= 192
    if ticks not in best_of:
        for t in range(1, ticks + 1):                           # shortest decomposition, built up once and cached
            if t in best_of:
                continue
            best = None
            for value, token in table:
                if value <= t and best_of.get(t - value) is not None:
                    candidate = [token] + best_of[t - value]
                    if best is None or (len(candidate), sum(map(len, candidate))) < (len(best), sum(map(len, best))):
                        best = candidate
            best_of[t] = best
    if best_of[ticks] is None:
        raise MmlError(f"cannot write {ticks} ticks with lengths {', '.join(map(str, key))}")
    order = dict((tok, v) for v, tok in table)
    return head + sorted(best_of[ticks], key=lambda token: -order[token])


DYNAMICS = ("coarse", "full", "none")


class _Writer:
    def __init__(self, drums, drum_map, dynamics="coarse", fixed=None, denominators=ALL_DENOMINATORS, sharp="#"):
        self.drums, self.drum_map, self.dynamics, self.fixed = drums, drum_map, dynamics, fixed
        self.denominators, self.sharp = denominators, sharp
        self.text, self.octave, self.length, self.volume, self.notes = [], 4, "4", 8, 0

    def lengths(self, ticks):
        return _lengths(ticks, self.denominators)

    def emit(self, token):
        self.text.append(token)

    def tempo(self, bpm):
        self.emit(f"t{bpm}")

    def set_volume(self, velocity):
        """full: every change; coarse: levels 3 apart, written only when they change (saves characters); none: one level per part."""
        if self.dynamics == "none" or (self.dynamics == "coarse" and self.drums):     # drum accents would cost a v on every hit
            v = self.fixed
        else:
            v = max(1, min(15, int(round(velocity * 15))))
            if self.dynamics == "coarse":
                v = min(15, max(3, int(round(v / 3)) * 3))
        if v != self.volume:
            self.emit(f"v{v}")
            self.volume = v

    def rest(self, ticks):
        for token in self.lengths(ticks):
            self.emit("r" if token == self.length else "r" + token)

    def note(self, ticks, key, velocity, tied=False):
        """A note (tied = the continuation of the previous one, after a tempo change: counted once, written with '&')."""
        self.set_volume(velocity)
        if not tied:
            self.notes += 1
        tokens = self.lengths(ticks)
        if self.drums:                                      # a drum hit is never tied: the first length plays, the rest is silence
            if tokens[0] != self.length:
                self.emit(f"l{tokens[0]}")
                self.length = tokens[0]
            self.emit(f"n{key}")
            for token in tokens[1:]:
                self.emit("r" if token == self.length else "r" + token)
            return
        octave, name = key // 12 - 1, NAMES[key % 12].replace("#", self.sharp)
        prefix = ""
        if octave != self.octave:
            diff = octave - self.octave
            prefix = ">" * diff if 0 < diff <= 1 else "<" * -diff if -1 <= diff < 0 else f"o{octave}"
        pieces = [("&" if tied else "") + prefix + name + ("" if tokens[0] == self.length else tokens[0])]
        for token in tokens[1:]:
            pieces.append("&" + name + ("" if token == self.length else token))
        self.octave = octave
        self.emit("".join(pieces))

    def result(self):
        return "".join(self.text)


def _default_length(voice_events, denominators=ALL_DENOMINATORS):
    """The note length used most often (fewest characters when set with l)."""
    counts = {}
    for start, end, _, _ in voice_events:
        for token in _lengths(end - start, denominators):
            counts[token] = counts.get(token, 0) + 1
    return max(counts, key=lambda t: (counts[t] * len(t), -len(t))) if counts else "4"


def write_voice(events, start, end, tempo, drums=False, drum_map=None, dynamics="coarse", denominators=ALL_DENOMINATORS, sharp="#",
                with_tempo=True, carry=False):
    """MML for one voice between ticks [start, end): tempo changes (unless with_tempo is False: the game has one BPM, written in
    another voice), default length, volume, notes and rests. Notes crossing the boundaries are cut there. carry: this voice holds the
    tempo for every track, so changes after its last note are written too (rests up to each)."""
    inside = [(max(s, start), min(e, end), k, v) for s, e, k, v in events if e > start and s < end]
    level = max(1, min(15, int(round(sum(v for _, _, _, v in inside) / len(inside) * 15)))) if inside else 8
    w = _Writer(drums, drum_map, dynamics, level, denominators, sharp)
    w.length = _default_length(inside, denominators)
    if with_tempo:
        w.tempo([bpm for tick, bpm in tempo if tick <= start][-1])
    if w.length != "4":
        w.emit(f"l{w.length}")
    changes = [(tick, bpm) for tick, bpm in tempo if start < tick < end] if with_tempo else []
    cursor = start

    def advance(to):
        nonlocal cursor
        while changes and changes[0][0] <= to:
            tick, bpm = changes.pop(0)
            if tick > cursor:
                w.rest(tick - cursor)
                cursor = tick
            w.tempo(bpm)
        if to > cursor:
            w.rest(to - cursor)
            cursor = to

    for s, e, key, velocity in inside:
        advance(s)
        if drums and changes and changes[0][0] < e:
            e = changes[0][0]                               # a drum hit ends at a tempo change instead of being tied across it
        # A tempo change inside the note: the note is split there and tied across the change ("c4t100&c8"), so the change lands on time.
        tied = False
        while changes and changes[0][0] < e:
            tick, bpm = changes.pop(0)
            w.note(tick - cursor, key, velocity, tied)
            w.tempo(bpm)
            cursor, tied = tick, True
        w.note(e - cursor, key, velocity, tied)
        cursor = e
    if carry and changes:
        advance(changes[-1][0])
    return w.result(), w.notes


def _pitched_key(key, warnings, where):
    """MIDI key inside o1c-o8c (n12-n96), moved by octaves when outside."""
    moved = key
    while moved < 24:
        moved += 12
    while moved > 108:
        moved -= 12
    if moved != key:
        warnings.add(f"{where}: notes outside o1c-o8c were moved by octaves")
    return moved


def _bpm_at(tempo, tick):
    return [bpm for t, bpm in tempo if t <= tick][-1]


def score(meta, game, dynamics="coarse"):
    """{"game", "title", "about", "scores": [{"track", "source", "drums", "part", "of", "instrument", "mml", "chars": [...], "notes",
    "nodes", "bars": [first, last], "start", "fits"}], "quantized": {...}, "warnings": [...]} for compiled spec metadata. Every score is
    read back with the game's profile before it is returned: MmlError if it holds anything the game would not keep."""
    if game not in GAMES:
        raise MmlError(f"MML game must be one of {', '.join(GAMES)}")
    if dynamics not in DYNAMICS:
        raise MmlError(f"MML dynamics must be one of {', '.join(DYNAMICS)}")
    rules = GAMES[game]
    tempo = _tempo_points(meta["bpm"], meta["tempo"])
    step = 1 if rules["triplets"] else 3                      # bar lines and the end on the profile's smallest note
    bars = [int(round(_tick(b) / step)) * step
            for b in (meta.get("barStarts") or [i * meta["meter"] for i in range(int(meta["loop"] // meta["meter"]) + 1)])]
    bars = sorted(set(bars))
    end = int(round(_tick(meta["loop"]) / step)) * step
    if not bars or bars[-1] < end:
        bars.append(end)
    warnings, scores = set(), []
    moved, total, worst = 0, 0, (0.0, 0.0)                    # notes the grid moved; the largest move (beats, ms)
    drum_map = DRUM_N["lostark" if game == "lostark" else "mabinogi"]
    tempo_written = False
    for t in meta["tracks"]:
        if t.get("mute") or not t["notes"]:
            continue
        notes = []
        for beat, duration, key, velocity in t["notes"]:
            if t["drums"]:
                sound = GM_DRUMS.get(key)
                if sound is None:
                    warnings.add(f"track '{t['name']}': drum key {key} has no kit sound in the game and was left out")
                    continue
                if sound == "snare" and velocity < .4:
                    sound = "ghostSnare"
                notes.append([beat, min(duration, .25), drum_map[sound], velocity])
            else:
                notes.append([beat, duration, _pitched_key(key, warnings, f"track '{t['name']}'"), velocity])
        grid = 3 if t["drums"] else _grid(notes, rules["triplets"])
        for beat, duration, _, _ in notes:
            total += 1
            shift = max(abs(int(round(x * TICKS / grid)) * grid - x * TICKS) / TICKS
                        for x in ((beat,) if t["drums"] else (beat, beat + duration)))
            if shift > 1e-6:
                moved += 1
                worst = max(worst, (shift, shift * 60000 / _bpm_at(tempo, _tick(beat))))
        layered = voices(notes, grid, t["drums"])
        if not layered:
            continue
        groups = [layered[i:i + 3] for i in range(0, len(layered), 3)]
        if len(groups) > 1:
            warnings.add(f"track '{t['name']}': chords of {len(layered)} notes need {len(groups)} scores (three voices each)")
        for g, group in enumerate(groups):
            # "first": one BPM for the whole score, written in the first track's melody (which then carries every change)
            carry = 0 if rules["tempo"] == "first" and not tempo_written and g == 0 else None
            for segment in _segments(group, bars, end, tempo, t["drums"], drum_map, rules, dynamics, carry):
                label = t["name"] if len(groups) == 1 else f"{t['name']} {g + 1}"
                scores.append(dict(segment, track=label, source=t["name"], drums=t["drums"], instrument=suggest_instrument(game, t)))
        tempo_written = True
    parts_of = {}
    for s in scores:
        parts_of.setdefault(s["track"], []).append(s)
    for label, items in parts_of.items():
        for i, s in enumerate(items):
            s["part"], s["of"] = i + 1, len(items)
    if moved:
        why = ("Lost Ark keeps only binary note lengths, so triplets and other odd positions moved to the nearest 64th; "
               if not rules["triplets"] else "")
        warnings.add(f"{moved} of {total} notes moved to the MML time grid (at most {worst[0]:.4g} beat, {worst[1]:.1f} ms): {why}the WAV "
                     "plays the original timing; 'mml --compare' writes a spec that plays the MML as the game reads it")
    if game == "lostark":
        if len(parts_of) > 10:
            warnings.add(f"{len(parts_of)} tracks: Lost Ark has at most 10; merge or drop tracks")
        seconds = _seconds(meta)
        if seconds > 600:
            warnings.add(f"the song is {seconds / 60:.1f} minutes; Lost Ark scores end at 10 minutes")
        if any(s["of"] > 1 for s in scores):
            warnings.add("a Lost Ark score is one song: tracks longer than 5000 characters or 1000 notes were split into parts; "
                         "simplify them (fewer notes, longer default lengths) or keep only the first part")
        if len(tempo) > 1:
            warnings.add("Lost Ark plays one BPM for the whole score: the tempo and its changes are written in the first track's melody "
                         "only")
    return {"game": game, "title": rules["title"], "about": rules["about"], "scores": scores, "warnings": sorted(warnings),
            "quantized": {"notes": moved, "of": total, "maxBeats": round(worst[0], 4), "maxMs": round(worst[1], 1)}}


def _seconds(meta):
    seconds, cursor, current = 0.0, 0.0, meta["bpm"]
    for c in sorted(meta["tempo"], key=lambda c: c["Beat"]):
        seconds += (c["Beat"] - cursor) * 60 / current
        cursor, current = c["Beat"], c["Bpm"]
    return seconds + (meta["loop"] - cursor) * 60 / current


def _nodes(text):
    """Notes as a game counts them, conservatively: every note letter or n code (each tied piece too) and every t and v command."""
    return sum(text.count(c) for c in "abcdefgntv")


def _fits(texts, nodes, rules):
    if rules["parts"] is not None:
        return all(len(text) <= limit for text, limit in zip(texts, rules["parts"]))
    return sum(len(t) for t in texts) <= rules["total"] and nodes <= rules["notes"]


def _check(texts, group, a, b, drums, rules):
    """The written score read back with the game's profile (only lengths the game keeps, no ties on drums, drum parts with n codes
    only); every note must be where the writer meant it. A failure is a bug in the writer, never something to paste into the game."""
    back = read("MML@" + ",".join(texts) + ";", rules)
    for i, (text, voice, notes) in enumerate(zip(texts, group, back["voices"])):
        if drums and re.search(r"[a-g&]", text):
            raise MmlError(f"internal error: drum voice {i + 1} has note letters or ties: {text[:60]}")
        expected = [(round((max(s, a) - a) / TICKS, 6), round((min(e, b) - max(s, a)) / TICKS, 6), k + 12 if drums else k)
                    for s, e, k, _ in voice if e > a and s < b]
        got = [(n[0], n[1], n[2]) for n in notes]
        if drums:
            expected, got = [(x[0], x[2]) for x in expected], [(x[0], x[2]) for x in got]
        if expected != got:
            wrong = next((x, y) for x, y in zip(expected + [None] * len(got), got + [None] * len(expected)) if x != y)
            raise MmlError(f"internal error: voice {i + 1} reads back differently from what was written (expected {wrong[0]}, "
                           f"read {wrong[1]})")


def _segments(group, bars, end, tempo, drums, drum_map, rules, dynamics="coarse", carry=None):
    """The voices as one score, or as consecutive parts split at bar lines where the score is over the game's limits. carry: the voice
    that holds the tempo for every track (games with one BPM), or None (each voice writes its own tempo where the game wants it)."""
    def render(a, b):
        texts, count, nodes = [], 0, 0
        for i, voice in enumerate(group):
            text, n = write_voice(voice, a, b, tempo, drums, drum_map, dynamics, rules["denominators"], rules["sharp"],
                                  with_tempo=rules["tempo"] == "every" or i == carry, carry=i == carry)
            texts.append(text)
            count += n
            nodes += _nodes(text)
        return texts, count, nodes

    out, start_index = [], 0
    while start_index < len(bars) - 1:
        a, best = bars[start_index], None
        lo, hi = start_index + 1, len(bars) - 1
        while lo <= hi:                                          # the longest run of bars that still fits
            mid = (lo + hi) // 2
            texts, count, nodes = render(a, bars[mid])
            if _fits(texts, nodes, rules):
                best, lo = (mid, texts, count, nodes), mid + 1
            else:
                hi = mid - 1
        if best is None:                                         # one bar alone is over the limit: keep it and say so
            best = (start_index + 1,) + render(a, bars[start_index + 1])
        stop, texts, count, nodes = best
        plays = any(re.search(r"[a-gn]", text) for text in texts)
        if plays or (carry is not None and texts[carry].count("t") > 1):    # skip parts with nothing to play (or tempo to carry)
            _check(texts, group, a, bars[stop], drums, rules)
            out.append({"mml": "MML@" + ",".join(texts) + ";", "chars": [len(t) for t in texts], "notes": count, "nodes": nodes,
                        "bars": [start_index + 1, stop], "start": a / TICKS, "fits": _fits(texts, nodes, rules)})
        start_index = stop
    return out


def text_file(result, meta):
    """A plain-text sheet: one block per score, ready to copy into the game."""
    rules = GAMES[result["game"]]
    lines = [f"# {meta['title']} — {result['title']} MML", f"# {result['about']}", "# Paste each MML@...; block into its own score "
             "(or each voice into melody / chord 1 / chord 2).", ""]
    for s in result["scores"]:
        part = f" (part {s['part']}/{s['of']}, bars {s['bars'][0]}-{s['bars'][1]})" if s["of"] > 1 else ""
        size = "+".join(str(c) for c in s["chars"])
        count = f"{s['notes']} notes" + (f" ({s['nodes']} of {rules['notes']} counting ties, t and v)" if rules["notes"] else "")
        lines += [f"## {s['track']}{part} — {s['instrument']} — {size} characters, {count}" + ("" if s["fits"] else " (OVER THE LIMIT)"),
                  s["mml"], ""]
    if result["warnings"]:
        lines += ["# Notes:"] + [f"# - {w}" for w in result["warnings"]]
    return "\n".join(lines) + "\n"


_TOKEN = re.compile(r"[tvo]\d+|l\d+\.?|[<>&]|[a-g][#+-]?\d*\.?|n\d+|r\d*\.?")


def read(text, game=None):
    """{"voices": [[[beat, duration, key, velocity]]], "tempo": [(voice, beat, bpm)]} from an MML@a,b,c; string (ties merged; an n code
    is the MIDI key minus 12). game (a GAMES name or profile) reads strictly, as that game would keep it: unknown text, a length outside
    the game's denominators or a tie between different notes raises MmlError instead of being read anyway."""
    profile = GAMES[game] if isinstance(game, str) else game
    allowed = {token for _, token in _length_table(profile["denominators"])} if profile else None
    body = re.sub(r"^\s*MML@|;\s*$", "", text.strip()).lower()
    voices_out, tempo = [], []
    for index, part in enumerate(body.split(",")):
        part = re.sub(r"\s+", "", part)
        tokens = _TOKEN.findall(part)
        if profile and "".join(tokens) != part:
            raise MmlError(f"voice {index + 1}: text the game does not read: {part[:60]}")
        notes, beat, octave, length, volume, tied = [], Fraction(0), 4, Fraction(1), 8, False   # defaults: o4, l4 (one beat), v8

        def dur(digits, dotted, token):
            if allowed is not None and digits and digits + dotted not in allowed:
                raise MmlError(f"voice {index + 1}: length '{token}' is not one the game keeps ({', '.join(sorted(allowed, key=lambda t: (int(t.rstrip('.')), t)))})")
            d = Fraction(4, int(digits)) if digits else length
            return d * Fraction(3, 2) if dotted else d

        for token in tokens:
            c = token[0]
            if c in "cdefgab" or c == "n":
                if c == "n":
                    key, d = int(token[1:]) + 12, length
                else:
                    m = re.match(r"[a-g]([#+-]?)(\d*)(\.?)", token)
                    key = (octave + 1) * 12 + "c d ef g a b".index(c) + {"": 0, "#": 1, "+": 1, "-": -1}[m.group(1)]
                    d = dur(m.group(2), m.group(3), token)
                if tied and notes and notes[-1][2] == key:
                    notes[-1][1] += d
                else:
                    if tied and profile:
                        raise MmlError(f"voice {index + 1}: a tie joins two different notes before '{token}'")
                    notes.append([beat, d, key, volume / 15])
                beat += d
                tied = False
            elif c == "r":
                m = re.match(r"r(\d*)(\.?)", token)
                beat += dur(m.group(1), m.group(2), token)
                tied = False
            elif c == "l":
                m = re.match(r"l(\d+)(\.?)", token)
                length = dur(m.group(1), m.group(2), token)
            elif c == "o":
                octave = int(token[1:])
            elif c == "v":
                volume = int(token[1:])
            elif c == "t":
                tempo.append((index, float(beat), int(token[1:])))
            elif c == ">":
                octave += 1
            elif c == "<":
                octave -= 1
            elif c == "&":
                tied = True
        voices_out.append([[round(float(b), 6), round(float(d), 6), k, v] for b, d, k, v in notes])
    return {"voices": voices_out, "tempo": tempo}


def parse(text, game=None):
    """Notes [beat, duration, key, velocity] per part of an MML@a,b,c; string (see read; t is ignored for timing)."""
    return read(text, game)["voices"]


# Kit sounds back to General MIDI keys, for the spec that plays the MML.
GM_OF_SOUND = {"kick": 36, "lowTom": 45, "snare": 38, "rimshot": 39, "sidestick": 37, "ghostSnare": 38, "lowMidTom": 47, "highTom": 50,
               "openHat": 46, "closedHat": 42, "pedalHat": 44, "crash": 49, "crashChoke": 55, "ride": 51, "rideBell": 53}
_NOT_IN_GAME_SPEC = ("sections", "patterns", "notes", "tempo", "chords", "keys", "swing", "swingGrid", "meterChanges", "variationSeed",
                     "regions", "masterAutomation")
_NOT_IN_GAME_TRACK = ("transpose", "velocity", "expression", "automation", "ignoreSwing")


def game_spec(result, spec, meta):
    """A spec that plays the exported MML as the game reads it (the final notes, lengths, volumes and tempo, read back strictly), with
    the original spec's instruments and track settings: render it to compare with the original WAV, which plays the original timing."""
    game, rules = result["game"], GAMES[result["game"]]
    kit = {n + 12: sound for sound, n in DRUM_N["lostark" if game == "lostark" else "mabinogi"].items()}
    notes, tempo = {}, {}
    for s in result["scores"]:
        back = read(s["mml"], game)
        for voice in back["voices"]:
            for b, d, k, v in voice:
                if v <= 0:
                    continue
                notes.setdefault(s["source"], []).append([round(b + s["start"], 6), round(d, 6), GM_OF_SOUND[kit[k]] if s["drums"] else k,
                                                         round(v, 4)])
        for _, b, bpm in back["tempo"]:
            tempo[round(b + s["start"], 6)] = bpm
    changes = []
    for beat in sorted(tempo):
        if not changes or changes[-1][1] != tempo[beat]:
            changes.append((beat, tempo[beat]))
    out = {k: v for k, v in spec.items() if k not in _NOT_IN_GAME_SPEC}
    out["title"] = f"{meta['title']} ({rules['title']} MML)"
    out["description"] = (f"plays the {rules['title']} MML as the game reads it (notewright mml --compare); the original spec plays the "
                          "original timing")
    if changes and changes[0][0] == 0:
        out["bpm"] = changes.pop(0)[1]
    if changes:
        out["tempo"] = [{"beat": b, "bpm": v} for b, v in changes]
    out["tracks"] = {}
    for name, t in (spec.get("tracks") or {}).items():
        if name in notes:
            t = {"instrument": t} if isinstance(t, str) else t
            out["tracks"][name] = {k: v for k, v in t.items() if k not in _NOT_IN_GAME_TRACK}
    out["notes"] = {name: {"notes": sorted(n)} for name, n in notes.items()}
    return out
