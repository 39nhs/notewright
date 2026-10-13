"""Builds and runs the headless renderer: the music engine (engine/Runtime/*.cs) + engine/Renderer.

Runtimes, tried in this order (NOTEWRIGHT_RUNTIME=netfx|dotnet|mono picks one):
- dotnet: a .NET SDK 6 or newer, compiled with the SDK's own C# compiler, so nothing is downloaded. Debian/Ubuntu ship it
  as the dotnet-sdk-8.0 package, which also works where only the OS package archive is reachable (claude.ai's sandbox).
  It renders about twice as fast as the other two, so it comes first wherever it is installed.
- netfx (Windows): the built-in .NET Framework 4.x, compiled with Roslyn downloaded once from NuGet. The fallback that works
  on every Windows without installing anything.
- mono (Linux/macOS): Mono, compiled with Roslyn downloaded once from NuGet.
Every runtime renders byte-identical audio; engine/run-tests.sh runs the playback fingerprints on Mono and on .NET."""
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

from . import paths

ROSLYN = "4.8.0"
ROSLYN_URL = f"https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/{ROSLYN}/microsoft.net.compilers.toolset.{ROSLYN}.nupkg"
NOWARN = "-nowarn:0414,0169,0649,0162,0618"


class EngineError(RuntimeError):
    pass


def install_hint() -> str:
    if os.name == "nt":
        return "install .NET Framework 4.8 (built into Windows 10/11) or the .NET 8 SDK"
    if sys.platform == "darwin":
        return "install the .NET 8 SDK (brew install --cask dotnet-sdk) or Mono (brew install mono)"
    return ("install the .NET 8 SDK (Debian/Ubuntu: sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0) "
            "or Mono (sudo apt-get install -y mono-devel)")


def sdk_hint() -> str:
    """How to install the .NET SDK, the fastest runtime."""
    if os.name == "nt":
        return "winget install Microsoft.DotNet.SDK.8"
    if sys.platform == "darwin":
        return "brew install --cask dotnet-sdk"
    return "sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0 (Debian/Ubuntu)"


def _version(text):
    return tuple(int(n) for n in re.findall(r"\d+", text)[:3])


def _netfx_assemblies(folder):
    return [str(folder / name) for name in ("mscorlib.dll", "System.dll", "System.Core.dll")]


def _netfx():
    if os.name != "nt":
        raise EngineError(".NET Framework is Windows only.")
    windir = os.environ.get("WINDIR", r"C:\Windows")
    for sub in ("Framework64", "Framework"):
        folder = Path(windir) / "Microsoft.NET" / sub / "v4.0.30319"
        if (folder / "mscorlib.dll").exists():
            return {"kind": "netfx", "version": "4.x", "run": [], "refs": _netfx_assemblies(folder), "ext": ".exe"}
    raise EngineError(".NET Framework 4.x not found.")


def _mono():
    mono = shutil.which("mono")
    if not mono:
        raise EngineError("Mono not found.")
    prefix = Path(os.path.realpath(mono)).parent.parent
    for folder in (prefix / "lib" / "mono" / "4.5", Path("/usr/lib/mono/4.5"),
                   Path("/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/4.5")):
        if (folder / "mscorlib.dll").exists():
            return {"kind": "mono", "version": "", "run": [mono], "refs": _netfx_assemblies(folder), "ext": ".exe"}
    raise EngineError("Mono found but its 4.5 framework assemblies are missing (install mono-devel).")


def _dotnet():
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise EngineError(".NET SDK (dotnet) not found.")
    roots = [Path(os.environ["DOTNET_ROOT"])] if os.environ.get("DOTNET_ROOT") else []
    roots.append(Path(os.path.realpath(dotnet)).parent)
    for root in roots:
        compilers = sorted(root.glob("sdk/*/Roslyn/bincore/csc.dll"), key=lambda c: _version(c.parent.parent.parent.name))
        packs = [r for r in root.glob("packs/Microsoft.NETCore.App.Ref/*/ref/net*") if _version(r.parent.parent.name)[:1] >= (6,)]
        if compilers and packs:
            ref = max(packs, key=lambda r: _version(r.parent.parent.name))
            major, minor = _version(ref.parent.parent.name)[:2]
            return {"kind": "dotnet", "version": ref.parent.parent.name, "run": [dotnet], "compiler": [dotnet, str(compilers[-1])],
                    "refs": sorted(str(dll) for dll in ref.glob("*.dll")), "ext": ".dll", "tfm": ref.name,
                    "framework": f"{major}.{minor}.0"}
    raise EngineError("dotnet found, but no .NET 6+ SDK (C# compiler and reference assemblies); install the .NET 8 SDK, not only the runtime.")


FINDERS = {"netfx": _netfx, "dotnet": _dotnet, "mono": _mono}


def _order():
    return ["dotnet", "netfx", "mono"] if os.name == "nt" else ["dotnet", "mono"]


def _verified_file():
    return paths.home() / "runtime.json"


def verified_kind():
    """The runtime `setup` last verified with a test render here, or None."""
    try:
        kind = json.loads(_verified_file().read_text(encoding="utf-8")).get("kind")
        return kind if kind in FINDERS else None
    except (OSError, ValueError):
        return None


def runtime() -> dict:
    """The .NET runtime that builds and runs the renderer: NOTEWRIGHT_RUNTIME, else the one `setup` verified (while it is still
    installed), else the first found in the module docstring's order."""
    choice = os.environ.get("NOTEWRIGHT_RUNTIME", "").strip().lower()
    if choice and choice not in FINDERS:
        raise EngineError(f"NOTEWRIGHT_RUNTIME={choice}: use one of {', '.join(FINDERS)}")
    verified = None if choice else verified_kind()
    order = [choice] if choice else ([verified] if verified else []) + [k for k in _order() if k != verified]
    problems = []
    for kind in order:
        try:
            return FINDERS[kind]()
        except EngineError as error:
            problems.append(str(error))
    raise EngineError(" ".join(problems) + " To render audio, " + install_hint() + ".")


def _roslyn():
    folder = paths.home() / "tools" / f"roslyn-{ROSLYN}"
    csc = folder / "tasks" / "net472" / "csc.exe"
    if not csc.exists():
        folder.mkdir(parents=True, exist_ok=True)
        package = folder.with_suffix(".nupkg")
        print(f"downloading the C# compiler (Roslyn {ROSLYN}, once) ...", file=sys.stderr)
        try:
            urllib.request.urlretrieve(ROSLYN_URL, package)
        except OSError as error:
            raise EngineError(f"could not download the C# compiler from api.nuget.org ({error}); "
                              "install the .NET 8 SDK instead, which has its own compiler") from error
        with zipfile.ZipFile(package) as z:
            z.extractall(folder)
        package.unlink()
    return csc


def build(force=False, rt=None) -> Path:
    """Compiles the renderer if the engine or renderer sources changed; returns the exe (or .dll on .NET) path."""
    rt = rt or runtime()
    sources = paths.engine_sources()
    digest = hashlib.sha256()
    for source in sources:
        digest.update(source.name.encode())
        digest.update(source.read_bytes())
    exe = paths.home() / "engine" / f"{digest.hexdigest()[:16]}-{rt['kind']}" / ("graze-renderer" + rt["ext"])
    if exe.exists() and not force:
        return exe
    exe.parent.mkdir(parents=True, exist_ok=True)
    compiler = rt.get("compiler") or rt["run"] + [str(_roslyn())]
    command = compiler + ["-nologo", "-optimize+", "-langversion:9", NOWARN, "-noconfig", "-nostdlib"] + \
        [f"-r:{ref}" for ref in rt["refs"]] + [f"-out:{exe}"] + [str(s) for s in sources]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0 or not exe.exists():
        raise EngineError("renderer build failed:\n" + (result.stdout + result.stderr)[-4000:])
    if rt["kind"] == "dotnet":
        config = {"runtimeOptions": {"tfm": rt["tfm"], "rollForward": "LatestMajor",
                                     "framework": {"name": "Microsoft.NETCore.App", "version": rt["framework"]}}}
        exe.with_suffix(".runtimeconfig.json").write_text(json.dumps(config), encoding="utf-8")
    return exe


def render(song_json: Path, out_wav: Path, *, rate=48000, bits=16, start_beat=None, length_beats=None, tail=0.0,
           normalize=None, stems_dir=None, jobs=1, rt=None) -> dict:
    rt = rt or runtime()
    exe = build(rt=rt)
    command = rt["run"] + [str(exe), "render", "--song", str(song_json), "--out", str(out_wav), "--rate", str(rate), "--bits", str(bits),
                      "--tail", str(tail)]
    if start_beat is not None:
        command += ["--start", str(start_beat)]
    if length_beats is not None:
        command += ["--length", str(length_beats)]
    if normalize is not None:
        command += ["--normalize", str(normalize)]
    if stems_dir is not None:
        command += ["--stems", str(stems_dir), "--jobs", str(max(1, jobs))]
    command += _vst3_options()
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
    lines = [l for l in result.stdout.splitlines() if l.startswith("{")]
    if not lines:
        raise EngineError("renderer produced no report:\n" + (result.stdout + result.stderr)[-4000:])
    report = json.loads(lines[-1])
    if not report.get("ok"):
        raise EngineError(report.get("error", "render failed"))
    return report


def _vst3_options():
    """Where the renderer finds the VST3 worker and keeps its plugin scan (used only by songs with VST3 plugins, Windows x64)."""
    return ["--plugin-host", str(paths.PLUGIN_HOST), "--scan-cache", str(paths.home() / "vst3-scan.json")]


def _plugin_command(args, rt=None) -> dict:
    if not paths.vst3_supported():
        raise EngineError(paths.VST3_UNAVAILABLE)
    return _command(args, rt)


def _command(args, rt=None) -> dict:
    """Runs a renderer action (plugins, plugin-info, tempo) and returns its JSON report."""
    rt = rt or runtime()
    exe = build(rt=rt)
    result = subprocess.run(rt["run"] + [str(exe)] + args + _vst3_options(), capture_output=True, text=True, encoding="utf-8")
    lines = [l for l in result.stdout.splitlines() if l.startswith("{")]
    if not lines:
        raise EngineError("renderer produced no report:\n" + (result.stdout + result.stderr)[-4000:])
    report = json.loads(lines[-1])
    if not report.get("ok"):
        raise EngineError(report.get("error", "plugin command failed"))
    return report


def plugins(rescan=False, roots=None) -> dict:
    """Installed VST3 plugins (Windows x64): {roots, plugins [{name, vendor, version, classId, kind, path}], errors}."""
    args = ["plugins"] + (["--rescan", "true"] if rescan else []) + (["--vst3-roots", roots] if roots else [])
    return _plugin_command(args)


def plugin_info(name, preset=None, roots=None) -> dict:
    """One VST3 plugin's parameters (keys for "parameters"/"automation"), latency and state (default, or a .vstpreset's)."""
    args = ["plugin-info", "--plugin", name] + (["--preset", str(preset)] if preset else []) + (["--vst3-roots", roots] if roots else [])
    return _plugin_command(args)


def tempo(audio, beats_per_bar=4, min_bpm=60, max_bpm=200) -> dict:
    """Tempo, beats and the first bar start of a WAV recording (the engine's MusicBeatDetector)."""
    return _command(["tempo", "--audio", str(audio), "--beats-per-bar", str(beats_per_bar), "--min", str(min_bpm), "--max", str(max_bpm)])


def _smoke(rt):
    """Builds the renderer with this runtime and renders a one-bar song with the built-in synth; returns its peak (dBFS)."""
    import tempfile
    folder = Path(tempfile.mkdtemp(prefix="notewright-setup-"))
    try:
        song = {"name": "setup", "bpm": 120, "beatsPerBar": 4, "loopBeats": 4, "playbackVersion": 3,
                "parts": [{"name": "test", "layer": "Melody", "synthPreset": "Pluck", "gain": .5, "notes": [0, 1, 60, .8, 1, 1, 64, .8]}]}
        (folder / "setup.song.json").write_text(json.dumps(song), encoding="utf-8")
        report = render(folder / "setup.song.json", folder / "setup.wav", rate=22050, tail=.2, rt=rt)
        if report["peakDb"] < -60:
            raise EngineError(f"the test render with {rt['kind']} is silent")
        return report["peakDb"]
    finally:
        shutil.rmtree(folder, ignore_errors=True)


def setup_runtime() -> dict:
    """Finds the runtime that really works here: tries each installed one in order (fastest first) with a build and a test render,
    keeps the first that passes (saved for later runs) and reports the others. NOTEWRIGHT_RUNTIME limits it to that one."""
    choice = os.environ.get("NOTEWRIGHT_RUNTIME", "").strip().lower()
    kinds = [choice] if choice else _order()
    tried = []
    for kind in kinds:
        if kind not in FINDERS:
            raise EngineError(f"NOTEWRIGHT_RUNTIME={kind}: use one of {', '.join(FINDERS)}")
        try:
            rt = FINDERS[kind]()
        except EngineError as error:
            tried.append({"runtime": kind, "status": "not installed", "detail": str(error)})
            continue
        try:
            _smoke(rt)
        except Exception as error:  # a broken install: fall back to the next runtime
            tried.append({"runtime": kind, "status": "failed", "detail": str(error)[-600:]})
            continue
        tried.append({"runtime": kind, "status": "ok", "version": rt["version"]})
        if not choice:
            _verified_file().write_text(json.dumps({"kind": kind}), encoding="utf-8")
        return {"ok": True, "runtime": (rt["kind"] + " " + rt["version"]).strip(), "kind": kind, "tried": tried}
    try:
        _verified_file().unlink()
    except OSError:
        pass
    return {"ok": False, "tried": tried, "fix": install_hint()}


def doctor() -> dict:
    info = {"python": sys.version.split()[0], "platform": sys.platform, "engine": str(paths.ENGINE_DIR)}
    try:
        rt = runtime()
        info["runtime"] = (rt["kind"] + " " + rt["version"]).strip()
        info["renderer"] = str(build())
        info["ok"] = True
        info["vst3"] = ("available (Windows x64): `notewright plugins` lists the installed VST3 plugins" if paths.vst3_supported()
                        and paths.PLUGIN_HOST.exists() else "not available here: VST3 plugins work only on Windows x64")
    except Exception as error:  # report, don't crash
        info["ok"] = False
        info["error"] = str(error)
        info["fix"] = install_hint()
        return info
    if rt["kind"] != "dotnet":
        try:
            sdk = _dotnet()
            info["faster"] = (f"dotnet {sdk['version']} is installed and renders about 2x faster with identical output: "
                              f"unset NOTEWRIGHT_RUNTIME (now {os.environ.get('NOTEWRIGHT_RUNTIME')}) or set it to dotnet")
        except EngineError:
            info["faster"] = f"the .NET 8 SDK renders about 2x faster than {rt['kind']} with identical output; install it: {sdk_hint()}"
    return info
