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
  "playbackVersion": 3,              // engine sound version (default 3: every feature below; 2 or 1 only to match an older song)
  "instruments": { "<alias>": <instrument>, ... },
  "tracks":      { "<name>": <track>, ... },
  "patterns":    { "<name>": <pattern>, ... },
  "sections":    [ <section>, ... ],
  "notes":       { "<track>": <pattern> },      // optional free notes at absolute beats (added to section notes)
  "tempo":       [ <tempo change>, ... ],
  "master":      { <mixer fields> },
  "regions":     [ <noise/glitch region>, ... ],
  "output":      { "rate": 48000, "bits": 24, "tail": 2, "normalize": -1 },  // render defaults; CLI flags override
                                                // tail: seconds after the end where only releases/echoes/reverb ring out
  // playback V3 (see "Playback V3 features" below)
  "buses":       { "<name>": <bus>, ... },      // shared effect chains tracks send or route to
  "masterAutomation": { "<target>": [ <point>, ... ] },
  "masterImpulse": "cab.wav",                   // IR on the master (≤ 250 ms WAV)
  "targetLufs": -14,                            // loudness target, reached with the master limiter (turned on for you)
  "swing": 0.2, "swingGrid": 0.5,               // delay every second grid step by swing × grid (0..0.75)
  "variationSeed": 7,                           // seed for notes' "skip" chances
  "keys":   [ {"bar": 17, "key": "C# minor"} ], // key changes (metadata), "chords": [ {"bar": 1, "chord": "i"} ] (metadata)
  "meterChanges": [ {"bar": 9, "numerator": 7, "denominator": 8, "grouping": "2+2+3"} ]   // time signature metadata
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
| `{"synthPreset": "Acid bass"}` | the engine's built-in synth (V3), a preset: `Init`, `Saw lead`, `Square lead`, `Pluck`, `Warm pad`, `Sub bass`, `Acid bass`, `Brass`, `Bell keys`, `Noise sweep`, `Jazz organ`, `Jazz organ perc`, `Rock organ`, `Gospel organ`, `FM e-piano`, `FM bell`, `FM bass`, `Wavetable pad`, `Wavetable vox` |
| `{"synth": {"Osc1Wave": "Saw", "CutoffHz": 1200, ...}}` | the built-in synth with your own settings (C# field names of `MusicSynthSettings`: oscillators incl. `Organ` drawbars, `Fm`, `Wavetable`; filter and envelopes, two LFOs, mod matrix, unison); optional `"wavetable": "table.wav"` |
| `{"vst3": "Surge XT", ...}` | **Windows x64 only**: an installed VST3 instrument (V3); see [VST3 plugins](#vst3-plugins-windows-x64-only) |

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
  "splits": 8,                   // V1/V2: max sample zones (engine parts) this track may use; fewer = fewer parts, more stretching
  "zones": true,                 // V3: the whole track is one engine part with a zone per sample (false = V2-style splitting)
  "mute": false,
  "program": 0,                  // MIDI export General MIDI program (0-127); guessed from the instrument when absent
  "clef": "treble",              // score: treble | bass | treble8vb (guessed from the range)
  "score": true,                 // false leaves the track out of the MusicXML score
  "plugin": { ... }              // VST3 instrument tracks only (Windows x64): per-track parameters/automation, see below
}
```
A track may also be just the alias string: `"pad": "pad"`.

## patterns

A pattern is a reusable block of notes, written in any of three notations (they can be combined in one pattern):

**events** — `"beat duration pitch[,pitch…] [@velocity]"`, separated by `|`, `;` or new lines:
```json
"chords": {"bars": 4, "notes": "0 4 A3,C4,E4 @0.8 | 4 4 F3,A3,C4 | 8 4 C4,E4,G4 | 12 4 G3,B3,D4"}
```
**melody** — sequential `pitch:duration` tokens; `r:1` is a rest; `C4+E4+G4:2` a chord; a pitch without `:duration` reuses
the previous duration; `|` bar lines are ignored. Velocity: a token `@0.7` on its own sets it for the notes after it (until the
next `@`); a suffix `E5:1@0.9` sets it for that note only:
```json
"hook": {"melody": "@0.75 r:0.5 E5:0.5 A5:1@0.95 G5:0.5 E5:1.5 | @0.6 C5:1 D5 E5 r:1"}
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
- `"polymeter": true` (drum grids): each lane loops at its own length instead of all lanes stopping at the longest one, e.g.
  `{"drums": {"kick": "x..x.", "hat": "x.x"}, "step": 0.5, "polymeter": true}` (a 2.5-beat kick figure against a 1.5-beat hat).

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
With `meterChanges`, a section's `bars` follow the meter (two 7/8 bars = 7 beats).

### Generated parts: chords, bass, arpeggio, drum groove

Instead of a pattern, a track can play a generator. It writes ordinary notes (they show up in MIDI, the score and `check`), so use
it for accompaniment and keep writing melodies and hooks yourself.
```jsonc
"play": {
  "pad":   {"chords": "i VI III VII", "rhythm": "x-x.", "step": 1, "range": ["E3", "G4"], "voices": 4},
  "bass":  {"chords": "Am F C G", "as": "bass", "style": "rootFifth", "rhythm": "x.x.", "step": 0.5, "register": "E2"},
  "arp":   {"chords": "Am F C G", "as": "arp", "order": "upDown", "rate": 0.25, "octaves": 2, "range": ["A3", "E5"]},
  "drums": {"groove": "rock", "fill": 4, "variation": 0.2, "seed": 1}
}
```
- `chords`: chord names (`Am F#m7 C/E Bbmaj7 G7 Dsus4 Edim A5`) or numerals in the song `key` (`I V vi IV`, `i bVI bVII`, `V7`,
  `ii7`); `Am:2` lasts two slots, `-` holds the previous chord one more slot. A slot is `per` beats (default one bar). The
  progression repeats to fill the section. Written chords also fill the song's chord track (`chords`), which `check` compares
  melodies against.
- `as`: `chords` (default; voiced with smooth voice leading inside `range`, default E3-E5; `voices` 2-6 = notes per chord),
  `bass` (`style` root / rootFifth / octave / walking, near `register`, default E2) or `arp` (`order` up / down / upDown / downUp /
  random / asVoiced, a note every `rate` beats across `octaves`, `gate` 0-1 = note length share, `range` default C4-C6).
- `rhythm` + `step`: when the notes play — `x` accent, `o` soft, `-` hold, `.` rest, one character per `step` beats
  (no rhythm = held for the whole chord). `velocity`, `transpose`, `octave` as for patterns.
- `groove` (drum tracks): `rock fourOnFloor halfTime breakbeat metal shuffle`. Plain n/4 bars use a 16-step groove; other bars (7/8,
  5/4 grouped 3+2, 13/16 …) accent each count group of the meter (`meterChanges` `grouping`, default 7/8 = 2+2+3). A crash opens
  every `fill` bars and their last bar ends in a fill (0 = none); `variation` 0-1 adds seeded ghost notes and hat dynamics.

### Polymeter

A riff that repeats at its own length, regardless of bars (math rock, minimalism, Afro-Cuban layering):
```jsonc
"lead":  {"pattern": "riff", "cycle": 2.5},                  // a 2.5-beat (5/8) riff over 4/4: meets the downbeat again every 20 beats
"synth": {"pattern": "seq", "cycle": "7/4", "align": "song"} // the cycle runs on across sections instead of restarting
```
- `cycle`: the riff length in beats (default: the pattern's own `beats`); notes past the cycle are cut. Patterns whose `beats` are not
  whole bars already repeat at their length; `cycle` overrides it.
- `align`: `section` (default: restart at each section) or `song` (count from the song start, so the riff continues seamlessly).
- `check` lists every polymeter part and when it lines up with the bar again (`music.stats.polymeter`); end sections on those
  points (or let a fill cover the seam) for a satisfying landing.

## Playback V3 features

Default `playbackVersion` 3 plays everything V2 does the same way and adds the features here; a track or song that uses none
of them sounds exactly as in V2. On V1/V2 these keys are errors.

**Positions and points.** Automation points and placements use the same position keys as everywhere: `bar` (1-based), `beat`
(0-based) or `section` (+ `bar` inside it, + `beat`). A point is `{"bar": 9, "value": -12, "curve": "smooth"}` or `[beat, value, curve]`;
`curve` (`linear` default, `step`, `smooth`) is the shape from that point to the next.

**Track keys** (on any track):

```jsonc
"lead": {
  "automation": {                                   // one lane per target; {"points": [...], "disabled": true} keeps it silent
    "volume": [{"section": "chorus", "value": -12}, {"section": "chorus", "bar": 2, "value": 0, "curve": "smooth"}],   // dB on the gain, -80 = silent
    "lowpass": [[0, 800], [32, 12000, "smooth"]]    // effect targets need "effects" on this track (and the stage turned on)
  },
  "modulators": [                                   // up to 8, on top of the lanes
    {"kind": "lfo", "target": "lowpass", "amount": 1, "wave": "Triangle", "syncBeats": 4},   // or "rateHz"; amount in the target's units
    {"kind": "follower", "target": "volume", "amount": -12, "source": "drums", "attackMs": 5, "releaseMs": 150}  // ducks under another track
  ],
  "sends": [{"bus": "verb", "db": -8, "preFader": false, "automation": [[0, -80], [16, -8]]}],   // post-fader send (dB -80..12)
  "output": "glue",                                 // route this track into a bus instead of the master
  "expression": [{"section": "chorus", "beat": 2, "note": "A5", "bendTo": 2, "bendStart": 0, "bendEnd": 0.5, "vibrato": 0.3},
                 {"bar": 3, "note": "C5", "skip": 0.5}, {"bar": 4, "note": "E5", "every": 2, "offset": 1}],
      // per note, matched by position and the pitch as it sounds (after transpose): bend semitones over beats from the note start,
      // vibrato depth (semitones), "articulation" id, "skip" chance per loop pass, play only every n-th pass ("every"/"offset")
  "audio": [{"file": "take.wav", "bar": 5, "beats": 8, "sourceStart": 0.5, "gainDb": -3, "fadeIn": 0.25, "fadeOut": 1,
             "semitones": 0, "warp": "Tempo", "sourceBpm": 92}],   // recordings on the timeline; warp Off | Tempo | Markers ("markers": [[beat, seconds], ...])
                                                    // a track with only "audio" needs no instrument
  "loop": {"start": 0.4, "end": 1.2, "crossfade": 0.02},   // sustain loop (seconds of each sample): long notes from short recordings
  "sampleShape": {"DecaySeconds": 0.5, "Sustain": 0.4, "Filter": "LowPass", "CutoffHz": 1200, "EnvelopeOctaves": 2},   // sample voice envelope and filter
  "articulations": [{"Id": 1, "Name": "staccato", "StartSeconds": 0.01, "GainDb": -2, "LengthScale": 0.4}],
  "keySwitches": [{"key": "C1", "articulation": 1}, {"key": "D1", "articulation": 0}],   // notes on these keys switch articulation (not played)
  "vibratoRate": 5.5, "ignoreSwing": true,
  "stretchBpm": 92, "stretchGrainMs": 60,           // time-stretch the sample from its recorded tempo (keeps pitch)
  "impulse": "cab.wav"                              // IR cabinet on this track (≤ 250 ms WAV; turns its effects on)
}
```

Automation targets: `volume` (dB), `pan` (-1..1, tracks only), `lowpass` (0..18000 Hz) / `highpass` (0..2000), `echo`, `chorus`, `reverb`
(mix 0..1), `pitch` (semitones -24..24, tracks only), `modulation` (0..1, synth LFO depth, tracks only), `brightness` (synth filter octaves,
tracks only), `gate` (depth, needs `Gate`), `crush` (mix), `drive` (dB, needs `Drive`), `driveMix`, `stutter` (≥ 0.5 repeats, needs
`Stutter`), `width` (0..2), `rotary` (speed 0..1, needs Color `Rotary`), and on the master only `speed` (tape speed 0..2: 0 = stopped —
tape stop / start).

**Buses** (`"buses"`): `{"verb": {"effects": {"Reverb": true, "ReverbMix": 1}, "output": "glue", "pan": 0, "automation": {...}, "impulse": "ir.wav"}}`.
A bus is a shared effect chain: tracks `send` into it or route their whole `output` to it; a bus feeds the master or another bus
(`output`). Up to 16. Typical: one reverb bus all tracks send to; a "glue" compressor bus for drums; ducking with
`"SidechainTrack": "kick"` in a compressor's fields.

**Engine parts.** On V3 a sampled track is one engine part however many samples it uses (`zones`), so the 32-part limit
counts tracks (+ additive noise regions), and `splits` no longer applies.

## tempo

`[{"bar": 17, "bpm": 90, "rampBeats": 8}]` — at bar 17 start a linear ramp from the previous tempo to 90 BPM over 8 beats
(`rampBeats` 0 = instant). Position by `bar` (1-based), `beat` (0-based) or `{"section": "outro", "bar": 1}`.

## master and track effects (mixer fields)

| Field | Range | Notes |
|---|---|---|
| `OutputDb` | -36..30 | output gain |
| `LowPassHz`, `HighPassHz` | 0..18000, 0..2000 | 0 = off |
| `Compressor`, `ThresholdDb`, `Ratio`, `AttackMs`, `ReleaseMs` | bool, -48..0, 1..20, 1..100, 10..1000 | |
| `EchoWet`, `EchoSeconds`, `EchoFeedback` | 0..1, 0.01..2, 0..0.85 | delay |
| `EchoBeats` | 0..8 | V3: echo time in beats (follows the tempo; 0 = `EchoSeconds`) |
| `CrushBits`, `CrushDownsample`, `CrushMix` | 0..16 (0 off), 1..32, 0..1 | bitcrusher |
| `NoiseGate`, `NoiseGateThresholdDb`, `NoiseGateRangeDb`, `NoiseGateHoldMs`, `NoiseGateReleaseMs` | | |
| `Gate`, `GateBeats`, `GateOpen`, `GateDepth`, `GatePattern` | bool, 1/16..4, 0.05..0.95, 0..1, e.g. `"x.xx"` | tempo-synced trance gate |
| `Drive`, `DriveMode`, `DriveDb`, `DriveMix`, `DriveToneHz`, `DriveKeepBassHz`, `DriveTrimDb` | bool, Soft/Hard/Fold/Fuzz, 0..48, 0..1, 0..18000, 0..300, -24..12 | saturation |
| `Amp`, `AmpModel`, `AmpGainDb`, `AmpTightHz`, `AmpBassDb`, `AmpMidDb`, `AmpTrebleDb`, `AmpPresenceDb`, `AmpLevelDb` | bool, Clean/Crunch/HighGain/Bass, 0..60, 0..400, ±12 (tone stack), -36..12 | guitar/bass amp (feed a clean DI-like sound) |
| `Cabinet`, `CabinetMix` | Off/Closed4x12/Open2x12/Combo1x12/Bass4x10, 0..1 | speaker cabinet after the amp |
| `Eq`, `EqLowDb`, `EqLowHz`, `EqMidDb`, `EqMidHz`, `EqMidQ`, `EqHighDb`, `EqHighHz` | bool, ±24, 20..1000, ±24, 100..10000, 0.3..8, ±24, 1000..18000 | 3-band EQ |
| `Chorus`, `ChorusMix`, `ChorusRateHz`, `ChorusDepthMs`, `ChorusDelayMs` | bool, 0..1, 0.05..10, 0..20, 1..30 | stereo chorus |
| `Reverb`, `ReverbMix`, `ReverbSize`, `ReverbDamping`, `ReverbWidth`, `ReverbPreDelayMs` | bool, 0..1, 0..1, 0..1, 0..1, 0..200 | room reverb (order after drive: amp → cabinet → EQ → chorus → reverb) |
| `Limiter`, `LimiterInputDb`, `LimiterCeilingDb`, `LimiterReleaseMs` | V3, master: bool, -24..24, -12..0, 5..2000 | true-peak brick-wall limiter (2 ms look-ahead) instead of the soft limiter |
| `SidechainTrack` | V3: a track name | the compressor reacts to that track (ducking) |
| `Stutter`, `StutterBeats` | V3: bool, 1/32..4 | beat repeat driven by a `stutter` lane |
| `CabinetImpulseMix`, `CabinetImpulseGainDb`, `CabinetImpulseNormalize` | V3: 0..1, ±24, bool | the IR from `impulse` / `masterImpulse` / a bus `impulse` |
| `Color` | V3: object | `{"EqBands": [{"Enabled": true, "Type": 0, "Hz": 350, "Db": -4, "Q": 1.2}], "Phaser": true, "PhaserRateHz": 0.3, "Flanger": true, "Tremolo": true, "TremoloPan": true, "TremoloBeats": 2, "StereoWidth": 1.4, "Multiband": true, "DeEsser": true, "Rotary": true, …}` (C# field names; band `Type` 0 Peak, 1 LowShelf, 2 HighShelf, 3 LowCut, 4 HighCut, 5 Notch) |

V2 fields need `playbackVersion` 2+, V3 fields 3 (`check` warns otherwise). Enum fields take names (`"AmpModel": "Crunch"`).
`"inserts": [...]` in `master`, a track's `effects` or a bus's `effects` adds VST3 effects after the built-in chain (**Windows x64
only**, see below).

## VST3 plugins (Windows x64 only)

**Only on 64-bit Windows** (Claude Code on a Windows computer with the plugins installed). The engine runs each VST3 plugin in a
Windows worker process; **on macOS, Linux, the claude.ai/Cowork sandbox or any other system VST3 cannot be used at all**: `setup`
shows `vst3` as unavailable, `check` warns, `render` stops with an error (`--score-only` still writes MIDI and MusicXML). There, do
not offer VST3: use the built-in synth, samples and the built-in effects (or have the user export the sound to WAV/SFZ, see
instruments.md). AU/AAX plugins and Kontakt libraries cannot be loaded on any system.

On Windows:
- `plugins` lists installed VST3 instruments and effects (name, vendor, class ID) from `C:\Program Files\Common Files\VST3` and
  `%LOCALAPPDATA%\Programs\Common\VST3` (`--roots "D:\VST3;E:\More"` or `NOTEWRIGHT_VST3_PATH` for other folders). The scan is
  cached; `--rescan` after installing plugins. Only installed plugins in these folders load; a spec names a plugin, never a file.
- `plugins --info "Surge XT"` loads one and lists its parameters: `key` (use it in `parameters`/`automation`), current `value`
  (normalized 0-1), `unit`, `steps`. `--preset file.vstpreset --state` prints a preset as a `state` string.

```jsonc
"instruments": {
  "lead": {"vst3": "Surge XT",            // installed name (or its 32-hex-digit class ID); "vendor"/"version" pick one of several
           "state": "",                    // optional full plugin state (base64 from `plugins --info --state`), a sound/preset
           "parameters": {"filter_1_cutoff": 0.45},   // normalized 0-1, by key, name or numeric ID
           "voices": "poly",               // mono = one note at a time (legato lines)
           "transpose": 0, "velocityCurve": 1, "gain": 1, "pan": 0,
           "fallback": "Saw lead",         // built-in synth preset that plays if the plugin cannot run (the report says so)
           "drums": false}                 // true: a drum plugin (drum grids, Drums layer)
},
"tracks": {
  "lead": {"instrument": "lead",
           "plugin": {"parameters": {"filter_1_cutoff": 0.3},                 // per-track overrides of the instrument's settings
                      "automation": {"filter_1_cutoff": [{"bar": 1, "value": 0.2}, {"bar": 9, "value": 0.8, "curve": "smooth"}]}},
           "effects": {"inserts": [{"vst3": "Valhalla Supermassive", "mix": 0.4,
                                    "parameters": {"mix": 1}, "automation": {"feedback": [[0, 0.3], [32, 0.7]]}}]}}
},
"master": {"inserts": [{"vst3": "Pro-L 2", "state": "..."}]}
```
- A VST3 instrument track is one engine part; level, pan, automation, sends, buses and the built-in effects work as on any track.
- `inserts`: at most 4 per chain, each `{"vst3", "vendor", "version", "mix" 0-1, "bypass", "state", "parameters", "automation"}`;
  they run after the built-in effects of that track, bus or the master (before the master limiter).
- Automation points are positions with values 0-1 (curves like other lanes).
- Plugins render offline before the mix (a render takes longer than without them). A plugin that fails to load or crashes is
  reported in `pluginWarnings` and `advice`: the track played its `fallback` or the insert was bypassed — fix it and render again.
- `--unity`: VST3 instruments go to `<title>.plugin-mod.json` (a graze plugin mod; fill in class IDs where only a name was given).

## regions (noise and glitch effects over a time range)

```jsonc
{"kind": "PitchGlitch", "bar": 16, "beats": 2, "Wet": 1, "GlitchRate": 8, "PitchDepth": 12}
```
`kind`: `White Pink Brown` (noise risers: `Gain`, `FadeInBeats`, `FadeOutBeats`, `Seed`) or glitch filters
`SlowingGlitch AcceleratingGlitch PitchGlitch StutterGlitch BitcrushGlitch GateGlitch` (`Wet`, `GlitchRate` 1-24, `PitchDepth`
0-24; they process the mix, `MasterFilter` defaults to true). Position with `bar`/`beat`/`section`, length with `beats`
(or `DurationBeats`). Additive noise regions count toward the 32-part limit.

## What `check` / `render` report

`summary.tracks[].parts` shows how many engine parts each track needs (1 per track on V3); `warnings` lists merged sample zones, notes past the
end, substituted drum sounds. `render` adds `render` (peak/RMS dBFS, voices, dropped notes), `sections[]` (RMS per section),
`stems[]` (with `--stems`) and `advice`. `report.json` also has per-second levels (`perSecond`: [rmsDb, peakDb]).
