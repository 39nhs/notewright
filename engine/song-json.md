# song.json — engine input

`song.json` is what `notewright/spec.py` compiles a song spec into and what the renderer (`engine/Renderer/Program.cs`,
`SongLoader`) plays: `renderer render --song song.json --out mix.wav [--samples DIR] [--rate] [--bits] [--start BEAT]
[--length BEATS] [--tail SECONDS] [--normalize DBFS] [--stems DIR] [--jobs N] [--report FILE]`. Sample paths are absolute or relative to
`--samples` (default `<song folder>/Samples`). With `--length` ending before `loopBeats`, the renderer drops notes and regions that
start at or after the range end and shortens the ones still sounding to end there (a cut region keeps its fade-in and fades
out at the range end), so the `--tail` holds only releases and effect decay; the song.json itself is not changed. The cut also applies to pattern placements and audio regions; `targetLufs` is matched on the whole song before it. Missing numbers are 0; unknown fields in settings objects (`master`, `effects`, `regions`, `tempo`, `synth`, `sampleShape`, …) are errors.

`render --unity` writes the same format with relative sample names for the com.graze.music Unity package's
`SongJsonBuilder` (separate project). This engine is com.graze.music's playback V3 as of its part 1 completion (2026-10-10), without the Unity-only parts listed at
the end; the Unity package may add fields later that this renderer does not read. Plugin-only key: a part's `"stem"` (stem group name for `--stems`; dropped from the Unity export).

`--stems DIR` writes one WAV per stem group with that group's parts soloed, plus `regions.wav` (`_regions.wav` if a group is
called `regions`) with the regions that play as sound; `MasterFilter` regions stay in every stem, as they process the mix.
Stems are scaled by the mix's `--normalize` gain and render on up to `--jobs` threads (default min(4, processors)), each with its own engine.
The report's `peakDb`/`rmsDb` are before that gain; `outputPeakDb`/`outputRmsDb` are the written mix.

## Format

```jsonc
{
  "name": "Song title",
  "source": "optional credit text",
  "bpm": 176, "beatsPerBar": 4, "loopBeats": 360,
  "meter": [ { "beat": 16, "numerator": 7, "denominator": 8, "grouping": "2+2+3" } ],  // optional time-signature changes (bar grid only)
  "key": "A minor",                // optional key (metadata, never affects playback)
  "keys": [ { "beat": 64, "key": "C# minor" } ],          // optional key changes after "key" (metadata; engine CurrentKey)
  "chords": [ { "beat": 0, "chord": "i" }, { "beat": 4, "chord": "F/A" } ],  // optional chord track: Roman numerals in the key at that beat, or names
  "swing": 0.33, "swingGrid": 0.5,  // optional swing: every second grid step (beats) is delayed by swing × grid (0..0.75); absent = straight
  "targetLufs": -14,                // optional loudness target: the renderer sets the master limiter input to reach it (needs "Limiter")
  "variationSeed": 7,               // optional: seed of the notes' "skip" draws (expression entries: skip chance, every / offset = every n-th pass)
  "playbackVersion": 1,             // optional; absent/0 = V1 (MusicPlaybackVersion.Unrecorded), see playback-versions.md
  "tempo":   [ { "Beat": 344, "Bpm": 150, "RampBeats": 6 } ],          // MusicTempoChange[]
  "master":  { "OutputDb": 3, "Compressor": true, "CrushBits": 8, ... },  // MusicMixerSettings (field names as in C#)
  "regions": [ { "Kind": 9, "MasterFilter": true, "StartBeat": 24, "DurationBeats": 8, ... } ], // MusicNoiseRegion[], Kind as int
  "patterns": [ { "name": "Riff", "length": 2, "notes": [0, 0.5, 60, 0.8,  1, 0.5, 63, 0.6] } ],  // reusable patterns (notes relative to the pattern start)
  "sections": [ { "name": "intro", "beat": 0 } ],   // section markers (ones at/after loopBeats are skipped; never affect playback)
  "parts": [
    {
      "name": "Lead-80",            // also InstrumentId
      "stem": "lead",               // plugin only: stem group for --stems (default: the name)
      "sample": "Lead-80.wav",      // absolute path, or a file in the --samples folder (mono or stereo PCM/float WAV)
      "layer": "Melody",            // Melody | Bass | Harmony | Drums
      "root": 80,                   // MIDI key the sample was recorded at
      "gain": 0.17, "pan": 0.1, "attack": 0.003, "release": 0.08,
      "playbackVersion": 0,         // optional track pin; 0 = follow the song
      "glide": 0.08,                // optional legato glide seconds (playback V2+), 0 = off; see below
      "stretchBpm": 100,            // optional (playback V3+): time stretch — BPM the sample was recorded at (0 = off), keeps pitch
      "stretchGrainMs": 60,         // optional grain length 20–200 ms
      "fallbackVoice": "Bell",      // optional oscillator for a part without sample (V1/V2 for a synth part): Bell | Bass | Pad | Kick
      "synth": { "Enabled": true, "Osc1Wave": "Saw", "CutoffHz": 1200, "EnvelopeOctaves": 3 }, // optional (playback V3+): built-in synth, C# field names
      "synthPreset": "Acid bass",   // or a preset name instead of "synth"; a synth part needs no "sample"
                                    // organ: "synth": { "Enabled": true, "Osc1Wave": "Organ", "Drawbars": "888000000" }; rotary: "master"/"effects" "Color": { "Rotary": true }
                                    // FM: "Osc1Wave": "Fm", "FmRatio": 3.5, "FmIndex": 1.5, "FmEnvelope": 4; wavetable: "Osc1Wave": "Wavetable", "Wavetable": "Vocal", "WavetablePosition": 0.3
      "wavetable": "table.wav",     // optional: custom wavetable for the Wavetable waveform (frames of 2048 samples, in --samples)
      "drive": 12, "driveMode": 1,  // optional track drive dB (playback V2+, 0 = off), mode 0 Soft 1 Hard 2 Fold 3 Fuzz
      "slices": [0, 0.42, 0.9],     // optional (playback V2+) slice sampler: slice start seconds; key root + i plays slice i
      "variants": [                 // optional (playback V2+): more recordings of the same root note (not with slices)
        { "sample": "Lead-80-rr2.wav" },                                   // minVelocity 0: round robin with "sample"
        { "sample": "Lead-80-hard.wav", "minVelocity": 0.7, "gainDb": -3 } // velocity layer for notes >= 0.7
      ],
      "loopStart": 0.45, "loopEnd": 1.0, "loopCrossfade": 0.02,   // optional (playback V3+): sustain loop of "sample", seconds (loopEnd 0 = off)
      "zones": [                    // optional (playback V3+): multi-zone sampler, "sample" not needed (not with slices/variants); zones take "loopStart"/"loopEnd" too
        { "sample": "Piano-C3.wav", "root": 48, "low": 0, "high": 54 },     // root default 60, high absent/0 = 127
        { "sample": "Piano-C4.wav", "root": 60, "low": 55, "high": 127 },
        { "sample": "Piano-C4-ff.wav", "root": 60, "low": 55, "high": 127, "minVelocity": 0.75, "gainDb": 1 }, // loud layer
        { "sample": "Piano-C4-mute.wav", "root": 60, "low": 0, "high": 127, "articulation": 1, "start": 0.01 } // articulation 1's recording, starting 10 ms in
      ],
      "trackEffects": true,         // optional (playback V2+): this part gets its own effect chain ...
      "effects": { "EchoWet": 0.3, "EchoSeconds": 0.3, "CrushBits": 8 },  // ... with the same fields as "master"
      "automation": [               // optional (playback V3+): track automation lanes
        { "target": "volume", "points": [0, -30, 0,  16, 0, 2] },   // beat, value, curve (0 linear, 1 step, 2 smooth)
        { "target": "pan", "points": [0, -1, 0,  32, 1, 0], "disabled": false }
      ],
      "modulators": [               // optional (playback V3+): LFOs and envelope followers on top of the lanes (at most 8)
        { "kind": "lfo", "target": "lowpass", "amount": 1.5, "wave": "Triangle", "syncBeats": 4 },  // or "rateHz"; "phase" 0..1
        { "kind": "follower", "target": "volume", "amount": -12, "source": "Kick", "attackMs": 5, "releaseMs": 150 }
      ],
      "placements": [ { "pattern": "Riff", "start": 8, "transpose": 12, "repeats": 2 } ],  // optional: patterns this part plays (with its notes)
      "output": "Glue",             // optional (V3+): mix bus this part feeds instead of the master
      "ignoreSwing": true,          // optional: this part plays straight when the song swings
      "expression": [ { "beat": 0.5, "key": 86, "bendTo": 2, "bendStart": 0.25, "bendEnd": 0.5, "vibrato": 0.3, "articulation": 1, "skip": 0.5, "every": 4, "offset": 3 } ],  // optional (V3+): per-note
                                    // bendFrom/bendTo semitones between bendStart/bendEnd beats after the note start, vibrato depth; matched to a note by beat and key
      "vibratoRate": 5.5,           // optional per-note vibrato Hz
      "regions": [ { "sample": "loop.wav", "start": 8, "length": 16, "sourceStart": 0.5, "gainDb": -3, "fadeIn": 0.5, "fadeOut": 1, "semitones": 0,
                     "warp": "Tempo", "sourceBpm": 150, "markers": [0, 0.5, 4, 2.1] } ],  // optional (V3+) audio regions; markers = beat, seconds pairs (warp "Markers")
      "sampleShape": { "DecaySeconds": 0.4, "Sustain": 0.3, "Filter": "LowPass", "CutoffHz": 600, "EnvelopeOctaves": 3, "VelocityOctaves": 2 },  // optional (V3+) sample voice shape
      "articulations": [ { "Id": 1, "Name": "Palm mute", "StartSeconds": 0.004, "GainDb": -3, "LengthScale": 0.3 } ],  // optional (V3+) playing techniques
      "keySwitches": [ { "Key": 24, "Articulation": 1 }, { "Key": 25, "Articulation": 0 } ],  // optional: notes on these keys switch the articulation (not played)
      "sends": [ { "bus": "Verb", "db": -6, "preFader": false,
        "automation": { "target": "volume", "points": [0, -80, 1,  16, -6, 2,  32, 0, 0] } } ], // optional lane replaces db (-80..+12)
      "notes": [0, 0.45, 84, 0.9,  0.5, 0.45, 86, 0.8]   // flattened beat, duration, key, velocity
    }
  ],
  "masterAutomation": [ { "target": "volume", "points": [344, 0, 0,  360, -80, 0] } ],  // optional (V3+): fade-out
  "buses": [                                             // optional (V3+): mix buses (shared reverb, group compressor)
    { "name": "Verb", "output": "Glue", "pan": 0, "effects": { "Reverb": true, "ReverbMix": 1 },
      "automation": [ { "target": "reverb", "points": [0, 0.1, 0,  32, 0.9, 0] } ] },
    { "name": "Glue", "effects": { "Compressor": true, "ThresholdDb": -18, "SidechainTrack": "Kick" } }   // no output = master; ducks under the Kick part
  ]
}
```

Limits enforced by `MusicCueDefinition.Compile()`: BPM 10–60000, loop ≤ 100,000,000 beats, ≤ 1,000,000 notes per part,
parts + additive noise regions ≤ 32, notes must start inside the loop. Notes beyond the voice limit (1024 for playback V2 and
later, 128 for V1) are dropped and counted in the report (`droppedNotes`). WAVs are streamed to disk (WAV files are limited to 4 GB).

`automation` / `masterAutomation` (playback V3+). Track-only expression targets: `"pitch"` (semitones -24…24, bends every voice),
`"modulation"` (0…1, scales the built-in synth's LFO depth), `"brightness"` (octaves -8…8 on the synth filter). `target` is `"volume"` (dB added to the part gain or the final output,
-80…+24, -80 = silent), `"pan"` (-1…1, replaces the part pan; parts only), `"lowpass"` (0…18000 Hz) / `"highpass"` (0…2000 Hz;
0 = filter off), `"echo"`, `"chorus"`, `"reverb"` (mix 0…1), `"gate"` (gate depth 0…1, needs `Gate`), `"crush"` (crusher mix 0…1),
`"drive"` (0…48 dB) / `"driveMix"` (0…1, need `Drive`), `"stutter"` (≥ 0.5 repeats; needs `"Stutter": true`, slice `"StutterBeats"`),
`"width"` (0…2), `"rotary"` (rotary speed 0…1, needs Color `Rotary`). Master only: `"speed"` (tape speed 0…2: tempo and pitch together; 0 = stopped and silent while the clock runs on).
Effect targets drive the part's own chain (`"trackEffects": true`), a bus or the master mixer; chorus/reverb/gate/crush/drive/stutter lanes need that stage on. `points` are flattened beat, value, curve triples
inside the loop. One lane per target; `"disabled": true` keeps the points without playing them. V1/V2 songs ignore lanes.

Bus `automation` is the same array format: every song target except `pan`. Its `volume` adds dB to the bus output after its chain,
and effect targets replace the bus's settings (chorus/reverb require the stage on). A send's `automation` is one object targeting
`volume`, with values -80..+12 dB that replace `db`. It may turn a send on even when `db` is -80. Absent/disabled keeps `db`.
Bus and send points must be inside the song loop.

`playbackVersion`: leaving it out plays V1, so an older song.json never changes its sound; the spec compiler always writes
the number. Unsupported numbers fail the render.

Master drive goes in `master` with the C# field names: `"Drive": true, "DriveMode": 1, "DriveDb": 12, "DriveMix": 1,
"DriveToneHz": 0, "DriveKeepBassHz": 90, "DriveTrimDb": 6` (playback V2+). `OutputDb` accepts -36…+30.

`glide` (playback V2+, 0–2 s): a note that starts while an earlier note of the same part is still held (it started earlier
and ends after this one starts) takes over that voice and slides to the new pitch in that time — the 808 slide. Write slides as
overlapping notes; notes after a gap and notes starting together (chords) start normally. V1 songs ignore it.

V2 tone/space stage in `master` and `effects` (all off by default): `"Eq": true, "EqLowDb": 3, "EqLowHz": 120, "EqMidDb": 4,
"EqMidHz": 1600, "EqMidQ": 1.2, "EqHighDb": -6, "EqHighHz": 7000`, `"Chorus": true, "ChorusMix": .5, "ChorusRateHz": .8,
"ChorusDepthMs": 3, "ChorusDelayMs": 12`, `"Reverb": true, "ReverbMix": .25, "ReverbSize": .6, "ReverbDamping": .5,
"ReverbWidth": 1, "ReverbPreDelayMs": 20`. Order after drive: amp → cabinet → EQ → chorus → reverb.

V2 amp and cabinet in `master` and `effects` (off by default; feed a clean/DI instrument): `"Amp": true, "AmpModel": "HighGain",
"AmpGainDb": 30, "AmpTightHz": 90, "AmpBassDb": 0, "AmpMidDb": 0, "AmpTrebleDb": 0, "AmpPresenceDb": 2, "AmpLevelDb": -6`
(models Clean, Crunch, HighGain, Bass), `"Cabinet": "Closed4x12", "CabinetMix": 1` (Off, Closed4x12, Open2x12, Combo1x12, Bass4x10).

V3 IR cabinet: top-level `"masterImpulse": "cab.wav"`, part `"impulse": "cab.wav"`, or bus `"impulse": "cab.wav"` resolves a file
in the `--samples` folder (or an absolute path). Put `"CabinetImpulseMix": 1`, `"CabinetImpulseGainDb": 0`,
`"CabinetImpulseNormalize": false` in the corresponding `master`/`effects` object. Empty path = no IR. Maximum length: 250 ms;
mono/stereo, loaded PCM. Track IR needs `trackEffects`. V1/V2 and pinned V2 tracks ignore it.

`synth` / `synthPreset` (playback V3+): the part plays the built-in synth. `synth` uses the C# field
names of `MusicSynthSettings` and needs `"Enabled": true`; `synthPreset` takes a `MusicSynthPresets` name. Not both. A synth part
may omit `sample` (V1/V2 then play the fallback voice). `attack`/`release` of 0 or absent default to 0.008 / 0.12 s.

`meter`: time-signature changes; each must be on a bar line of the meter before it (n/d bars last n×4/d beats). They move the bar grid
that transitions and layers align to, never the sound. `"EchoBeats"` in `master`/`effects` (playback V3+) sets the echo time in beats,
following the tempo (0 = `EchoSeconds`).

Color stage (playback V3+) in `master`, part `effects` or bus `effects`: `"Color": { "EqBands": [ { "Enabled": true, "Type": 0, "Hz": 350, "Db": -4, "Q": 1.2 } ],
"Phaser": true, "PhaserRateHz": 0.3, "Flanger": true, "Tremolo": true, "TremoloPan": true, "TremoloBeats": 2, "StereoWidth": 1.4, "Multiband": true, "DeEsser": true, ... }`
(C# field names; `Type` 0 Peak, 1 LowShelf, 2 HighShelf, 3 LowCut, 4 HighCut, 5 Notch). Buses take `"pan"` (-1..1, after the bus volume) and sends `"preFader": true` (the send ignores the track's gain and volume lane).

Master limiter (playback V3+): `"Limiter": true, "LimiterInputDb": 6, "LimiterCeilingDb": -1, "LimiterReleaseMs": 150` in `master`.

`modulators` (playback V3+): per track, at most 8. An `lfo` swings its `target` by ±`amount` in the target's units (dB for volume,
-1..1 pan, octaves for lowpass/highpass/brightness, mix for echo/chorus/reverb, semitones for pitch, 0..1 modulation) with `wave`
(Sine, Triangle, Square, SawUp, SawDown, SampleAndHold) at `rateHz`, or one cycle per `syncBeats` beats (follows the tempo), from
`phase`. A `follower` adds `amount` × the level (0..1) of the track named `source`, with `attackMs`/`releaseMs`. Targets: volume, pan,
lowpass, highpass, echo, chorus, reverb, pitch, modulation, brightness, gate, crush, drive, driveMix, width, rotary (effect targets need
`trackEffects` and the stage on). `"disabled": true` keeps one without playing it.

VST3 plugins (playback V3, **Windows x64 only**; on any other system the renderer stops with an error instead of rendering):
- A part's `"plugin"` makes it a VST3 instrument: `{"name": "Surge XT"}` or `{"classId": "<32 hex digits>"}` (optional `"vendor"`,
  `"version"`), `"state"` (base64 VC2! state), `"parameters"` (`{key: 0..1}`), `"automation"` (`{key: [beat, value, curve, ...]}`),
  `"gain"`, `"pan"`, `"transpose"`, `"velocityCurve"`, `"voices"` (`mono`/`poly`), `"outputs"` (`[{"bus": 1, "instrument": "<part>"}]`:
  extra output buses into other parts). The part's own sound (sample, zones or synth; the Init synth when it has none) plays only
  if the plugin cannot run, and the report lists it in `pluginWarnings`.
- `"Inserts"` in `master`, part `effects` (with `"trackEffects": true`) or bus `effects`: at most 4 VST3 effects after the built-in
  chain, each `{"ClassId" or "Name", "Vendor", "Version", "Mix", "Bypass", "State", "Parameters": [{"Key", "Value", "Points": [{"Beat", "Value", "Curve"}]}]}`.
- The renderer finds plugins in the trusted VST3 folders (`--vst3-roots` / `NOTEWRIGHT_VST3_PATH`, default the standard ones) with the
  worker `engine/Native/PluginHost/graze-plugin-host.exe` (`--plugin-host`); names resolve to class IDs there. A song never names a file.
- The Unity package reads instruments from a plugin mod (`render --unity` writes `<title>.plugin-mod.json`) and `Inserts` by class ID.

Not in this engine (the Unity package has them): live input effects (`liveEffects`), clip-launcher scenes, take lanes and freeze.

