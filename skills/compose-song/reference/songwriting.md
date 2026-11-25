# Songwriting: the order of work for songs that are not monotonous

Read this before writing any song with words or a tune. Sections 1 and 2 are enough to start; read the others when you reach
that step. Styles (which tempo, key and sounds) are in [styles.md](styles.md); spec syntax in [spec.md](spec.md).

## 0. Why songs written by a model sound monotonous

Rules about what to avoid (no I–V–vi–IV, no stepwise chords) do not make a good song: a song can break them and be good, or
pass them all and still be dull. Measured side by side, a human-written band song and a model-written one differ like this:

| | Human-written | Model-written |
|---|---|---|
| Hook | one short shape repeated, often on one or two notes | new notes in every bar, circling the tonic |
| Vocal rhythm | six or seven different bar rhythms in a verse | the same rhythm in almost every bar |
| Pushed notes (an eighth early, held across the beat) | about 8 % of notes | about 1 % |
| Accompaniment | thin in the verse, dense in the chorus (1.7x) | dense everywhere (1.2x) |
| Register | the chorus sits about an octave above the verse | verse and chorus in the same octave |
| Bass | on the root 30–50 % of the time | on the root almost always |
| Chords | few, sometimes all in the key | many colourful chords, all four beats long |

A good song **repeats something memorable, contrasts its sections, and gives every part its own job.** Research on language
models writing music finds the same failure modes ([L1]–[L9] in section 9):

- They choose the most likely next note, so lines walk the scale, every note has the same length and bars get copied.
- They miscount beats and bars. Checking and fixing the count afterwards removes almost all of these errors.
- Plan → draft → rule check → choose among candidates raises expert ratings a lot; asking the model to grade its own work
  raises only the grade.
- The more instructions, the more get dropped, and naming a forbidden thing pulls output toward it. Keep the plan short and
  measure instead.

## 1. The core seven

1. **Write one hook and repeat it.** A 1–2 bar rhythm and note shape is the face of the song. It comes back 3–4 times in the
   chorus and is heard in the intro or a riff too. Roughly 15–35 % of a song is new material; the rest is repetition and variation.
2. **Change one big thing per section.** Between verse and chorus, flip one of: note length, amount of rest, line length,
   where lines start (before, on or after the beat). For example: a verse of short notes entering after the beat, a chorus of
   high long notes entering before it.
3. **Build the verse by taking things out of the chorus.** Decide the chorus arrangement first; the verse keeps drums, bass and
   one chord instrument. The moment the removed parts come back is the chorus.
4. **At most four roles at once.** Roles: foundation (drums, bass), bed (pads, long chords), rhythm (strums, arpeggios), lead
   (the tune), fill (fills, countermelody). Instruments playing the same rhythm count as one role.
5. **Write the tune in speech rhythm.** Start a stressed syllable an eighth early and hold it across the beat. Rest at the end
   of a line, and let a guitar, bass or drum fill answer in the gap. A line is 2–4 bars and crosses bar lines; its last note is
   held. The tune grows from section to section like the accompaniment does (4.6).
6. **The bass plays the root on beat 1, then moves.** Fifths, octaves and passing notes; on beat 4 it approaches the next root.
   It moves with the kick.
7. **Measure after writing.** `check` prints the song's shape (`music.shape`) and craft warnings (`music.craft`). Compare
   them with the plan and fix the differences. Never decide by yourself that it is "good".

## 2. Order of work

| Step | What you make | Read |
|---|---|---|
| 1. Reference | 1–2 reference songs and what to take from them | [styles.md](styles.md#0-start-from-a-reference) |
| 2. Plan | the plan table: sections, bars, energy, roles, register, harmonic rhythm | 3 |
| 3. Hook | three chorus-hook candidates → pick one | 4.1 |
| 4. Tune | chorus → verse → pre-chorus, with the words if there are words | 4 |
| 5. Harmony | a progression family and harmonic rhythm per section | [styles.md](styles.md#7-harmony-families) |
| 6. Parts | bass, two guitars or two keys, pads, countermelody, drums | 5 |
| 7. Links | fills, stops, builds, drop-outs | 6 |
| 8. Count | the beats of every bar | 7 |
| 9. Measure | `check`: shape against the plan, fix the craft warnings (three rounds at most) | 7 |

- Say each step's result to the user (or write it in a `plan.md` next to the spec) before the next one. No notes before the plan.
- Write notes **by hand, line by line** in patterns. Let generators (`"as": "bass"`, `"arp"`, `groove`) write accompaniment
  only, and still give them a different rhythm per section (section 8).

## 3. The plan table

Write this before any note. Energy is 1–10 and only the biggest section is 10; if every section is near 10 there is no contrast.

| Section | Bars | Energy | Roles heard | Drums | Vocal: range · entry | Harmonic rhythm | New here |
|---|---|---|---|---|---|---|---|
| intro | 4 | 5 | rhythm guitar (hook riff), drums | toms | — | a chord per 2 bars | the hook as a riff |
| verse 1 | 8 | 4 | foundation, muted guitar | closed hat 8ths | low, after the beat | a chord per bar | the voice |
| pre | 4 | 6→7 | + pad, long bass | snare 4ths → 8ths → 16ths | rising, lines shorten | last 2 bars: a chord per half bar | a one-beat stop at the end |
| chorus 1 | 8 | 8 | + open chord guitar, lead fills | open hat, crash | highest, pickup into beat 1 | two chords a bar | the hook, long notes |
| verse 2 | 4 | 5 | verse 1 + a countermelody | verse 1 + ghost notes | as verse 1 | a chord per bar | half as long |
| chorus 2 | 8 | 9 | chorus 1 + harmony voice | chorus 1 + 16th hats | as chorus 1 | as chorus 1 | one more layer |
| bridge | 8 | 3→6 | voice + one instrument → a layer every 4 bars | none → kick → backbeat | a new register | a new progression | the lowest point |
| last chorus | 8–16 | 10 | everything | ride or 16ths | a semitone or tone up, or a layer more | as chorus 1 | the biggest point |
| outro | 4 | 4 | the intro riff | one hit and out | — | — | — |

The values are a format sample: take them from your reference. Rules of thumb for the form:

- **The first chorus arrives within 30–45 seconds** (hit pop averages about 39 s; intros 5–10 s).
- **J-pop / anime form:** intro 4–8, A-melo 8–16, B-melo 4–8, sabi 8–16, interlude 4–8, C-melo 4–8, otoshi-sabi 4–8, last
  sabi, outro 4–8 bars. A TV-size cut is 89 seconds.
- **Verse 2 differs from verse 1:** half as long, one layer more, or no drums in its first half.
- **Choruses grow:** chorus 2 has one layer more than chorus 1, and the last is the biggest.
- **Plan the vocal in four columns** (4.6): entry, notes per bar, longest note, what is new. Changing only the accompaniment
  per section leaves the voice the one thing that never grows.
- **Something changes every 8 bars:** a part enters or leaves, or the drum pattern changes. Hits use about five drum patterns
  per song, and most change the pattern inside sections too.
- **The lowest point is near 60 % of the song, the final arrival near 80 %.** Build between them in three 4-bar steps; the
  backbeat (snare on 2 and 4) comes back last.

## 4. Hook and tune

### 4.1 Write the hook first

1. Decide the **rhythm** of the chorus's first line first. It must be recognisable clapped. One even note per beat is not a hook.
2. Put notes on it. Memorable tunes have a **common overall shape** (the first line rises, the second falls) plus **one
   unusual thing** (an unexpected leap, or unusually many repeated notes).
3. Write **three candidates with different rhythms** and choose by the table below, not by feel.
4. Plan more than one hook: besides the vocal hook, an intro riff or a wordless post-chorus figure (for example 5-3-5-3 eighths).

| Hook test | Check |
|---|---|
| Distinguishable from the others by rhythm alone | different onset positions |
| Long and short notes in the same bar | two or more lengths |
| A push | at least one note starts before a beat and holds across it |
| Can be sung again two bars later | still fits words when repeated |
| Above the verse | its long note a third or more above the verse's top |

### 4.2 Lines and phrases

- **A 16-bar section is statement 4, restatement 4, departure and close 8** (srdc). Lines go a a b a or a a b c; two
  unrelated halves are rare.
- **Two lines are a and a′:** same start, different end. The first stays open (ends on 5, 2 or 6), the second closes (3 or 1).
- **In the departure (the pre-chorus) cut the motif shorter:** 4-bar units become 2, then 1, and the chords speed up. The tune
  climbs but does not reach the chorus's top note.
- **A line is about 4 bars and one line of lyrics.** Put a rest or a long note where lines change.
- **Developing a motif** (eighth-eighth-quarter on 3-3-2):
  repeat 3-3-2 · sequence 4-4-3 · invert 3-3-4 · truncate 3-2 / 3-2 · augment to quarter-quarter-half · anticipate (an eighth early).
- **"A run of quick notes + three stresses" is a hook too:** keep that rhythm and change only the notes; same rhythm, same hook.

### 4.3 Rhythm

- **Push (anticipation):** start about one stressed syllable in four an eighth early and hold it across the beat. In rock,
  about 23 % of stressed syllables are sung this way.
  - Most often on the "and" of 2 and of 4, anticipating 3 and the next bar's 1.
  - Sixteenth pushes only below about 120 BPM; eighth pushes at any tempo.
  - To make one: take equal notes, halve the first, move the rest earlier by that much.
  - In notewright: `E5:1.5 G5:2.5` starts G5 on the "and" of 2 and holds it across 3 and 4. `|` in a melody is only a visual
    mark, so a note may run over it: `… A5:0.5 C6:2.5 | E5:1 …` pushes C6 onto the "and" of 3 and holds it into the next bar,
    while `… C6:1.5 | r:0.5 …` stops at every bar line.
- **3+3+2** (eight eighths grouped 3·3·2) is more common in pop and rock than triplets.
- **Density:** 4–5 syllables a bar is classic, 6–7 modern pop. The busiest and sparsest bars should differ a lot.
- **Where a line starts sets its stability:** on beat 1 or a pickup (after 3) is stable; after beat 1 is unstable, the later the
  more so. Give verse and chorus different entry points.
- **A busy verse wants a calmer chorus.** The same tune with a different arrangement energy still reads as another section.

### 4.4 Notes

- **Pop tunes mostly move by small steps:** the average interval is 2.0–2.3 semitones, and two thirds of the notes sit within a
  5–7 semitone span. Leaps are rare and therefore heard: about one per line.
- **Don't walk the scale.** Pop moves between two notes or repeats a note; more than four notes in one direction is a folk habit.
- **After a leap, step back toward the middle** of the range (a leap that gets closer to the middle may continue).
- **Common degrees:** 1 most, then 5; 1-2-3-5-6 (the pentatonic) carries the tune, 4 and 7 are rarer; in rock b7 outnumbers 7.
- **Loose verse, tight chorus:** the verse may float on the pentatonic, the chorus lands on chord tones.
- **Blue notes:** b3 and b7 over major chords.
- **An appoggiatura** (a non-chord note on the strong beat resolving by step to a chord tone) is the most moving device;
  about once a line.
- **Line ends:** about 35 % of line ends are on 1, but only about 20 % of notes inside lines. Don't repeat the same 3-2-1 landing.
- **Over a chord outside the key, sing its colour notes** (its 3rd, 7th or 9th: b6 over a borrowed iv, #1 over VI7), or the
  colour stays in the accompaniment.
- **Register:** the centre rises verse → pre → chorus; the chorus may start high and long. (Common rule of thumb: verse within
  9 semitones, chorus centre 3–5 semitones higher, the whole song 14–18 semitones.)

### 4.5 Words and notes

- **Korean:** put syllables without a final consonant (or whose consonant links to the next syllable) on long, high or melismatic
  notes; a final consonant cuts the breath. Consonant-heavy syllables on the beat sound percussive. Korean needs about twice the
  syllables English does for the same meaning: fix the syllable count, then the rhythm.
- **Japanese:** one mora per note by default; ん, long vowels and the second vowel of ai/ou can share the previous note. Mora by
  mora sounds plain, merged sounds like Western pop (verse by mora, chorus merged works). Follow pitch accent only where homophones
  would be confused.
- **English:** stressed syllables on strong beats or pushed onto them; weak syllables short.
- One syllable = one note; a line of lyrics = a 2–4 bar line; breathe between lines.

### 4.6 The vocal line: not boxed in bars, growing through the song

A vocal can pass every pitch rule (each bar different, leaps present, chorus higher) and still sound stuck when it is cut
into bar-sized pieces: three or four notes, stop, two or three notes, stop, every bar re-entering at the same spot after a silent
bar line, each bar climbing the chord, nothing held. `check` measures exactly this:

| Measure | Target | Warning |
|---|---|---|
| Notes sung without a rest of half a beat | 4.5 or more on average, or a bar or longer | `melody-short-lines` |
| Bar lines the voice crosses (holds over, or starts a note on) | most of them; not silent on 80 % | `melody-bar-boxes` |
| Where bars restart after a silent bar line | not the same spot in half the bars | `melody-same-start` |
| Bars opening with the same three-note climb or fall | under 60 % | `melody-contour-loop` |
| Notes of 2 beats or longer (1 s in slow songs; a tie counts as one note) | 1 in 25 or more | `melody-no-long-note` |
| Verse vs chorus rhythm (entry, notes per bar, note length) | at least one 15 % apart | `melody-verse-chorus-rhythm` (info) |
| Longest note per section | longest in the choruses, longest of all in the last | `music.shape` `vocal.longest` |

- **A line is 2–4 bars, not one bar.** Decide the line of lyrics, then place its notes. Inside a line, don't rest for more than
  half a beat, and let sound pass the bar lines: hold the bar's last note over the line (a push), or pick up at the end of the
  bar and land on the next 1. Gather rests at the end of lines.
- **Move the entry:** on 1, after 1, a pickup from the previous bar's beat 4, after two beats of rest. One spot for at most two
  of three lines in a section; verse and chorus favour different spots.
- **Don't sweep the chord.** Climbing root-3rd-5th every bar is a guitar arpeggio. A voice stays on two or three neighbouring
  notes and repeats notes (4.4); one leap per line (not per bar), on a stressed word, then a step back.
  Don't add a leap in every bar just to silence `melody-small-intervals`: it means "about one per line".
- **Answer a shape with its opposite:** if the first line rises, the second falls or stays (a and a′). Not more than four bars
  in a row opening with the same contour.
- **Hold line ends:** a line's last syllable and the hook's top note last 2 beats or more (1 s or more in slow songs), on an open
  syllable (4.5). Synth voices only show vibrato on notes longer than about half a second; without long notes a lead sounds
  mechanical (track `vibratoRate`, per-note `expression` `vibrato`).
- **Plan the vocal per section** in four columns. When the section changes, at least two of the four change.

| Section | Entry | Notes per bar | Longest | New here |
|---|---|---|---|---|
| verse 1 | after 1 | 5–6 (spoken) | 2 beats (line end) | a narrow range, repeated notes |
| pre | on 1 | 3–4 rising to 6 | 2 beats | the motif cut shorter as it climbs |
| chorus 1 | pickup into 1 | 2–3 | 4 beats (the hook's top) | highest and longest |
| verse 2 | as verse 1, one line different | 5–6 | 2 beats | third line a third higher, or one note pushed |
| chorus 2 | as chorus 1 | as chorus 1 | 4 beats | harmony voice, last line ends higher |
| bridge | a new spot | 1–2 (long) or 7–8 (fast) | 4+ beats | a rhythm neither verse nor chorus had |
| last chorus | as chorus 1 | as chorus 1 | the longest of the song (4–8 beats) | the top note once more, or the hook once more |

These values are a format sample; take them from the reference. Then compare with `music.shape[].vocal`: `barsStart` (the most
common entry and its share), `notesPerBar` and `longest` should follow this table. If all three are the same in every section,
the voice did not grow.

## 5. Parts

**Sounds first.** A band song in notewright has no recorded guitars, bass or drum kit built in: use the user's samples
(`instruments scan`), VSCO 2 CE for orchestral parts, or the synth and say what stands in for what ([styles.md](styles.md#5-gaps)).

### 5.1 Bass

- **Root on beat 1** of every chord change. Fifths freely; 3rds and 7ths as decoration on weak beats.
- **A bar template:** 1 root, 2 a scale note, 3 a chord tone other than the root, 4 an approach to the next root (a fifth above
  it, a scale neighbour, or a semitone above or below; outside the key is fine if it resolves on the next beat).
  For C7 → F: `C3 D3 E3 C3`, `C3 D3 Bb2 G2`, `C3 D3 G3 E3`.
- **Lock with the kick:** beat 1 with the kick, then answer the kick or fill between its hits.
- **Change register and rhythm per section:** middle in the verse, low in the chorus, high and melodic in a breakdown (or the
  reverse). Leaving the bass out of a whole verse is contrast too.
- **Don't loop one line:** decorate the end of 4 and 8 bars and the bar before the chorus, in the gaps between vocal lines.
- **In the spec:** write it as a pattern (`melody` tokens, one note at a time, slightly shorter than the step); a slide is a
  `glide` on the track with overlapping notes; ghost notes are very short and soft (`@0.35`) just before a main note.
  `"as": "bass"` with `style` rootFifth / octave / walking and a `rhythm` is fine for a first draft, but `check` will say
  `bass-roots-only` if it stays on roots.

### 5.2 Two guitars (or two keyboards)

- **Never the same chord in the same rhythm.** Split by register (low three strings / a high inversion), by figure (strum /
  arpeggio), or by role (a muted single-note riff / high chords).
- **Split the beat:** if one plays accented 8ths, the other plays only the strong beats and lets them ring. Palm-mute only one.
- **Riffs:** 1–4 bars, repeated two or three times, the last bar changed; made of the low minor pentatonic, open E/A pedals or
  chord arpeggios; recognisable clapped.
- **Fills between vocal lines:** two guitars playing the same fill an octave apart sound bigger than in unison.
- **Harmony lead:** the second guitar plays a diatonic third above, now and then a fourth or fifth; sparingly.
- **Strums are note order** (no stroke field exists): a downstroke starts low strings first, 0.01–0.015 beats apart, on the beats;
  an upstroke plays only the top 3–4 strings, high to low, between the beats at 0.6–0.7x velocity; a muted scratch is 3–4 notes of
  0.05–0.1 beats at `@0.35`. The hand keeps moving (down on the beat, up on the "and"): `D.DU.UDU`, `D..D.UD.`, `DxUxDUxU`.
  In an events pattern: `0 0.5 E3 @0.9 | 0.012 0.49 B3 | 0.024 0.48 E4 | 0.5 0.4 E4 @0.6 | 0.512 0.39 B3` …
  `guitar-all-down` fires when a track named guitar/gtr has eight or more strums and no upstroke.

### 5.3 Keys, pads, strings

- **No close intervals low down:** a chord's 3rd not below E3, tensions above G3; if it is muddy, raise the lowest note an octave
  (`range` on a chord generator).
- **With a bass player, the left hand plays single notes**; the right hand plays triad inversions.
- **Pads hold the 3rd and 7th and move by the smallest step**; drop a note rather than leap a fourth. Keep the voicing inside an
  octave so the tune has room.
- **String countermelody:** a few long notes while the voice is busy, moving when it is sparse; the top voice moves by step.
- **Running into a chorus:** eight 16ths up an octave in two beats, optionally doubled a sixth below.
- **A static pad needs motion:** pulse it or break it into an arpeggio. Octave doubling only for big moments.

### 5.4 Countermelody and fills

- **When the voice moves, hold; when it holds, move.** A countermelody must work as a tune on its own; it usually enters in the
  later choruses.
- **Contrary motion:** up when the voice goes down, a different rhythm, with rests.
- **Listeners follow the highest, loudest, busiest thing.** Under the voice, accompany in half notes and put moving lines in the
  voice's gaps or under its long notes. Guitars above the voice's register, keys below it.
- **A fill is an answer:** it enters a gap in the tune, is rare and has a different sound.

### 5.5 Groove and velocity

- **Push the chord change** an eighth early (a 16th is tenser). **Everyone moves together**, kick and an accented cymbal on the push.
- **Random timing is not groove.** Snares shifted randomly by 8–24 ms were rated worse. Keep timing on the grid (or a deliberate,
  consistent offset) and make the human feel with velocity.
- **Velocity by section and beat** (notewright 0–1): p .38, mp .5, mf .63, f .76, ff .88. Verse mp–mf, chorus f, last chorus ff.
  Beats strong, off-beats softer, ghost notes .3–.45.
- **Drums per section:** see [styles.md](styles.md#8-drums-per-section).

## 6. Linking sections

- **Make the pre-chorus unstable:** start on a subdominant (IV, ii), end on V, and let the chorus's first beat land on I.
  - Speed up the harmony: a chord per 2 bars → per bar → per half bar.
  - Build by subtraction: drop the bass or kick and the moment it returns is the chorus.
  - Build by acceleration: snare 4ths → 8ths → 16ths. Speed changes only on a bar line or beat (16-beat build: 8 beats of
    4ths, 4 of 8ths, 3 of 16ths, 1 beat of silence); every hit on one grid counted from the build's first beat; 32nds only for
    the last beat or two below about 150 BPM; velocity rising, strong on the beats.
  - Move the snare from 2 and 4 to 3 only, hold the guitars, lengthen the bass notes.
- **Just before the chorus:** leave the last beat of the bar empty (two eighths at fast tempi), or stop only kick and bass;
  crash and low end return on 1. Use stops sparingly; rotate stop / fill / thin out from chorus to chorus.
- **The chorus arrives** on the tonic with everyone and a crash on beat 1; the tune may pick up into it.
- **Hats:** 8ths in verse and pre, a 16th feel in the chorus; 8ths → 16ths is the commonest last-chorus lift.
- **If every section uses the same chord loop**, arrangement and velocity make the form: a quiet verse, a tense pre, the biggest chorus.
- **Bridge / C-melo:** once, after chorus 2: new register, new rhythm, new chords, a drum pattern not heard before.
- **Otoshi-sabi (the dropped chorus):** the chorus tune for 4–8 bars with the voice and one instrument, then the full last chorus.
- **Lifting the last chorus:** modulate up a semitone (or tone): old key's V → new key's V → new I (`keys` in the spec). Check the
  top note is still singable. Or stretch the chorus from 8 to 16 bars, or add a layer.
- **Taking away is more dramatic than adding.**
- **Intro** = the chorus with things removed, or a preview of a chorus line. **Outro**, 4–8 bars: one hit and out, the last
  chorus line repeated, or back to the intro riff.

## 7. Count, measure, fix

1. **Count.** The note lengths of every bar add up to the meter, and every section starts on a bar line. Miscounting is the
   commonest model error; `check` reports patterns whose length does not fit.
2. **Measure.** Run `check <spec>` and read `music.shape` (one row per section) against the plan table:

   | `music.shape` field | Compare with the plan |
   |---|---|
   | `band` (accompaniment onsets per bar, the lead excluded) | the energy column's order; sung sections 1.35x or more apart |
   | `parts` | more in the chorus than in the verse |
   | `velocity` | the chorus louder than the verse |
   | `vocal.range` | the chorus higher than the verse |
   | `vocal.space` (share of the section the lead is silent) | room for fills |
   | `vocal.barsStart` (the commonest entry in a bar, `tied` = held over the bar line) | different per section, no spot above about 65 % |
   | `vocal.notesPerBar`, `vocal.longest` | the vocal plan (4.6); the chorus's longest note longer than the verse's |
   | `bassOnRoot` | under 0.85 |
   | `chordsPerBar` | different per section |

   The lead is the track named in `"lead"`, else the busiest one-note-at-a-time Melody track (names like vox, vocal, lead,
   melody win). Put chord and arpeggio tracks on the `Harmony` layer so they are not taken for the tune. A lead named
   vox/vocal/voice/sing is sung (`music.leadSung`); any other lead is an instrument and gets the same line checks: lines boxed
   in bars and one rhythm per bar still warn, the checks about breath and syllables (pushes, line length, entries, contour, long
   notes) come as info.
3. **Fix.** Fix each `music.craft` warning and measure again; three rounds at most. Tell the user what is left and why (a rap or
   a vocal chop written narrow on purpose, a genre that keeps one groove).
4. **Don't grade yourself.** Self-grading raises the score, not the song. Choose by the hook table (4.1) and the measurements.
5. **Deliver:** the plan table, the hook (note names and rhythm), the warnings left, what was borrowed from the reference
   ([styles.md](styles.md#0-start-from-a-reference)) and what to listen for. Ask for reactions in musical terms and use them
   in the next song.

| `music.craft` check | Meaning | Fix in |
|---|---|---|
| `energy-flat` | the accompaniment is about as dense under every sung section | 3, 6 |
| `arrangement-no-lift` | the first chorus brings in no part or drum sound the verse lacked | 1.3, 6 |
| `melody-no-peak` | the chorus sings no higher than the verse | 4.4 |
| `melody-rhythm-loop` | over 70 % of a section's sung bars share one rhythm | 4.2, 8 |
| `melody-no-push` | under 3 % of the lead's notes are pushed across a beat | 4.3 |
| `melody-small-intervals` | under 8 % of the whole song's moves are a fourth or wider (per section: info) | 4.4 |
| `melody-tonic-orbit` | most of a section's notes on 7, 1 and 2, with few leaps | 4.4 |
| `melody-scale-cadence` | lines keep stepping down onto the tonic (3-2-1) | 4.4 |
| `melody-bar-boxes` | the voice is silent on 80 % of a section's bar lines | 4.6 |
| `melody-short-lines` | the voice sings in bursts of under 4.5 notes, under a bar | 4.6 |
| `melody-same-start` | half the sung bars restart at the same spot after a silent bar line | 4.6 |
| `melody-contour-loop` | 60 % of a section's bars open with the same climb or fall | 4.6 |
| `melody-no-long-note` | under 4 % of the notes last 2 beats (or 1 s) | 4.6 |
| `melody-verse-chorus-rhythm` (info) | verse and chorus enter at the same spot with the same note count and length | 1.2, 4.6 |
| `bass-roots-only` | 85 % of the bass is the root at one length | 5.1 |
| `harmony-rhythm-flat` | every chord lasts the same | [styles.md](styles.md#7-harmony-families) |
| `progression-*` | stepwise runs, no leaning root moves, the same loop everywhere, nothing outside the key, stock progressions | [styles.md](styles.md#7-harmony-families) |
| `drums-loop`, `drums-one-groove`, `drums-no-fills`, `drums-flat` | one drum bar for a section, one groove for the song, no fills at section changes, one velocity | [styles.md](styles.md#8-drums-per-section) |
| `guitar-all-down` | strums with no upstroke | 5.2 |

## 8. Writing it in the spec

Loops make monotony, in code and in patterns alike.

- **Write the tune, bass line and fills per line**, as patterns of 2–4 bars, not one bar repeated 8 times. A list of patterns
  (`"vox": ["v1", "v2", "v1b", "v3"]`) plays them in order; repeat a written line only when the song repeats it, and change the
  last repeat (`v1b`).
- **One pattern set per section kind.** A chorus is not the verse pattern transposed.
- **Generators are accompaniment:** give each section its own `rhythm`, `style`, `order` and voicing `range`, and change
  `per` (harmonic rhythm) between sections.
- **Velocity per note** (`@0.9` on beats, `@0.6` off-beats, ghosts `@0.35`), not one velocity for a whole part.
- **No randomness for variation.** Vary with the tools of 4.2 (sequence, truncate, push); fix seeds where a generator needs one.
- **A script that writes a spec** follows the same rules: hand-written bars per section, a loop only to repeat a written bar,
  with its last repeat changed.

## 9. Sources

Language models and music
- [L1] ByteComposer: beat-count errors 68.8 % → 1.8 % after checking and fixing; planning, self-evaluation and candidate voting
  raised expert ratings from 2.32 to 4.42. https://arxiv.org/html/2402.17785v1
- [L2] Can LLMs "Reason" in Music? (ISMIR 2024): repetitive rhythm, no development. https://arxiv.org/html/2407.21531v1
- [L3] ComposerX: weak cadences, misaligned voices, no dynamic contrast. https://arxiv.org/html/2404.18081v1
- [L4] Libretto: measure → verbal feedback → fix raised pass rates from 62 % to 94 %; a reference song as a template from 25 % to
  75 %. https://arxiv.org/html/2606.22708v1
- [L5] Dai et al., What is missing in deep music generation?: 15–35 % new material; tonic at 35 % of phrase ends vs 20 % inside.
  https://ar5iv.labs.arxiv.org/html/2209.00182
- [L6] Spontaneous Reward Hacking in Iterative Self-Refinement: self-grading raises only the grade. https://arxiv.org/abs/2407.04549
- [L7] How Many Instructions Can LLMs Follow at Once?: 94.8 % at 100 instructions, 72.9 % at 250. https://arxiv.org/html/2507.11538v1
- [L8] Semantic Gravity Wells: naming a forbidden word pulls output toward it. https://arxiv.org/html/2601.08070v1
- [L9] Gold et al. 2019: liking peaks at medium surprise. https://pmc.ncbi.nlm.nih.gov/articles/PMC6867811

Melody and rhythm
- Summach, The Structure, Function, and Genesis of the Prechorus (srdc). https://mtosmt.org/issues/mto.11.17.3/mto.11.17.3.summach.html
- Open Music Theory 2e, ch. 7 (phrases, making syncopation, 3+3+2). https://human.libretexts.org/Bookshelves/Music/Music_Theory/Open_Music_Theory_2e_(Gotham_et_al.)/07%3A_Popular_Music
- Tan, Lustig & Temperley 2019, Anticipatory Syncopation in Rock. https://doi.org/10.1525/mp.2019.36.4.353
- Hamilton & Pearce 2024 (Billboard melodies: intervals, notes per bar). https://pmc.ncbi.nlm.nih.gov/articles/PMC11224395/
- Temperley & de Clercq 2013 (scale-degree frequencies in rock melodies). https://www.midside.com/publications/temperley_declercq_2013.pdf
- Jakubowski et al. 2017 (features of earworms). https://www.eurekalert.org/news-releases/555535
- Pattison, The Art of Phrasing. https://www.patpattison.com/art-of-phrasing
- Berklee Online, Simple Tools for Better Melodies. https://online.berklee.edu/takenote/simple-tools-for-better-melodies/
- Starr & Shih 2017 (Japanese morae and notes). https://doi.org/10.5334/gjgl.355

Form and energy
- Nobile, Teleology in Verse–Prechorus–Chorus Form. https://www.mtosmt.org/ojs/index.php/mto/article/view/864/206
- Geary 2024 (drum patterns per hit song, changes inside sections). https://www.mtosmt.org/issues/mto.24.30.2/mto.24.30.2.geary.php
- Building Bridges: Rebuilds in Popular Song (lowest point 62.6 %, arrival 79.9 %). https://theory.esm.rochester.edu/integral/?p=9827
- Owsinski, Song Arrangement Elements (five roles, four at once). https://bobbyowsinskiblog.com/song-arrangement-elements/
- Senior, Mix Rescue: Strong Arrangements. https://www.soundonsound.com/techniques/mix-rescue-strong-arrangements
- Ewer, Making an Energy Chart for Your Song. https://www.secretsofsongwriting.com/2014/07/16/making-an-energy-chart-for-your-song/
- Hit Songs Deconstructed (time to the first chorus). https://reports.hitsongsdeconstructed.com/articles/hip-hop-vs-pop-which-hooks-you-faster/
- J-pop form: https://www.starroute.jp/column/kyoku-kousei-arrange/ , https://kogamusic.net/articles/anison-production.html ;
  modulation: https://soundquest.jp/quest/chord/chord-mv4/modulation-in-pops-2/

Arrangement and groove
- Hein, Low end theory. https://www.ethanhein.com/wp/2026/low-end-theory/
- Guitar World on walking bass and James Jamerson. https://www.guitarworld.com/lessons/bring-a-new-level-of-rhythm-and-groove-to-your-guitar-playing-with-jazz-walking-basslines
- Premier Guitar, On Bass / Double Trouble. https://www.premierguitar.com/on-bass-when-your-go-to-lines-arent-enough , https://www.premierguitar.com/double-trouble-make-rhythm-guitar-parts-great-again
- Berklee, Writing String Pads. https://www.berklee.edu/node/2436901
- Sound On Sound, Arranging Pop. https://www.soundonsound.com/techniques/arranging-pop
- iZotope, arrangements around vocals. https://www.izotope.com/en/learn/how-to-write-better-song-arrangements-around-vocals
- Frontiers in Psychology 2017 (random timing deviations lower ratings). https://www.frontiersin.org/journals/psychology/articles/10.3389/fpsyg.2017.01709/full
