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
        song, meta = spec.compile(tiny_spec(playbackVersion=2))
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

    def test_compile_v3_zones(self):
        """Playback V3 (the default): every sampled track is one engine part with a zone per sample."""
        song, meta = spec.compile(tiny_spec())
        self.assertEqual(song["playbackVersion"], 3)
        self.assertEqual(sorted(p["name"] for p in song["parts"]), ["drums", "lead"])
        drums = next(p for p in song["parts"] if p["name"] == "drums")
        self.assertEqual([(z["root"], z["low"], z["high"]) for z in drums["zones"]], [(36, 36, 36), (38, 38, 38)])
        self.assertNotIn("sample", drums)
        self.assertEqual(len(drums["notes"]) // 4, 12)                  # 8 kicks + 4 snares in one part
        self.assertEqual(len(meta["parts"]), 2)

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

    def test_melody_velocity(self):
        notes, _ = spec._melody("C4:1 @0.5 D4 E4@0.9 F4 @100 G4", "m")
        self.assertEqual([round(n[3], 3) for n in notes], [1, .5, .9, .5, round(100 / 127, 3)])   # '@v' lasts, 'note@v' is one note
        self.assertEqual([n[0] for n in notes], [0, 1, 2, 3, 4])
        with self.assertRaisesRegex(spec.SpecError, "velocity 'x' is not a number"):
            spec._melody("C4:1 @x D4", "m")

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
            spec.compile(tiny_spec(master={"Reverberation": 1}))

    def test_v3_song_features(self):
        """Every playback V3 spec key compiles into the song.json fields the renderer reads."""
        folder = Path(tempfile.mkdtemp())
        tone(folder / "loop.wav", seconds=2, freq=330)
        tone(folder / "ir.wav", seconds=.1, freq=2000)
        s = tiny_spec(
            instruments={"lead": "synth:square-lead", "drums": "synth:drum-kit", "acid": {"synthPreset": "acid BASS"}},
            tracks={"lead": {"instrument": "lead", "gain": .3, "sends": [{"bus": "verb", "db": -6, "preFader": True}],
                             "automation": {"volume": [{"bar": 1, "value": -20}, {"bar": 2, "value": 0, "curve": "smooth"}],
                                            "pitch": {"points": [[0, 0], [4, 1, "step"]], "disabled": True}},
                             "modulators": [{"kind": "lfo", "target": "pan", "amount": .5, "syncBeats": 2}],
                             "expression": [{"bar": 1, "beat": 1, "note": "E5", "bendTo": 2, "bendEnd": .5, "vibrato": .2}],
                             "keySwitches": [{"key": "C1", "articulation": 1}], "articulations": [{"Id": 1, "GainDb": -3}],
                             "sampleShape": {"DecaySeconds": .3, "Sustain": .5}, "loop": {"start": .05, "end": .15, "crossfade": .01},
                             "impulse": "ir.wav", "vibratoRate": 6, "ignoreSwing": True},
                    "drums": {"instrument": "drums", "output": "glue"},
                    "acid": {"instrument": "acid", "layer": "Bass", "gain": .2},
                    "tape": {"audio": [{"file": "loop.wav", "section": "a", "bar": 2, "beats": 4, "fadeOut": 1, "warp": "tempo", "sourceBpm": 120}]}},
            sections=[{"name": "a", "bars": 2, "play": {"lead": "tune", "drums": "beat", "acid": "C2:2 G1:2"}}],
            buses={"verb": {"effects": {"Reverb": True, "ReverbMix": 1}, "output": "glue", "pan": .2},
                   "glue": {"effects": {"Compressor": True, "SidechainTrack": "drums"}, "impulse": "ir.wav"}},
            masterAutomation={"volume": [[6, 0], [8, -40, "smooth"]], "speed": [[0, 1], [8, 1]]},
            master={"Color": {"StereoWidth": 1.2}, "AmpModel": "Crunch"},
            swing=.2, swingGrid=.5, targetLufs=-16, variationSeed=3, key="C major",
            keys=[{"bar": 2, "key": "A minor"}], chords=[{"bar": 1, "chord": "I"}], meterChanges=[{"bar": 2, "numerator": 7, "denominator": 8}],
            masterImpulse="ir.wav")
        song, meta = spec.compile(s, spec_dir=folder)
        parts = {p["name"]: p for p in song["parts"]}
        lead, acid, tape = parts["lead"], parts["acid"], parts["tape"]
        self.assertEqual(lead["sends"], [{"bus": "verb", "db": -6.0, "preFader": True}])
        self.assertEqual(lead["automation"][0], {"target": "volume", "points": [0.0, -20.0, 0, 4.0, 0.0, 2]})
        self.assertTrue(lead["automation"][1]["disabled"])
        self.assertEqual(lead["expression"], [{"beat": 1.0, "key": 76, "bendTo": 2, "bendEnd": .5, "vibrato": .2}])
        self.assertEqual(lead["keySwitches"], [{"Key": 24, "Articulation": 1}])
        self.assertEqual((lead["zones"][0]["loopStart"], lead["zones"][0]["loopEnd"], lead["loopCrossfade"]), (.05, .15, .01))
        self.assertTrue(lead["trackEffects"] and lead["effects"]["CabinetImpulseMix"] == 1 and Path(lead["impulse"]).exists())
        self.assertEqual(parts["drums"]["output"], "glue")
        self.assertEqual((acid["synthPreset"], acid.get("sample")), ("Acid bass", None))
        self.assertEqual(tape["regions"][0]["start"], 4.0)
        self.assertEqual(tape["regions"][0]["warp"], "Tempo")
        self.assertEqual(tape["notes"], [])
        self.assertEqual([b["name"] for b in song["buses"]], ["verb", "glue"])
        self.assertTrue(song["master"]["Limiter"], "targetLufs turns the limiter on")
        self.assertEqual(song["meter"], [{"beat": 4.0, "numerator": 7, "denominator": 8}])
        self.assertEqual((song["swing"], song["targetLufs"], song["variationSeed"], song["key"]), (.2, -16.0, 3, "C major"))
        self.assertEqual([m["target"] for m in song["masterAutomation"]], ["volume", "speed"])
        self.assertTrue(any("targetLufs" in w for w in meta["warnings"]))

    def test_v3_errors(self):
        with self.assertRaisesRegex(spec.SpecError, "need playbackVersion 3"):
            spec.compile(tiny_spec(playbackVersion=2, tracks={"lead": {"instrument": "lead", "automation": {"volume": [[0, 0]]}}, "drums": "drums"}))
        with self.assertRaisesRegex(spec.SpecError, "is not a bus"):
            spec.compile(tiny_spec(tracks={"lead": {"instrument": "lead", "output": "nope"}, "drums": "drums"}))
        with self.assertRaisesRegex(spec.SpecError, "no E4 starts at beat 0"):
            spec.compile(tiny_spec(tracks={"lead": {"instrument": "lead", "expression": [{"beat": 0, "note": "E4", "bendTo": 1}]}, "drums": "drums"}))
        with self.assertRaisesRegex(spec.SpecError, "unknown target 'loudness'"):
            spec.compile(tiny_spec(masterAutomation={"loudness": [[0, 0]]}))
        with self.assertRaisesRegex(spec.SpecError, "track target"):
            spec.compile(tiny_spec(masterAutomation={"pan": [[0, 0]]}))
        with self.assertRaisesRegex(spec.SpecError, "unknown synthPreset"):
            spec.compile(tiny_spec(instruments={"lead": {"synthPreset": "Theremin"}, "drums": "synth:drum-kit"}))

    def test_mixer_fields_match_the_engine(self):
        """spec.MIXER_FIELDS lists the engine's MusicMixerSettings fields (the IR clip goes in "impulse" instead)."""
        import re
        text = (PLUGIN / "engine" / "Runtime" / "Mixing" / "MusicMixer.cs").read_text(encoding="utf-8")
        body = text[text.index("public sealed class MusicMixerSettings"):]
        body = body[:body.index("\n    }\n")]
        fields = {n.strip() for m in re.finditer(r"public\s+(?!const)[\w.\[\]<>]+\s+([\w, ]+?)\s*(?:=[^;]*)?;", body) for n in m.group(1).split(",")}
        self.assertEqual(spec.MIXER_FIELDS, fields - {"CabinetImpulse", "Inserts"})   # VST3 inserts are written as "inserts"

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
        v3, _ = spec.compile(s)
        self.assertEqual([len(p["zones"]) for p in v3["parts"] if p["name"] in ("a", "b")], [28, 28])   # V3: one part per track
        song, meta = spec.compile(dict(s, playbackVersion=2))
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


class Cli(unittest.TestCase):
    def test_utf8_output_on_any_code_page(self):
        """Piped output on a Korean Windows console is cp949; the JSON must still be written (as UTF-8)."""
        import io
        from notewright import cli
        raw = io.BytesIO()
        stream = io.TextIOWrapper(raw, encoding="cp949")
        old, sys.stdout = sys.stdout, stream
        try:
            code = cli.main(["instruments", "find", "harp"])
            stream.flush()
        finally:
            sys.stdout = old
        self.assertEqual(code, 0)
        self.assertIn("\u2014", raw.getvalue().decode("utf-8"))           # the em dash in "VSCO 2 Community Edition — Harp"

    def test_range_names_notes(self):
        from notewright import cli
        brief = cli._brief({"id": "x", "regions": [{"lo": 24, "hi": 60, "key": 48, "sample": "a"},
                                                   {"lo": 61, "hi": 103, "key": 72, "sample": "b"}]})
        self.assertEqual(brief["range"], "C1-G7 (24-103)")

    def test_advice_wants_a_lead_in_and_a_ring_out(self):
        from notewright import cli
        report = {"peakDb": -3, "rmsDb": -14, "gainDb": 0, "outputRmsDb": -14, "seconds": 10}
        abrupt = [[-12, -3]] * 8 + [[-40, -30], [-80, -70]]          # full level from the first to the last second, then the 2 s tail
        tips = cli._advice(report, {}, [], abrupt, tail=2)
        self.assertTrue(any("starts at almost full level" in t for t in tips), tips)
        self.assertTrue(any("ends at almost full level" in t for t in tips), tips)
        soft = [[-30, -20], [-20, -10]] + [[-12, -3]] * 4 + [[-20, -10], [-34, -24]] + [[-50, -40], [-80, -70]]
        self.assertEqual(cli._advice(report, {}, [], soft, tail=2), [])
        self.assertEqual(cli._advice(report, {}, [], None, tail=2), [], "range renders are not checked")

    def test_advice_reads_the_normalized_level(self):
        from notewright import cli
        report = {"peakDb": -9, "rmsDb": -30, "gainDb": 8, "outputRmsDb": -22, "parts": [
            {"name": "lead", "kind": "track", "rmsDb": -20}, {"name": "regions", "kind": "regions", "rmsDb": -60}]}
        self.assertEqual(cli._advice(report, {}, []), [])                  # quiet before --normalize is fine; noise stem may be quiet
        quiet = cli._advice(dict(report, outputRmsDb=-29), {}, [])
        self.assertEqual(len(quiet), 1)
        self.assertIn("already normalized", quiet[0])


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
        from notewright import export
        self.assertTrue(all((unity.parent / "Samples" / owner[key]).exists() for owner, key in export._file_refs(song)))
        self.assertTrue((unity.parent / "CREDITS.md").exists())
        self.assertTrue(Path(result["files"]["midi"]).exists() and Path(result["files"]["musicxml"]).exists())

    def test_analysis_and_tempo(self):
        """The report carries loudness, bands and width; the tempo detector finds a rendered song's tempo."""
        from notewright import cli, engine
        import io
        import contextlib
        folder = Path(tempfile.mkdtemp())
        data = tiny_spec(bpm=128, sections=[{"name": "a", "bars": 8, "play": {"lead": "tune", "drums": "beat"}}])
        (folder / "s.json").write_text(json.dumps(data))
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = cli.main(["render", str(folder / "s.json"), "--out", str(folder / "out"), "--stems", "--no-score"])
        result = json.loads(buffer.getvalue())
        self.assertEqual(code, 0, result)
        self.assertLess(result["render"]["lufs"], 0)
        self.assertEqual(set(result["render"]["bands"]), set(cli.BAND_RANGES))
        self.assertIn("bands", result["sections"][0])
        self.assertIn("lufs", result["stems"][0])
        self.assertIn("music", result)
        tempo = engine.tempo(result["files"]["wav"])
        self.assertAlmostEqual(tempo["bpm"], 128, delta=1.5)
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            cli.main(["render", str(folder / "s.json"), "--out", str(folder / "out"), "--stems", "--no-score"])
        self.assertIn("no level moved", json.loads(buffer.getvalue())["changes"]["note"])       # same song, same range: nothing moved

    def test_setup_checks_and_caches(self):
        """setup verifies a runtime with a test render, saves it, and answers from its cache until the environment changes."""
        from notewright import cli, engine
        import io
        import contextlib

        def run(*flags):
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                code = cli.main(["setup", *flags])
            return code, json.loads(buffer.getvalue())

        code, first = run("--force")
        self.assertEqual(code, 0, first)
        self.assertTrue(first["ok"] and not first["cached"])
        self.assertEqual(next(c for c in first["checks"] if c["name"] == "renderer")["status"], "ok")
        self.assertEqual(engine.verified_kind() or engine.runtime()["kind"], engine.runtime()["kind"])
        _, again = run()
        self.assertTrue(again["cached"])
        self.assertEqual(again["runtime"], first["runtime"])

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

    def test_v3_spec_renders(self):
        """A spec using playback V3 features renders: built-in synth, buses and sends, automation, a modulator, an audio clip."""
        from notewright import cli
        import io
        import contextlib
        folder = Path(tempfile.mkdtemp())
        tone(folder / "loop.wav", seconds=2, freq=330)
        (folder / "s.json").write_text(json.dumps(tiny_spec(
            instruments={"lead": {"synthPreset": "Saw lead"}, "drums": "synth:drum-kit"},
            tracks={"lead": {"instrument": "lead", "gain": .3, "sends": [{"bus": "verb", "db": -6}],
                             "automation": {"lowpass": [[0, 800], [8, 8000, "smooth"]]}, "effects": {"LowPassHz": 8000},
                             "modulators": [{"kind": "lfo", "target": "pan", "amount": .5, "syncBeats": 2}]},
                    "drums": {"instrument": "drums", "gain": .5},
                    "tape": {"audio": [{"file": "loop.wav", "bar": 2, "beats": 4}], "gain": .3}},
            buses={"verb": {"effects": {"Reverb": True, "ReverbMix": 1}}},
            masterAutomation={"volume": [[6, 0], [8, -30, "smooth"]]})))
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = cli.main(["render", str(folder / "s.json"), "--out", str(folder / "out"), "--tail", "1", "--no-score", "--stems"])
        result = json.loads(buffer.getvalue())
        self.assertEqual(code, 0, result)
        self.assertEqual(result["render"]["playbackVersion"], 3)
        self.assertEqual(result["summary"]["engineParts"], 3)
        stems = {s["name"]: s for s in result["stems"]}
        self.assertGreater(stems["tape"]["rmsDb"], -60, "the audio clip plays")
        self.assertGreater(stems["lead"]["rmsDb"], -60, "the built-in synth plays")

    def test_stems_keep_regions_apart(self):
        """Noise regions get their own stem instead of sounding in every track's stem; parallel stems match serial ones."""
        from notewright import cli
        import io
        import contextlib
        folder = Path(tempfile.mkdtemp())
        (folder / "s.json").write_text(json.dumps(tiny_spec(
            sections=[{"name": "a", "bars": 1, "play": {"drums": "beat"}}, {"name": "b", "bars": 1, "play": {"lead": "tune"}}],
            regions=[{"kind": "Pink", "section": "a", "beats": 4, "Gain": .5}])))

        def render(out, jobs):
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                code = cli.main(["render", str(folder / "s.json"), "--out", str(folder / out), "--tail", "0", "--no-score",
                                 "--stems", "--jobs", str(jobs)])
            result = json.loads(buffer.getvalue())
            self.assertEqual(code, 0, result)
            return {s["name"]: s for s in result["stems"]}

        stems = render("one", 1)
        self.assertEqual(sorted(stems), ["drums", "lead", "regions"])
        self.assertEqual((stems["regions"]["kind"], stems["regions"]["notes"]), ("regions", 1))
        self.assertGreater(stems["regions"]["rmsDb"], -60)
        import wave
        with wave.open(stems["lead"]["file"]) as w:                    # 2 bars at 120 BPM = 4 s
            self.assertEqual(w.getnframes(), 4 * w.getframerate())
            first_bar = w.readframes(2 * w.getframerate())
        self.assertEqual(first_bar.count(0), len(first_bar))           # the lead rests in section a, where the noise plays
        parallel = render("four", 4)
        for name in stems:
            self.assertEqual(Path(stems[name]["file"]).read_bytes(), Path(parallel[name]["file"]).read_bytes(), name)


def vst3_spec(**extra):
    base = tiny_spec(instruments={"surge": {"vst3": "Surge XT", "parameters": {"cutoff": .4}, "voices": "mono", "fallback": "saw lead"},
                                  "drums": "synth:drum-kit"},
                     tracks={"lead": {"instrument": "surge", "plugin": {"automation": {"cutoff": [{"bar": 1, "value": .2}, [8, .9, "smooth"]]}},
                                      "effects": {"inserts": [{"vst3": "0123456789abcdef0123456789ABCDEF", "mix": .5}]}},
                             "drums": {"instrument": "drums"}},
                     master={"inserts": [{"vst3": "Some Reverb", "automation": {"mix": [[0, .1], [4, .6]]}, "parameters": {"size": .7}}]})
    base.update(extra)
    return base


class Vst3(unittest.TestCase):
    """VST3 plugins compile on every system; rendering them works only on Windows x64 and fails clearly elsewhere."""

    def test_compiles_to_plugin_and_inserts(self):
        song, meta = spec.compile(vst3_spec())
        lead = next(p for p in song["parts"] if p["name"] == "lead")
        self.assertEqual(lead["plugin"], {"name": "Surge XT", "parameters": {"cutoff": .4}, "voices": "mono",
                                          "automation": {"cutoff": [0.0, .2, 0, 8.0, .9, 2]}})
        self.assertEqual(lead["synthPreset"], "Saw lead")
        self.assertTrue(lead["trackEffects"])
        self.assertEqual(lead["effects"]["Inserts"][0]["ClassId"], "0123456789ABCDEF0123456789ABCDEF")
        reverb = song["master"]["Inserts"][0]
        self.assertEqual((reverb["Name"], reverb["ClassId"], reverb["Mix"]), ("Some Reverb", "", 1.0))
        self.assertEqual(reverb["Parameters"][0], {"Key": "size", "Value": .7})
        self.assertEqual(reverb["Parameters"][1]["Points"], [{"Beat": 0.0, "Value": .1, "Curve": 0}, {"Beat": 4.0, "Value": .6, "Curve": 0}])
        self.assertEqual(meta["vst3"], ["master inserts", "track 'lead'", "track 'lead'.effects inserts"])
        from notewright import paths
        self.assertEqual(any("Windows x64" in w for w in meta["warnings"]), not paths.vst3_supported())

    def test_errors(self):
        cases = [
            (vst3_spec(playbackVersion=2), "playbackVersion 3"),
            (vst3_spec(tracks={"lead": {"instrument": "drums", "plugin": {"parameters": {"a": .1}}}, "drums": {"instrument": "drums"}}), "VST3 instrument"),
            (vst3_spec(master={"inserts": [{"vst3": "X", "parameters": {"a": 2}}]}), "0-1"),
            (vst3_spec(master={"inserts": [{"vst3": "X"}] * 5}), "at most 4"),
            (vst3_spec(master={"inserts": [{"vst3": "X", "wet": 1}]}), "unknown keys"),
            (vst3_spec(instruments={"surge": {"vst3": ""}, "drums": "synth:drum-kit"}), "name or class ID"),
        ]
        for data, message in cases:
            with self.assertRaises(spec.SpecError) as caught:
                spec.compile(data)
            self.assertIn(message, str(caught.exception))

    def test_unity_export_moves_instruments_to_a_plugin_mod(self):
        from notewright import export
        song, meta = spec.compile(vst3_spec())
        folder = Path(tempfile.mkdtemp())
        path = export.unity_folder(song, meta, folder, "t")
        self.assertFalse(any("plugin" in p for p in json.loads(path.read_text(encoding="utf-8"))["parts"]))
        mod = json.loads((folder / "t.plugin-mod.json").read_text(encoding="utf-8"))
        self.assertEqual((mod["format"], mod["targets"][0]["instrument"], mod["targets"][0]["plugin"]), ("graze-plugin-mod/1", "lead", {"name": "Surge XT"}))
        self.assertIn("todo", mod)

    def test_render_off_windows_says_why(self):
        from notewright import cli, paths
        if paths.vst3_supported():
            self.skipTest("this machine can host VST3 plugins")
        import io
        import contextlib
        folder = Path(tempfile.mkdtemp())
        (folder / "s.json").write_text(json.dumps(vst3_spec()))
        for argv, code in ((["render", str(folder / "s.json"), "--out", str(folder / "out")], 2), (["plugins"], 1)):
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                self.assertEqual(cli.main(argv), code)
            self.assertIn("only on Windows x64", json.loads(buffer.getvalue())["error"])
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            self.assertEqual(cli.main(["render", str(folder / "s.json"), "--out", str(folder / "out"), "--score-only"]), 0)

    @unittest.skipUnless(_engine_available(), "needs the .NET SDK, Mono (Linux/macOS) or .NET Framework (Windows)")
    def test_worker_starts_on_windows(self):
        """On Windows x64 the packaged worker starts and answers; a folder without plugins lists none and a missing plugin is named."""
        from notewright import cli, paths
        if not paths.vst3_supported():
            self.skipTest("VST3 needs Windows x64")
        import io
        import contextlib
        empty = tempfile.mkdtemp()
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = cli.main(["plugins", "--roots", empty, "--rescan"])
        result = json.loads(buffer.getvalue())
        self.assertEqual(code, 0, result)
        self.assertEqual((result["worker"], result["plugins"]), ("protocol 4", []))
        folder = Path(tempfile.mkdtemp())
        song, _ = spec.compile(vst3_spec())
        (folder / "s.song.json").write_text(json.dumps(song))
        from notewright import engine
        old = os.environ.get("NOTEWRIGHT_VST3_PATH")
        os.environ["NOTEWRIGHT_VST3_PATH"] = empty
        try:
            with self.assertRaises(engine.EngineError) as caught:
                engine.render(folder / "s.song.json", folder / "s.wav")
            self.assertIn("is not installed", str(caught.exception))
        finally:
            if old is None:
                os.environ.pop("NOTEWRIGHT_VST3_PATH")
            else:
                os.environ["NOTEWRIGHT_VST3_PATH"] = old

    @unittest.skipUnless(_engine_available(), "needs the .NET SDK, Mono (Linux/macOS) or .NET Framework (Windows)")
    def test_renderer_refuses_plugins_off_windows(self):
        """The renderer itself checks too (a song.json written by hand never plays a stand-in sound instead of the plugin)."""
        from notewright import engine, paths
        if paths.vst3_supported():
            self.skipTest("this machine can host VST3 plugins")
        folder = Path(tempfile.mkdtemp())
        song, _ = spec.compile(vst3_spec())
        (folder / "s.song.json").write_text(json.dumps(song))
        with self.assertRaises(engine.EngineError) as caught:
            engine.render(folder / "s.song.json", folder / "s.wav")
        self.assertIn("needs Windows x64", str(caught.exception))


class Theory(unittest.TestCase):
    def test_keys_chords_progressions(self):
        from notewright import theory
        self.assertEqual(str(theory.Key.parse("F# dorian")), "F# dorian")
        self.assertEqual(str(theory.Key.parse("C#m")), "C# minor")
        self.assertEqual(theory.Chord.parse_name("F#m7").pitch_classes, [1, 4, 6, 9])
        self.assertEqual(theory.Chord.parse_name("C/E").bass, 4)
        a_minor = theory.Key.parse("A minor")
        self.assertEqual([str(c) for c, _, _ in theory.progression("i VI III VII", a_minor)], ["Am", "F", "C", "G"])
        self.assertEqual([str(c) for c, _, _ in theory.progression("I V vi IV V7", theory.Key.parse("G"))], ["G", "D", "Em", "C", "D7"])
        steps = theory.progression("Am:2 F - G", None, 4, 8)
        self.assertEqual([(b, n) for _, b, n in steps], [(8, 8), (16, 8), (24, 4)])
        with self.assertRaises(theory.TheoryError):
            theory.progression("Am Hm")
        key, confidence = theory.detect_key([[i, 1, k, 1] for i, k in enumerate([57, 60, 64, 62, 65, 69, 67, 64, 57, 72, 69])])
        self.assertIn(str(key), ("A minor", "C major"))
        self.assertEqual(str(theory.name_chord({9: 1, 0: 1, 4: 1})), "Am")


class Compose(unittest.TestCase):
    def test_voicing_bass_arpeggio(self):
        from notewright import compose, theory
        steps = theory.progression("C Am F G", None, 4)
        voiced = compose.voice(steps, 52, 76)
        self.assertTrue(all(52 <= k <= 76 for v in voiced for k in v))
        self.assertLessEqual(sum(abs(a - b) for a, b in zip(voiced[0], voiced[1])), 4)           # smooth voice leading
        walking = compose.bass(steps, "walking", 40, "x.x.x.x.", .5)
        self.assertEqual(walking[0][2] % 12, 0)
        self.assertEqual(walking[3][2] % 12, 8)                                                   # approach note into A
        arp = compose.arpeggio(steps, "upDown", .5)
        self.assertEqual(len(arp), 32)
        self.assertEqual(compose.arpeggio(steps, "random", seed=3), compose.arpeggio(steps, "random", seed=3))

    def test_drums_follow_the_meter(self):
        from notewright import compose
        four = compose.drums("rock", [(0, 4, 4, 4, None)], fill_every=0, variation=0)
        self.assertEqual(sorted(n[0] for n in four if n[2] == 36), [0, 2, 2.5])
        self.assertEqual(sorted(n[0] for n in four if n[2] == 38), [1, 3])
        seven = compose.drums("rock", [(0, 3.5, 7, 8, None)], fill_every=0, variation=0)            # 7/8 = 2+2+3 eighths
        self.assertEqual(sorted(n[0] for n in seven if n[2] == 36), [0, 2])
        self.assertEqual(sorted(n[0] for n in seven if n[2] == 38), [1])
        self.assertEqual(compose.default_grouping(7, 8), [2, 2, 3])
        self.assertEqual(compose.default_grouping(13, 16), [4, 3, 3, 3])
        fill = compose.drums("rock", [(0, 4, 4, 4, None)] * 1 + [(4, 4, 4, 4, None)], fill_every=2)
        self.assertTrue(any(n[2] == 45 and n[0] >= 7 for n in fill) and any(n[2] == 49 and n[0] == 0 for n in fill))

    def test_polymeter(self):
        from notewright import compose
        riff = [[0, .5, 60, 1], [1, .5, 62, 1], [2, 1, 64, 1]]
        tiled = compose.tile(riff, 2.5, 0, 10)
        self.assertEqual([n[0] for n in tiled], [0, 1, 2, 2.5, 3.5, 4.5, 5, 6, 7, 7.5, 8.5, 9.5])
        self.assertEqual(tiled[2][1], .5)                                                         # cut at the cycle end
        self.assertEqual([n[0] for n in compose.tile(riff, 2.5, 4, 6, phase_origin=0)], [4.5, 5])   # runs on from the song start
        self.assertEqual(compose.realign(1.25, 4), 20)
        self.assertEqual(compose.realign(3.5, 4), 28)


class Generators(unittest.TestCase):
    SPEC = {"title": "Gen", "bpm": 120, "meter": 4, "key": "A minor",
            "instruments": {"pad": {"synthPreset": "Warm pad"}, "bass": {"synthPreset": "Sub bass"}, "drums": "synth:drum-kit",
                            "lead": {"synthPreset": "Saw lead"}},
            "tracks": {"pad": "pad", "bass": {"instrument": "bass", "layer": "Bass"}, "drums": "drums", "lead": "lead"},
            "patterns": {"riff": {"melody": "A4:0.5 C5 E5 G5 E5", "beats": 2.5},
                         "poly": {"drums": {"kick": "x..x.", "hat": "x.x"}, "step": 0.5, "polymeter": True}},
            "meterChanges": [{"section": "odd", "numerator": 7, "denominator": 8}],
            "sections": [{"name": "a", "bars": 4, "play": {"pad": {"chords": "i VI III VII", "rhythm": "x-x.", "step": 1},
                                                          "bass": {"chords": "i VI III VII", "as": "bass", "style": "rootFifth"},
                                                          "drums": {"groove": "rock"}, "lead": {"pattern": "riff", "cycle": 2.5}}},
                         {"name": "b", "bars": 2, "play": {"lead": {"pattern": "riff", "align": "song"}, "drums": "poly"}},
                         {"name": "odd", "bars": 2, "play": {"drums": {"groove": "breakbeat"}}}]}

    def test_compile(self):
        song, meta = spec.compile(json.loads(json.dumps(self.SPEC)))
        tracks = {t["name"]: t for t in meta["tracks"]}
        self.assertEqual(sorted({n[2] % 12 for n in tracks["pad"]["notes"] if n[0] < 4}), [0, 4, 9])     # Am
        self.assertEqual([c["chord"] for c in song["chords"]][:4], ["Am", "F", "C", "G"])
        self.assertEqual([n[0] for n in tracks["lead"]["notes"] if 16 <= n[0] < 18], [16, 16.5, 17, 17.5])  # runs on from beat 15
        hats = [n[0] for n in tracks["drums"]["notes"] if 16 <= n[0] < 24 and n[2] == 42]
        self.assertEqual(hats[:4], [16, 17, 17.5, 18.5])                                        # a 1.5-beat hat lane
        self.assertEqual(meta["sections"][2]["beats"], 7)                                       # two 7/8 bars
        self.assertEqual(meta["loop"], 31)
        self.assertEqual({p["cycle"] for p in meta["polymeter"]}, {2.5, 1.5})

    def test_errors(self):
        for play, message in (({"pad": {"groove": "rock"}}, "drum track"), ({"drums": {"chords": "Am"}}, "pitched"),
                              ({"pad": {"chords": "Am", "as": "strum"}}, "'as'"), ({"pad": {"chords": "Am", "range": ["C4", "E4"]}}, "octave"),
                              ({"drums": {"groove": "polka"}}, "groove must be"), ({"lead": {"pattern": "riff", "align": "bar"}}, "'align'")):
            data = json.loads(json.dumps(self.SPEC))
            data["sections"] = [{"name": "x", "bars": 1, "play": play}]
            data.pop("meterChanges")
            with self.assertRaises(spec.SpecError) as caught:
                spec.compile(data)
            self.assertIn(message, str(caught.exception))


class MusicCheck(unittest.TestCase):
    def test_finds_clashes_key_and_repeats(self):
        from notewright import musiccheck
        data = tiny_spec(key="C major", tracks={"lead": {"instrument": "lead"}, "pad": {"instrument": "lead", "layer": "Harmony"},
                                                "drums": {"instrument": "drums"}},
                         patterns={"beat": {"drums": {"kick": "x...x...x...x..."}}, "tune": {"melody": "F#5:1 C#5 G#5 D#5"},
                                   "held": {"notes": "0 4 F5"}},
                         sections=[{"name": "a", "bars": 1, "play": {"lead": "tune", "pad": "held", "drums": "beat"}},
                                   {"name": "b", "bars": 1, "play": {"lead": "tune", "pad": "held", "drums": "beat"}}])
        _, meta = spec.compile(data)
        report = musiccheck.check(meta)
        self.assertTrue(any("outside C major" in i for i in report["issues"]), report)
        self.assertTrue(any("semitone clashes" in i and "F#5" in i for i in report["issues"]), report)
        self.assertTrue(any("exactly the same notes" in n for n in report["notes"]), report)
        clean = musiccheck.check(spec.compile(tiny_spec(key="C major"))[1])
        self.assertEqual(clean["issues"], [])


class MidiRead(unittest.TestCase):
    def test_reads_back_a_written_file(self):
        from notewright import midiread
        song, meta = spec.compile(Generators.SPEC | {"meterChanges": [], "sections": Generators.SPEC["sections"][:1]})
        path = Path(tempfile.mkdtemp()) / "g.mid"
        midi.write(path, meta)
        info = midiread.summary(path)
        self.assertEqual((info["bpm"], info["meter"]), (120, "4/4"))
        roots = [c[:2] if len(c) > 1 and c[1] in "#b" else c[:1] for c in info["chordsPerBar"][:4]]
        self.assertEqual((roots, info["chordsPerBar"][0]), (["A", "F", "C", "G"], "Am"))      # the riff's E may color F as Fmaj7
        self.assertTrue(any(t["drums"] for t in info["tracks"]))
        converted = midiread.to_spec(path)
        self.assertEqual(converted["chords"][0], {"bar": 1, "chord": "Am"})
        data = json.loads(json.dumps(converted))
        data["instruments"] = {alias: ("synth:drum-kit" if alias == "drums" else "synth:pad") for alias in data["instruments"]}
        song2, meta2 = spec.compile(data)
        self.assertEqual(sum(len(t["notes"]) for t in meta2["tracks"]), sum(len(t["notes"]) for t in meta["tracks"] if t["notes"]))


class Workflow(unittest.TestCase):
    def test_spectrum_advice_masking_and_changes(self):
        from notewright import cli
        mix = [[-20, -20, -25, -40, -50]] * 12
        pad = {"name": "pad", "kind": "track", "bands": {"low": -40, "lowMid": -22, "mid": -30, "highMid": -50, "high": -60},
               "bandsPerSecond": [[-40, -22, -30, -50, -60]] * 12}
        keys = {"name": "keys", "kind": "track", "bands": {"low": -40, "lowMid": -23, "mid": -31, "highMid": -50, "high": -60},
                "bandsPerSecond": [[-40, -23, -31, -50, -60]] * 12}
        report = {"bands": {"low": -20, "lowMid": -10, "mid": -25, "highMid": -40, "high": -60}, "bandNames": list(cli.BAND_RANGES),
                  "bandsPerSecond": mix, "widthPerSecond": [0.01] * 12, "lufs": -24, "truePeakDb": -3, "gainDb": 0}
        tips = cli._spectrum_advice(report, [pad, keys])
        for words in ("muddy", "dark", "almost mono", "targetLufs", "mask each other"):
            self.assertTrue(any(words in t for t in tips), (words, tips))
        changes = cli._changes({"outputRmsDb": -18, "advice": ["a"]}, {"outputRmsDb": -16}, [], ["b"])
        self.assertEqual(changes["outputRmsDb"], "-18 → -16 (+2.0)")
        self.assertEqual((changes["adviceResolved"], changes["adviceNew"]), (["a"], ["b"]))
        self.assertIn("beat, duration", cli._hint("x: expected 'beat duration pitch[,pitch] [@velocity]'"))


class Mml(unittest.TestCase):
    """Game MML: the format, the limits, drums as n codes, and an exact round trip of the notes."""

    def test_lengths_and_drum_maps(self):
        from notewright import mml
        self.assertEqual(mml._lengths(48), ["4"])
        self.assertEqual(mml._lengths(72), ["4."])
        self.assertEqual(sorted(mml._lengths(20)), ["12", "48"])                                 # greedy 18 + 2 would fail
        self.assertEqual(sum(dict((t, v) for v, t in mml.LENGTHS)[t] for t in mml._lengths(1000 - 1000 % 3)), 999)
        lost, mabi = mml.DRUM_N["lostark"], mml.DRUM_N["mabinogi"]
        self.assertEqual((lost["kick"], lost["snare"], lost["closedHat"], lost["crash"], lost["rideBell"]), (53, 57, 69, 72, 77))
        self.assertEqual((mabi["kick"], mabi["snare"], mabi["openHat"], mabi["ride"]), (29, 33, 43, 52))  # o2f o2a o3g o4e

    def test_scores_round_trip(self):
        import re
        from notewright import mml
        for name in ("pop", "orchestral", "mathrock"):
            data = spec.load(PLUGIN / "examples" / f"{name}.json")
            for alias, use in data["instruments"].items():          # no downloads here: the notes are what matters
                if "vsco2:" in json.dumps(use) or "find:" in json.dumps(use):
                    data["instruments"][alias] = "synth:pad"
            _, meta = spec.compile(data)
            for game in mml.GAMES:
                result = mml.score(meta, game, "full")
                self.assertTrue(all(s["fits"] for s in result["scores"]), (name, game))
                for s in result["scores"]:
                    self.assertRegex(s["mml"], r"^MML@[^,;]*(,[^,;]*){0,2};$")
                    if game.startswith("mabinogi"):
                        self.assertTrue(all(c <= limit for c, limit in zip(s["chars"], mml.GAMES[game]["parts"])))
                    else:
                        self.assertLessEqual(sum(s["chars"]), 5000)
                        self.assertLessEqual(s["nodes"], 1000)
                for t in meta["tracks"]:
                    scores = [x for x in result["scores"] if x["track"] == t["name"]]
                    if not t["notes"] or not scores:
                        continue
                    if t["drums"]:
                        codes = {int(n) for x in scores for n in re.findall(r"n(\d+)", x["mml"])}
                        self.assertTrue(codes <= set(mml.DRUM_N["lostark" if game == "lostark" else "mabinogi"].values()), codes)
                        self.assertFalse(re.search(r"[a-gA-G]", re.sub(r"MML@", "", scores[0]["mml"])))   # no note letters on drums
                        continue
                    layered = mml.voices(t["notes"], mml._grid(t["notes"], mml.GAMES[game]["triplets"]))
                    want = sorted((v, round(a / 48, 4), round((e - a) / 48, 4), k) for v, voice in enumerate(layered) for a, e, k, _ in voice)
                    got = sorted((v, round(b + x["start"], 4), round(d, 4), k)
                                 for x in scores for v, part in enumerate(mml.parse(x["mml"], game)) for b, d, k, _ in part)
                    self.assertEqual(want, got, (name, game, t["name"]))

    def test_limits_split_into_parts_and_voices(self):
        from notewright import mml
        _, meta = spec.compile(spec.load(PLUGIN / "examples" / "pop.json"))
        old = dict(mml.GAMES["mabinogi"])
        try:
            mml.GAMES["mabinogi"]["parts"] = (200, 150, 100)
            result = mml.score(meta, "mabinogi")
        finally:
            mml.GAMES["mabinogi"] = old
        drums = [s for s in result["scores"] if s["track"] == "drums"]
        self.assertGreater(len(drums), 1)
        self.assertTrue(all(s["fits"] for s in drums))
        self.assertEqual([s["bars"][0] for s in drums][0], 1)
        self.assertTrue(all(a["bars"][1] + 1 == b["bars"][0] for a, b in zip(drums, drums[1:])))
        chords = tiny_spec(tracks={"lead": {"instrument": "lead"}, "drums": {"instrument": "drums"}},
                           patterns={"beat": {"drums": {"kick": "x..."}}, "tune": {"notes": "0 4 C4,E4,G4,B4,D5"}})
        result = mml.score(spec.compile(chords)[1], "lostark")
        self.assertEqual([s["track"] for s in result["scores"] if s["track"].startswith("lead")], ["lead 1", "lead 2"])
        self.assertTrue(any("need 2 scores" in w for w in result["warnings"]))

    def test_lostark_keeps_binary_lengths_only(self):
        # Lost Ark drops notes and rests with lengths 3, 6, 12, 24 and 48 (seen in a score entered in the game and copied back).
        import re
        from notewright import mml
        notes = [[bar * 4 + n / 3, 1 / 3, 60 + n % 7, .7] for bar in range(8) for n in range(12)]
        meta = {"title": "t", "bpm": 156, "meter": 4, "loop": 32, "barStarts": list(range(0, 33, 4)),
                "tempo": [{"Beat": 8, "Bpm": 187, "RampBeats": 0}, {"Beat": 16, "Bpm": 156, "RampBeats": 0}],
                "tracks": [{"name": "piano", "instrument": "piano", "tags": [], "drums": False, "notes": notes},
                           {"name": "drums", "instrument": "kit", "tags": [], "drums": True,
                            "notes": [[b / 3, .5, 36 if b % 3 == 0 else 42, .8] for b in range(96)]}]}

        def lengths(text):           # independent of mml.parse: every number after a note, rest or l
            return {int(n) for n in re.findall(r"[a-gr][#+-]?(\d+)|l(\d+)", text.lower()) for n in n if n}

        lost = mml.score(meta, "lostark", "none")
        for s in lost["scores"]:
            self.assertLessEqual(lengths(s["mml"]), {1, 2, 4, 8, 16, 32, 64}, s["mml"])
            mml.parse(s["mml"], "lostark")                            # strict read: raises on anything the game would drop
        piano = [n for s in lost["scores"] if s["track"] == "piano" for v in mml.parse(s["mml"]) for n in v]
        want = sorted((round(b * 16) / 16, round((b + d) * 16) / 16 - round(b * 16) / 16, k) for b, d, k, _ in notes)
        self.assertEqual(sorted((b, d, k) for b, d, k, _ in piano), want)    # absolute start and end on 64ths: no drift
        drums = [s for s in lost["scores"] if s["track"] == "drums"]
        self.assertFalse(any(re.search(r"[a-g&]", s["mml"][4:]) for s in drums))
        self.assertEqual(sum(len(v) for s in drums for v in mml.parse(s["mml"])), 96)
        tempo = [(b, bpm) for s in lost["scores"] for _, b, bpm in mml.read(s["mml"])["tempo"]]
        self.assertEqual(tempo, [(0.0, 156), (8.0, 187), (16.0, 156)])           # once, in the first track's melody
        self.assertFalse(any("#" in s["mml"] for s in lost["scores"]))         # the game's own sharp: c+
        with self.assertRaises(mml.MmlError):
            mml.parse("MML@c12;", "lostark")
        mabi = mml.score(meta, "mabinogi", "none")                    # the other games keep triplets, in the same process
        self.assertIn("l12", mabi["scores"][0]["mml"])
        self.assertEqual(lost["scores"], mml.score(meta, "lostark", "none")["scores"])
        self.assertGreater(lost["quantized"]["notes"], 0)
        self.assertTrue(any("64th" in w for w in lost["warnings"]))

    def test_lostark_instrument_ranges(self):
        # Lost Ark greys out and does not play notes outside an instrument's range: a tuba part's C2 and C#2 were greyed out in the game.
        from notewright import mml
        def meta(keys, instrument="tuba"):
            return {"title": "t", "bpm": 120, "meter": 4, "loop": 8, "tempo": [], "barStarts": [0, 4, 8],
                    "tracks": [{"name": "bass", "instrument": instrument, "tags": [instrument], "drums": False,
                                "notes": [[i, 1, k, .6] for i, k in enumerate(keys)]}]}
        low = meta([36, 37, 38, 54])
        kept = mml.score(low, "lostark", "none", {"bass": "튜바"})
        self.assertEqual(kept["scores"][0]["range"]["status"], "outside")
        self.assertEqual(kept["scores"][0]["range"]["outside"], 2)
        self.assertTrue(any("outside 튜바's range" in w and "bars 1" in w for w in kept["warnings"]), kept["warnings"])
        self.assertIn("NOT PLAYED", mml.text_file(kept, low))
        self.assertEqual(sorted(n[2] for v in mml.parse(kept["scores"][0]["mml"]) for n in v), [36, 37, 38, 54])  # warn keeps the notes
        moved = mml.score(low, "lostark", "none", {"bass": "튜바"}, "octave")
        self.assertEqual(sorted(n[2] for v in mml.parse(moved["scores"][0]["mml"]) for n in v), [38, 48, 49, 54])
        self.assertEqual([(r["from"], r["to"]) for r in moved["repairs"]], [(36, 48), (37, 49)])
        self.assertEqual(moved["scores"][0]["range"]["status"], "ok")
        self.assertEqual(mml.score(low, "lostark", "none", {"bass": "튜바"}, "octave")["repairs"], moved["repairs"])   # never twice
        suggested = mml.score(low, "lostark", "none")              # the suggestion picks an instrument that plays every note
        self.assertEqual(suggested["scores"][0]["instrument"], "첼로")
        self.assertTrue(any("instead of 튜바" in w for w in suggested["warnings"]))
        d2 = mml.score(meta([38]), "lostark", "none")
        self.assertEqual((d2["scores"][0]["instrument"], d2["scores"][0]["range"]["status"]), ("튜바", "ok"))
        self.assertEqual([n[:3] for v in mml.parse(d2["scores"][0]["mml"]) for n in v], [[0, 1, 38]])
        unknown = mml.score(meta([60], "violin"), "lostark")
        self.assertEqual(unknown["scores"][0]["range"]["status"], "unknown")       # not checked is said, never passed
        self.assertTrue(any("not checked" in w for w in unknown["warnings"]))
        self.assertEqual(mml.score(low, "mabinogi")["scores"][0]["range"]["status"], "unknown")
        with self.assertRaises(mml.MmlError):
            mml.score(low, "lostark", instruments={"nope": "튜바"})
        spec_ = {"title": "t", "bpm": 120, "instruments": {"b": "synth:pad"}, "tracks": {"bass": {"instrument": "b"}}}
        played = mml.game_spec(kept, spec_, low)                     # the comparison leaves out what the game does not play
        self.assertEqual([n[2] for n in played["notes"]["bass"]["notes"]], [38, 54])

    def test_tempo_change_inside_a_note_is_tied(self):
        from notewright import mml
        tempo = [(0, 120), (48, 90)]
        text, notes = mml.write_voice([(0, 96, 60, .8)], 0, 96, tempo)
        self.assertIn("t90&", text)
        self.assertEqual(notes, 1)
        self.assertEqual(mml.parse("MML@" + text + ";")[0], [[0, 2.0, 60, mml.parse("MML@" + text + ";")[0][0][3]]])

    def test_cli_writes_files(self):
        from notewright import cli, mml
        import io
        import contextlib
        folder = Path(tempfile.mkdtemp())
        (folder / "s.json").write_text(json.dumps(tiny_spec()))
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            self.assertEqual(cli.main(["mml", str(folder / "s.json"), "--game", "lostark", "--out", str(folder / "out")]), 0)
        result = json.loads(buffer.getvalue())
        text = Path(result["files"]["mml-lostark"]).read_text(encoding="utf-8")
        self.assertIn("MML@", text)
        self.assertEqual({s["track"] for s in result["mml"]["lostark"]["scores"]}, {"lead", "drums"})
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            self.assertEqual(cli.main(["mml", str(folder / "s.json"), "--game", "lostark", "--out", str(folder / "out"), "--compare"]), 0)
        game = json.loads(buffer.getvalue())["files"]["mml-lostark-spec"]
        _, played = spec.compile(spec.load(game), folder)
        self.assertEqual(sum(len(t["notes"]) for t in played["tracks"]),
                         sum(len(v) for s in mml.score(spec.compile(tiny_spec())[1], "lostark")["scores"] for v in mml.parse(s["mml"])))
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            cli.main(["render", str(folder / "s.json"), "--out", str(folder / "out2"), "--score-only", "--mml", "all"])
        files = json.loads(buffer.getvalue())["files"]
        self.assertEqual({k for k in files if k.startswith("mml-")}, {"mml-mabinogi", "mml-mabinogi-mobile", "mml-lostark"})


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
