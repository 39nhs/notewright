"""Instrument catalog: built-in synth voices, downloadable libraries (data/registry.json, e.g. VSCO 2 CE) and instruments
scanned from folders on this computer (SFZ files, note-named WAV sets, drum one-shots, a graze Instruments~ library).

An instrument entry is a list of sample regions: {sample, lo, hi, key (root), lovel, hivel, volume dB, seq}. The engine plays
one sample per part, so a song uses only the regions its notes need (see choose_regions)."""
import json
import re
import shutil
import sys
import urllib.parse
import urllib.request
from pathlib import Path

from . import paths, sfz, synthkit, wavinfo
from .notes import DRUMS, NOTE_RE, note_number

VELOCITY_WORDS = {"ppp": 16, "pp": 33, "p": 49, "mp": 64, "mf": 80, "f": 96, "ff": 112, "fff": 127}
SYNONYMS = {"piano": ["keys", "upright"], "violin": ["strings"], "strings": ["violin", "viola", "cello", "pad"],
            "drums": ["kit", "percussion"], "kit": ["drums"], "bass": ["sub", "contrabass"], "pad": ["strings", "ambient"],
            "lead": ["synth"], "bell": ["mallet", "glockenspiel", "chimes"], "orchestra": ["orchestral"], "orchestral": ["strings", "brass"],
            "horn": ["french horn", "brass"], "guitar": ["pluck", "plucked"], "short": ["staccato", "pizzicato"], "long": ["sustained"],
            "soft": ["quiet"], "808": ["sub", "bass"], "electronic": ["synth"], "acoustic": ["orchestral"]}


class CatalogError(RuntimeError):
    pass


def _catalog_path():
    return paths.home() / "catalog.json"


def load():
    path = _catalog_path()
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    return {"version": 1, "instruments": {}}


def save(catalog):
    _catalog_path().write_text(json.dumps(catalog, indent=1, ensure_ascii=False), encoding="utf-8")


def registry():
    return json.loads((paths.DATA_DIR / "registry.json").read_text(encoding="utf-8"))


def all_entries():
    """Every known instrument: installed (built-in + catalog) and downloadable (status 'available')."""
    entries = {e["id"]: dict(e, status="installed") for e in synthkit.entries()}
    for e in load()["instruments"].values():
        entries[e["id"]] = dict(e, status="installed")
    for e in registry()["instruments"]:
        if e["id"] not in entries:
            entries[e["id"]] = dict(e, status="available", source=e["library"])
    return entries


def get(instrument_id):
    entry = all_entries().get(instrument_id)
    if not entry:
        raise CatalogError(f"unknown instrument '{instrument_id}' (see: instruments list / find)")
    return entry


# ---------- search ----------

def _words(text):
    return [w for w in re.split(r"[^a-z0-9#]+", text.lower()) if w]


def find(query, kind=None, limit=8):
    """Ranks instruments by how many query words (and synonyms) match id, name, tags and description. Installed first on ties."""
    wanted = _words(query)
    expanded = set(wanted)
    for w in wanted:
        expanded.update(x for s in SYNONYMS.get(w, []) for x in _words(s))
    results = []
    for e in all_entries().values():
        if kind and e.get("kind") != kind:
            continue
        hay = set(_words(" ".join([e["id"], e.get("name", ""), e.get("description", "")] + e.get("tags", []))))
        direct = sum(1 for w in wanted if w in hay)
        related = sum(1 for w in expanded - set(wanted) if w in hay)
        if direct or related:
            results.append((direct * 3 + related + (0.5 if e["status"] == "installed" else 0), e))
    results.sort(key=lambda x: (-x[0], x[1]["id"]))
    return [dict(e, score=round(score, 1)) for score, e in results[:limit]]


# ---------- downloads ----------

def _download(url, target: Path):
    target.parent.mkdir(parents=True, exist_ok=True)
    temp = target.with_suffix(target.suffix + ".part")
    with urllib.request.urlopen(url, timeout=120) as response, open(temp, "wb") as out:
        shutil.copyfileobj(response, out)
    temp.replace(target)


def _layer(regions, velocity):
    """Keeps one velocity layer and the first round robin per key zone."""
    zones = {}
    for r in regions:
        zones.setdefault((r["lo"], r["hi"]), []).append(r)
    chosen = []
    for zone in zones.values():
        first = [r for r in zone if r.get("seq", 1) == 1] or zone
        covering = [r for r in first if r["lovel"] <= velocity <= r["hivel"]] or first
        chosen.append(min(covering, key=lambda r: abs((r["lovel"] + r["hivel"]) / 2 - velocity)))
    return chosen


def fetch(instrument_id, velocity=100, all_layers=False, quiet=False):
    """Downloads a registry instrument (its SFZ map and only the samples it needs) and installs it in the catalog."""
    entry = get(instrument_id)
    if entry["status"] == "installed":
        return entry
    library = registry()["libraries"][entry["library"]]
    folder = paths.home() / "libraries" / entry["library"]
    sfz_path = folder / entry["sfz"]
    if not sfz_path.exists():
        _download(library["raw"] + urllib.parse.quote(entry["sfz"]), sfz_path)
    regions = sfz.parse(sfz_path)
    if not regions:
        raise CatalogError(f"{instrument_id}: no playable regions in {entry['sfz']}")
    keep = regions if all_layers else _layer(regions, velocity)
    for i, r in enumerate(keep):
        sample = Path(r["sample"])
        if not sample.exists():
            relative = sample.relative_to(folder.resolve()).as_posix()
            if not quiet:
                print(f"  {instrument_id}: {i + 1}/{len(keep)} {relative}", file=sys.stderr)
            _download(library["raw"] + urllib.parse.quote(relative), sample)
    attack = next((r["attack"] for r in keep if r.get("attack") is not None), .005)
    release = next((r["release"] for r in keep if r.get("release") is not None), .3)
    installed = {k: entry[k] for k in ("id", "name", "kind", "tags", "description")}
    installed.update(source=entry["library"], license=library["license"], credit=library.get("credit", ""),
                     attack=attack, release=release, regions=[{k: r[k] for k in ("sample", "lo", "hi", "key", "lovel", "hivel", "volume", "seq")} for r in keep])
    catalog = load()
    catalog["instruments"][instrument_id] = installed
    save(catalog)
    return dict(installed, status="installed")


# ---------- scanning folders on this computer ----------

def _slug(text):
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-") or "x"


def _note_in_name(stem):
    for token in re.split(r"[_\-\s.]+", stem):
        if NOTE_RE.match(token):
            return note_number(token)
    return None


def _velocity_in_name(stem):
    for token in re.split(r"[_\-\s.]+", stem.lower()):
        if token in VELOCITY_WORDS:
            return VELOCITY_WORDS[token]
        m = re.fullmatch(r"(?:v|vel|dyn|layer)(\d{1,3})", token)
        if m:
            return int(m.group(1))
    return 0


def _round_robin(stem):
    m = re.search(r"(?:rr|_)(\d)$", stem.lower())
    return int(m.group(1)) if m else 1


def _drum_in_name(stem):
    words = _words(stem.replace("_", " "))
    joined = "-".join(words)
    for alias in sorted(DRUMS, key=len, reverse=True):
        if alias in words or alias in joined:
            return DRUMS[alias]
    for word, key in (("bassdrum", 36), ("bdrum", 36), ("kick", 36), ("snare", 38), ("hihat", 42), ("hat", 42), ("cymbal", 49),
                      ("crash", 49), ("ride", 51), ("tom", 47), ("clap", 39), ("shaker", 70), ("tamb", 54), ("cowbell", 56)):
        if word in joined.replace("-", ""):
            return key
    return None


def _layer_by_rank(files):
    """For note-named sample sets: maps each file's velocity token to a velocity range so _layer can pick one."""
    levels = sorted({f[2] for f in files})
    width = 128 // max(1, len(levels))
    ranges = {lvl: (i * width, 127 if i == len(levels) - 1 else (i + 1) * width - 1) for i, lvl in enumerate(levels)}
    return ranges


def scan(folder, library=None):
    """Registers every instrument found under a folder on this computer. Returns the installed entries."""
    folder = Path(folder).expanduser().resolve()
    if not folder.is_dir():
        raise CatalogError(f"not a folder: {folder}")
    library = _slug(library or folder.name)
    found, used = [], set()
    # 1) SFZ instruments
    for path in sorted(folder.rglob("*.sfz")):
        try:
            regions = [r for r in sfz.parse(path) if Path(r["sample"]).exists() and Path(r["sample"]).suffix.lower() == ".wav"]
        except Exception as error:  # keep scanning
            print(f"  skipped {path}: {error}", file=sys.stderr)
            continue
        if not regions:
            continue
        used.update(r["sample"] for r in regions)
        drums = all(r["lo"] == r["hi"] for r in regions) and len({r["lo"] for r in regions}) > 3 and \
            any(k in path.stem.lower() for k in ("drum", "kit", "perc"))
        found.append({"id": f"local:{library}/{_slug(path.stem)}", "name": path.stem, "kind": "drums" if drums else "melodic",
                      "source": "local", "path": str(path), "tags": _words(path.stem) + _words(path.parent.name),
                      "description": f"SFZ {path.relative_to(folder)}", "license": "see the library's own license",
                      "attack": next((r["attack"] for r in regions if r.get("attack") is not None), .005),
                      "release": next((r["release"] for r in regions if r.get("release") is not None), .3),
                      "regions": [{k: r[k] for k in ("sample", "lo", "hi", "key", "lovel", "hivel", "volume", "seq")} for r in regions]})
    # 2) a graze Instruments~ library (sessions/*.json + samples/)
    if (folder / "sessions").is_dir():
        for session in sorted((folder / "sessions").glob("session-*.json")):
            for e in json.loads(session.read_text(encoding="utf-8")).get("instruments", []):
                sample = folder / "samples" / e.get("file", "")
                if e.get("removed") or not sample.exists():
                    continue
                used.add(str(sample.resolve()))
                found.append({"id": f"local:{library}/{_slug(e['instrumentId'])}", "name": e["instrumentId"], "kind": "melodic",
                              "source": "local", "tags": _words(e["instrumentId"]) + ["graze", "game"], "license": e.get("license", ""),
                              "description": f"graze instrument {e['instrumentId']} ({e.get('patch', '')})", "attack": .005, "release": .2,
                              "regions": [{"sample": str(sample.resolve()), "lo": 0, "hi": 127, "key": e.get("rootKey", 60),
                                           "lovel": 0, "hivel": 127, "volume": 0, "seq": 1}]})
    # 3) loose WAVs: note-named sets per folder become multi-sample instruments; drum words become kits; others one-shots
    by_folder = {}
    for path in sorted(folder.rglob("*.wav")):
        if str(path.resolve()) in used:
            continue
        try:
            wavinfo.info(path)
        except ValueError:
            continue
        by_folder.setdefault(path.parent, []).append(path)
    for parent, files in by_folder.items():
        named = [(f, _note_in_name(f.stem), _velocity_in_name(f.stem), _round_robin(f.stem)) for f in files]
        pitched = [n for n in named if n[1] is not None]
        tag_words = _words(str(parent.relative_to(folder))) if parent != folder else _words(folder.name)
        if len(pitched) >= 2:
            ranges = _layer_by_rank(pitched)
            keys = sorted({n[1] for n in pitched})
            regions = []
            for f, key, vel, rr in pitched:
                i = keys.index(key)
                lo = 0 if i == 0 else (keys[i - 1] + key) // 2 + 1
                hi = 127 if i == len(keys) - 1 else (key + keys[i + 1]) // 2
                regions.append({"sample": str(f.resolve()), "lo": lo, "hi": hi, "key": key, "lovel": ranges[vel][0],
                                "hivel": ranges[vel][1], "volume": 0, "seq": rr})
            name = parent.name if parent != folder else folder.name
            found.append({"id": f"local:{library}/{_slug(str(parent.relative_to(folder)) if parent != folder else name)}", "name": name,
                          "kind": "melodic", "source": "local", "tags": tag_words, "description": f"{len(keys)} pitched samples in {parent}",
                          "license": "see the library's own license", "attack": .005, "release": .3, "regions": regions})
            continue
        drums = [(f, _drum_in_name(f.stem)) for f in files]
        kit = {}
        for f, key in drums:
            if key is not None and key not in kit:
                kit[key] = f
        if len(kit) >= 2:
            found.append({"id": f"local:{library}/{_slug(parent.name)}-kit", "name": parent.name + " kit", "kind": "drums", "source": "local",
                          "tags": tag_words + ["drums", "kit"], "description": f"drum kit from {parent}", "license": "see the library's own license",
                          "attack": .001, "release": .1,
                          "regions": [{"sample": str(f.resolve()), "lo": k, "hi": k, "key": k, "lovel": 0, "hivel": 127, "volume": 0, "seq": 1}
                                      for k, f in sorted(kit.items())]})
        for f in files:
            if f in kit.values():
                continue
            found.append({"id": f"local:{library}/{_slug(f.stem)}", "name": f.stem, "kind": "melodic", "source": "local",
                          "tags": _words(f.stem) + tag_words + ["one-shot"], "description": f"one-shot sample {f}",
                          "license": "see the file's own license", "attack": .002, "release": .2,
                          "regions": [{"sample": str(f.resolve()), "lo": 0, "hi": 127, "key": 60, "lovel": 0, "hivel": 127, "volume": 0, "seq": 1}]})
    catalog = load()
    for e in found:
        catalog["instruments"][e["id"]] = e
    save(catalog)
    return found


def add_file(instrument_id, sample, root=60, tags=()):
    sample = Path(sample).expanduser().resolve()
    wavinfo.info(sample)
    entry = {"id": instrument_id if ":" in instrument_id else "local:" + instrument_id, "name": sample.stem, "kind": "melodic",
             "source": "local", "tags": list(tags) + _words(sample.stem), "description": f"sample {sample}", "license": "",
             "attack": .005, "release": .3,
             "regions": [{"sample": str(sample), "lo": 0, "hi": 127, "key": int(root), "lovel": 0, "hivel": 127, "volume": 0, "seq": 1}]}
    catalog = load()
    catalog["instruments"][entry["id"]] = entry
    save(catalog)
    return entry


def remove(instrument_id):
    catalog = load()
    if catalog["instruments"].pop(instrument_id, None) is None:
        raise CatalogError(f"'{instrument_id}' is not in the local catalog")
    save(catalog)


# ---------- use in songs ----------

def resolve(use, auto_fetch=True):
    """Spec instrument reference → installed entry. Forms: 'synth:pad', 'vsco2:harp', 'local:lib/x', 'find:warm pad',
    'file:/path/x.wav' (root 60 unless {'use':..., 'root':N}), 'sfz:/path/x.sfz', 'dir:/folder' (note-named WAV set)."""
    options = use if isinstance(use, dict) else {"use": use}
    ref = options.get("use") or ("find:" + options["find"] if "find" in options else None)
    if not ref:
        raise CatalogError(f"instrument needs 'use' or 'find': {use}")
    kind = options.get("kind")
    if ref.startswith("find:"):
        hits = find(ref[5:], kind=kind, limit=1)
        if not hits:
            raise CatalogError(f"no instrument matches '{ref[5:]}' (see: instruments list)")
        ref = hits[0]["id"]
    if ref.startswith("file:"):
        path = Path(ref[5:]).expanduser().resolve()
        wavinfo.info(path)
        root = int(options.get("root", 60))
        return {"id": ref, "name": path.stem, "kind": kind or "melodic", "source": "file", "tags": [], "license": "", "attack": .005,
                "release": .3, "regions": [{"sample": str(path), "lo": 0, "hi": 127, "key": root, "lovel": 0, "hivel": 127, "volume": 0, "seq": 1}]}
    if ref.startswith("sfz:"):
        regions = sfz.parse(Path(ref[4:]).expanduser())
        return {"id": ref, "name": Path(ref[4:]).stem, "kind": kind or "melodic", "source": "file", "tags": [], "license": "",
                "attack": next((r["attack"] for r in regions if r.get("attack") is not None), .005),
                "release": next((r["release"] for r in regions if r.get("release") is not None), .3), "regions": regions}
    if ref.startswith("dir:"):
        found = scan(ref[4:], library="dir-" + _slug(Path(ref[4:]).name))
        if not found:
            raise CatalogError(f"no instrument found in {ref[4:]}")
        return dict(found[0], status="installed")
    entry = get(ref)
    if entry["status"] != "installed":
        if not auto_fetch:
            raise CatalogError(f"'{ref}' is not downloaded yet (run: instruments fetch {ref})")
        print(f"downloading {ref} ...", file=sys.stderr)
        entry = fetch(ref, velocity=int(options.get("velocity", 100)))
    for r in entry["regions"]:
        if r["sample"].startswith(str(synthkit.folder())):
            synthkit.ensure(Path(r["sample"]))
    return entry


def choose_regions(entry, velocity=100):
    """One region per key zone (velocity layer nearest the target, first round robin), sorted by key."""
    return sorted(_layer(entry["regions"], velocity), key=lambda r: (r["lo"], r["key"]))
