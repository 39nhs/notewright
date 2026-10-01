"""Built-in instruments synthesized deterministically on first use (no download): melodic voices and a GM-mapped drum kit.
Files go to <home>/synth/v1/. Bump VERSION when a sound changes so old renders keep their files."""
import math
import random
from pathlib import Path

from . import paths, wavinfo

VERSION = "v1"
RATE = 44100

# GM percussion keys used by the kit (and by drum names in specs).
DRUM_KEYS = {"kick": 36, "rim": 37, "snare": 38, "clap": 39, "tom-floor": 41, "hat-closed": 42, "tom-low": 45, "hat-pedal": 44,
             "hat-open": 46, "tom-mid": 47, "crash": 49, "tom-high": 50, "ride": 51}


def _table(harmonics, size=2048):
    """One period of a waveform from (harmonic, amplitude) pairs."""
    return [sum(a * math.sin(2 * math.pi * h * i / size) for h, a in harmonics) for i in range(size)]


def _osc(table, freq, seconds, detune_cents=(0,), phase_seed=0):
    n = int(seconds * RATE)
    size = len(table)
    rng = random.Random(phase_seed)
    voices = [(freq * 2 ** (c / 1200), rng.random() * size) for c in detune_cents]
    out = [0.0] * n
    for f, phase in voices:
        step = f * size / RATE
        for i in range(n):
            p = phase + i * step
            k = int(p) % size
            frac = p - int(p)
            out[i] += table[k] * (1 - frac) + table[(k + 1) % size] * frac
    return out


def _saw_harmonics(freq, top=11000):
    return [(h, 1 / h) for h in range(1, max(2, int(top / freq)) + 1)]


def _square_harmonics(freq, top=11000):
    return [(h, 1 / h) for h in range(1, max(2, int(top / freq)) + 1, 2)]


def _env(signal, attack=0.003, decay=None, sustain=1.0, tail=0.05):
    n = len(signal)
    out = []
    for i, s in enumerate(signal):
        t = i / RATE
        a = min(1.0, t / attack) if attack > 0 else 1.0
        d = sustain + (1 - sustain) * math.exp(-t / decay) if decay else 1.0
        r = min(1.0, (n - i) / (tail * RATE))
        out.append(s * a * d * r)
    return out


def _lowpass(signal, cutoff):
    a = 1 - math.exp(-2 * math.pi * cutoff / RATE)
    y, out = 0.0, []
    for s in signal:
        y += a * (s - y)
        out.append(y)
    return out


def _midi_hz(key):
    return 440 * 2 ** ((key - 69) / 12)


def _fm(freq, seconds, ratio, index, index_decay, amp_decay):
    out = []
    for i in range(int(seconds * RATE)):
        t = i / RATE
        mod = index * math.exp(-t / index_decay) * math.sin(2 * math.pi * freq * ratio * t)
        out.append(math.sin(2 * math.pi * freq * t + mod) * math.exp(-t / amp_decay))
    return out


def _pluck(freq, seconds, seed=3):
    rng = random.Random(seed)
    period = max(2, int(RATE / freq))
    buf = [rng.uniform(-1, 1) for _ in range(period)]
    out = []
    for i in range(int(seconds * RATE)):
        k = i % period
        v = buf[k]
        buf[k] = 0.996 * 0.5 * (v + buf[(k + 1) % period])
        out.append(v)
    return out


def _noise(seconds, seed):
    rng = random.Random(seed)
    return [rng.uniform(-1, 1) for _ in range(int(seconds * RATE))]


def _highpass(signal, cutoff):
    low = _lowpass(signal, cutoff)
    return [s - l for s, l in zip(signal, low)]


MELODIC = {
    # id: (root key, seconds, description, tags, generator)
    "sub-bass": (36, 3.0, "Sine sub bass with a touch of 2nd harmonic", ["bass", "sub", "808", "electronic", "sine"],
                 lambda f, s: _env(_osc(_table([(1, 1), (2, .15)]), f, s), .002, 1.2, .55)),
    "saw-bass": (36, 2.5, "Filtered saw bass", ["bass", "synth", "saw", "electronic"],
                 lambda f, s: _env(_lowpass(_osc(_table(_saw_harmonics(f)), f, s), 1800), .002, .6, .4)),
    "saw-lead": (72, 3.0, "Bright detuned saw lead", ["lead", "synth", "saw", "electronic", "bright"],
                 lambda f, s: _env(_osc(_table(_saw_harmonics(f)), f, s, (-7, 0, 7), 1), .004, 1.5, .7)),
    "square-lead": (72, 3.0, "Hollow square lead (chiptune)", ["lead", "synth", "square", "chiptune", "8bit"],
                    lambda f, s: _env(_osc(_table(_square_harmonics(f)), f, s), .002, 2.0, .8)),
    "pad": (60, 6.0, "Warm detuned saw pad (use a long attack)", ["pad", "synth", "warm", "ambient", "strings"],
            lambda f, s: _env(_lowpass(_osc(_table(_saw_harmonics(f, 6000)), f, s, (-12, -4, 4, 12), 2), 2500), .4, None, 1, .3)),
    "epiano": (60, 3.5, "FM electric piano", ["keys", "electric piano", "epiano", "rhodes", "soft"],
               lambda f, s: _fm(f, s, 1.0, 1.6, .5, 1.4)),
    "organ": (60, 4.0, "Drawbar organ (8' 4' 2')", ["keys", "organ", "drawbar"],
              lambda f, s: _env(_osc(_table([(1, 1), (2, .6), (4, .35), (3, .2)]), f, s), .01, None, 1, .05)),
    "bell": (72, 4.0, "FM bell", ["bell", "mallet", "keys", "chime", "glass"],
             lambda f, s: _fm(f, s, 3.5, 2.5, 1.2, 1.3)),
    "pluck": (60, 2.0, "Karplus-Strong plucked string", ["pluck", "guitar", "harp", "string", "plucked"],
              lambda f, s: _pluck(f, s)),
    "piano-synth": (60, 3.5, "Synthetic piano (simple, use a real sampled piano for realism)", ["piano", "keys"],
                    lambda f, s: _env(_osc(_table([(1, 1), (2, .5), (3, .25), (4, .12), (5, .06)]), f, s), .002, .9, .05)),
}


def _drum(name, seed):
    if name == "kick":
        out, phase = [], 0.0
        for i in range(int(.6 * RATE)):
            t = i / RATE
            phase += 2 * math.pi * (48 + 110 * math.exp(-t * 28)) / RATE
            out.append(math.sin(phase) * math.exp(-t * 7) + (.3 * math.exp(-t * 300) if t < .01 else 0))
        return out
    if name == "snare":
        tone = [math.sin(2 * math.pi * 190 * i / RATE) * math.exp(-i / RATE * 25) for i in range(int(.35 * RATE))]
        noise = _highpass(_noise(.35, seed), 1200)
        return [.5 * a + .8 * b * math.exp(-i / RATE * 14) for i, (a, b) in enumerate(zip(tone, noise))]
    if name == "rim":
        return [math.sin(2 * math.pi * 820 * i / RATE) * math.exp(-i / RATE * 90) for i in range(int(.1 * RATE))]
    if name == "clap":
        noise = _highpass(_noise(.4, seed), 900)
        return [n * sum(math.exp(-max(0, i / RATE - d) * (40 if d < .03 else 12)) * (i / RATE >= d) for d in (0, .011, .022, .033)) / 2
                for i, n in enumerate(noise)]
    if name.startswith("hat") or name in ("crash", "ride"):
        length, decay, cut = {"hat-closed": (.12, 45, 7000), "hat-pedal": (.15, 30, 6000), "hat-open": (.6, 5, 6500),
                              "crash": (2.5, 1.4, 4000), "ride": (2.0, 2.2, 5500)}[name]
        metal = [sum(math.sin(2 * math.pi * f * i / RATE) for f in (3150, 4480, 5370, 6720, 8130)) / 5 for i in range(int(length * RATE))]
        noise = _highpass(_noise(length, seed), cut)
        return [(.6 * a + b) * math.exp(-i / RATE * decay) for i, (a, b) in enumerate(zip(metal, noise))]
    if name.startswith("tom"):
        base = {"tom-floor": 80, "tom-low": 100, "tom-mid": 130, "tom-high": 165}[name]
        out, phase = [], 0.0
        for i in range(int(.7 * RATE)):
            t = i / RATE
            phase += 2 * math.pi * base * (1 + .5 * math.exp(-t * 20)) / RATE
            out.append(math.sin(phase) * math.exp(-t * 6))
        return out
    raise KeyError(name)


def folder() -> Path:
    return paths.home() / "synth" / VERSION


def ensure(sample_path: Path):
    """Generates a built-in sample on demand."""
    if sample_path.exists():
        return
    stem = sample_path.stem
    if stem.startswith("kit-"):
        name = stem[4:]
        wavinfo.write_mono16(sample_path, _drum(name, DRUM_KEYS[name]), RATE)
        return
    root, seconds, _, _, generator = MELODIC[stem]
    wavinfo.write_mono16(sample_path, generator(_midi_hz(root), seconds), RATE)


def entries():
    """Catalog entries for the built-in instruments (files are generated when first used)."""
    base = folder()
    result = []
    for name, (root, _, description, tags, _) in MELODIC.items():
        result.append({"id": "synth:" + name, "name": name, "kind": "melodic", "source": "builtin", "license": "generated (CC0)",
                       "description": description, "tags": tags + ["builtin", "synth"], "attack": .005, "release": .3,
                       "regions": [{"sample": str(base / f"{name}.wav"), "lo": 0, "hi": 127, "key": root, "lovel": 0, "hivel": 127}]})
    result.append({"id": "synth:drum-kit", "name": "drum-kit", "kind": "drums", "source": "builtin", "license": "generated (CC0)",
                   "description": "Synth drum kit on General MIDI keys (kick 36, snare 38, hats 42/44/46, toms, crash 49, ride 51)",
                   "tags": ["drums", "kit", "electronic", "808", "builtin", "synth"], "attack": .001, "release": .1,
                   "regions": [{"sample": str(base / f"kit-{name}.wav"), "lo": key, "hi": key, "key": key, "lovel": 0, "hivel": 127}
                               for name, key in DRUM_KEYS.items()]})
    return result
