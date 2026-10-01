"""Note names, drum names and small parsing helpers shared by specs, SFZ and file-name scanning. C4 = MIDI 60."""
import re
from fractions import Fraction

PITCH_CLASSES = {"c": 0, "d": 2, "e": 4, "f": 5, "g": 7, "a": 9, "b": 11}
NOTE_RE = re.compile(r"^([A-Ga-g])(#{1,2}|b{1,2}|s)?(-?\d)$")

# General MIDI percussion names → keys (several aliases each).
DRUMS = {
    "kick": 36, "bd": 36, "bass-drum": 36, "kick2": 35,
    "rim": 37, "sidestick": 37, "snare": 38, "sd": 38, "clap": 39, "snare2": 40,
    "tom-floor": 41, "floor-tom": 41, "hat": 42, "hh": 42, "hat-closed": 42, "chh": 42, "closed-hat": 42,
    "tom-low": 45, "low-tom": 45, "hat-pedal": 44, "pedal-hat": 44, "phh": 44,
    "hat-open": 46, "ohh": 46, "open-hat": 46, "tom-mid": 47, "mid-tom": 47, "tom": 47,
    "crash": 49, "cymbal": 49, "tom-high": 50, "high-tom": 50, "ride": 51, "china": 52, "ride-bell": 53,
    "tambourine": 54, "splash": 55, "cowbell": 56, "crash2": 57, "shaker": 70, "maracas": 70, "triangle": 81,
}


def note_number(token):
    """'C4' → 60, 'F#3' → 54, 'Bb2' → 46, '60' → 60, 'kick' → 36. Returns None if not a note."""
    token = token.strip()
    if re.fullmatch(r"-?\d+", token):
        return int(token)
    m = NOTE_RE.match(token)
    if m:
        letter, accidental, octave = m.groups()
        pc = PITCH_CLASSES[letter.lower()]
        if accidental:
            pc += accidental.count("#") + (1 if accidental == "s" else 0) - accidental.count("b")
        return (int(octave) + 1) * 12 + pc
    return DRUMS.get(token.lower())


def name(key):
    return ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"][key % 12] + str(key // 12 - 1)


def number(token):
    """'1.5', '3/4', '2+1/3' → float."""
    token = token.strip()
    total = Fraction(0)
    for piece in token.split("+"):
        total += Fraction(piece)
    return float(total)
