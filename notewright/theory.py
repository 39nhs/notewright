"""Keys, chords, progressions and key detection — the engine's MusicTheory (engine/Runtime/Composing/MusicTheory.cs) in Python, so specs
compile and get checked without the renderer. Pitch classes: 0 = C … 11 = B; MIDI keys: C4 = 60."""
import math
import re

SCALES = {
    "major": (0, 2, 4, 5, 7, 9, 11), "minor": (0, 2, 3, 5, 7, 8, 10), "dorian": (0, 2, 3, 5, 7, 9, 10), "phrygian": (0, 1, 3, 5, 7, 8, 10),
    "lydian": (0, 2, 4, 6, 7, 9, 11), "mixolydian": (0, 2, 4, 5, 7, 9, 10), "locrian": (0, 1, 3, 5, 6, 8, 10),
    "harmonic minor": (0, 2, 3, 5, 7, 8, 11), "melodic minor": (0, 2, 3, 5, 7, 9, 11), "major pentatonic": (0, 2, 4, 7, 9),
    "minor pentatonic": (0, 3, 5, 7, 10), "blues": (0, 3, 5, 6, 7, 10),
}
MODE_NAMES = {"": "major", "major": "major", "maj": "major", "ionian": "major", "m": "minor", "min": "minor", "minor": "minor",
              "aeolian": "minor", "dorian": "dorian", "phrygian": "phrygian", "lydian": "lydian", "mixolydian": "mixolydian",
              "locrian": "locrian", "harmonic minor": "harmonic minor", "harmonicminor": "harmonic minor", "melodic minor": "melodic minor",
              "melodicminor": "melodic minor", "major pentatonic": "major pentatonic", "pentatonic": "major pentatonic",
              "majorpentatonic": "major pentatonic", "minor pentatonic": "minor pentatonic", "minorpentatonic": "minor pentatonic",
              "blues": "blues"}
PITCH_NAMES = ("C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B")

# Chord qualities: suffix → (canonical suffix, intervals above the root).
CHORDS = {
    "": ("", (0, 4, 7)), "maj": ("", (0, 4, 7)), "M": ("", (0, 4, 7)),
    "m": ("m", (0, 3, 7)), "min": ("m", (0, 3, 7)), "-": ("m", (0, 3, 7)),
    "dim": ("dim", (0, 3, 6)), "°": ("dim", (0, 3, 6)), "o": ("dim", (0, 3, 6)),
    "aug": ("aug", (0, 4, 8)), "+": ("aug", (0, 4, 8)),
    "sus2": ("sus2", (0, 2, 7)), "sus4": ("sus4", (0, 5, 7)), "sus": ("sus4", (0, 5, 7)), "5": ("5", (0, 7)),
    "7": ("7", (0, 4, 7, 10)), "maj7": ("maj7", (0, 4, 7, 11)), "M7": ("maj7", (0, 4, 7, 11)), "Δ": ("maj7", (0, 4, 7, 11)),
    "Δ7": ("maj7", (0, 4, 7, 11)), "m7": ("m7", (0, 3, 7, 10)), "min7": ("m7", (0, 3, 7, 10)), "-7": ("m7", (0, 3, 7, 10)),
    "m7b5": ("m7b5", (0, 3, 6, 10)), "ø": ("m7b5", (0, 3, 6, 10)), "ø7": ("m7b5", (0, 3, 6, 10)),
    "dim7": ("dim7", (0, 3, 6, 9)), "°7": ("dim7", (0, 3, 6, 9)), "o7": ("dim7", (0, 3, 6, 9)),
    "add9": ("add9", (0, 4, 7, 14)), "m9": ("m9", (0, 3, 7, 10, 14)), "9": ("9", (0, 4, 7, 10, 14)),
}
# Krumhansl & Kessler (1982) probe-tone profiles.
MAJOR_PROFILE = (6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88)
MINOR_PROFILE = (6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17)


class TheoryError(ValueError):
    pass


def _pitch_length(s):
    if not s or s[0] not in "ABCDEFGabcdefg":
        return 0
    return 2 if len(s) > 1 and s[1] in "#b♯♭" else 1


def _pitch_class(name):
    pc = {"A": 9, "B": 11, "C": 0, "D": 2, "E": 4, "F": 5, "G": 7}[name[0].upper()]
    if len(name) > 1:
        pc += 1 if name[1] in "#♯" else -1
    return pc % 12


class Key:
    """A tonic pitch class and a mode: Key.parse("A minor"), "F# dorian", "Bb", "C#m"."""

    def __init__(self, tonic, mode="major"):
        self.tonic, self.mode = tonic % 12, mode

    @property
    def scale(self):
        return SCALES[self.mode]

    def contains(self, key):
        return (key - self.tonic) % 12 in self.scale

    def snap(self, key):
        """The nearest scale note (ties go down)."""
        for d in range(12):
            for candidate in (key - d, key + d):
                if self.contains(candidate):
                    return candidate
        return key

    def __str__(self):
        return f"{PITCH_NAMES[self.tonic]} {self.mode}"

    def __eq__(self, other):
        return isinstance(other, Key) and (self.tonic, self.mode) == (other.tonic, other.mode)

    @staticmethod
    def parse(text):
        s = (text or "").strip()
        n = _pitch_length(s)
        if n == 0:
            raise TheoryError(f"unknown key '{text}' (e.g. \"A minor\", \"F# dorian\", \"Bb\", \"C#m\")")
        mode = MODE_NAMES.get(s[n:].strip().lower().replace("-", " ").replace("_", " "))
        if mode is None:
            raise TheoryError(f"unknown key '{text}': modes are {', '.join(sorted(set(MODE_NAMES.values())))}")
        return Key(_pitch_class(s[:n]), mode)


class Chord:
    def __init__(self, root, suffix="", bass=None):
        self.root, self.suffix = root % 12, CHORDS[suffix][0]
        self.intervals = CHORDS[suffix][1]
        self.bass = self.root if bass is None else bass % 12

    @property
    def pitch_classes(self):
        return sorted({(self.root + i) % 12 for i in self.intervals})

    @property
    def fifth(self):
        return 6 if self.suffix in ("dim", "m7b5", "dim7") else 8 if self.suffix == "aug" else 7

    def __str__(self):
        return PITCH_NAMES[self.root] + self.suffix + (f"/{PITCH_NAMES[self.bass]}" if self.bass != self.root else "")

    @staticmethod
    def parse_name(token):
        """C, Am, F#m7, Bbmaj7, G7, Dsus4, Edim, C/E, A5 → Chord, or None."""
        n = _pitch_length(token)
        if n == 0 or not token[0].isupper():
            return None
        root, rest, bass = _pitch_class(token[:n]), token[n:], None
        if "/" in rest:
            rest, b = rest.split("/", 1)
            if not b or _pitch_length(b) != len(b):
                return None
            bass = _pitch_class(b)
        return Chord(root, rest, bass) if rest in CHORDS else None


_NUMERALS = (("vii", 6), ("iii", 2), ("vi", 5), ("iv", 3), ("ii", 1), ("v", 4), ("i", 0))


def _roman(token, key):
    i, shift = 0, 0
    while i < len(token) and token[i] in "b#♭♯":
        shift += 1 if token[i] in "#♯" else -1
        i += 1
    rest = token[i:]
    for numeral, degree in _NUMERALS:
        if rest[:len(numeral)].lower() != numeral:
            continue
        written, suffix = rest[:len(numeral)], rest[len(numeral):]
        if not (written.isupper() or written.islower()):
            return None
        upper = written.isupper()
        diatonic = key is not None and len(key.scale) == 7 and shift == 0
        tonic = key.tonic if key is not None else 0
        root = tonic + (key.scale[degree] if diatonic else (0, 2, 4, 5, 7, 9, 11)[degree] + shift)
        special = {"°": "dim", "o": "dim", "dim": "dim", "°7": "dim7", "o7": "dim7", "ø": "m7b5", "ø7": "m7b5", "+": "aug"}
        if suffix in special:
            return Chord(root, special[suffix])
        quality = {"": "" if upper else "m", "7": "7" if upper else "m7", "maj7": "maj7", "9": "9" if upper else "m9", "sus2": "sus2",
                   "sus4": "sus4", "sus": "sus4", "5": "5", "add9": "add9"}.get(suffix)
        return None if quality is None else Chord(root, quality)
    return None


def progression(text, key=None, beats_per_chord=4.0, start=0.0):
    """'Am F C G', 'I V vi IV', 'Am:2 F G' (':n' = n slots), '-' extends the previous chord → [(chord, beat, beats)]."""
    steps, beat = [], start
    for raw in re.split(r"[\s|,]+", (text or "").strip()):
        if not raw:
            continue
        if raw == "-":
            if not steps:
                raise TheoryError("a progression cannot start with '-'")
            chord, at, length = steps[-1]
            steps[-1] = (chord, at, length + beats_per_chord)
            beat += beats_per_chord
            continue
        token, slots = raw, 1.0
        if ":" in raw:
            token, n = raw.split(":", 1)
            try:
                slots = float(n)
            except ValueError:
                raise TheoryError(f"bad chord length '{raw}'") from None
            if not 0 < slots <= 64:
                raise TheoryError(f"bad chord length '{raw}'")
        chord = Chord.parse_name(token) or _roman(token, key)
        if chord is None:
            raise TheoryError(f"unknown chord '{raw}' (names like Am, F#m7, C/E or numerals like I, vi, bVII, V7 in the song's key)")
        steps.append((chord, beat, slots * beats_per_chord))
        beat += slots * beats_per_chord
    if not steps:
        raise TheoryError("empty chord progression")
    return steps


def _correlation(weights, profile, tonic):
    mw, mp = sum(weights) / 12, sum(profile) / 12
    num = dw = dp = 0.0
    for i in range(12):
        a, b = weights[(i + tonic) % 12] - mw, profile[i] - mp
        num, dw, dp = num + a * b, dw + a * a, dp + b * b
    return 0.0 if dw <= 0 or dp <= 0 else num / math.sqrt(dw * dp)


def detect_key(notes):
    """(Key, confidence -1..1) from [beat, duration, key, velocity] notes (leave drums out). Major and minor only."""
    weights = [0.0] * 12
    for _, duration, key, velocity in notes:
        if duration > 0 and velocity > 0:
            weights[key % 12] += duration * velocity
    if not any(weights):
        return None, 0.0
    best, confidence = None, -2.0
    for tonic in range(12):
        for mode, profile in (("major", MAJOR_PROFILE), ("minor", MINOR_PROFILE)):
            r = _correlation(weights, profile, tonic)
            if r > confidence + 1e-12:
                best, confidence = Key(tonic, mode), r
    return best, confidence


def name_chord(pitch_classes, bass=None):
    """The chord that best explains a set of pitch classes (weights: {pc: weight}); None when nothing fits."""
    best, score = None, 0.0
    total = sum(pitch_classes.values()) or 1.0
    for root in range(12):
        for suffix in ("", "m", "7", "maj7", "m7", "dim", "sus4", "sus2", "5", "aug", "m7b5"):
            tones = {(root + i) % 12 for i in CHORDS[suffix][1]}
            covered = sum(w for pc, w in pitch_classes.items() if pc in tones) / total
            missing = sum(1 for t in tones if pitch_classes.get(t, 0) <= 0) / len(tones)
            s = covered - .5 * missing - .05 * (len(tones) - 3) + (.05 if bass is not None and bass % 12 == root else 0)
            if s > score + 1e-9:
                best, score = Chord(root, suffix), s
    return best if score >= .5 else None
