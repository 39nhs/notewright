# Instruments

All commands: `python3 "${CLAUDE_PLUGIN_ROOT}/notewright.py" instruments <action> ...`

| Action | Example | Does |
|---|---|---|
| `find` | `find warm pad`, `find drums --kind drums` | ranks every known instrument by id, name, tags and synonyms (piano→keys/upright, 808→sub/bass, …) |
| `list` | `list --installed`, `list --kind drums` | all instruments (`status`: installed or available to download) |
| `info` | `info vsco2:harp` | regions (key ranges, root keys, sample files) and license |
| `fetch` | `fetch vsco2:harp vsco2:timpani [--velocity 64] [--all-layers]` | downloads now (specs also download on first use) |
| `scan` | `scan "D:/Samples/MyPiano" --name mypiano` | registers instruments found in a folder on this computer |
| `add` | `add local:boom D:/one-shots/boom.wav --root C2` | registers one WAV played chromatically from `root` |
| `remove` | `remove local:mypiano/rhodes` | forgets a scanned/added/downloaded instrument (files stay) |

## Sources

**Built-in synths** (`synth:`, generated on first use, CC0): `sub-bass` (808-style sine), `saw-bass`, `saw-lead`,
`square-lead`, `pad` (detuned saws, soft), `epiano` (FM), `organ`, `bell` (FM), `pluck` (Karplus-Strong, guitar/harp-like),
`piano-synth`, and `drum-kit` on General MIDI keys (kick 36, rim 37, snare 38, clap 39, toms 41/45/47/50, closed hat 42,
pedal hat 44, open hat 46, crash 49, ride 51). Good for electronic, lo-fi, game and chiptune-adjacent styles.

**The engine's own synth** (playback V3, no samples): `{"synthPreset": "<name>"}` in `instruments` — `Saw lead`, `Square lead`,
`Pluck`, `Warm pad`, `Sub bass`, `Acid bass`, `Brass`, `Bell keys`, `Noise sweep`, `Jazz organ`, `Jazz organ perc`, `Rock organ`,
`Gospel organ`, `FM e-piano`, `FM bell`, `FM bass`, `Wavetable pad`, `Wavetable vox`, `Init` — or `{"synth": {...}}` with your own
settings (see spec.md). Unlike the sampled `synth:` instruments it has filters with envelopes, LFOs, unison and real organ/FM
tones, and `automation` can sweep its `brightness` (filter) and `modulation` (LFO depth).

**Your recordings** play as `audio` clips on a track (spec.md, Playback V3 features): loops, vocal takes, sound effects.

**VS Chamber Orchestra 2 Community Edition** (`vsco2:`, CC0 1.0 — public domain, credit appreciated: "VSCO 2 CE by
Versilian Studios"). 67 instruments: string sections (violin, viola, cello, contrabass: sustain, vibrato, tremolo, pizzicato,
spiccato), solo violin, woodwinds (flute, piccolo, oboe, clarinet, bassoon), brass (french horn, trumpet, trombone, tuba, with
mutes), harp, upright pianos, organ, glockenspiel, marimba, xylophone, tubular bells, timpani, orchestral percussion kit.
Downloads are per sample from a pinned commit, one velocity layer (default 100) and the first round robin, so an instrument
is typically 1–30 MB. `--all-layers` downloads everything but the engine still plays one layer per song.

**The user's computer** (`local:`):
- **SFZ** instruments (any `.sfz` with WAV samples): key ranges, root keys, velocity layers, volume, envelopes and default
  key switch are read. FLAC/OGG samples are not supported: convert to WAV.
- **Note-named WAV sets**: files whose names contain a note (`Piano_C4_mf.wav`, `strings-A#2.wav`) become one multi-sample
  instrument per folder; velocity words (`pp`…`ff`, `v1`…) become layers.
- **Drum one-shots**: files named like kick/snare/hat/clap/crash/ride/tom/shaker… become a kit on GM keys.
- Other WAVs become one-shot instruments (root C4; change with `add … --root`).
- A **graze `Instruments~` library** (sessions/*.json + samples/) from com.graze.music: each recorded instrument with its root key.

**VST3 plugins work only on Windows x64**: there, `plugins` lists the installed ones and a spec uses them as
`{"vst3": "<name>"}` instruments and `inserts` effects ([spec.md](spec.md#vst3-plugins-windows-x64-only)). **On macOS, Linux, the
claude.ai/Cowork sandbox or any other system VST3 cannot be used**, and **AU/AAX plugins and Kontakt libraries cannot be loaded
anywhere**. Ways around it:
1. Render the plugin's notes to WAV in a DAW — one note every few semitones (e.g. every minor third, C1–C7), named
   `<Name>_<Note>.wav` — and `scan` the folder; or
2. Convert the patch to SFZ + WAV with a sampler or converter that exports SFZ, then `scan` it; or
3. Use the MIDI export and play it through the plugin in a DAW.

## Shouts (vocal ad-libs)

The "hey", "let's go" and "come on" in dance music mostly come from sample packs. Many commercial packs allow use in songs but
not redistributing the samples, so a song folder or a game cannot ship them. A CC0 set: **Producer Space — Dance Vocal Shouts
1 and 2** (WAV 24-bit 44.1 kHz, about 275 shouts, 48 MB: Hey, Go, Let's Go, Come On, Here We Go, Drop, Oh, Yeah, Everybody,
Turn it Up, counts One–Ten, Here Comes the Drop, Are You Ready for This; high and low voices mixed; pack 2 has some
profanity) from https://archive.org/details/producer-space-cc0-sample-library (license: https://producerspace.com/license).
Download only those two pack folders, and only on the user's computer with their go-ahead.

- **Use them as `audio` clips** on a track named `shouts` (not vox/vocal, so `check` does not take it for the tune). Trim the
  silence and match the peaks first: the files range from −0.1 to −15 dB.
- **Put the strongest syllable on the beat,** not the file's start: "let's GO" starts its GO about 0.3 s in. Measure where the
  loudest syllable begins and start the clip that much earlier (`"beat"` a little before the target, or `"sourceStart"` past
  the breath). One-syllable shouts (hey, go) start 0.01–0.05 s in.

| Where | What | Note |
|---|---|---|
| the silent beat at the end of a build | "here we go", "let's go" | the strong syllable on the silent beat or the drop's first beat |
| the bar before a stop | "here comes the drop" | "drop" on the beat |
| the last four beats of a build | "one, two, three, four" | one a beat, louder each time |
| a breakdown | "are you ready for this" | once, with a reverb send |
| drops and choruses | "hey" | with the clap/snare (beat 3 in half time, 4 in four on the floor), **only in the last bar of a 4-bar line**; in the last chorus on 2 and 4 of that bar |
| line ends, a section's first beat | "oh", "yeah", "come on", "go" | once in 4–8 bars |

- **Level:** shout peaks 2–3 dB under the drum peaks; a repeated "hey" at `gainDb` well below the one before the drop.
- **Sparingly:** a shout announces something (a drop, a count, a breakdown line, a section's first beat). A "hey" every bar
  becomes part of the drum kit and stops sounding like an ad-lib: once per four bars, about 10–25 in a two-minute song, none in
  verses or quiet sections, different words per section.
- **One voice per song:** the high or the low voice; cute songs the high one, hardcore and hip-hop suit the low one too.
- Credit the pack when delivering (CC0 needs no credit, but say where the voices came from). There are no pitched vocal chops
  in these packs: `Wavetable vox` short notes stand in.

## How instruments map to the engine

The engine plays one sample per part, pitched from its root key. A track becomes one engine part per **sample zone its notes
use** (the region whose key range contains the note) — at most `splits` per track, and 32 parts per song. When there are
more zones than allowed, the least used are merged into their nearest kept zone (that sample is pitched further). Drum tracks
use one part per drum sound played; a sound missing from the kit falls back to a close one (e.g. pedal hat → closed hat) with a
warning. Each zone's SFZ `volume` is applied as gain; amounts above 1 go to the track's output stage automatically.

Licenses: always tell the user which sources a song uses. `render --unity` writes `CREDITS.md` with them.
