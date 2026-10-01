# notewright Claude Code plugin

A standalone Claude Code plugin: Claude writes song specs, `notewright/` compiles them, and `engine/` (a C# music engine plus a
headless renderer) renders WAV, stems, MIDI, MusicXML/PDF and Unity song folders.

## Independent project

- This project is developed **separately from the com.graze.music Unity module** (a private repository).
  `engine/` started as a copy of its runtime on 2026-10-01 and is now owned here. Do not merge from or push to
  the Unity repository, and do not plan work in one repository around the other's progress. If a change is wanted in both, it
  is done separately in each, as each project's own work.
- `render --unity` writes the song.json format as of the split for the Unity package's `SongJsonBuilder`. If engine fields
  change here, say in `engine/song-json.md` which ones the Unity export cannot carry; never reference game project paths.

## Layout

| Path | Contents |
|---|---|
| `.claude-plugin/` | plugin manifest + marketplace (keep `version` in plugin.json current) |
| `skills/compose-song/` | the skill Claude follows (SKILL.md + reference/) |
| `notewright.py`, `notewright/` | Python CLI: catalog/sfz/synthkit (instruments), spec (compiler), midi, musicxml, export, engine (build + run) |
| `engine/Runtime/` | the music engine (C#, namespace `Graze.Presentation.Audio`; Unity types come from `engine/Stubs/UnityStubs.cs`) |
| `engine/Renderer/` | headless renderer: song.json → engine → WAV + JSON report |
| `engine/Tests/`, `engine/run-tests.sh` | engine tests incl. playback fingerprints (`bash engine/run-tests.sh [filter]` on Mono, `NOTEWRIGHT_RUNTIME=dotnet` for .NET) |
| `data/registry.json` | downloadable libraries, pinned (regenerate with `tools/build_vsco2_registry.py`) |
| `examples/`, `tests/` | example specs (compiled by the tests) and Python tests |

## Rules

- **Playback versions are frozen** (`engine/playback-versions.md`): any change to how audio is produced goes behind a new
  `MusicPlaybackVersion` with its own dispatch branch and recorded fingerprints; never edit a released version's code path or
  fingerprints, never remove a version, never change `Unrecorded`. Songs users made must keep sounding the same.
- Engine and renderer changes go together with the spec compiler (`notewright/spec.py`: track keys, mixer fields), the renderer's
  `SongLoader`, `skills/compose-song/reference/spec.md`, `engine/song-json.md` and tests — a feature is finished when Claude can
  write it in a spec, `check` validates it and `render` plays it.
- Python: standard library only, 3.9+, Windows/macOS/Linux. Every CLI command prints one JSON document on stdout; progress and
  downloads go to stderr. No `bin/` folder (claude.ai cannot install plugins that have one).
- Never commit audio samples or downloaded libraries; users' data lives in `~/.notewright` (`NOTEWRIGHT_HOME`).
  Registry instrument ids are public API: do not rename ids users may already have in specs.
- Code is licensed GPL-3.0-only (`LICENSE`); keep `license` in plugin.json and the README in line with it.
- Credit and license: keep each instrument source's license in the registry/catalog; exports carry credits.
- The renderer runs on .NET Framework (Windows), the .NET SDK and Mono (`notewright/engine.py`); all of them must render
  byte-identical audio, so engine code may not depend on runtime-specific behavior. The plugin must keep working where only
  package archives and github.com are reachable (claude.ai's default sandbox): no new download hosts without a fallback.
- Keep the README's network section current: list every host the plugin downloads from and what it writes.
- Keep the skill (SKILL.md, reference/) in sync with the CLI and spec format in the same change; keep examples rendering.

## Git and checks

- Work on a topic branch (`feat/`, `fix/`, `docs/`, or an assigned `claude/` branch) and reach `main` through a PR merged with a
  merge commit; never force-push `main`. Commit messages: `<scope>: <summary>` (scopes: plugin, engine, skill, instruments,
  docs, ci, release, merge).
- Before pushing: `python3 -m unittest discover -s tests` (renders through the engine; needs the .NET SDK or Mono on
  Linux/macOS) and, when `engine/` changed, `bash engine/run-tests.sh` on Mono and with `NOTEWRIGHT_RUNTIME=dotnet`.
  CI (`.github/workflows/plugin.yml`) runs all of them and checks that Mono and .NET render byte-identical WAVs.
- Releases: bump `.claude-plugin/plugin.json` `version` and `notewright/__init__.py` `__version__` together (a test checks;
  new playback version = MINOR) and tag `vX.Y.Z` on `main`; pushed tags
  are never moved.
- If a session may end, push work in progress and note what is left in the PR body.
