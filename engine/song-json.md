# song.json — engine input

`song.json` is what `notewright/spec.py` compiles a song spec into and what the renderer (`engine/Renderer/Program.cs`,
`SongLoader`) plays: `renderer render --song song.json --out mix.wav [--samples DIR] [--rate] [--bits] [--start BEAT]
[--length BEATS] [--tail SECONDS] [--normalize DBFS] [--stems DIR] [--report FILE]`. Sample paths are absolute or relative to
`--samples` (default `<song folder>/Samples`). With `--length` ending before `loopBeats`, the renderer drops notes and regions that
start at or after the range end and shortens the ones still sounding to end there (a cut region keeps its fade-in and fades
out at the range end), so the `--tail` holds only releases and effect decay; the song.json itself is not changed. Missing numbers are 0, as with Unity's JsonUtility; unknown fields in `master`, `effects`, `regions` and `tempo` are errors.

`render --unity` writes the same format with relative sample names for the com.graze.music Unity package's
`SongJsonBuilder` (separate project). It matches the format as of the 2026-10-01 split; fields added here later may not exist
there. Plugin-only key: a part's `"stem"` (stem group name for `--stems`; dropped from the Unity export).

## Format

```jsonc
{
  "name": "Song title",
  "source": "optional credit text",
  "bpm": 176, "beatsPerBar": 4, "loopBeats": 360,
  "playbackVersion": 1,             // optional; absent/0 = V1 (MusicPlaybackVersion.Unrecorded), see playback-versions.md
  "tempo":   [ { "Beat": 344, "Bpm": 150, "RampBeats": 6 } ],          // MusicTempoChange[]
  "master":  { "OutputDb": 3, "Compressor": true, "CrushBits": 8, ... },  // MusicMixerSettings (field names as in C#)
  "regions": [ { "Kind": 9, "MasterFilter": true, "StartBeat": 24, "DurationBeats": 8, ... } ], // MusicNoiseRegion[], Kind as int
  "sections": [ { "name": "intro", "beat": 0 } ],
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
      "drive": 12, "driveMode": 1,  // optional track drive dB (playback V2+, 0 = off), mode 0 Soft 1 Hard 2 Fold 3 Fuzz
      "trackEffects": true,         // optional (playback V2+): this part gets its own effect chain ...
      "effects": { "EchoWet": 0.3, "EchoSeconds": 0.3, "CrushBits": 8 },  // ... with the same fields as "master"
      "notes": [0, 0.45, 84, 0.9,  0.5, 0.45, 86, 0.8]   // flattened beat, duration, key, velocity
    }
  ]
}
```

Limits enforced by `MusicCueDefinition.Compile()`: BPM 10–60000, loop ≤ 100,000,000 beats, ≤ 1,000,000 notes per part,
parts + additive noise regions ≤ 32, notes must start inside the loop. Notes beyond the voice limit (1024 for playback V2 and
later, 128 for V1) are dropped and counted in the report (`droppedNotes`). WAVs are streamed to disk (WAV files are limited to 4 GB).

`playbackVersion`: leaving it out plays V1, so an older song.json never changes its sound; the spec compiler always writes
the number (default 2). Unsupported numbers fail the render.

Master drive goes in `master` with the C# field names: `"Drive": true, "DriveMode": 1, "DriveDb": 12, "DriveMix": 1,
"DriveToneHz": 0, "DriveKeepBassHz": 90, "DriveTrimDb": 6` (playback V2+). `OutputDb` accepts -36…+30.

`glide` (playback V2+, 0–2 s): a note that starts while an earlier note of the same part is still held (it started earlier
and ends after this one starts) takes over that voice and slides to the new pitch in that time — the 808 slide. Write slides as
overlapping notes; notes after a gap and notes starting together (chords) start normally. V1 songs ignore it.
