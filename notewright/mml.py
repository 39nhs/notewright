"""MML export for in-game performance in Mabinogi (PC and mobile) and Lost Ark.

Every spec track becomes one score: up to three single-note voices (melody, chord 1, chord 2), written as MML@melody,chord1,chord2;
— a Mabinogi scroll (one player each) or a Lost Ark track. Chords are spread over the voices (top notes first); voices beyond three
go to an extra score for the same track. Scores longer than the game allows are split at bar lines into consecutive parts.

MML: t tempo, l default length, v volume 0-15, o octave, < > octave down/up, c d e f g a b with # for sharp, r rest, & tie, a length
n = a 1/n note (4/n beats), "." dotted, nN an absolute pitch (o4c = n48 = middle C, MIDI 60). Drums are written as n codes, the only
form both games accept for them (Lost Ark's drum part ignores note letters)."""
import math
import re

TICKS = 48                                      # per beat (quarter note); a whole note = 192
LENGTHS = [(192 // n, str(n)) for n in (1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64)] + \
          [(192 // n * 3 // 2, f"{n}.") for n in (1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48) if (192 // n) % 2 == 0]
LENGTHS.sort(reverse=True)
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
    # name → (title, part limits (melody, chord1, chord2) or a total per score, note limit per score, notes)
    "mabinogi": {"title": "Mabinogi", "parts": (1200, 800, 500), "total": None, "notes": None,
                 "about": "one scroll per player (composition rank 1: melody 1200, chord 1 800, chord 2 500 characters); everyone starts "
                          "together in an ensemble"},
    "mabinogi-mobile": {"title": "Mabinogi Mobile", "parts": (2400, 2400, 2400), "total": None, "notes": None,
                        "about": "one score per player, 2400 characters per part"},
    "lostark": {"title": "Lost Ark", "parts": None, "total": 5000, "notes": 1000,
                "about": "one track each (4 tracks, up to 10 with crystals); per track 5000 characters (melody + chord 1 + chord 2) and "
                         "1000 notes; at most 10 minutes"},
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


def _grid(notes):
    """3 ticks (a 64th) or 4 ticks (a 48th, for triplets): whichever fits the track's note times better."""
    def error(step):
        return sum(abs(x * TICKS / step - round(x * TICKS / step)) for b, d, _, _ in notes for x in (b, b + d))
    return 4 if notes and error(4) < error(3) * .5 else 3


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
        merged[tick] = int(round(min(255, max(32, value))))
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


_BEST = {0: []}


def _lengths(ticks):
    """Length tokens adding up to `ticks`, as few characters as possible (whole notes first for long values)."""
    head = []
    while ticks > 384:
        head.append("1")
        ticks -= 192
    if ticks not in _BEST:
        for t in range(1, ticks + 1):                           # shortest decomposition, built up once and cached
            if t in _BEST:
                continue
            best = None
            for value, token in LENGTHS:
                if value <= t and _BEST.get(t - value) is not None:
                    candidate = [token] + _BEST[t - value]
                    if best is None or (len(candidate), sum(map(len, candidate))) < (len(best), sum(map(len, best))):
                        best = candidate
            _BEST[t] = best
    if _BEST[ticks] is None:
        raise MmlError(f"cannot write {ticks} ticks")
    return head + sorted(_BEST[ticks], key=lambda token: -dict((tok, v) for v, tok in LENGTHS)[token])


DYNAMICS = ("coarse", "full", "none")


class _Writer:
    def __init__(self, drums, drum_map, dynamics="coarse", fixed=None):
        self.drums, self.drum_map, self.dynamics, self.fixed = drums, drum_map, dynamics, fixed
        self.text, self.octave, self.length, self.volume, self.notes = [], 4, "4", 8, 0

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
        for token in _lengths(ticks):
            self.emit("r" if token == self.length else "r" + token)

    def note(self, ticks, key, velocity, tied=False):
        """A note (tied = the continuation of the previous one, after a tempo change: counted once, written with '&')."""
        self.set_volume(velocity)
        if not tied:
            self.notes += 1
        tokens = _lengths(ticks)
        if self.drums:
            n = f"n{key}"
            pieces = []
            for i, token in enumerate(tokens):
                if token != self.length:
                    pieces.append(f"l{token}")
                    self.length = token
                pieces.append(n if i == 0 and not tied else "&" + n)
            self.emit("".join(pieces))
            return
        octave, name = key // 12 - 1, NAMES[key % 12]
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


def _default_length(voice_events):
    """The note length used most often (fewest characters when set with l)."""
    counts = {}
    for start, end, _, _ in voice_events:
        for token in _lengths(end - start):
            counts[token] = counts.get(token, 0) + 1
    return max(counts, key=lambda t: (counts[t] * len(t), -len(t))) if counts else "4"


def write_voice(events, start, end, tempo, drums=False, drum_map=None, dynamics="coarse"):
    """MML for one voice between ticks [start, end): tempo changes, default length, volume, notes and rests. Notes crossing the
    boundaries are cut there."""
    inside = [(max(s, start), min(e, end), k, v) for s, e, k, v in events if e > start and s < end]
    level = max(1, min(15, int(round(sum(v for _, _, _, v in inside) / len(inside) * 15)))) if inside else 8
    w = _Writer(drums, drum_map, dynamics, level)
    w.length = _default_length(inside)
    current = [bpm for tick, bpm in tempo if tick <= start][-1]
    w.tempo(current)
    if w.length != "4":
        w.emit(f"l{w.length}")
    changes = [(tick, bpm) for tick, bpm in tempo if start < tick < end]
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
        # A tempo change inside the note: the note is split there and tied across the change ("c4t100&c8"), so the change lands on time.
        tied = False
        while changes and changes[0][0] < e:
            tick, bpm = changes.pop(0)
            w.note(tick - cursor, key, velocity, tied)
            w.tempo(bpm)
            cursor, tied = tick, True
        w.note(e - cursor, key, velocity, tied)
        cursor = e
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


def score(meta, game, dynamics="coarse"):
    """{"game", "about", "scores": [{"track", "part", "of", "instrument", "mml", "chars": [...], "notes", "bars": [first, last]}],
    "warnings": [...]} for compiled spec metadata."""
    if game not in GAMES:
        raise MmlError(f"MML game must be one of {', '.join(GAMES)}")
    if dynamics not in DYNAMICS:
        raise MmlError(f"MML dynamics must be one of {', '.join(DYNAMICS)}")
    rules = GAMES[game]
    tempo = _tempo_points(meta["bpm"], meta["tempo"])
    bars = [_tick(b) for b in (meta.get("barStarts") or [i * meta["meter"] for i in range(int(meta["loop"] // meta["meter"]) + 1)])]
    end = _tick(meta["loop"])
    if not bars or bars[-1] < end:
        bars.append(end)
    warnings, scores = set(), []
    drum_map = DRUM_N["lostark" if game == "lostark" else "mabinogi"]
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
        grid = 3 if t["drums"] else _grid(notes)
        layered = voices(notes, grid, t["drums"])
        groups = [layered[i:i + 3] for i in range(0, len(layered), 3)]
        if len(groups) > 1:
            warnings.add(f"track '{t['name']}': chords of {len(layered)} notes need {len(groups)} scores (three voices each)")
        for g, group in enumerate(groups):
            for segment in _segments(group, bars, end, tempo, t["drums"], drum_map, rules, dynamics):
                label = t["name"] if len(groups) == 1 else f"{t['name']} {g + 1}"
                scores.append(dict(segment, track=label, instrument=suggest_instrument(game, t)))
    parts_of = {}
    for s in scores:
        parts_of.setdefault(s["track"], []).append(s)
    for label, items in parts_of.items():
        for i, s in enumerate(items):
            s["part"], s["of"] = i + 1, len(items)
    if game == "lostark":
        if len(parts_of) > 10:
            warnings.add(f"{len(parts_of)} tracks: Lost Ark has at most 10; merge or drop tracks")
        seconds = _seconds(meta)
        if seconds > 600:
            warnings.add(f"the song is {seconds / 60:.1f} minutes; Lost Ark scores end at 10 minutes")
        if any(s["of"] > 1 for s in scores):
            warnings.add("a Lost Ark score is one song: tracks longer than 5000 characters or 1000 notes were split into parts; "
                         "simplify them (fewer notes, longer default lengths) or keep only the first part")
    return {"game": game, "title": rules["title"], "about": rules["about"], "scores": scores, "warnings": sorted(warnings)}


def _seconds(meta):
    seconds, cursor, current = 0.0, 0.0, meta["bpm"]
    for c in sorted(meta["tempo"], key=lambda c: c["Beat"]):
        seconds += (c["Beat"] - cursor) * 60 / current
        cursor, current = c["Beat"], c["Bpm"]
    return seconds + (meta["loop"] - cursor) * 60 / current


def _fits(texts, notes, rules):
    if rules["parts"] is not None:
        return all(len(text) <= limit for text, limit in zip(texts, rules["parts"]))
    return sum(len(t) for t in texts) <= rules["total"] and notes <= rules["notes"]


def _segments(group, bars, end, tempo, drums, drum_map, rules, dynamics="coarse"):
    """The voices as one score, or as consecutive parts split at bar lines where the score is over the game's limits."""
    def render(a, b):
        texts, count = [], 0
        for voice in group:
            text, n = write_voice(voice, a, b, tempo, drums, drum_map, dynamics)
            texts.append(text)
            count += n
        return texts, count

    out, start_index = [], 0
    while start_index < len(bars) - 1:
        a, best = bars[start_index], None
        lo, hi = start_index + 1, len(bars) - 1
        while lo <= hi:                                          # the longest run of bars that still fits
            mid = (lo + hi) // 2
            texts, count = render(a, bars[mid])
            if _fits(texts, count, rules):
                best, lo = (mid, texts, count), mid + 1
            else:
                hi = mid - 1
        if best is None:                                         # one bar alone is over the limit: keep it and say so
            texts, count = render(a, bars[start_index + 1])
            best = (start_index + 1, texts, count)
        stop, texts, count = best
        if any(any(c in "abcdefgn" for c in text) for text in texts):    # skip parts with nothing to play
            out.append({"mml": "MML@" + ",".join(texts) + ";", "chars": [len(t) for t in texts], "notes": count,
                        "bars": [start_index + 1, stop], "fits": _fits(texts, count, rules)})
        start_index = stop
    return out


def text_file(result, meta):
    """A plain-text sheet: one block per score, ready to copy into the game."""
    lines = [f"# {meta['title']} — {result['title']} MML", f"# {result['about']}", "# Paste each MML@...; block into its own score "
             "(or each voice into melody / chord 1 / chord 2).", ""]
    for s in result["scores"]:
        part = f" (part {s['part']}/{s['of']}, bars {s['bars'][0]}-{s['bars'][1]})" if s["of"] > 1 else ""
        size = "+".join(str(c) for c in s["chars"])
        lines += [f"## {s['track']}{part} — {s['instrument']} — {size} characters, {s['notes']} notes" + ("" if s["fits"] else " (OVER THE LIMIT)"),
                  s["mml"], ""]
    if result["warnings"]:
        lines += ["# Notes:"] + [f"# - {w}" for w in result["warnings"]]
    return "\n".join(lines) + "\n"


def parse(text):
    """Notes [beat, duration, key, velocity] per part of an MML@a,b,c; string (for tests and round trips): t is ignored for timing."""
    body = re.sub(r"^\s*MML@|;\s*$", "", text.strip())
    parts = []
    for part in body.split(","):
        notes, beat, octave, length, volume, i = [], 0.0, 4, 1.0, 8, 0          # defaults: o4, l4 (one beat), v8
        tie_from = None
        while i < len(part):
            c = part[i].lower()
            m = re.match(r"(\d*)(\.?)", part[i + 1:])
            number, dot = m.group(1), m.group(2)

            def dur(num, dotted):
                d = 4 / float(num) if num else length
                return d * 1.5 if dotted else d
            if c in "cdefgab":
                j = i + 1
                acc = 0
                while j < len(part) and part[j] in "#+-":
                    acc += -1 if part[j] == "-" else 1
                    j += 1
                m = re.match(r"(\d*)(\.?)", part[j:])
                d = dur(m.group(1), m.group(2))
                key = (octave + 1) * 12 + "c d ef g a b".index(c) + acc
                if tie_from is not None and notes and notes[-1][2] == key:
                    notes[-1][1] += d
                else:
                    notes.append([beat, d, key, volume / 15])
                beat += d
                tie_from = None
                i = j + len(m.group(0))
                continue
            if c == "n":
                m = re.match(r"(\d+)", part[i + 1:])
                key = int(m.group(1)) + 12
                if tie_from is not None and notes and notes[-1][2] == key:
                    notes[-1][1] += length
                else:
                    notes.append([beat, length, key, volume / 15])
                beat += length
                tie_from = None
                i += 1 + len(m.group(1))
                continue
            if c == "r":
                beat += dur(number, dot)
            elif c == "o":
                octave = int(number)
            elif c == "l":
                length = dur(number, dot)
            elif c == "v":
                volume = int(number)
            elif c == ">":
                octave += 1
            elif c == "<":
                octave -= 1
            elif c == "&":
                tie_from = True
            i += 1 + (len(number) + len(dot) if c in "rolvt" else 0)
        parts.append([[round(b, 6), round(d, 6), k, v] for b, d, k, v in notes])
    return parts
