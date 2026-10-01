"""Builds and runs the headless renderer: the music engine (engine/Runtime/*.cs) + engine/Renderer.

Runtimes, tried in this order (NOTEWRIGHT_RUNTIME=netfx|dotnet|mono picks one):
- netfx (Windows): the built-in .NET Framework 4.x, compiled with Roslyn downloaded once from NuGet.
- dotnet: a .NET SDK 6 or newer, compiled with the SDK's own C# compiler, so nothing is downloaded. Debian/Ubuntu ship it
  as the dotnet-sdk-8.0 package, which also works where only the OS package archive is reachable (claude.ai's sandbox).
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


def runtime() -> dict:
    """The .NET runtime that builds and runs the renderer (see the module docstring for the order)."""
    choice = os.environ.get("NOTEWRIGHT_RUNTIME", "").strip().lower()
    if choice and choice not in FINDERS:
        raise EngineError(f"NOTEWRIGHT_RUNTIME={choice}: use one of {', '.join(FINDERS)}")
    order = [choice] if choice else (["netfx", "dotnet", "mono"] if os.name == "nt" else ["dotnet", "mono"])
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


def build(force=False) -> Path:
    """Compiles the renderer if the engine or renderer sources changed; returns the exe (or .dll on .NET) path."""
    rt = runtime()
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
           normalize=None, stems_dir=None) -> dict:
    exe = build()
    command = runtime()["run"] + [str(exe), "render", "--song", str(song_json), "--out", str(out_wav), "--rate", str(rate), "--bits", str(bits),
                      "--tail", str(tail)]
    if start_beat is not None:
        command += ["--start", str(start_beat)]
    if length_beats is not None:
        command += ["--length", str(length_beats)]
    if normalize is not None:
        command += ["--normalize", str(normalize)]
    if stems_dir is not None:
        command += ["--stems", str(stems_dir)]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
    lines = [l for l in result.stdout.splitlines() if l.startswith("{")]
    if not lines:
        raise EngineError("renderer produced no report:\n" + (result.stdout + result.stderr)[-4000:])
    report = json.loads(lines[-1])
    if not report.get("ok"):
        raise EngineError(report.get("error", "render failed"))
    return report


def doctor() -> dict:
    info = {"python": sys.version.split()[0], "platform": sys.platform, "engine": str(paths.ENGINE_DIR)}
    try:
        rt = runtime()
        info["runtime"] = (rt["kind"] + " " + rt["version"]).strip()
        info["renderer"] = str(build())
        info["ok"] = True
    except Exception as error:  # report, don't crash
        info["ok"] = False
        info["error"] = str(error)
        info["fix"] = install_hint()
    return info
