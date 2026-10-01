# Song spec reference

A spec is JSON; `//` and `/* */` comments and trailing commas are allowed. Times are in **beats** (quarter notes) unless a
key says `bar`. Bars are 1-based, beats are 0-based. Unknown keys are errors, so typos are caught by `check`.
Working examples: `${CLAUDE_PLUGIN_ROOT}/examples/pop.json` and `orchestral.json`.

```jsonc
{
  "title": "Night Drive",            // also the output file name
  "bpm": 104,                        // 10-60000
  "meter": 4,                        // beats per bar (x/4 time)
  "key": "A minor",                  // score key signature only ("F# major", "Bb", "Em"); notes are always absolute
  "playbackVersion": 2,              // engine sound version; keep 2
  "instruments": { "<alias>": <instrument>, ... },
  "tracks":      { "<name>": <track>, ... },
  "patterns":    { "<name>": <pattern>, ... },
  "sections":    [ <section>, ... ],
  "notes":       { "<track>": <pattern> },      // optional free notes at absolute beats (added to section notes)
  "tempo":       [ <tempo change>, ... ],
  "master":      { <mixer fields> },
  "regions":     [ <noise/glitch region>, ... ],
  "output":      { "rate": 48000, "bits": 24, "tail": 2, "normalize": -1 }   // render defaults; CLI flags override
                                                // tail: seconds after the end where only releases/echoes/reverb ring out
}
```

## instruments

`alias → reference`. A reference is one of:

| Form | Meaning |
|---|---|
| `"synth:pad"` | built-in synth (no download): `sub-bass saw-bass saw-lead square-lead pad epiano organ bell pluck piano-synth`, `drum-kit` |
| `"vsco2:harp"` | VSCO 2 CE orchestral instrument, downloaded on first use (`instruments find` lists ids) |
| `"local:mylib/rhodes"` | an instrument registered with `instruments scan` / `add` |
| `"find:warm pad"` or just `"warm pad"` | best search match (installed instruments win ties) |
| `"file:samples/hit.wav"` | one WAV, played at its pitch for `root` (`{"use": "file:x.wav", "root": "A3"}`; default C4) |
| `"sfz:/path/inst.sfz"` | an SFZ instrument file directly |
| `"dir:/path/folder"` | scan a folder and use the first instrument found |
| `{"use": "...", "velocity": 60, "kind": "drums"}` | dict form: `velocity` picks the sample layer (1-127, default 100); `kind` filters `find:` |

Relative paths are relative to the spec file.

## tracks

```jsonc
"lead": {
  "instrument": "lead",          // alias from "instruments" (or a reference like "synth:pad"); default = the track name
  "layer": "Melody",             // Melody | Bass | Harmony | Drums (engine grouping; drums instruments default to Drums)
  "gain": 0.3,                   // 0-1 linear (default 0.5). Sample-library level offsets are added automatically
  "pan": -0.2,                   // -1 left .. 1 right
  "attack": 0.01, "release": 0.3,// seconds (default: the instrument's)
  "glide": 0.06,                 // 0-2 s: overlapping notes slide (808 slides, portamento leads)
  "drive": 12, "driveMode": "Soft", // per-voice saturation dB 0-48; Soft | Hard | Fold | Fuzz
  "effects": { "EchoWet": 0.2, "LowPassHz": 6000 }, // this track's own mixer chain (same fields as "master")
  "transpose": 0,                // semitones for every note (ignored on drum tracks)
  "velocity": 1.0,               // multiplier for every note's velocity
  "splits": 8,                   // max sample zones (engine parts) this track may use; fewer = fewer parts, more stretching
  "mute": false,
  "program": 0,                  // MIDI export General MIDI program (0-127); guessed from the instrument when absent
  "clef": "treble",              // score: treble | bass | treble8vb (guessed from the range)
  "score": true                  // false leaves the track out of the MusicXML score
}
```
A track may also be just the alias string: `"pad": "pad"`.

## patterns

A pattern is a reusable block of notes, written in any of three notations (they can be combined in one pattern):

**events** — `"beat duration pitch[,pitch…] [@velocity]"`, separated by `|`, `;` or new lines:
```json
"chords": {"bars": 4, "notes": "0 4 A3,C4,E4 @0.8 | 4 4 F3,A3,C4 | 8 4 C4,E4,G4 | 12 4 G3,B3,D4"}
```
**melody** — sequential `pitch:duration` tokens; `r:1` is a rest; `C4+E4+G4:2` a chord; `@0.7` sets velocity;
a pitch without `:duration` reuses the previous duration; `|` bar lines are ignored:
```json
"hook": {"melody": "r:0.5 E5:0.5 A5:1 G5:0.5 E5:1.5 | C5:1 D5 E5 r:1"}
```
**drums** — a step grid per sound; `step` = beats per character (default 0.25 = 16ths; use 1/3 or 1/6 for triplets).
`X` accent (1.0), `x` hit (0.85), `o` soft (0.45), `g` ghost (0.3), `.` `-` `_` rest; spaces and `|` are ignored:
```json
"beat": {"drums": {"kick": "x... .... x.x. ....", "snare": ".... x... .... x...", "hat": "x.o. x.o. x.o. x.o."}}
```

- Pitches: `C4` = MIDI 60 (middle C), `F#3`, `Bb2`, `Ebb4`, or numbers 0-127. Drum names: `kick bd snare sd clap rim sidestick
  hat hh chh hat-closed hat-pedal phh hat-open ohh crash crash2 ride ride-bell china splash tom tom-high tom-mid tom-low
  tom-floor tambourine cowbell shaker triangle` (General MIDI keys; numbers work too).
- Numbers may be fractions: `1/3`, `2+1/3`, `0.75`.
- Velocity is 0-1 (or 1-127).
- Length: `"bars"` or `"beats"`; otherwise the notes' end rounded up to whole bars.
- A plain string is accepted as a pattern: events if it starts with two numbers, otherwise melody.

## sections

```jsonc
{"name": "chorus", "bars": 8, "repeat": 1, "play": {
  "drums": ["beat", "beat", "beat", "fill"],               // list: played in order, then repeated to fill the section
  "bass":  "bassline",                                      // a pattern repeats to fill the section
  "keys":  {"pattern": "chords", "octave": 1, "transpose": 0, "velocity": 0.8, "offset": 0, "repeat": true},
  "lead":  "r:2 E5:1 G5:1"                                  // inline pattern
}}
```
Sections run back to back; the song length is their sum. `repeat` duplicates the section (each copy is a separate section
in the score/markers). Tracks not named in `play` are silent there. `offset` delays the pattern (beats) inside the section.

## tempo

`[{"bar": 17, "bpm": 90, "rampBeats": 8}]` — at bar 17 start a linear ramp from the previous tempo to 90 BPM over 8 beats
(`rampBeats` 0 = instant). Position by `bar` (1-based), `beat` (0-based) or `{"section": "outro", "bar": 1}`.

## master and track effects (mixer fields)

| Field | Range | Notes |
|---|---|---|
| `OutputDb` | -36..30 | output gain |
| `LowPassHz`, `HighPassHz` | 0..18000, 0..2000 | 0 = off |
| `Compressor`, `ThresholdDb`, `Ratio`, `AttackMs`, `ReleaseMs` | bool, -48..0, 1..20, 1..100, 10..1000 | |
| `EchoWet`, `EchoSeconds`, `EchoFeedback` | 0..1, 0.01..2, 0..0.85 | delay; the only space effect (no reverb) |
| `CrushBits`, `CrushDownsample`, `CrushMix` | 0..16 (0 off), 1..32, 0..1 | bitcrusher |
| `NoiseGate`, `NoiseGateThresholdDb`, `NoiseGateRangeDb`, `NoiseGateHoldMs`, `NoiseGateReleaseMs` | | |
| `Gate`, `GateBeats`, `GateOpen`, `GateDepth`, `GatePattern` | bool, 1/16..4, 0.05..0.95, 0..1, e.g. `"x.xx"` | tempo-synced trance gate |
| `Drive`, `DriveMode`, `DriveDb`, `DriveMix`, `DriveToneHz`, `DriveKeepBassHz`, `DriveTrimDb` | bool, Soft/Hard/Fold/Fuzz, 0..48, 0..1, 0..18000, 0..300, -24..12 | saturation |

## regions (noise and glitch effects over a time range)

```jsonc
{"kind": "PitchGlitch", "bar": 16, "beats": 2, "Wet": 1, "GlitchRate": 8, "PitchDepth": 12}
```
`kind`: `White Pink Brown` (noise risers: `Gain`, `FadeInBeats`, `FadeOutBeats`, `Seed`) or glitch filters
`SlowingGlitch AcceleratingGlitch PitchGlitch StutterGlitch BitcrushGlitch GateGlitch` (`Wet`, `GlitchRate` 1-24, `PitchDepth`
0-24; they process the mix, `MasterFilter` defaults to true). Position with `bar`/`beat`/`section`, length with `beats`
(or `DurationBeats`). Additive noise regions count toward the 32-part limit.

## What `check` / `render` report

`summary.tracks[].parts` shows how many engine parts each track needs; `warnings` lists merged sample zones, notes past the
end, substituted drum sounds. `render` adds `render` (peak/RMS dBFS, voices, dropped notes), `sections[]` (RMS per section),
`stems[]` (with `--stems`) and `advice`. `report.json` also has per-second levels (`perSecond`: [rmsDb, peakDb]).
