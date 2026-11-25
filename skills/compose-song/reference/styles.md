# Styles: keep songs from sounding alike

Asked for "a song", a test song, a demo or several songs, a model tends to write the same piece every time: the tempo, key,
sounds, harmony and form of the first example it read. This file names what pulls songs together in this plugin, the rules that
keep them apart, the style axes the spec offers, and style cards to start from. (Adapted from com.graze.music's
`Documentation~/composition-styles.md`, which found the same problem in the engine's own test songs.)

**Songs start from a reference** (section 0). The rules and cards below fill in what the reference leaves open, and are the
whole story only for songs nobody can be asked about (automatic tests, a quick feature check). The order of work for the song
itself (plan, hook, tune, parts, measuring) is [songwriting.md](songwriting.md).

**When the cards apply:** whenever the user has not fixed the style and there is no reference: test songs, demos, "make me a
song/BGM", several songs at once, and every song after the first in a conversation. What the user asks for always wins (genre,
tempo, key, instruments, a reference song); the cards only fill in what the brief leaves open.

## 0. Start from a reference

Without a reference a model drifts to the most common choices (I–V–vi–IV, chords stepping through the scale, one groove), and
rules against them only block the bad without saying what is good: avoiding the stock progressions it reaches for unrelated
colourful chords that do not lead anywhere. A song people liked brings progression, groove and form that already fit together.

1. **The user names a song: use it.** If they do not say how, follow 0.1.
2. **No song named: ask for one or two** — the title, the parts they like (the chorus progression, the drums, the sound) and a
   chord chart or MIDI if they have one. Do not invent a reference or substitute "something like it". In a conversation that
   already had songs, reuse what the user liked and avoid the references of the last few songs.
3. **Cards (section 4) only for songs nobody can be asked about:** automatic tests, CI songs, short feature checks. Say
   "no reference: test song" in the description.
4. **Chords only from sure sources:** the user's chart, a MIDI file (`midi <file>` reads the chord of every bar), or a song you
   know for certain. When unsure, ask; a wrong progression is not recognised.

### 0.1 What to take and what to write new

| Take (what makes the reference recognisable) | Write new (never take) |
|---|---|
| **The signature progression**, 2–8 chords listeners know it by (chorus, hook or opening riff): qualities and order, the bass motion, the harmonic rhythm, the meter | **The melody**: a new tune over the borrowed chords, not following both the rhythm and contour of the reference's hook; change the starting chord tone and the first rhythm |
| **Groove and drum feel**: kick and snare places, hat density, swing, how the drums change between sections | **Lyrics** and **title** |
| **Tempo and meter** (within ±5 BPM), the original key or a close one | **Riffs note for note**: take the *kind* of figure (arpeggio, syncopated comping), write the notes yourself |
| **Form and energy curve**: section order and lengths, where it thins out and builds, where the breaks and stops are | **Recordings and samples** of the reference |
| **Instrumentation, roles and registers** | |
| **Sound choices**: drive, reverb size, synth families | |

- **Put the borrowed progression where it is heard** (chorus or theme) and once early (intro or first verse); your own harmony in
  the other sections leads into it — for example the dominant of its first chord just before it.
- **At most two references per song:** one for harmony and form, one for sound and groove. Three or more and none is heard.
- **Progression, groove, form and instrumentation are shared genre material; melody, lyrics, riffs and recordings belong to the
  song.** Keep the user's reference files out of the delivered folder unless they say otherwise.
- **The borrowed progression stays as it is**, stock progression or staircase; just do not reuse that cliché in your own
  sections. Put the reference in the spec (`"reference": {"song": "…", "borrowed": ["chorus", "intro"]}`) so `check` reports
  progression warnings in the borrowed sections as info only.

### 0.2 The reference map: write it before the notes

| Section | Bars | From the reference | Written new |
|---|---|---|---|
| intro | 1–4 | the chorus progression's first four chords, the falling bass | piano figure |
| verse | 5–20 | drum feel (16th hats, syncopated kick) | progression (a family from section 7), melody, words |
| chorus | 21–36 | the signature progression, harmonic rhythm, instrumentation | melody, words |

(A format sample.) Sections the map marks as written new follow section 7 (harmony), section 8 (drums) and
[songwriting.md](songwriting.md) (tune and parts).

### 0.3 Deliver and record the reactions

When delivering, say what was borrowed: the progression (numerals and chord names), from which part of the reference, in which
bars of the new song, and where the groove and sounds came from. The user's reactions ("the chorus is good", "the drums are
weaker than the original") decide the next song: keep them in mind for the rest of the conversation.

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

Patterns listeners have rejected in machine-written songs (each has a check in `check`'s `music.craft`):

- Chords that climb or fall through the scale (I–ii–iii, IV–iii–ii, ii–iii–IV–V) and "a third down, then a step"
  (IV–iii, iii–ii); the same few stock loops (1-2-5, 4-3, 4-5-3-6, 1-5-6-4) in every song.
- Every chord the same length; the comping rhythm the same in every section.
- One drum groove for the whole song, the same fill every four bars, every hit at one velocity.
- Guitars that only strum down.
- A tune that walks in seconds and thirds, circles the tonic and sits down on 3-2-1 at the end of every line.
- A vocal cut into bar-sized pieces: a few notes, stop, a few notes, stop, every bar entering at the same spot, nothing held.
- A mix made "easy on the ears" by low-passing everything ([mixing.md](mixing.md#dont-darken-the-mix)).

## 2. Rules

1. **A reference first** (section 0). Without one (a song nobody can be asked about), **pick a style card** (section 4) or fill
   in the same columns for a new one. No "some song" without a reference or a card.
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
   (reference songs follow section 0).

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

## 6. Sound

Don't make a song "easy on the ears" by low-passing every track: that is a muffled mix, not a comfortable one. Fix a harsh
sound on its own track and keep the hats; see [mixing.md](mixing.md#dont-darken-the-mix) and the band mix in
[mixing.md](mixing.md#band-layout).

## 7. Harmony families

For sections the reference does not fix, and songs without a reference. Design harmony by **how far the roots move**, **where
it leaves the key** and **a different family per section**, not by chord names alone: renaming chords keeps the sound. Numbers
below are roots counted from the key's tonic (in C major `1-2-5` = C–Dm–G); minor keys and modes are also counted from their
relative major (A minor's i–VI–III–VII = C major's 6-4-1-5).

### 7.1 Families: pick one per section

Examples in C major are format samples: change the target chord, lengths and key.

| Family | How the roots move | In the spec (C major) | Good for |
|---|---|---|---|
| A. Circle of fifths | up a fourth again and again | `iii7 vi7 ii7 V7 I`; with secondary dominants `III7 VI7 II7 V7 I` | chorus ends, before a key change |
| B. Secondary dominants | the target's V7 before it | `I VI7 ii7 V7`, `I I7 IV iv`, `vi II7 V7 I` (V/ii = `VI7`, V/IV = `I7`, V/V = `II7`) | chorus, pre-chorus |
| C. Secondary ii–V | the target key's ii–V before it | `I v7 I7 IV` (to IV), `I viiø7 III7 vi7` (to vi) | sending a line somewhere new |
| D. Borrowed chords | from the parallel minor | `I bVI bVII I`, `IV iv I`, `I bIII IV I`, `I iiø7 V7 I` | the chorus peak, endings |
| E. Chromatic mediants | straight to a major triad a third away | `I bIII`, `I VI`, `I bVI`, `I bVI bIII bVII` | scene changes, a bridge's first bar |
| F. Tritone substitution | a dominant a semitone above, falling | `ii7 bII7 I`, `VI7 bVI7 V7` | jazz and city-pop cadences |
| G. Deceptive cadence | V not followed by I | `V vi`, `V bVI`, `V7 IV`, `ii7 V7 bVImaj7` | stretching a chorus once more |
| H. Pedal | the bass stays, the chords change above | `C Bb/C F/C C`, `Am D/A F/A Am` | intros, builds, under solos |
| I. Walking bass under chords | the chords leap, only the bass steps | `C G/B Am Am/G F C/E Dm7 G7` | ballad and city-pop openings |
| J. Modal vamps | two chords back and forth | dorian `i IV`, mixolydian `I bVII IV`, lydian `I II`, phrygian `i bII` | a simple verse |
| K. Passing diminished | a semitone passing chord | `C C#dim7 Dm7`, `F F#dim7 G7` | when you want a step |
| L. Key change | the tonal centre moves | `keys`: up a semitone, a minor third up or down, a fourth up, the relative key; enter by the new key's ii–V or a pivot chord | bridge, last chorus |

- Chord names notewright reads: see [spec.md](spec.md) (`m 7 maj7 m7 m7b5 dim dim7 aug sus2 sus4 7sus4 6 m6 add9 9 m9 maj9
  7b9 5`, slash chords). Numerals: plain numbers are degrees of the song's scale, so in a minor key `III` and `VI` are the key's
  own chords; write names when in doubt. Name lines (H, I, K) are absolute: rewrite them per key.

### 7.2 Root-motion budget

| Move | What | Sound |
|---|---|---|
| **Lean** | fourth or fifth, tritone, a dominant 7th falling a semitone (bII7–I) | pulls into the next chord: the backbone |
| **Third** | minor or major third up or down | a change of colour (dramatic as a chromatic mediant) |
| **Second** | tone or semitone up or down | a slide; inside the key in a row it becomes a staircase |

1. **Seconds and thirds under 70 % of the song's root moves** (more than three leaning moves in ten). Families A, B, C, F and G lean.
2. **A section with four or more changes has at least one lean.** Alternating thirds and seconds only (C–Am–G–Em–F–Dm…) is the
   "third down, then a step" sound.
3. **No three key chords stepping the same way** (1-2-3, 3-2-1, 4-5-6, 6-5-4, 5-4-3), and no four of any colour (b6-b7-1-2).
   To move by step: slide by a semitone (bII7–I, #iv°7–V, a passing dim7), borrow (bVI–bVII–I), or keep the chord and walk only
   the bass (slash chords).
4. **Write each section's progression and root moves in the plan:** `chorus: I VI7 ii7 V7 | bVI bVII I — ↓3 ↑4 ↑4 ↑4 | ↑2 ↑2`.

### 7.3 What `check` reports

| Check | When | Fix |
|---|---|---|
| `progression-stepwise` | three key chords (or four of any colour) step the same way; passing dims, bVI–bVII–I and slash-chord bass walks excepted | 7.2 rule 3 |
| `progression-small-moves` | a section of 4+ changes without a lean, or 70 % of 10+ song moves are seconds and thirds | 7.2 rules 1–2 |
| `progression-same-loop` | sections of different kinds (verse and chorus) turn the same loop (a rotation counts; intro and outro excepted) | a family per section |
| `progression-diatonic` | 3+ sections with chords, 12+ changes, not one chord outside the key | B, C, D, E, F or L in the chorus or bridge |
| `progression-cliche` | a stock loop in 3+ sections, or stock loops in half the sections and this one in 2+ | one section at most |
| `harmony-rhythm-flat` | 16+ chords, all the same length | 7.4 rule 3 |

Stock loops counted: 1-2-5 (2-5-1 as a cadence is not), 4-3, 3-2, 4-5-3-6, 1-5-6-4 (any rotation, so 6-4-1-5 too), 1-6-4-5.
They are not bad sounds; they are bad when every song and every section comes back to them. Borrowed sections
(`reference.borrowed`) report these as info.

### 7.4 Per section: the order

1. **A harmony map first:** a family per section, never the same family in two sections in a row, and not the previous song's
   combination. (Format sample: verse = H pedal + J vamp, pre = A circle of fifths speeding up, chorus = B secondary dominants +
   D borrowed iv, bridge = E chromatic mediant + L key change, last chorus = G deceptive cadence once.)
2. **Place the chords outside the key:** bar 3–4 of the chorus, line ends, the bridge's first bar. Colourful genres (anime,
   J-pop, game, city pop, jazz) want two or more per chorus; plain genres (rock, punk, folk, blues) still change the loop between
   sections (punk's I–IV–V for that section only; blues uses substitutions inside the 12 bars: quick IV7, #iv°7, VI7–II7–V7).
3. **Harmonic rhythm per section:** not one chord per bar throughout. Mix bars of two chords, chords held two bars, faster
   chords at the end of a chorus, a chord pushed half a beat early. In generators `:n` makes a chord *longer* (`I:2`); for
   faster chords lower `per` or use fractions (`V:0.5 I7:0.5`).
4. **Check the budget (7.2).**
5. **The tune sings the outside chords' colour notes** ([songwriting.md](songwriting.md#44-notes)).
6. **Comping rhythm and voicing change per section:** long chords (pad), open voicing, syncopated comping, arpeggio, rests (a
   bar of silence, a break); two chord instruments with different rhythms in one section; 7ths and 9ths, inversions moving by
   the nearest step, high chords over a low root.
7. **Do not move a whole progression from a song** except as the reference's borrowed passage (section 0).

## 8. Drums per section

The drums are the biggest energy handle. Machine-written songs tend to lay one groove over the whole song: kick on 1 and 3,
snare on 2 and 4, 8th hats, the same fill every four bars.

1. **Each section has its own drums:** intro (none, or percussion only), verse (sparse: kick, soft snare, closed hats or rim),
   pre (denser: 16th hats, a snare roll in the last bar), chorus (full: open hats, a crash on line starts), bridge (half time,
   double time, ride or toms), break (no drums).
2. **At least two sections use a different `groove` or a hand-written grid.** Genres that keep one groove (house, punk) still
   take hats, open hats and percussion in and out per section.
3. **The kick moves with the bass and riff:** not fixed on 1 and 3; syncopate with the bass and push into the next bar.
4. **Fills at line ends, never the same twice:** tom runs, snare flams and rolls, band hits, a one-beat stop. Not mechanically
   every four bars (`fill` 0 inside a verse, 2 in a build, at 8 or 16 bars elsewhere).
5. **Silence:** a beat-to-a-bar drum stop before the chorus, two bars without kick, a breakdown without drums.
6. **Dynamics:** not every cell `x`; mix accents and `o` ghosts on the snare, soft off-beat hats, a groove's `variation` .3–.5
   and its own `seed` per section.
7. **Hats are not the only time-keeper:** 16th motion can come from shaker, tambourine, ride or an arpeggio; change hats between
   8ths, 16ths, off-beats and open hats per section.
8. **Inside a section the drums move too:** change the hats or kick after four bars, end with a fill, stop or crash.

`check` reports `drums-loop` (every bar of a 5+ bar section the same), `drums-one-groove` (one groove in 75 % of 3+ drum
sections), `drums-no-fills` (75 % of 3+ section changes go by on the usual bar) and `drums-flat` (16+ hits at one velocity).

## 9. Genres

### 9.1 Families at a glance

| Family | Genres | Tempo | Meter and feel |
|---|---|---|---|
| Rock | rock'n'roll, classic/hard rock, punk, pop-punk, alternative, indie, post-rock, shoegaze, progressive, surf | 70–200 | 4/4 straight or shuffle, odd meters |
| Metal | heavy, thrash, doom, power, metalcore, djent | 60–240 | 4/4, double kick, half-time breakdowns |
| Jazz | swing, bebop, cool/modal, jazz waltz, ballad, bossa nova, fusion, neo-soul, gypsy jazz | 50–320 | swing 0.25–0.4, 3/4, straight 8ths (Latin) |
| Blues, country, folk | shuffle, slow blues, boogie, country, bluegrass, folk, Celtic, Americana | 60–260 | 12/8, two-beat, 6/8, 2/2 |
| Soul, funk, gospel | Motown, funk, disco, gospel, soul ballad, R&B | 60–130 | 4/4 16ths, 12/8 |
| Hip-hop | boom bap, trap, drill, lo-fi, phonk | 70–160 | swung 16ths, half time |
| Electronic | house, techno, trance, drum & bass, dubstep (9.3), UK garage, synthwave, chiptune, ambient, IDM, future bass, kawaii future bass / cutecore (9.2) | 60–180 | four on the floor, breakbeat, half time |
| Pop | K-pop, J-pop / anime, city pop, ballad, electropop | 60–190 | 4/4, two-beat harmonic rhythm |
| Latin, Caribbean, African | salsa, samba, bossa nova, reggaeton, cumbia, tango, flamenco, reggae, ska, dancehall, afrobeat | 60–200 | clave, 3+3+2, 12/8 |
| Classical, film, game | orchestral epic, baroque, minimalism, horror, march, waltz, game situations | 40–200 | varied |
| World | Middle-Eastern, Indian, East-Asian pentatonic, Balkan, gamelan-like | varied | 7/8, 9/8, 11/8, drones |

**Before writing, state:** the genre and the card it starts from; tempo, meter and swing (and grouping); key or mode,
progression and harmonic rhythm; drum style and bass pattern; instruments per role and effects (drive, reverb, sidechain);
loudness target; form (sections and lengths, loop or not); and what stands in for what the plugin lacks (section 5).

### 9.2 Kawaii future bass and cutecore

A future-bass drop (wide chord stabs breathing with the kick) carrying the bright tunes and toy-like sounds of J-pop, anime
and chiptune; cutecore puts the same sounds over faster, more chopped drums. These are conventions read about, not
transcriptions: start from a reference the user names (section 0) and correct by their reactions.

**Form: build–drop, not verse–chorus,** 1:30–2:30.

| Section | Bars | Heard | Drums |
|---|---|---|---|
| intro | 4–8 | a bell or piano motif (a preview of the drop tune) | none, or hats only |
| A (quiet) | 8 | pluck chords; the lead states the motif, the piano answers | rim and hats without kick, or half time |
| build | 4–8 | long chords, the filter opening, a noise riser (`regions`) | snare roll 4ths → 8ths → 16ths, the last beat silent |
| drop 1 | 8–16 | chord stabs + the lead hook, sub bass | half time, hat rolls |
| break | 4–8 | piano or bell alone, new chords | none |
| drop 2 | 8–16 | two or more changes from drop 1 (lead up an octave, a countermelody, drums doubled, a new stab rhythm) | breakbeat or four on the floor |
| outro | 4 | the intro motif, one note to end | none |

**Harmony:** bright — major or lydian, maj7, add9 and 9th chords (triads alone sound like a nursery rhyme); two beats a chord,
pushed an eighth early in the drop. A family per section (7.1): a bass falling by semitones from ii in the quiet part, a circle
of fifths in the drop, borrowed iv or bVI in the break. `IVmaj7 V iii vi` is the genre's habit but a stock loop: only in a
borrowed section.

**The drop's chord sound (what makes the genre):**
- Wide 3–5 note stabs (`"range": ["C3", "C6"]`, top note clear of the tune) on `synthPreset` `Saw lead` or `synth:saw-lead`; two
  tracks panned ±0.5 (one with more unison detune in its own `synth` settings) sound thicker.
- Duck them under the kick: a compressor with `SidechainTrack` set to the drum track, or a `follower` modulator on `volume`
  (`"source": "drums", "amount": -12`).
- Don't hold them as pads: chop them (3+3+2 or syncopated `rhythm`, `step` 0.25) and swell each beat with `volume` automation or
  an `lfo` modulator on `volume`.

**Tune:** an instrumental lead, so wider than a vocal (4.4 of songwriting.md): octave leaps, grace notes, 16th arpeggios. Motifs
of 1–2 bars; a bell or chip lead speaks, the piano or arpeggio answers in the gaps. Sequence the motif and change the fourth
time; no 7-1-2 circling or 3-2-1 landings. **Piano runs:** 16th or 32nd scale or arpeggio runs in the rests, velocity per note.

**Drums (16 steps):**

```text
drop (half time)                        cutecore (breakbeat, double feel)
kick  x.....x.......x.                  kick  x.....x...x.....
snare ........x.......                  snare ....x..o....x.ox
hat   x.x.x.xxx.x.xxxx                  hat   x.x.x.x.x.x.x.x.
```

- Hat rolls (`step` 1/6, six a beat) on the last beat of a bar, not every bar; drop that beat's plain hats.
- **Build rolls** double only on a bar line or a beat (16 beats: 8 of 4ths, 4 of 8ths, 3 of 16ths, 1 silent; 8 beats: 4·2·1·rest),
  every hit on one grid from the build's first beat; 16ths are the limit at 150 BPM and above (32nds only for the last beat or two
  below that); louder towards the drop but strong on the beats.
- Clap layered on the snare; fills are snare rolls and one-beat stops more than toms. Drop 1 and drop 2 differ.

**Bass:** `synth:sub-bass` with `glide` (an 808 stand-in, `drive` for grit), long roots with the kick, sliding just before a
chord change; cutecore cuts it short.

**Sounds:**

| Role | notewright | Use |
|---|---|---|
| chord stabs | `Saw lead` / `synth:saw-lead`, two tracks ±0.5 | sidechain or follower, open the `lowpass` in the build |
| lead | piano, `synth:pluck`, `synth:square-lead`; bell sounds (`Bell keys`, `FM bell`, `synth:bell`) easily turn clangy: ask before making one the lead | C5–C7, velocity under .8, a short echo |
| chip arpeggio | `synth:square-lead` with `CrushBits` 6–8 on that track only (no pulse-width preset) | 16th `upDown` |
| piano | the user's piano samples, else `FM e-piano` or `synth:piano-synth` | runs and answers; the break's main voice |
| vocal-chop stand-in | `Wavetable vox`, short notes | an eighth, on line ends only |
| bass | `synth:sub-bass` + `glide` | slides |
| risers, effects | White/Pink noise `regions`, an `expression` `bendFrom` (a "boing" up into a note), stutter | builds, just before drops |
| shouts ("hey", "let's go") | CC0 shout samples as `audio` clips ([instruments.md](instruments.md#shouts-vocal-ad-libs)) | sparingly: before drops, counts, a section's first beat |

**Mix:** about −11.5 LUFS (`targetLufs`), bright but not harsh: no bells ringing all song, nothing high and hard.

**Common mistakes:** a 128–138 BPM four-on-the-floor drop (that is trance or house; this drop is half time); dark minor keys;
chords held as pads instead of stabs; no build before the drop; drop 2 a copy of drop 1; cute sounds in every section (no
contrast); a tune of even eighths with no rests or answers.

Sources: https://www.melodigging.com/genre/kawaii-future-bass-2037 , https://daily.bandcamp.com/kawaii-japanese-electronic-feature ,
https://melodyaudio.myblog.arts.ac.uk/?p=6 , https://blog.native-instruments.com/how-to-make-future-bass ,
https://www.loopmasters.com/articles/4372-How-To-Make-A-Future-Bass-Drop-A-Future-Bass-Production-Tutorial (cutecore tempo and
drum figures are common lore, not from a checked source).

### 9.3 Dubstep drop: hit the bass, don't drone it

A first dubstep drop written as one- and two-beat wobble notes joined end to end, with a sine LFO on the filter, came out as a
continuous "wow-wow" with no "dub" and no "step". The "dub" is the bass and kick striking together, the "step" is the snare;
a bass that never stops buries both. Measured on the bass stem alone (50 ms windows, no master):

| Measure | Droning first draft | Fixed | Target |
|---|---|---|---|
| Loud (90th percentile) minus quiet (10th) windows of the bass | 3.4 dB (never silent) | silence between notes | 12 dB or more |
| Rise time of a bass note (10 % to 90 %) | 12 ms | 2 ms | under 5 ms |
| Drums minus bass level | −5.7 dB | −0.4 dB | within ±2 dB |
| The same loud-minus-quiet spread on the finished mix | 3.6 dB | 6.2 dB | 6 dB or more |

- **Two bass sounds**, each its own instrument and track (`{"synth": {...}}`, C# field names):
  - **The hit ("dub"):** saw + square (`"Osc1Wave": "Saw", "Osc2Wave": "Square", "Osc2Level": 0.7`), a filter envelope that slams
    shut: `"AmpSustain": 0, "AmpDecay": 0.22, "CutoffHz": 150, "EnvelopeOctaves": 4.5, "FilterDecay": 0.15`; track
    `"drive": 11, "driveMode": "Hard"`. Played as separate eighth notes.
  - **The wobble:** the same waves with the filter LFO locked to the tempo: `"LfoWave": "SawDown"` (each cycle starts bright and
    closes), `"LfoSyncBeats": 0.5` (or 1/3, 0.25), `"LfoFilterOctaves": 5`, `"LfoLevel": 0.55` so the level pumps too. A sine LFO
    only swells. No note longer than a beat.
  - `LfoSyncBeats` is one per patch: one wobble track per rate (half, third, quarter beat); moving the riff between tracks changes
    the rate inside a bar.
- **Leave holes.** For half a beat on the snare (beat 3, the ninth of 16 steps in half time) the bass plays nothing; leave about a
  16th between notes too.
- **The sub follows the bass rhythm**, cut short the same way; held long it becomes the drone again.
- **Drums as loud as the bass:** an 808-like kick layered on beat 1, snare and clap together at full velocity on 3, hats at ghost
  velocity (.4). Duck the bass under the kick (`SidechainTrack` = the kick's track).
- **Write the riff per bar:** two hand-written 4-bar riffs (drop 1, drop 2) mixing hits and wobbles ("two dubs + a wobble",
  "a wobble + two dubs").

```text
1 e & a 2 e & a 3 e & a 4 e & a      D = dub (an eighth), w = wobble (a beat, half-beat rate), . = rest
D . . D w w w w . . D . w w w w      beat 3 (the middle) is empty: the snare's place
K x . . . . . . . . . x . . . . .
S . . . . . . . . X . . . . . . .
```

- **Quiet sections on piano:** intro, break and outro motifs on a piano rather than a bell.
- **Check:** render with `--stems` and read the bass and drum stems' `rmsDb` per section (drums within about 2 dB of the bass);
  a bass whose stem never drops between notes is still droning. The wobble's rate at 140 BPM: half beat 4.67 Hz, third 7 Hz,
  quarter 9.33 Hz.
- **Common mistakes:** a sine LFO on long notes; bass louder than drums; bass sounding over the snare; drop 2 the same riff.

## 10. A directive to give a session that writes songs

Short on purpose: the more rules in a prompt, the more get dropped. The details are found step by step.

```text
[Songwriting]
Read skills/compose-song/reference/songwriting.md and follow its order. Before any note, write the plan table
(sections, bars, energy, roles, vocal range and entry, harmonic rhythm).
1. Start from a reference song (styles.md section 0). If none was given, ask for one or two.
2. Write three chorus-hook candidates (a 1-2 bar rhythm and note shape), pick one, repeat it 3-4 times in the chorus and let the
   intro or a riff echo it.
3. Flip one of note length, rests, line length or entry point between verse and chorus; sing the chorus higher.
4. Build the verse by taking parts out of the chorus; at most four roles at once; change something every 8 bars; the last
   chorus is the biggest.
5. Speech rhythm: push stressed syllables an eighth early across the beat, rest at line ends, let instruments answer.
6. Bass: root on 1, then fifths, octaves, passing and approach notes; two guitars or keys split register and rhythm; write the
   tune, bass and fills per line by hand (no bar copied eight times).
7. Count the beats of every bar, run `check`, compare music.shape with the plan and fix music.craft (three rounds at most).
   Never decide by yourself that it is good.
Sound (mixing.md), harmony families (styles.md 7), drums (styles.md 8) and genres (styles.md 9) when you reach that step.
```
