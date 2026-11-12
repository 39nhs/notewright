# Styles: keep songs from sounding alike

Asked for "a song", a test song, a demo or several songs, a model tends to write the same piece every time: the tempo, key,
sounds, harmony and form of the first example it read. This file names what pulls songs together in this plugin, the rules that
keep them apart, the style axes the spec offers, and style cards to start from. (Adapted from com.graze.music's
`Documentation~/composition-styles.md`, which found the same problem in the engine's own test songs.)

**When it applies:** whenever the user has not fixed the style: test songs, demos, "make me a song/BGM", several songs at once,
and every song after the first in a conversation. What the user asks for always wins (genre, tempo, key, instruments, a
reference song); the cards only fill in what the brief leaves open.

## 1. What pulls songs together

| Pull | Where | Pulls toward |
|---|---|---|
| The pop template | `examples/pop.json` (it was the default of `new` until 0.3.8): 104 BPM, 4/4, A minor, `synth:epiano` + `pad` + `sub-bass` + `square-lead`, Am F C G, intro 4 · verse 8 · chorus 8 · outro 4 | Every song a mid-tempo minor synth-pop loop |
| Any one example | A template, a code block in [spec.md](spec.md) or an error hint shows one progression, one groove and one tempo | That progression, groove and tempo in every song |
| First rows of lists | composition.md: pop I–V–vi–IV and minor i–VI–III–VII first; the pop/rock drum grid on top | Diatonic triads in 4/4 |
| The previous song | Earlier songs in the conversation | The next song reuses its tempo, key and sounds |
| Mix defaults | `targetLufs` -14 in the examples | Ballads and ambient pieces mixed as loud as pop |
| Game MML | Lost Ark limits (binary lengths, instrument ranges) | Fine for those songs; not a reason to simplify songs that are not for the game |

None of these values is wrong. Songs become alike when they are **copied instead of chosen**.

## 2. Rules

1. **Pick a style card first** (section 4), or fill in the same columns for a new one. No "some song" without a card.
2. **Say it before writing:** one line to the user naming the card and the value of each axis (tempo, meter and feel, key or mode,
   harmonic rhythm and chord colour, drums, bass, sounds, form, loudness). Put the card in the spec's `description`.
3. **Differ on at least four axes** from the previous song in this conversation and from the pop template (section 3 lists the
   axes). Writing several songs in one go: rotate the groups below so each group comes up once before any repeats.
4. **Leave the defaults alone** unless the card asks for them: 104 or 120 BPM, plain 4/4, A minor or C major, Am F C G or
   I–V–vi–IV, `square-lead` + `pad` + `sub-bass`, one chord per bar, `"groove": "rock"`, `targetLufs` -14, an 8-bar verse/chorus.
5. **Numbers in the docs and templates are format samples,** not a recipe: they show how to write a field, not what to put in it.
6. **Make the song exercise what it is for:** a test of swing gets a shuffle or jazz card, a meter test a 6/8 or 7/8 card, a
   reverb or long-note test the ambient card, a game-MML test a part that actually meets the game's limits.
7. **Repeatable:** fix the seeds (`variationSeed`, a groove's `seed`) so a re-render gives the same song.
8. **Borrow conventions, not songs:** a style is its tempo range, rhythm and harmonic habits. Never copy a melody, riff or lyric
   (reference songs follow [composition.md](composition.md#reference-songs)).

Rotation groups (pick a different one each time):

| Axis | Groups |
|---|---|
| Tempo | slow (< 80) · medium (80-130) · fast (> 130) |
| Meter and feel | straight 4/4 · triple or compound (3/4, 6/8, 12/8) · odd or changing (5/4, 7/8, `meterChanges`) · swung (`swing`) |
| Tonality | major · minor · a mode (dorian, phrygian, lydian, mixolydian) · blues · harmonic/melodic minor |
| Sound | electronic synths · acoustic-like (pluck, e-piano, organ, user's samples) · orchestral (VSCO 2 CE) · hybrid |

## 3. Style axes in the spec

| Axis | Spec keys |
|---|---|
| Tempo | `bpm` (10-60000), `tempo` changes and ramps (`rampBeats`) |
| Meter | `meter`, `meterChanges` with `numerator`/`denominator`/`grouping` (7/8 = 2+2+3, 6/8 = 3+3), polymeter `cycle` and `"polymeter": true` |
| Key and mode | `key`: major, minor, dorian, phrygian, lydian, mixolydian, locrian, harmonic minor, melodic minor, major/minor pentatonic, blues; `keys` for key changes |
| Harmony | chord names (`Fmaj7 Em7b5 A7 Dm9 Gsus4 C/E Bb5`) or numerals (`ii7 V7 Imaj7`, `i bVII bVI`), `:n` slots and `per` for the harmonic rhythm, `voices` and `range` for the voicing |
| Bass | `"as": "bass"`, `style` root / rootFifth / octave / walking, `rhythm`, `register` |
| Arpeggio | `"as": "arp"`, `order` up / down / upDown / downUp / random / asVoiced, `rate`, `octaves`, `gate` |
| Drums | `groove` rock / fourOnFloor / halfTime / breakbeat / metal / shuffle (`fill`, `variation`, `seed`; odd meters follow the grouping), or any style as a drum grid ([composition.md](composition.md#drum-grids-step-025--16ths-16-characters-per-44-bar)) |
| Feel | `swing` 0-0.75 with `swingGrid`, `ignoreSwing` per track, note offsets (laid-back or pushed), velocities |
| Sounds | `synth:*` (sub-bass, saw-bass, saw-lead, square-lead, pad, epiano, organ, bell, pluck, piano-synth, drum-kit), 19 `synthPreset`s (FM e-piano, Jazz organ, Brass, Acid bass, Wavetable pad …), your own `synth` settings, VSCO 2 CE orchestra, the user's samples, VST3 on Windows |
| Effects | drive modes, amp and cabinet IR, EQ, chorus, echo, reverb, crusher, gate, stutter, sidechain, LFOs and automation |
| Mix | `targetLufs`, buses and sends, master compressor and limiter |
| Form | `sections` (lengths need not be 8: 6, 12, AABA, 12-bar blues), `repeat`, lead-in and ring-out, seamless loops |

## 4. Style cards

A card is a starting point: change any cell, but keep rule 3. "Template" names an example to start from with
`new <path> --template <name>`; its values are still that card's choices, not defaults. "Gap" is what the plugin lacks for
the style (work around it as noted).

| Card | Tempo · meter | Key · harmony | Rhythm · feel | Sounds · effects | Mix | Template · gap |
|---|---|---|---|---|---|---|
| **A. City pop** | 96-104, 4/4 | Bb or Eb major / lydian; maj7, m7, 9th chords two beats each | rock groove plus a 16th-hat grid, `octave` bass, no swing | FM e-piano, Brass, pluck with chorus, reverb bus | -14 | — · no slap bass (FM bass) |
| **B. Bossa nova** | 130-140 (two-beat feel), 4/4 | F or D major; ii–V chains, m7b5 | 2-bar clave grid on the rim, `rootFifth` bass on 1 and 3 | pluck comping, FM e-piano, soft lead | -16 | `bossa` · no nylon-guitar sample (pluck) |
| **C. Jazz waltz** | 150-180, 3/4 | numerals ii–V–I with two `keys` changes | swing 0.33 on eighths, `walking` bass, jazz-ride grid | Jazz organ perc, FM e-piano | -15 | — · no ride groove (grid) |
| **D. Trap** | 140 half-time, 4/4 | E phrygian or minor, almost no chords | halfTime groove plus hat rolls at `step` 1/6 | sub-bass with `glide` (808-like), Pluck, sidechain duck | -12 | — · no 808 preset |
| **E. Drum & bass** | 172-176, 4/4 | D dorian, a two-chord loop | breakbeat groove, stutter automation | Acid bass, Wavetable pad, crusher | -11 | — |
| **F. Ambient** | 56-70, tempo ramp | lydian, a chord every 8-16 beats, sus and slash chords | no drums, random arpeggio at a rate against the bar | Wavetable pad, Bell keys, long echo and reverb, pink-noise region | -18 | `ambient` |
| **G. Progressive 7/8** | 120-140, 7/8 (2+2+3) ↔ 4/4 | minor, key change up a semitone | groove follows the grouping, polymeter riff | Saw lead with drive and amp | -14 | `mathrock` |
| **H. Folk 6/8** | dotted quarter 66-76 (quarter `bpm` 100-115), 6/8 | mixolydian, I–bVII–IV | frame-drum grid in eighths, root bass | pluck arpeggio, organ drone, fiddle-like saw lead | -16 | `folk68` · no fiddle or accordion sample |
| **I. Blues shuffle** | 88-110, 4/4 swung | blues; 12-bar I7–IV7–V7 | shuffle groove, swing 0.33, walking bass | Rock organ with drive, saw lead, spring-like reverb | -14 | `blues` |
| **J. Chiptune boss** | 180-200, 4/4 ↔ 3/4 | harmonic minor, fast arpeggios | fourOnFloor, arp upDown at 1/16 | square-lead, `CrushBits` 4-6, noise snare | -12 | — · no pulse-width lead (square) |
| **K. Orchestral** | 70-90, 4/4 → 3/4 | minor → relative major, four-voice voicing | timpani and toms, crescendo automation | VSCO 2 CE strings, brass, harp, timpani; large reverb | -16 | `orchestral` |
| **L. Metal** | 160-200, 4/4 | phrygian; power chords (`E5 F5`) | metal groove, double kick | saw-lead doubled, `DriveMode` Hard or Fuzz, amp and cabinet | -12 | — |
| **M. Reggae one-drop** | 70-80, 4/4 | major; I–IV–V with long bars | one-drop grid (kick and snare on 3), off-beat skank chords (`rhythm` `.x.x`, `step` 0.5), melodic bass with rests | Jazz organ bubble, pluck skank, echo on the snare | -14 | — · no reggae groove (grid) |
| **N. Neo-soul / lo-fi** | 70-90, 4/4 | Dm9 G9 Cmaj7, extended chords | swing 0.15-0.25 on 16ths, laid-back snare offsets | FM e-piano, crusher, low-pass on the drums | -14 | — |
| **O. Synth-pop** | 100-112, 4/4 | minor; i–VI–III–VII | rock or fourOnFloor | epiano, pad, sub-bass, square lead | -14 | `pop` · the pull everyone falls into: only when the brief is synth-pop |

Game covers and arrangements of an existing piece follow its source, not these cards.

## 5. Gaps

What the plugin cannot do for some styles yet:

- **Grooves:** only rock, fourOnFloor, halfTime, breakbeat, metal and shuffle generate. Latin (bossa, samba), jazz ride, reggae
  one-drop and trap rolls are drum grids ([composition.md](composition.md#drum-grids-step-025--16ths-16-characters-per-44-bar)).
- **Sounds:** no built-in nylon or acoustic guitar, slap bass, 808, pulse-width chip lead, fiddle or accordion. Use pluck,
  FM bass, sub-bass with glide, square-lead, saw-lead with vibrato and organ, or the user's samples (`instruments scan`);
  VSCO 2 CE covers the orchestra.

## 6. A directive to give a session that writes songs

```text
[Song variety]
1. Values in notewright's docs, examples and templates (104 BPM, A minor, Am F C G, square-lead + pad + sub-bass, groove rock,
   one chord per bar, targetLufs -14) are format samples. Do not copy them.
2. Before writing, pick a card from skills/compose-song/reference/styles.md and say the card and the value of each axis
   (tempo, meter and feel, key or mode, harmonic rhythm and chord colour, drums, bass, sounds, form, loudness).
   Differ from the previous song on at least four axes.
3. Use modes (D dorian, E phrygian, Bb lydian, G mixolydian), 3/4, 6/8, odd meters and swing, 7th and 9th chords, inversions,
   two chords per bar or one per two bars, syncopation and rests.
4. Fix the seeds and write the card into the spec's description. Never copy a melody, riff or lyric.
```
