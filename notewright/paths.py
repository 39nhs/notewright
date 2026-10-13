"""Where things live: the plugin folder (engine sources, data) and the per-user data folder (catalog, samples, builds)."""
import os
from pathlib import Path

PLUGIN_DIR = Path(__file__).resolve().parent.parent          # plugin root: notewright.py, engine/, data/, examples/
ENGINE_DIR = PLUGIN_DIR / "engine"                            # the music engine (C#) and the headless renderer
DATA_DIR = PLUGIN_DIR / "data"
PLUGIN_HOST = ENGINE_DIR / "Native" / "PluginHost" / "graze-plugin-host.exe"   # VST3 worker (Windows x64 only)


def vst3_supported() -> bool:
    """VST3 plugins work only on 64-bit Windows: the worker is a Windows x64 program."""
    import platform
    return os.name == "nt" and platform.machine().lower() in ("amd64", "x86_64")


VST3_UNAVAILABLE = ("VST3 plugins work only on Windows x64 (the plugin host is a Windows program). This machine is not, so use the "
                    "built-in synth, samples and the built-in effects instead (macOS, Linux and the claude.ai/Cowork sandbox cannot "
                    "load VST3 plugins).")


def home() -> Path:
    """User data: NOTEWRIGHT_HOME, else ~/.notewright. Kept across plugin updates."""
    path = Path(os.environ.get("NOTEWRIGHT_HOME") or Path.home() / ".notewright")
    path.mkdir(parents=True, exist_ok=True)
    return path


def engine_sources():
    runtime = sorted((ENGINE_DIR / "Runtime").rglob("*.cs"))
    if not runtime:
        raise FileNotFoundError(f"engine sources not found under {ENGINE_DIR / 'Runtime'}")
    return runtime + [ENGINE_DIR / "Stubs" / "UnityStubs.cs"] + sorted((ENGINE_DIR / "Renderer").glob("*.cs"))
