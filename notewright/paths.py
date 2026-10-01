"""Where things live: the plugin folder (engine sources, data) and the per-user data folder (catalog, samples, builds)."""
import os
from pathlib import Path

PLUGIN_DIR = Path(__file__).resolve().parent.parent          # plugin root: notewright.py, engine/, data/, examples/
ENGINE_DIR = PLUGIN_DIR / "engine"                            # the music engine (C#) and the headless renderer
DATA_DIR = PLUGIN_DIR / "data"


def home() -> Path:
    """User data: NOTEWRIGHT_HOME, else ~/.notewright. Kept across plugin updates."""
    path = Path(os.environ.get("NOTEWRIGHT_HOME") or Path.home() / ".notewright")
    path.mkdir(parents=True, exist_ok=True)
    return path


def engine_sources():
    runtime = sorted((ENGINE_DIR / "Runtime").glob("*.cs"))
    if not runtime:
        raise FileNotFoundError(f"engine sources not found under {ENGINE_DIR / 'Runtime'}")
    return runtime + [ENGINE_DIR / "Stubs" / "UnityStubs.cs"] + sorted((ENGINE_DIR / "Renderer").glob("*.cs"))
