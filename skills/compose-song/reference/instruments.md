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

**VST/AU/AAX plugins and Kontakt libraries cannot be loaded** (the engine plays WAV samples). Ways around it:
1. Render the plugin's notes to WAV in a DAW — one note every few semitones (e.g. every minor third, C1–C7), named
   `<Name>_<Note>.wav` — and `scan` the folder; or
2. Convert the patch to SFZ + WAV with a sampler or converter that exports SFZ, then `scan` it; or
3. Use the MIDI export and play it through the plugin in a DAW.

## How instruments map to the engine

The engine plays one sample per part, pitched from its root key. A track becomes one engine part per **sample zone its notes
use** (the region whose key range contains the note) — at most `splits` per track, and 32 parts per song. When there are
more zones than allowed, the least used are merged into their nearest kept zone (that sample is pitched further). Drum tracks
use one part per drum sound played; a sound missing from the kit falls back to a close one (e.g. pedal hat → closed hat) with a
warning. Each zone's SFZ `volume` is applied as gain; amounts above 1 go to the track's output stage automatically.

Licenses: always tell the user which sources a song uses. `render --unity` writes `CREDITS.md` with them.
