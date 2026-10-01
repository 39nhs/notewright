"""Unit tests for the plugin layer (python -m unittest discover -s tests). The render test needs .NET/Mono and is skipped
without it. No network: registry instruments are not fetched here."""
import json
import math
import os
import shutil
import struct
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

PLUGIN = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PLUGIN))
HOME = tempfile.mkdtemp(prefix="notewright-test-home-")
os.environ["NOTEWRIGHT_HOME"] = HOME

from notewright import catalog, midi, musicxml, sfz, spec, wavinfo  # noqa: E402
from notewright.notes import name, note_number, number  # noqa: E402


def tone(path, seconds=.2, freq=440):
    wavinfo.write_mono16(path, [math.sin(2 * math.pi * freq * i / 22050) for i in range(int(seconds * 22050))], 22050)


def tiny_spec(**extra):
    base = {"title": "Test", "bpm": 120, "meter": 4,
            "instruments": {"lead": "synth:square-lead", "drums": "synth:drum-kit"},
            "tracks": {"lead": {"instrument": "lead", "gain": .3}, "drums": {"instrument": "drums", "gain": .5}},
            "patterns": {"beat": {"drums": {"kick": "x...x...x...x...", "snare": "....x.......x..."}},
                         "tune": {"melody": "C5:1 E5 G5 C6:0.5 r:0.5"}},
            "sections": [{"name": "a", "bars": 2, "play": {"lead": "tune", "drums": "beat"}}]}
    base.update(extra)
    return base


class Notes(unittest.TestCase):
    def test_names(self):
        self.assertEqual(note_number("C4"), 60)
        self.assertEqual(note_number("F#3"), 54)
        self.assertEqual(note_number("Bb2"), 46)
        self.assertEqual(note_number("c-1"), 0)
        self.assertEqual(note_number("kick"), 36)
        self.assertEqual(note_number("72"), 72)
        self.assertIsNone(note_number("H4"))
        self.assertEqual(name(61), "C#4")

    def test_numbers(self):
        self.assertAlmostEqual(number("3/4"), .75)
        self.assertAlmostEqual(number("2+1/3"), 7 / 3)


class Sfz(unittest.TestCase):
    def test_parse(self):
        folder = Path(tempfile.mkdtemp())
        (folder / "Samples" / "Harp").mkdir(parents=True)
        for n in ("C3", "C4"):
            tone(folder / "Samples" / "Harp" / f"harp {n}.wav")
        (folder / "x.sfz").write_text("""
// comment
<control> default_path=Samples\\Harp\\
<global> ampeg_release=1.5
<group> lovel=0 hivel=127 /* block */ volume=3
<region> sample=harp C3.wav lokey=c2 hikey=f3 pitch_keycenter=c3
<region> sample=harp C4.wav lokey=f#3 hikey=c6 pitch_keycenter=60 transpose=12
<region> sample=harp C4.wav key=70 trigger=release
""")
        regions = sfz.parse(folder / "x.sfz")
        self.assertEqual(len(regions), 2)
        self.assertEqual((regions[0]["lo"], regions[0]["hi"], regions[0]["key"]), (36, 53, 48))
        self.assertEqual(regions[1]["key"], 48)          # transpose 12 lowers the root
        self.assertEqual(regions[0]["release"], 1.5)
        self.assertEqual(regions[0]["volume"], 3)
        self.assertTrue(Path(regions[0]["sample"]).exists())


class Catalog(unittest.TestCase):
    def test_find_builtin_and_registry(self):
        ids = [e["id"] for e in catalog.find("warm pad")]
        self.assertEqual(ids[0], "synth:pad")
        self.assertIn("vsco2:harp", [e["id"] for e in catalog.find("harp")])
        self.assertTrue(all(e["kind"] == "drums" for e in catalog.find("drums", kind="drums")))

    def test_scan_folder(self):
        folder = Path(tempfile.mkdtemp()) / "MyLib"
        for n, f in (("C3", 130.8), ("C4", 261.6), ("C5", 523.3)):
            tone(folder / "Rhodes" / f"Rhodes_{n}_mf.wav", freq=f)
        for d in ("Kick_01", "Snare-Tight", "HiHat closed"):
            tone(folder / "Drums" / f"{d}.wav")
        tone(folder / "Fx" / "riser.wav")
        found = {e["id"]: e for e in catalog.scan(folder)}
        rhodes = found["local:mylib/rhodes"]
        self.assertEqual(sorted(r["key"] for r in rhodes["regions"]), [48, 60, 72])
        kit = found["local:mylib/drums-kit"]
        self.assertEqual(kit["kind"], "drums")
        self.assertEqual(sorted(r["key"] for r in kit["regions"]), [36, 38, 42])
        self.assertIn("local:mylib/riser", found)
        self.assertEqual(catalog.find("rhodes")[0]["id"], "local:mylib/rhodes")
        resolved = catalog.resolve("local:mylib/rhodes")
        self.assertEqual(len(catalog.choose_regions(resolved)), 3)


class Spec(unittest.TestCase):
    def test_compile(self):
        song, meta = spec.compile(tiny_spec())
        self.assertEqual(song["loopBeats"], 8)
        self.assertEqual(song["playbackVersion"], 2)
        names = sorted(p["name"] for p in song["parts"])
        self.assertEqual(names, ["drums-kick", "drums-snare", "lead"])
        lead = next(p for p in song["parts"] if p["name"] == "lead")
        quads = [lead["notes"][i:i + 4] for i in range(0, len(lead["notes"]), 4)]
        self.assertEqual([q[2] for q in quads], [72, 76, 79, 84] * 2)        # the 4-beat tune repeats over 2 bars
        self.assertEqual([q[0] for q in quads][:5], [0, 1, 2, 3, 4])
        kick = next(p for p in song["parts"] if p["name"] == "drums-kick")
        self.assertEqual(kick["root"], 36)
        self.assertEqual(len(kick["notes"]) // 4, 8)

    def test_events_lists_and_options(self):
        s = tiny_spec(sections=[
            {"name": "a", "bars": 1, "play": {"lead": "0 1 C4,E4 @0.5 | 2 2 G4"}},
            {"name": "b", "bars": 2, "play": {"lead": {"patterns": ["tune", "tune"], "transpose": 2, "velocity": .5}}},
        ])
        song, meta = spec.compile(s)
        notes = meta["tracks"][0]["notes"]
        self.assertEqual(notes[0], [0, 1, 60, .5])
        self.assertEqual(notes[2][:3], [2, 2, 67])
        self.assertEqual(notes[3][:3], [4, 1, 74])                     # section b starts at beat 4, +2 semitones
        self.assertEqual(notes[3][3], .5)
        self.assertEqual([s["beat"] for s in song["sections"]], [0, 4])

    def test_tempo_regions_master(self):
        song, _ = spec.compile(tiny_spec(tempo=[{"bar": 2, "bpm": 90, "rampBeats": 2}],
                                         regions=[{"kind": "PitchGlitch", "section": "a", "bar": 2, "beats": 2}],
                                         master={"Drive": True, "DriveMode": "Fuzz"}))
        self.assertEqual(song["tempo"], [{"Beat": 4.0, "Bpm": 90.0, "RampBeats": 2.0}])
        self.assertEqual(song["regions"][0]["Kind"], 6)
        self.assertTrue(song["regions"][0]["MasterFilter"])
        self.assertEqual(song["regions"][0]["StartBeat"], 4)
        self.assertEqual(song["master"]["DriveMode"], 3)

    def test_errors(self):
        with self.assertRaisesRegex(spec.SpecError, "unknown keys"):
            spec.compile(tiny_spec(tracks={"lead": {"instrument": "lead", "volume": 1}}))
        with self.assertRaisesRegex(spec.SpecError, "unknown pattern"):
            spec.compile(tiny_spec(sections=[{"name": "a", "bars": 1, "play": {"lead": "nope"}}]))
        with self.assertRaisesRegex(spec.SpecError, "not a note"):
            spec.compile(tiny_spec(sections=[{"name": "a", "bars": 1, "play": {"lead": "H9:1"}}]))
        with self.assertRaisesRegex(spec.SpecError, "unknown mixer fields"):
            spec.compile(tiny_spec(master={"Reverb": 1}))

    def test_zone_split_and_budget(self):
        folder = Path(tempfile.mkdtemp())
        keys = list(range(24, 108, 3))                                 # 28 zones
        for k in keys:
            tone(folder / f"s{k}.wav")
        (folder / "wide.sfz").write_text("\n".join(f"<region> sample=s{k}.wav lokey={k} hikey={k + 2} pitch_keycenter={k}" for k in keys))
        s = tiny_spec(instruments={"wide": f"sfz:{folder / 'wide.sfz'}", "drums": "synth:drum-kit"},
                      tracks={"a": {"instrument": "wide", "splits": 32}, "b": {"instrument": "wide", "splits": 32},
                              "drums": {"instrument": "drums"}},
                      sections=[{"name": "x", "bars": 8, "play": {
                          "a": {"notes": " | ".join(f"{i} 1 {k}" for i, k in enumerate(keys))},
                          "b": {"notes": " | ".join(f"{i} 1 {k + 1}" for i, k in enumerate(keys))},
                          "drums": "beat"}}])
        song, meta = spec.compile(s)
        self.assertLessEqual(len(song["parts"]), 32)
        self.assertEqual(sum(1 for p in song["parts"] if p["name"].startswith("drums")), 2)
        for p in song["parts"]:
            for i in range(0, len(p["notes"]), 4):
                self.assertLessEqual(abs(p["notes"][i + 2] - p["root"]), 48)
        self.assertTrue(any("sample zones used" in w for w in meta["warnings"]))

    def test_load_comments(self):
        path = Path(tempfile.mkdtemp()) / "s.json"
        path.write_text('// head\n{"title": "a // not a comment", /* x */ "bpm": 90,}\n')
        self.assertEqual(spec.load(path), {"title": "a // not a comment", "bpm": 90})

    def test_examples_compile(self):
        for example in (PLUGIN / "examples").glob("*.json"):
            data = spec.load(example)
            uses = json.dumps(data.get("instruments", {}))
            if "vsco2:" in uses or "find:" in uses:
                continue                                               # needs downloads
            with self.subTest(example=example.name):
                spec.compile(data, spec_dir=example.parent)


class Exports(unittest.TestCase):
    def test_midi(self):
        _, meta = spec.compile(tiny_spec(tempo=[{"bar": 2, "bpm": 60, "rampBeats": 1}]))
        path = Path(tempfile.mkdtemp()) / "x.mid"
        midi.write(path, meta)
        data = path.read_bytes()
        self.assertEqual(data[:4], b"MThd")
        fmt, tracks, ppq = struct.unpack(">HHH", data[8:14])
        self.assertEqual((fmt, tracks, ppq), (1, 3, 480))
        self.assertEqual(data.count(b"MTrk"), 3)
        self.assertEqual(midi.guess_program("vsco2:cello-ens-sus-vib", ["cello"]), 42)

    def _measures(self, path):
        root = ET.parse(path).getroot()
        out = []
        for part in root.iter("part"):
            for m in part.iter("measure"):
                voices = {}
                for el in m:
                    if el.tag == "note" and el.find("chord") is None:
                        v = el.findtext("voice")
                        voices[v] = voices.get(v, 0) + int(el.findtext("duration"))
                out.append(voices)
        return out

    def test_musicxml_rhythms(self):
        s = tiny_spec(meter=3, key="Eb major", sections=[{"name": "a", "bars": 3, "play": {
            "lead": "0 1/3 C5 | 1/3 1/3 D5 | 2/3 1/3 Eb5 | 1 2.5 F5 | 3.5 0.75 G5,Bb5 | 4.25 3 C6 | 8 1 C4 | 8 0.5 E4"}}])
        _, meta = spec.compile(s)
        path = Path(tempfile.mkdtemp()) / "x.musicxml"
        warnings = musicxml.write(path, meta)
        self.assertEqual(warnings, [])
        for voices in self._measures(path):
            self.assertEqual(voices.get("1"), 72)                        # 3 beats × 24 divisions
            for total in voices.values():
                self.assertLessEqual(total, 72)
        text = path.read_text()
        self.assertIn("<fifths>-3</fifths>", text)
        self.assertIn("<actual-notes>3</actual-notes>", text)
        self.assertIn('<tie type="start"/>', text)
        self.assertIn("<step>E</step><alter>-1</alter>", text)
        self.assertIn("<voice>2</voice>", text)

    def test_key_fifths(self):
        self.assertEqual(musicxml.key_fifths("A minor"), 0)
        self.assertEqual(musicxml.key_fifths("F# major"), 6)
        self.assertEqual(musicxml.key_fifths("G#m"), 5)
        self.assertEqual(musicxml.key_fifths("Bb"), -2)


def _engine_available():
    from notewright import engine
    try:
        engine.runtime()
        return True
    except engine.EngineError:
        return False


@unittest.skipUnless(_engine_available(), "needs the .NET SDK, Mono (Linux/macOS) or .NET Framework (Windows)")
class Render(unittest.TestCase):
    def test_render_cli(self):
        from notewright import cli
        folder = Path(tempfile.mkdtemp())
        (folder / "s.json").write_text(json.dumps(tiny_spec(master={"Compressor": True})))
        out = folder / "out"
        import io
        import contextlib
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = cli.main(["render", str(folder / "s.json"), "--out", str(out), "--normalize", "-1", "--tail", "0.5", "--unity"])
        result = json.loads(buffer.getvalue())
        self.assertEqual(code, 0, result)
        info = wavinfo.info(result["files"]["wav"])
        self.assertEqual((info["channels"], info["rate"], info["bits"]), (2, 48000, 24))
        self.assertAlmostEqual(info["seconds"], 4.5, places=1)            # 2 bars at 120 + 0.5 s tail
        self.assertEqual(result["render"]["droppedNotes"], 0)
        self.assertAlmostEqual(result["render"]["outputPeakDb"], -1, places=1)
        unity = Path(result["files"]["unity"])
        song = json.loads(unity.read_text())
        self.assertTrue(all((unity.parent / "Samples" / p["sample"]).exists() for p in song["parts"]))
        self.assertTrue((unity.parent / "CREDITS.md").exists())
        self.assertTrue(Path(result["files"]["midi"]).exists() and Path(result["files"]["musicxml"]).exists())

    def test_section_tail_plays_nothing_later(self):
        """A section render's tail lets sounding notes ring out; the next section's notes and regions never start in it."""
        from notewright import cli
        import io
        import contextlib
        folder = Path(tempfile.mkdtemp())

        def render(data, *flags):
            (folder / "s.json").write_text(json.dumps(data))
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                code = cli.main(["render", str(folder / "s.json"), "--out", str(folder / "out"), "--tail", "1", "--no-score"] + list(flags))
            result = json.loads(buffer.getvalue())
            self.assertEqual(code, 0, result)
            return result, json.loads(Path(result["files"]["report"]).read_text())["perSecond"]

        a = {"name": "a", "bars": 2, "play": {"lead": "tune"}}
        result, per_second = render(tiny_spec(sections=[a, {"name": "b", "bars": 2, "play": {"lead": "tune", "drums": "beat"}}],
                                              regions=[{"kind": "White", "section": "b", "bar": 1, "beats": 8, "Gain": .5}]),
                                    "--section", "a", "--stems")
        self.assertAlmostEqual(wavinfo.info(result["files"]["wav"])["seconds"], 5, places=1)   # 2 bars at 120 + 1 s tail
        self.assertEqual([s["name"] for s in result["sections"]], ["a"])
        stems = {s["name"]: s for s in result["stems"]}
        self.assertEqual(stems["drums"]["notes"], 0)
        self.assertLess(stems["drums"]["rmsDb"], -150)                  # section b's drums do not play in the tail
        self.assertGreater(stems["lead"]["rmsDb"], -60)
        _, alone = render(tiny_spec(sections=[a]))                      # the same as a song that ends after section a
        self.assertEqual(per_second, alone)
        self.assertLess(per_second[-1][0], per_second[-2][0] - 20)      # the tail is only the lead's release


class Manifest(unittest.TestCase):
    def test_version_matches_plugin_json(self):
        import notewright
        manifest = json.loads((Path(__file__).resolve().parent.parent / ".claude-plugin" / "plugin.json").read_text(encoding="utf-8"))
        self.assertEqual(notewright.__version__, manifest["version"])


class Runtime(unittest.TestCase):
    def test_choice(self):
        from notewright import engine
        old = os.environ.get("NOTEWRIGHT_RUNTIME")
        try:
            os.environ["NOTEWRIGHT_RUNTIME"] = "nonsense"
            with self.assertRaises(engine.EngineError):
                engine.runtime()
        finally:
            if old is None:
                os.environ.pop("NOTEWRIGHT_RUNTIME", None)
            else:
                os.environ["NOTEWRIGHT_RUNTIME"] = old


@unittest.skipUnless(os.environ.get("NOTEWRIGHT_NETWORK_TESTS"), "set NOTEWRIGHT_NETWORK_TESTS=1 (downloads from github.com)")
class GitDownload(unittest.TestCase):
    def test_fetch_over_git(self):
        """The fallback for networks without raw.githubusercontent.com: only github.com over git."""
        os.environ["NOTEWRIGHT_DOWNLOAD"] = "git"
        try:
            self.assertEqual(catalog.downloads(), "git")
            entry = catalog.fetch("vsco2:timpani", quiet=True)
        finally:
            os.environ.pop("NOTEWRIGHT_DOWNLOAD", None)
        self.assertEqual(entry["status"], "installed")
        for region in entry["regions"]:
            self.assertGreater(wavinfo.info(region["sample"])["seconds"], 0)


if __name__ == "__main__":
    unittest.main()
