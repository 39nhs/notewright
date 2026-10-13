---
name: compose-song
description: Compose, arrange, mix and render music with the com.graze.music engine (playback V3) — writes a song spec, picks or downloads instruments (built-in synths, VSCO 2 CE orchestral samples, SFZ/WAV instruments on the user's computer, or the user's recordings), renders a WAV with a level report, and exports stems, MIDI, MusicXML/PDF sheet music and a Unity song folder. Use when the user asks to make, write, compose, arrange, remix, mix or render a song, melody, BGM, jingle, loop or score, or to turn music into .wav / MIDI / sheet music.
---

# Compose a song with notewright

You write a **song spec** (JSON), the plugin compiles it for the com.graze.music engine (playback V3: 1024 voices, a built-in
synth with presets, multi-zone sampler, automation and modulators, mix buses with sends and sidechain, EQ/chorus/reverb/amp,
limiter with a loudness target, per-note bends and vibrato, audio clips) and renders it offline. You cannot hear the result: use the render
report (levels per section and per track, clipping, dropped notes) and the user's feedback.

Run every command as (on Windows use `python` instead of `python3`):

```
python3 "${CLAUDE_PLUGIN_ROOT}/notewright.py" <command> ...
```

Every command prints JSON. Data (instrument downloads, catalog, renderer build) lives in `~/.notewright`
(override with `NOTEWRIGHT_HOME`).

## Workflow

1. **Start of every task: `setup`** — the start-up loop that heads off problems of this particular computer or sandbox. It
   checks Python, the data and temp folders (writable, free space), the renderer (it builds it and renders a test bar on each
   installed runtime, fastest first, and keeps the first that works, so a broken .NET install falls back to .NET Framework or
   Mono by itself), the built-in instruments, instrument downloads and MuseScore. It fixes what it can, lists the rest in
   `actions`, and caches a good result, so later runs answer at once until something changes (a new plugin version, a newly
   installed runtime, other settings). Loop until it is clean:
   - For each item in `actions`: on the user's own computer, tell the user the command and ask before installing anything;
     in a disposable sandbox (claude.ai chat, Cowork, a cloud session), do it yourself — e.g. the .NET 8 SDK from the OS
     package archive, Debian/Ubuntu: `apt-get update && apt-get install -y --no-install-recommends dotnet-sdk-8.0` (prefix `sudo`
     when not root). Then run `setup --force` again.
   - Stop when `ok` is true and nothing in `actions` can still be done here (a `speed` warning on the user's computer when they
     decline the SDK is fine).
   The renderer runs on the .NET 6+ SDK (`dotnet`), Windows' built-in .NET Framework (`netfx`) or Mono; all render byte-identical
   audio, `dotnet` about twice as fast. With Mono or .NET Framework the first run downloads the C# compiler (~10 MB, once).
   `doctor` is the same check without fixing or caching, for diagnosing.
   - **No runtime at all:** still deliver the music: `render <spec> --score-only` writes the MIDI and MusicXML score without
     the renderer. Say the WAV needs .NET or Mono (Claude Code on a computer, or a sandbox that can install packages).
   - `vst3`: `ok` only on Windows x64 (VST3 plugins usable); `info` everywhere else means VST3 cannot be used here.
   - `instrument downloads`: `https`, or `git` when only github.com is reachable (handled automatically, git must be installed).
     If it is unavailable, use built-in synths (`synth:*`), the engine synth and the user's own instruments, and say that VSCO 2
     needs network access to github.com (on claude.ai: code execution network access "package managers" or wider).
2. **Understand the brief:** genre, mood, tempo, length, key, instruments, output formats (WAV / stems / MIDI / sheet music
   PDF / Unity). Ask only about what you cannot reasonably choose yourself; otherwise decide and say what you chose.
   **A reference song** ("like X", "in the style of X"): unless the user says how to use it, borrow part of its chord
   progression so a listener recognizes it on first hearing, and write your own melody over it; see
   [reference/composition.md](reference/composition.md#reference-songs).
   **Start and end:** unless the user wants a cold open, a hard ending or a seamless loop, give every song a soft 1–2 bar
   lead-in and let its last 1–2 bars ring out; see [reference/composition.md](reference/composition.md#start-and-end-default).
3. **Pick instruments** (see [reference/instruments.md](reference/instruments.md)):
   - `instruments find <words> [--kind drums]` searches built-in synths, the downloadable VSCO 2 CE orchestra and anything
     scanned on this computer. Use the ids it returns, or write `"find:<words>"` in the spec to pick the best match automatically.
   - The user's own instruments: `instruments scan <folder>` registers SFZ files, note-named WAV sets (e.g. `Piano_C4.wav`),
     drum one-shots and graze `Instruments~` libraries as `local:<lib>/<name>`; `instruments add <id> <file.wav> --root C3`
     adds one sample.
   - **VST3 plugins: Windows x64 only.** On Windows, `plugins` lists the installed VST3 instruments and effects and
     `plugins --info NAME` their parameters; use them as `{"vst3": "<name>"}` instruments and `inserts` effects
     ([reference/spec.md](reference/spec.md#vst3-plugins-windows-x64-only)). **Everywhere else (macOS, Linux, the claude.ai/Cowork
     sandbox) VST3 cannot be used at all** (`setup` shows `vst3` unavailable): don't offer it; use the built-in synth, samples and
     built-in effects, or ask the user to export the plugin's sound to WAV/SFZ (see reference). AU/AAX plugins and Kontakt
     libraries cannot be loaded on any system.
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
7. **Mix from the report** (see [reference/mixing.md](reference/mixing.md); use automation, sends and buses rather than
   static levels only): `render.peakDb` / `outputPeakDb`,
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
| Unity | `--unity` | `unity/<title>.song.json` + `Samples/` + `CREDITS.md` for com.graze.music's `SongJsonBuilder` (+ `<title>.plugin-mod.json` for VST3 instruments) |
| Audio only / score only | `--no-score` / `--score-only` | skip the other half |

## Rules

- Use `playbackVersion` 3 (the default; see [reference/spec.md](reference/spec.md) "Playback V3 features"). Use 2 or 1 only to
  match a song made with an older version. Never promise what the engine lacks: VST3 plugins only on Windows x64 (never on
  macOS, Linux or a sandbox), no AU/AAX plugins or Kontakt anywhere, and no live/MIDI-keyboard playing or vocal synthesis.
- Engine limits per song: 32 engine parts (on V3 one per track, plus additive noise regions), 1024 simultaneous voices, 16 buses,
  10–60000 BPM. `check` reports the part count.
- In a sandbox the user cannot open your file paths: render with `--out` into the folder your environment shares with the user
  (for example its outputs folder), or copy the deliverables there.
- Don't overwrite a user's spec or output without asking; write new files or versioned names (`song-v2.json`).
- Credit instrument sources when delivering. Files from the user's own libraries keep their own licenses.
