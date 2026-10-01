# Composition notes

Write music, not just notes: decide form, harmony, melody and groove first, then encode them as patterns.

## Plan before writing

1. **Form** with bar counts (multiples of 4/8): e.g. intro 4 · verse 8 · pre 4 · chorus 8 · verse 8 · chorus 8 · bridge 8 ·
   chorus 8 · outro 4. Game BGM loops: no intro/outro, last bar leads back into the first.
2. **Harmony**: choose a key and a progression per section (pop: I–V–vi–IV, vi–IV–I–V; minor: i–VI–III–VII, i–iv–v;
   jazz/lo-fi: ii7–V7–Imaj7 with 7ths/9ths; epic: i–VI–III–VII, i–VI–iv–V). Change something in the chorus (harmonic rhythm,
   register, inversion).
3. **Melody**: a short motif (2–4 notes rhythm idea) developed by repetition and variation; stepwise motion with occasional
   leaps; chord tones on strong beats; phrase in 2- or 4-bar units with question/answer; peak note near the chorus climax.
   Keep it singable (about an octave and a half).
4. **Groove**: genre drum pattern (below), bass locks to kick, chords rhythm complements melody (sustained under busy melody,
   rhythmic under long notes).
5. **Energy curve**: add/remove layers per section; raise register, density and velocity toward choruses; drop elements before
   a big entry (a bar of silence or only pads before the chorus works).

## Reference songs

When the user names a song to work from ("like X", "X 느낌으로") and does not say how to use it, the default is fixed: **borrow
part of its chord progression**, not a loose homage. A new piece that only shares the mood, tempo or instruments is rarely
recognized; the reference's own harmony is what makes a listener name it on first hearing.

1. **Pick the signature progression**: the 2–8 chords listeners know it by, usually the chorus, hook or opening riff
   (with the bass motion, e.g. a descending bass under the chords). Take one or two such passages, not the whole song.
2. **Keep it recognizable**: the same chord qualities and order, harmonic rhythm (chords per bar) and meter; the original key or
   one close to it; the characteristic voicing or accompaniment figure (arpeggio, rhythm, register) and the tempo and feel.
3. **Put it where it is heard**: the chorus or main theme, and once early (intro or first verse) so it registers; build the
   other sections around it with your own harmony that leads into it.
4. **Melody and lyrics stay original**: write a new melody over the borrowed chords; do not copy the reference's melody, lyrics,
   recorded sounds or distinctive riff note for note.
5. **Say what you borrowed** when delivering: which progression (Roman numerals and chord names), from which part of the
   reference, and in which bars of the new song.

If you are not sure of the reference's actual chords, say so and ask the user for them (or for a chord chart) instead of
guessing; a wrong progression is no longer recognizable. When the user asks for something else (an homage in mood only, a
cover, an original with no borrowing), do that instead.

## Voicing and ranges (MIDI keys)

- Bass: E1–G2 (28–43); sub-bass roots only. Avoid thirds below C3 (muddy).
- Chords/pads: C3–G5 (48–79); close voicing around C4; move with smallest steps (common tones held).
- Melody: C4–C6 (60–84); above the chords or in a different register.
- Typical orchestral ranges (check the sampled range with `instruments info <id>` → `range`, e.g. `"C1-G7 (24-103)"`, after the instrument is downloaded): violin G3–C7, viola C3–E6, cello C2–C5, contrabass E1–G3, flute C4–C7,
  oboe Bb3–A5, clarinet D3–Bb6, bassoon Bb1–Eb5, horn B1–F5, trumpet F#3–C6, trombone E2–F5, tuba D1–F4, harp C1–G7,
  timpani D2–A3. Notes outside the sampled range are pitched far and sound unnatural.

## Drum grids (step 0.25 = 16ths, 16 characters per 4/4 bar)

| Style | kick | snare / clap | hats |
|---|---|---|---|
| Pop / rock | `x.......x.x.....` | `....x.......x...` | `x.x.x.x.x.x.x.x.` |
| Four on the floor (house/EDM) | `x...x...x...x...` | `....x.......x...` (clap) | `..x...x...x...x.` (open hat) |
| Hip-hop / lo-fi (swing it with step 1/3 grids or offsets) | `x......x..x.....` | `....x.......x...` | `x.x.x.x.x.x.x.x.` with `o` ghosts |
| Trap (half time, bpm 130–150) | `x.........x.....` | `........x.......` | `xxxxxxxxx.x.xxxx` + rolls `step: 1/6` |
| Drum & bass (170+) | `x.........x.....` | `....x.......x...` | `x.x.x.x.x.x.x.x.` |
| Waltz 3/4 | `x...........` | `....x...x...` | — |

Add a fill (snare/toms 16ths, crescendo with `o`→`x`→`X`) in the last bar of 4- or 8-bar phrases, and a crash on the next
downbeat. Use velocities (accents and ghosts) — flat velocities sound mechanical.

## Making it sound less mechanical

- Vary velocities (0.6–1.0) by beat strength; accents on downbeats and backbeats.
- Slight offsets are possible with events (`0.02 0.5 C4`), e.g. laid-back snares or strummed chords
  (`0 4 C4 | 0.02 3.98 E4 | 0.04 3.96 G4`).
- Legato: overlap notes slightly for sustained instruments; staccato: duration 30–50% of the step.
- Use different patterns for repeated sections (variation every 4 or 8 bars).
- Long releases on pads; short on basses/plucks to keep the low end clean.
