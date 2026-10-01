---
name: compose-song
description: Compose, arrange, mix and render music with the com.graze.music engine (playback V2) — writes a song spec, picks or downloads instruments (built-in synths, VSCO 2 CE orchestral samples, or SFZ/WAV instruments on the user's computer), renders a WAV with a level report, and exports stems, MIDI, MusicXML/PDF sheet music and a Unity song folder. Use when the user asks to make, write, compose, arrange, remix, mix or render a song, melody, BGM, jingle, loop or score, or to turn music into .wav / MIDI / sheet music.
---

# Compose a song with notewright

You write a **song spec** (JSON), the plugin compiles it for the com.graze.music engine (the same runtime the Unity game uses,
playback V2: 1024 voices, glide, drive, per-track effects) and renders it offline. You cannot hear the result: use the render
report (levels per section and per track, clipping, dropped notes) and the user's feedback.

Run every command as (on Windows use `python` instead of `python3`):

```
python3 "${CLAUDE_PLUGIN_ROOT}/notewright.py" <command> ...
```

Every command prints JSON. Data (instrument downloads, catalog, renderer build) lives in `~/.notewright`
(override with `NOTEWRIGHT_HOME`).

## Workflow

1. **First use:** `doctor`. It reports the runtime that renders audio (`runtime`), how instruments download
   (`instrumentDownloads`) and, if rendering is not possible, `error` and `fix`. The renderer runs on the .NET 6+ SDK
   (`dotnet`, used first when installed), Windows' built-in .NET Framework (`netfx`) or Mono; with Mono or .NET Framework the
   first run downloads the C# compiler (~10 MB, once). All of them render byte-identical audio, but `dotnet` is about twice as
   fast, which adds up over stems and several mix passes.
   - **`faster` in the report** (`ok` is true but the runtime is `netfx` or `mono`): improve the runtime before the first
     render. If it says dotnet is already installed, use it (unset `NOTEWRIGHT_RUNTIME` or set it to `dotnet`). Otherwise, on
     the user's own computer, show the install command it names and ask before installing; in a disposable sandbox, install
     the .NET 8 SDK yourself (below) and run `doctor` again.
   - **On the user's own computer:** if `ok` is false, tell the user the `fix` command; don't install system packages yourself
     unless they say so.
   - **In a disposable sandbox** (claude.ai chat, Cowork, a cloud session): if `ok` is false and you can install packages,
     install the .NET 8 SDK from the OS package archive and run `doctor` again — Debian/Ubuntu:
     `apt-get update && apt-get install -y --no-install-recommends dotnet-sdk-8.0` (prefix `sudo` when not root).
   - **No runtime at all:** still deliver the music: `render <spec> --score-only` writes the MIDI and MusicXML score without
     the renderer. Say the WAV needs .NET or Mono (Claude Code on a computer, or a sandbox that can install packages).
   - `instrumentDownloads`: `https`, or `git` when only github.com is reachable (handled automatically, git must be installed).
     If it is unavailable, use built-in synths (`synth:*`) and the user's own instruments, and say that VSCO 2 needs network
     access to github.com (on claude.ai: code execution network access "package managers" or wider).
2. **Understand the brief:** genre, mood, tempo, length, key, instruments, output formats (WAV / stems / MIDI / sheet music
   PDF / Unity). Ask only about what you cannot reasonably choose yourself; otherwise decide and say what you chose.
   **A reference song** ("like X", "in the style of X"): unless the user says how to use it, borrow part of its chord
   progression so a listener recognizes it on first hearing, and write your own melody over it; see
   [reference/composition.md](reference/composition.md#reference-songs).
3. **Pick instruments** (see [reference/instruments.md](reference/instruments.md)):
   - `instruments find <words> [--kind drums]` searches built-in synths, the downloadable VSCO 2 CE orchestra and anything
     scanned on this computer. Use the ids it returns, or write `"find:<words>"` in the spec to pick the best match automatically.
   - The user's own instruments: `instruments scan <folder>` registers SFZ files, note-named WAV sets (e.g. `Piano_C4.wav`),
     drum one-shots and graze `Instruments~` libraries as `local:<lib>/<name>`; `instruments add <id> <file.wav> --root C3`
     adds one sample. VST/AU plugins cannot be hosted: ask the user to export the sound to WAV/SFZ first (see reference).
4. **Write the spec** to a `.json` file in the user's project or a folder they chose (start from
   `new <path> --template pop|orchestral` or write it directly). Format: [reference/spec.md](reference/spec.md).
   Composition guidance: [reference/composition.md](reference/composition.md).
5. **Check:** `check <spec>` compiles without rendering (downloads missing instruments) and lists sections, tracks, engine parts
   and warnings. Fix errors and re-check.
6. **Render:** `render <spec> [--stems] [--pdf] [--unity] [--section NAME | --bars 9-16]`. Default output:
   `<spec folder>/out/<title>/` with `<title>.wav`, `.mid`, `.musicxml`, `report.json` and `<title>.song.json`
   (the engine data). A render runs about 8× faster than real time on `dotnet` (about 4× on .NET Framework or Mono).
   `--stems` adds one pass per track (plus one for noise regions), each about as long as the mix, rendered up to 4 at a time
   (`--jobs`), so use it once for balance and render a section or bar range while iterating. A range render plays what comes before it (so reverb and held notes carry in) and
   stops at its end: the tail (`--tail`, default 2 s) only lets sounding notes, echoes and reverb ring out, and nothing of the
   next section plays in it; `sections[]` and `stems[].notes` cover only the range.
7. **Mix from the report** (see [reference/mixing.md](reference/mixing.md)): `render.peakDb` / `outputPeakDb`,
   `render.outputRmsDb` (the file's level; `rmsDb` and every other level are before `--normalize`), `sections[].rmsDb` (energy curve), `stems[].rmsDb` (track balance, with `--stems`), `render.droppedNotes`,
   and `advice`. Adjust gains, pans, effects, master and arrangement, then re-render. Two or three passes are usually enough.
8. **Deliver:** give the user the file paths (WAV, stems, MIDI, MusicXML/PDF, Unity folder), a short description of the
   structure (sections with bar numbers), the instruments and their licenses (VSCO 2 CE is CC0; built-in synths are generated),
   and what to listen for. Ask for feedback in musical terms (e.g. "lead too loud in the chorus?") and iterate.

## Output formats

| Want | Flag | Result |
|---|---|---|
| WAV mix | (default) | `<title>.wav`, 48 kHz 24-bit stereo (`--rate`, `--bits 16/24/32`, `--normalize -1`, `--tail 3`) |
| Stems | `--stems` | `stems/<track>.wav`, one per track, plus `stems/regions.wav` for noise regions; same gain as the mix, they add up to it |
| MIDI | (default) | `<title>.mid` (type 1, tempo map, section markers, GM programs, drums on channel 10) |
| Sheet music | (default) / `--pdf` | `<title>.musicxml` (opens in MuseScore, Dorico, Finale, Sibelius); `--pdf` needs MuseScore installed. `convert a.musicxml a.pdf` converts later |
| Unity | `--unity` | `unity/<title>.song.json` + `Samples/` + `CREDITS.md` for com.graze.music's `SongJsonBuilder` |
| Audio only / score only | `--no-score` / `--score-only` | skip the other half |

## Rules

- Keep `playbackVersion` at 2 unless the user needs V1 (older Unity builds). Never promise effects the engine lacks: there is no
  reverb, EQ bands, automation curves or per-note pitch bend; use echo, filters, drive, compressor, gate and glitch regions.
- Engine limits per song: 32 engine parts (each track uses one part per sample zone or drum sound actually played, plus additive
  noise regions), 1024 simultaneous voices, 10–60000 BPM. `check` reports the part count and reduces sample zones automatically.
- In a sandbox the user cannot open your file paths: render with `--out` into the folder your environment shares with the user
  (for example its outputs folder), or copy the deliverables there.
- Don't overwrite a user's spec or output without asking; write new files or versioned names (`song-v2.json`).
- Credit instrument sources when delivering. Files from the user's own libraries keep their own licenses.
- If the user wants the song inside the Unity game project, deliver `--unity` output and point to `SongJsonBuilder.Build`
  (the com.graze.music Unity package; see `${CLAUDE_PLUGIN_ROOT}/engine/song-json.md`); do not edit the game repository unless asked.
