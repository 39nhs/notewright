# Mixing with the render report

You cannot listen, so mix by numbers and ask the user to confirm by ear.

## Targets

| Measure | Where | Good range |
|---|---|---|
| Peak before normalize | `render.peakDb` | below -1 dBFS; `nearFullScale` > 0 means clipping risk |
| Final peak | `render.outputPeakDb` | -1 dBFS with `--normalize -1` (recommended for delivery) |
| Loudness | `render.rmsDb` | about -16…-12 dBFS for pop/EDM after normalize, -22…-16 for orchestral/ambient |
| Dynamics | `sections[].rmsDb` | intros/verses 3–8 dB below choruses; a flat curve sounds monotonous |
| Balance | `stems[].rmsDb` (`--stems`) | see below; a track 20+ dB under the loudest is likely inaudible |
| Voices | `render.droppedNotes` | must be 0 (lower `release`, thin dense parts) |

Typical stem RMS relative to the loudest stem (genre-dependent starting points): kick and bass 0 to -3 dB, snare -2 to -5,
lead/vocal-like melody -2 to -6, chords/keys -6 to -10, pads -10 to -16, hats/percussion -12 to -20, effects quieter still.
For orchestral music: melody instrument loudest, strings pad -4 to -8, harp/arpeggios -6 to -10, timpani short peaks.

`gain` is linear: halving it is -6 dB; × 0.7 ≈ -3 dB; × 1.4 ≈ +3 dB.

## Tools

- **Space and depth:** no reverb. Use `EchoWet` 0.1–0.3 with `EchoSeconds` ≈ a dotted-eighth or quarter of the beat
  (60 / bpm × 0.75 or × 1) and `EchoFeedback` 0.2–0.45; longer `release`; `LowPassHz` on background tracks (pads 2–5 kHz)
  and quieter gain push things back; panning (±0.2–0.6) separates parts of similar range.
- **Clarity:** keep one instrument per register at a time; `HighPassHz` 100–250 on pads/keys clears the low end for bass and kick.
- **Glue/loudness:** master `Compressor: true, ThresholdDb -14…-18, Ratio 2–4, AttackMs 10–30, ReleaseMs 100–250`; then
  `--normalize -1`.
- **Grit/character:** track `drive` 6–18 dB (`Soft` warm, `Hard` aggressive, `Fold` metallic, `Fuzz` broken); master `Drive`
  with `DriveMix` < 1 for parallel saturation; `CrushBits` 6–10 for lo-fi/chiptune.
- **Movement:** `Gate` with `GatePattern` (`"x.x.xx.x"`) for trance/side-chain style chops; glitch `regions` for transitions
  (StutterGlitch / PitchGlitch over the last 1–2 beats before a drop); White/Pink noise regions as risers (`FadeInBeats` ≈
  length).
- **808 slides:** `glide` 0.04–0.12 on the bass track and overlapping notes.

## Iterating

1. Render with `--stems` once to get balance; fix gross level problems (a track 20 dB off, clipping).
2. Then render only the section you are changing (`--section chorus` or `--bars 17-24`) to save time.
3. Finish with a full render with `--normalize -1` and report the final numbers to the user.
