"""Composition assistance — the engine's MusicComposeAssist in Python: chord voicings with smooth voice leading, bass lines, arpeggios and
drum grooves that follow the meter (grouped odd bars like 7/8 = 2+2+3), plus polymeter helpers. Notes are [beat, duration, key, velocity].
Rhythm patterns are step strings: "x" accent, "o" soft, "-" hold, "." rest."""
import math

MAX_NOTES = 65536
DRUM_KEYS = {"kick": 36, "snare": 38, "closedHat": 42, "openHat": 46, "crash": 49, "tom": 45}
# One 4/4 bar in 16 sixteenth steps: kick, snare, closed hat, open hat. Shuffle swings the off-eighths.
GROOVES = {
    "rock":        ("x.......x.o.....", "....x.......x...", "x.o.x.o.x.o.x.o.", "................"),
    "fourOnFloor": ("x...x...x...x...", "....x.......x...", "x.x.x.x.x.x.x.x.", "..x...x...x...x."),
    "halfTime":    ("x.........o.....", "........x.......", "x.o.x.o.x.o.x.o.", "................"),
    "breakbeat":   ("x.x.......xo....", "....x..o.o..x..o", "x.x.x.x.x.x.x.x.", "................"),
    "metal":       ("xxxxxxxxxxxxxxxx", "....x.......x...", "x...x...x...x...", "................"),
    "shuffle":     ("x.......x.o.....", "....x.......x...", "x.ox.ox.ox.ox.o.", "................"),
}
BASS_STYLES = ("root", "rootFifth", "octave", "walking")
ARP_ORDERS = ("up", "down", "upDown", "downUp", "random", "asVoiced")


class ComposeError(ValueError):
    pass


class _Random:
    """xorshift32, the same sequence as the engine's generator."""

    def __init__(self, seed):
        self.state = (seed or 0x9E3779B9) & 0xFFFFFFFF

    def next(self):
        s = self.state
        s ^= (s << 13) & 0xFFFFFFFF
        s ^= s >> 17
        s ^= (s << 5) & 0xFFFFFFFF
        self.state = s
        return s

    def unit(self):
        return self.next() / 0xFFFFFFFF


def _add(notes, beat, length, key, velocity):
    if 0 <= key <= 127 and length > 0:
        if len(notes) >= MAX_NOTES:
            raise ComposeError("too many generated notes")
        notes.append([round(beat, 6), round(length, 6), int(key), round(max(0.0, min(1.0, velocity)), 4)])


def hits(pattern, step, start, beats):
    """(beat, length, accent) for a rhythm pattern over [start, start + beats); no pattern = one hit for the whole span."""
    if not pattern:
        yield start, beats, 1.0
        return
    if not 1 / 64 <= step <= 16:
        raise ComposeError("rhythm step must be 1/64-16 beats")
    bad = set(pattern) - set("xo-.")
    if bad:
        raise ComposeError(f"rhythm patterns use x (accent), o (soft), - (hold) and . (rest), not {''.join(sorted(bad))}")
    steps = int(math.floor(beats / step + 1e-9))
    for s in range(steps):
        c = pattern[s % len(pattern)]
        if c not in "xo":
            continue
        hold = 1
        while s + hold < steps and pattern[(s + hold) % len(pattern)] == "-":
            hold += 1
        yield start + s * step, hold * step * .95, 1.0 if c == "x" else .6


def voice(steps, low=52, high=76, voices=0):
    """Each chord voiced inside [low, high] with smooth voice leading (the first near the middle of the range) → MIDI keys per step."""
    if high - low < 12:
        raise ComposeError("a voicing range must span at least an octave")
    result, previous = [], None
    for chord, _, _ in steps:
        tones = sorted({(chord.root + t) % 12 for t in chord.intervals})
        count = min(max(voices, 2), 6) if voices > 0 else len(tones)
        best, best_cost = None, float("inf")
        for inversion in range(len(tones)):
            for bottom in range(low, high + 1):
                if bottom % 12 != tones[inversion]:
                    continue
                stack, k, fits = [bottom], inversion, True
                for _ in range(1, count):
                    k = (k + 1) % len(tones)
                    note = stack[-1] + 1
                    while note % 12 != tones[k]:
                        note += 1
                    stack.append(note)
                    if note > high:
                        fits = False
                        break
                if not fits:
                    continue
                if previous is None:
                    cost = abs((stack[0] + stack[-1]) * .5 - (low + high) * .5)
                else:
                    n = max(len(previous), len(stack))
                    cost = sum(abs(previous[min(i, len(previous) - 1)] - stack[min(i, len(stack) - 1)]) for i in range(n))
                if cost < best_cost - 1e-9:
                    best, best_cost = stack, cost
        if best is None:
            raise ComposeError(f"the range is too small for {chord}")
        result.append(best)
        previous = best
    return result


def chords(steps, low=52, high=76, pattern="", step=.5, velocity=.7, voices=0):
    """Voiced chords, held for the whole chord or played on a rhythm pattern."""
    notes = []
    for (chord, beat, beats), keys in zip(steps, voice(steps, low, high, voices)):
        for at, length, accent in hits(pattern, step, beat, beats):
            for key in keys:
                _add(notes, at, length, key, velocity * accent)
    return notes


def _near(pitch_class, register):
    key = register - (register - pitch_class) % 12
    return key + 12 if register - key > 6 else key


def bass(steps, style="root", register=40, pattern="", step=1.0, velocity=.85):
    """A bass line under the progression (the chord's bass note for slash chords) near the register: root, rootFifth, octave or walking
    (chord tones, then a chromatic approach into the next chord on the last hit)."""
    if style not in BASS_STYLES:
        raise ComposeError(f"bass style must be one of {', '.join(BASS_STYLES)}")
    notes = []
    for i, (chord, beat, beats) in enumerate(steps):
        root = _near(chord.bass, register)
        fifth = root + chord.fifth
        nxt = _near(steps[i + 1][0].bass, register) if i + 1 < len(steps) else root
        line = list(hits(pattern, step, beat, beats))
        for h, (at, length, accent) in enumerate(line):
            if style == "rootFifth":
                key = root if h % 2 == 0 else fifth
            elif style == "octave":
                key = root if h % 2 == 0 else root + 12
            elif style == "walking":
                if h == len(line) - 1 and len(line) > 1:
                    key = nxt + (-1 if nxt > root else 1)
                else:
                    key = root if h == 0 else root + chord.intervals[min(1, len(chord.intervals) - 1)] if h % 3 == 1 else fifth
            else:
                key = root
            _add(notes, at, length, key, velocity * accent)
    return notes


def arpeggio(steps, order="up", rate=.25, octaves=1, gate=.8, low=60, high=84, seed=1, velocity=.7):
    """One note of the voiced chord every `rate` beats across `octaves` octaves."""
    if order not in ARP_ORDERS:
        raise ComposeError(f"arpeggio order must be one of {', '.join(ARP_ORDERS)}")
    if not 1 / 64 <= rate <= 16 or not 1 <= octaves <= 4 or not 0 < gate <= 1:
        raise ComposeError("arpeggio: rate 1/64-16 beats, octaves 1-4, gate 0-1")
    random = _Random(seed)
    notes = []
    for (chord, beat, beats), voiced in zip(steps, voice(steps, low, max(high, low + 12))):
        keys = [k + 12 * o for o in range(octaves) for k in voiced if k + 12 * o <= 127]
        up, down = list(keys), list(reversed(keys))
        inner = lambda xs: xs[1:-1] if len(xs) > 2 else []
        sequence = {"down": down, "upDown": up + inner(down), "downUp": down + inner(up)}.get(order, up)
        for n in range(int(math.floor(beats / rate + 1e-9))):
            key = keys[random.next() % len(keys)] if order == "random" else sequence[n % len(sequence)]
            _add(notes, beat + n * rate, rate * gate, key, velocity * (1 if n % 4 == 0 else .85))
    return notes


def default_grouping(numerator, denominator):
    """How a bar's counts group (the engine's MusicMeter.DefaultGrouping): 7/8 → 2+2+3, 6/8 → 3+3, 4/4 → 1+1+1+1."""
    if denominator <= 4 and numerator <= 4:
        return [1] * numerator
    big = 4 if denominator >= 16 else 2
    if big == 2 and numerator % 3 == 0:
        return [3] * (numerator // 3)
    for threes in range(numerator // 3 + 1):
        if (numerator - threes * 3) % big == 0:
            return [big] * ((numerator - threes * 3) // big) + [3] * threes
    groups, left = [3], numerator - 3
    while left > 0:
        groups.append(min(2, left))
        left -= 2
    return groups


def drums(style, bars, fill_every=4, seed=1, variation=.2):
    """A groove over bars [(start beat, length beats, numerator, denominator, groups or None)]. Plain n/4 bars use the 16-step 4/4 groove;
    other bars (7/8, 5/4 written as 3+2, 13/16 …) accent each count group. A crash starts every phrase of `fill_every` bars and its last
    bar ends in a fill (0 = none)."""
    if style not in GROOVES:
        raise ComposeError(f"groove must be one of {', '.join(GROOVES)}")
    if not 0 <= variation <= 1 or fill_every < 0:
        raise ComposeError("groove: variation 0-1, fill 0 or more bars")
    random = _Random(seed)
    notes = []
    for index, (start, length, numerator, denominator, groups) in enumerate(bars):
        fill = fill_every > 0 and (index + 1) % fill_every == 0
        crash = fill_every > 0 and index % fill_every == 0
        if denominator == 4 and groups is None and length == int(length) and 1 <= length <= 12:
            _groove_bar(notes, style, start, int(length), fill, crash, random, variation)
        else:
            _grouped_bar(notes, style, start, length, 4.0 / denominator, groups or default_grouping(numerator, denominator), fill, crash,
                         random, variation)
    notes.sort(key=lambda n: (n[0], n[2]))
    return notes


def _groove_bar(notes, style, start, beats, fill, crash, random, variation):
    lanes = ("kick", "snare", "closedHat", "openHat")
    fill_from = (beats - 1) * 4 if fill else 10 ** 9
    for step in range(beats * 4):
        beat = start + step * .25 + (.0833 if style == "shuffle" and step % 2 == 1 else 0)
        if step >= fill_from:
            f = step - fill_from
            _add(notes, beat, .2, DRUM_KEYS["tom" if f >= 2 else "snare"], min(1.0, .6 + .1 * f))
            if f == 0:
                _add(notes, beat, .2, 36, .9)
            continue
        for lane, role in enumerate(lanes):
            c = GROOVES[style][lane][step % 16]
            vel = .9 if c == "x" else .45 if c == "o" else 0.0
            if vel == 0 and lane == 1 and step % 2 == 1 and random.unit() < variation * .15:
                vel = .3
            if vel == 0:
                continue
            if lane >= 2:
                vel = max(.2, min(1.0, vel * (1 - variation * .3 + random.unit() * variation * .6) * (1 if step % 4 == 0 else .8)))
            _add(notes, beat, .4 if lane == 3 else .2, DRUM_KEYS[role], vel)
    if crash:
        _add(notes, start, 1, 49, .85)


def _grouped_bar(notes, style, start, length, count, groups, fill, crash, random, variation):
    total = sum(groups)
    hat_step = .5 if count >= 1 else count
    fill_group = len(groups) - 1 if fill else 10 ** 9
    middle, acc = 0, 0
    for g, size in enumerate(groups):
        if acc * 2 >= total:
            middle = g
            break
        acc += size
    at = 0
    hit = lambda role, beat, vel, dur=.2: _add(notes, beat, dur, DRUM_KEYS[role], vel)
    for g, size in enumerate(groups):
        group_start, group_length = start + at * count, size * count
        if g == fill_group:
            n = int(round(group_length / min(count, .25)))
            for f in range(n):
                hit("tom" if f >= n - 2 else "snare", group_start + f * min(count, .25), min(1.0, .6 + .4 * f / max(1, n - 1)))
            hit("kick", group_start, .9)
            at += size
            continue
        odd = g % 2 == 1
        if style == "fourOnFloor":
            hit("kick", group_start, .95)
            if odd:
                hit("snare", group_start, .85)
            if size >= 2:
                hit("openHat", group_start + count * (size // 2), .6, .4)
        elif style == "halfTime":
            if g == 0:
                hit("kick", group_start, .95)
            if g == middle and g > 0:
                hit("snare", group_start, .9)
        elif style == "breakbeat":
            hit("snare" if odd else "kick", group_start, .9 if odd else .95)
            if g == len(groups) - 1 and size >= 2:
                hit("kick", group_start + count, .7)
        elif style == "metal":
            for k in range(size):
                hit("kick", group_start + k * count, .95 if k == 0 else .7)
            if odd:
                hit("snare", group_start, .9)
        else:
            hit("snare" if odd else "kick", group_start, .9 if odd else .95)
            if len(groups) == 1 and size >= 2:
                hit("snare", group_start + count * (size // 2), .9)
        if style != "metal" and size >= 2 and random.unit() < variation * .5:
            hit("snare", group_start + (size - 1) * count, .3)
        t = 0.0
        while t < group_length - 1e-9:
            if not ((style == "shuffle" and 0 < t < group_length - count - 1e-9) or (style == "metal" and t > 0)):
                vel = .85 if t == 0 else .5
                hit("closedHat", group_start + t, max(.2, min(1.0, vel * (1 - variation * .3 + random.unit() * variation * .6))))
            t += hat_step
        at += size
    if crash:
        hit("crash", start, .85, 1)


# ---------- polymeter ----------

def tile(cycle, cycle_beats, start, end, phase_origin=None):
    """Polymeter: a riff of `cycle_beats` repeated back to back over [start, end) regardless of the bar grid. With `phase_origin` the
    cycle keeps counting from that beat (a riff that runs on across sections) instead of restarting at `start`."""
    if cycle_beats < 1 / 64:
        raise ComposeError("a cycle must be at least 1/64 beat")
    if (end - start) / cycle_beats * max(1, len(cycle)) > MAX_NOTES:
        raise ComposeError("too many generated notes")
    origin = start if phase_origin is None else phase_origin
    offset = origin + math.floor((start - origin) / cycle_beats + 1e-9) * cycle_beats
    notes = []
    while offset < end - 1e-9:
        for beat, duration, key, velocity in cycle:
            if beat >= cycle_beats - 1e-9:
                continue
            at = offset + beat
            if start - 1e-9 <= at < end - 1e-9:
                notes.append([round(at, 6), round(min(duration, cycle_beats - beat, end - at), 6), key, velocity])
        offset += cycle_beats
    return notes


def realign(a, b, resolution=1 / 192):
    """Beats after which two cycles start together again: realign(1.25, 4) = 20 (a 5/16 riff against 4/4), realign(3.5, 4) = 28."""
    x, y = int(round(a / resolution)), int(round(b / resolution))
    if x < 1 or y < 1:
        raise ComposeError("cycles must be positive")
    return x // math.gcd(x, y) * y * resolution
