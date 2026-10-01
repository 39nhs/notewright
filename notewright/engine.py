"""Builds and runs the headless renderer: the music engine (engine/Runtime/*.cs) + engine/Renderer, compiled with
Roslyn (downloaded once from NuGet). Windows runs it on the built-in .NET Framework 4.x; Linux/macOS need Mono."""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

from . import paths

ROSLYN = "4.8.0"
ROSLYN_URL = f"https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/{ROSLYN}/microsoft.net.compilers.toolset.{ROSLYN}.nupkg"


class EngineError(RuntimeError):
    pass


def _framework():
    """(host prefix for running .NET exes, folder with mscorlib/System/System.Core)."""
    if os.name == "nt":
        windir = os.environ.get("WINDIR", r"C:\Windows")
        for sub in ("Framework64", "Framework"):
            folder = Path(windir) / "Microsoft.NET" / sub / "v4.0.30319"
            if (folder / "mscorlib.dll").exists():
                return [], folder
        raise EngineError(".NET Framework 4.x not found (Windows 10/11 include it; install .NET Framework 4.8).")
    mono = shutil.which("mono")
    if not mono:
        raise EngineError("Mono is required on Linux/macOS: install mono-devel (Debian/Ubuntu: sudo apt install mono-devel; "
                          "macOS: brew install mono).")
    prefix = Path(os.path.realpath(mono)).parent.parent
    for folder in (prefix / "lib" / "mono" / "4.5", Path("/usr/lib/mono/4.5"),
                   Path("/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/4.5")):
        if (folder / "mscorlib.dll").exists():
            return [mono], folder
    raise EngineError("Mono found but its 4.5 framework assemblies are missing (install mono-devel).")


def _roslyn():
    folder = paths.home() / "tools" / f"roslyn-{ROSLYN}"
    csc = folder / "tasks" / "net472" / "csc.exe"
    if not csc.exists():
        folder.mkdir(parents=True, exist_ok=True)
        package = folder.with_suffix(".nupkg")
        print(f"downloading the C# compiler (Roslyn {ROSLYN}, once) ...", file=sys.stderr)
        urllib.request.urlretrieve(ROSLYN_URL, package)
        with zipfile.ZipFile(package) as z:
            z.extractall(folder)
        package.unlink()
    return csc


def build(force=False) -> Path:
    """Compiles the renderer if the engine or renderer sources changed; returns the exe path."""
    host, framework = _framework()
    sources = paths.engine_sources()
    digest = hashlib.sha256()
    for source in sources:
        digest.update(source.name.encode())
        digest.update(source.read_bytes())
    exe = paths.home() / "engine" / digest.hexdigest()[:16] / "graze-renderer.exe"
    if exe.exists() and not force:
        return exe
    exe.parent.mkdir(parents=True, exist_ok=True)
    csc = _roslyn()
    command = host + [str(csc), "-nologo", "-optimize+", "-langversion:9", "-nowarn:0414,0169,0649,0162,0618", "-noconfig",
                      "-nostdlib", f"-r:{framework / 'mscorlib.dll'}", f"-r:{framework / 'System.dll'}",
                      f"-r:{framework / 'System.Core.dll'}", f"-out:{exe}"] + [str(s) for s in sources]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0 or not exe.exists():
        raise EngineError("renderer build failed:\n" + (result.stdout + result.stderr)[-4000:])
    return exe


def render(song_json: Path, out_wav: Path, *, rate=48000, bits=16, start_beat=None, length_beats=None, tail=0.0,
           normalize=None, stems_dir=None) -> dict:
    exe = build()
    host, _ = _framework()
    command = host + [str(exe), "render", "--song", str(song_json), "--out", str(out_wav), "--rate", str(rate), "--bits", str(bits),
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
        host, framework = _framework()
        info["runtime"] = "mono " + host[0] if host else ".NET Framework"
        info["framework"] = str(framework)
        info["renderer"] = str(build())
        info["ok"] = True
    except Exception as error:  # report, don't crash
        info["ok"] = False
        info["error"] = str(error)
    return info
