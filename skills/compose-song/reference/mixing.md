# Mixing with the render report

You cannot listen, so mix by numbers and ask the user to confirm by ear.

## Targets

| Measure | Where | Good range |
|---|---|---|
| Peak before normalize | `render.peakDb` | below -1 dBFS; `nearFullScale` > 0 means clipping risk |
| Final peak | `render.outputPeakDb` | -1 dBFS with `--normalize -1` (recommended for delivery) |
| Loudness | `render.outputRmsDb` | about -16…-12 dBFS for pop/EDM after normalize, -22…-16 for orchestral/ambient |
| Dynamics | `sections[].rmsDb` | intros/verses 3–8 dB below choruses; a flat curve sounds monotonous |
| Balance | `stems[].rmsDb` (`--stems`) | see below; a track 20+ dB under the loudest is likely inaudible |

All levels except `outputPeakDb` and `outputRmsDb` (`rmsDb`, `peakDb`, `sections[]`, `stems[]`) are measured before
`--normalize`, so they compare between renders; add `render.gainDb` for the level in the file. Noise and glitch regions that
play as sound get their own stem (`kind: "regions"`, `stems/regions.wav`); track stems hold only their track, so their
levels show the track alone. Regions with `MasterFilter` process the whole mix and so act on every stem.
| Voices | `render.droppedNotes` | must be 0 (lower `release`, thin dense parts) |

**What the mix sounds like (analysis, before `--normalize`):**

| Measure | Where | Reading |
|---|---|---|
| Loudness | `render.lufs` (integrated, BS.1770), `render.truePeakDb` | streaming pop about -14 LUFS, orchestral/ambient -18…-23; true peak under -1 dBTP |
| Frequency balance | `render.bands`, `sections[].bands`, `stems[].bands` | dB in `low` 20-120 Hz, `lowMid` 120-500, `mid` 500-2k, `highMid` 2-6k, `high` 6k+ |
| Stereo width | `sections[].width` | side/mid: 0 = mono, 0.2-0.5 typical, above 0.9 phasey |

Pop/rock/electronic starting points relative to `mid`: `low` +4…+12 dB, `lowMid` -2…+4, `highMid` -14…-6, `high` -24…-10.
Orchestral and acoustic music sit lower in the lows and highs. `advice` turns these into tips: boomy or thin lows, mud
(120-500 Hz), dark or harsh highs, a mono or over-wide image, quiet loudness, true-peak overs, and **masking** — two stems whose
main energy sits in the same band at the same level (fix by high-passing or EQ-cutting the less important one, moving it an
octave, panning them apart or giving them different sections). Each render also reports `changes` against the previous render
of the same range: which levels and bands moved, which tips were resolved and which are new.

Typical stem RMS relative to the loudest stem (genre-dependent starting points): kick and bass 0 to -3 dB, snare -2 to -5,
lead/vocal-like melody -2 to -6, chords/keys -6 to -10, pads -10 to -16, hats/percussion -12 to -20, effects quieter still.
For orchestral music: melody instrument loudest, strings pad -4 to -8, harp/arpeggios -6 to -10, timpani short peaks.

`gain` is linear: halving it is -6 dB; × 0.7 ≈ -3 dB; × 1.4 ≈ +3 dB.

## Tools

- **Space and depth:** one reverb bus (`"buses": {"verb": {"effects": {"Reverb": true, "ReverbMix": 1, "ReverbSize": 0.5–0.8}}}`)
  that tracks `send` to (-12…-4 dB; more for pads and leads, little for bass and kick); echo with `EchoBeats` 0.75 (dotted
  eighth) or 1 and `EchoFeedback` 0.2–0.45; `LowPassHz` on background tracks (pads 2–5 kHz) and quieter gain push things back;
  panning (±0.2–0.6) separates parts of similar range.
- **Clarity:** keep one instrument per register at a time; `HighPassHz` 100–250 on pads/keys clears the low end for bass and kick;
  `Eq` (3-band) or `Color.EqBands` (parametric) to cut mud (250–400 Hz) or harshness (2–5 kHz); duck bass or pads under the kick
  with a compressor whose `SidechainTrack` is the kick track (or a `follower` modulator on volume).
- **Glue/loudness:** master `Compressor: true, ThresholdDb -14…-18, Ratio 2–4, AttackMs 10–30, ReleaseMs 100–250`; then either
  `--normalize -1` or a loudness target: `"targetLufs": -14` (streaming pop) / -16…-20 (orchestral, ambient), which drives the
  master `Limiter` (report: `lufs`, `limiterInputDb`).
- **Grit/character:** track `drive` 6–18 dB (`Soft` warm, `Hard` aggressive, `Fold` metallic, `Fuzz` broken); master `Drive`
  with `DriveMix` < 1 for parallel saturation; `CrushBits` 6–10 for lo-fi/chiptune.
- **Movement:** `Gate` with `GatePattern` (`"x.x.xx.x"`) for trance/side-chain style chops; glitch `regions` for transitions
  (StutterGlitch / PitchGlitch over the last 1–2 beats before a drop); White/Pink noise regions as risers (`FadeInBeats` ≈
  length).
- **808 slides:** `glide` 0.04–0.12 on the bass track and overlapping notes.
- **Changes over time:** `automation` lanes for builds (volume swells, a `lowpass` opening over 8 bars into the drop, more
  `reverb` in a bridge), `modulators` for motion (a slow LFO on a pad's filter or pan), `masterAutomation` `volume` for fades and
  `speed` for a tape stop. Automate instead of making whole sections louder or quieter by gain alone.

## Don't darken the mix

Asked to remove a harsh or "clangy" instrument, a model may low-pass every track at 1–3 kHz and report "highs reduced by 37 dB"
as a success. The result sounds muffled, as if through a wall: not comfortable, just dull. Balanced mixes keep about −6 to
−18 dB of their energy above 2.5 kHz (relative to the whole); darker than about −22 dB is muffled. In the report, `highMid`
and `high` far under the pop starting points above, with `advice` calling the highs dark, mean the same thing.

1. **No low-pass stacked on every track or on the master.** Low-pass a sound only for a reason (a lo-fi section, a distant pad,
   a filter sweep). Synth cutoff starting points: leads 6–12 kHz, plucks and keys 4–10 kHz, pads 3–8 kHz, bass 0.4–2 kHz.
2. **Keep the hats, shakers, cymbals and ride:** they carry the rhythm's outline and the air. If they are too much, lower their
   level (hat gain about a third to a quarter of the kick's), vary velocities, shorten the decay.
3. **Fix harshness on the one sound that causes it.** Usually: a square, pulse, chip or FM-bell lead played high and hard; a
   resonant filter; clipping or too much drive; hats too loud. Move that part down an octave, change its sound (square → saw,
   FM e-piano, brass, strings), lower its level or resonance, or cut 2–4 kHz by 3–6 dB on that track
   (`"Eq": true, "EqMidDb": -4, "EqMidHz": 3000, "EqMidQ": 1.5`). Do not darken the whole song.
4. **Keep the presence band (2–5 kHz) of the voice and lead:** it is where words and tunes stand out.
5. **Brightness is arrangement too:** spread bass, chords and lead over octaves instead of piling chords in one register; with
   no high instrument at all a song sounds dark.
6. **Deliberately dark songs** (lo-fi, ambient) still keep their highs above that −22 dB line; say why they are dark. Never report
   "suppressed the highs" as an improvement.

## Band layout

**Frequency slots**

| Band | Owner | Others make room |
|---|---|---|
| 20–120 Hz | kick, bass | everything else high-passed at 80–180 Hz; kick and bass split by sidechain |
| 120–500 Hz | upper bass, guitars, keys' left hand, snare body | keys and organ high-passed at 150–200; left hand roots and fifths only |
| 500 Hz–2 kHz | the lead (voice, trumpet, lead guitar), snare | accompanying guitars and keys −3…−5 dB here |
| 2–5 kHz | presence: lead, guitar attack, hats | if harsh, cut on that track only (above) |
| above 5 kHz | cymbals, shakers, air | never removed |

**Pan:** drums, bass and lead in the middle; two guitars ±0.5–0.6; keys −0.2, organ +0.15, acoustic guitar +0.3, percussion
+0.35, trumpet +0.2 / trombone −0.2, strings +0.1. Two instruments in the same register go to opposite sides.

**Space:** one reverb bus (for example `"room": {"effects": {"Reverb": true, "ReverbMix": 1, "ReverbSize": 0.45,
"ReverbDamping": 0.55, "ReverbPreDelayMs": 18}}`) with sends rather than a reverb per track: strings and horns −6…−8 dB, brass,
woodwinds and organ −8…−10, keys and guitars −12…−14, drums and bass not sent.

**Kick and bass:** on the bass track `"Compressor": true, "SidechainTrack": "<drum track>", "ThresholdDb": -26, "Ratio": 3,
"AttackMs": 5, "ReleaseMs": 120`, or a `follower` modulator on its volume. For EDM the same, deeper, on pads and synths. When the
kit is one track the snare and cymbals duck the bass too: put the kick on its own track and key the sidechain from it.

**Starting gains** (linear, then correct from `stems[].rmsDb`): drums .9, bass .5, clean guitar .6, driven guitar through an amp
.1, keys .4, organ .18, acoustic guitar .45, percussion .38, lead .5, brass stabs .5–.65, horn .35, strings .35, woodwinds .42.
A short stab or a part that plays in one section only shows a low song-wide RMS: judge it by its sections.

**Master:** compressor (`ThresholdDb` −16, `Ratio` 2.5, `AttackMs` 25) and a loudness target (`targetLufs`), true peak under
−1 dB; end with a 1–2 bar ring-out or a smooth `masterAutomation` fade.

**Reading `advice`:** a tip to high-pass the drum stem at 120–200 Hz would remove the kick: separate kick and bass with the
sidechain instead.

## Iterating

1. Render with `--stems` once to get balance; fix gross level problems (a track 20 dB off, clipping).
2. Then render only the section you are changing (`--section chorus` or `--bars 17-24`) to save time. The range ends where
   the section does: notes still held there are released and noise regions fade out at the end, and the tail holds only
   that decay, so a quiet stem or a section in `sections[]` really is in the range.
3. Finish with a full render with `--normalize -1` and report the final numbers to the user.
