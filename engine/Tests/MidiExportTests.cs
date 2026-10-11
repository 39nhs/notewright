using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MidiExportTests
    {
        // 24 beats: tempo 100 → ramp to 140 over beats 8–12, 7/8 from beat 16, E minor, sections, a lead with expression lanes, bass, drums.
        static MusicCueDefinition Song()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Export"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = 100; song.LoopBeats = 23; song.Key = "E minor";
            song.TempoChanges = new[] { new MusicTempoChange { Beat = 8, Bpm = 140, RampBeats = 4 } };
            song.MeterChanges = new[] { new MusicMeterChange(16, 7, 8) };
            song.Sections = new[] { new MusicSectionMarker("Intro", 0), new MusicSectionMarker("사비", 16) };
            song.Parts = new[]
            {
                new MusicCueDefinition.Part { Name = "Lead", MidiProgram = 81,
                    Notes = new[] { new MusicNote(0, 1, 64, .8f), new MusicNote(1, .5, 67, .5f), new MusicNote(1.5, .5, 67, .3f), new MusicNote(4, 2.25, 71, 1) },
                    Automation = new[]
                    {
                        new MusicAutomationLane(MusicAutomationTarget.PitchSemitones, new MusicAutomationPoint(0, 0, MusicAutomationCurve.Step), new MusicAutomationPoint(2, 5, MusicAutomationCurve.Step), new MusicAutomationPoint(3, -5, MusicAutomationCurve.Step), new MusicAutomationPoint(4, 0, MusicAutomationCurve.Step)),
                        new MusicAutomationLane(MusicAutomationTarget.Modulation, new MusicAutomationPoint(0, 0), new MusicAutomationPoint(4, 1, MusicAutomationCurve.Step)),
                        new MusicAutomationLane(MusicAutomationTarget.Brightness, new MusicAutomationPoint(0, 2, MusicAutomationCurve.Step)),
                    } },
                new MusicCueDefinition.Part { Name = "Bass", Layer = MusicLayer.Bass, Notes = new[] { new MusicNote(0, 4, 40, .7f), new MusicNote(16, 3.5, 43, .7f) } },
                new MusicCueDefinition.Part { Name = "Drums", Layer = MusicLayer.Drums, Notes = new[] { new MusicNote(0, .25, 36, 1), new MusicNote(1, .25, 38, .9f) } },
            };
            return song;
        }

        static void Run(Action<MusicCueDefinition, MidiMusicReader.Result, MidiMusicWriter.Result> test)
        {
            var song = Song();
            try { var written = MidiMusicWriter.Write(song); test(song, MidiMusicReader.Read(written.Bytes), written); }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        [Test]
        public void NotesTracksAndChannelsRoundTrip() => Run((song, midi, written) =>
        {
            Assert.AreEqual(3, midi.Parts.Count);
            Assert.AreEqual("Lead Bass Drums", string.Join(" ", midi.Parts.Select(p => p.Name)));
            Assert.AreEqual(9, midi.Parts[2].Channel, "drum layer on channel 10");
            Assert.AreEqual(3, midi.Parts.Select(p => p.Channel).Distinct().Count());
            Assert.AreEqual(81, midi.Parts[0].Program);
            for (int i = 0; i < 3; i++)
            {
                var a = song.Parts[i].Notes.OrderBy(n => n.Beat).ThenBy(n => n.Key).ToArray();
                var b = midi.Parts[i].Notes.OrderBy(n => n.Beat).ThenBy(n => n.Key).ToArray();
                Assert.AreEqual(a.Length, b.Length, song.Parts[i].Name);
                for (int k = 0; k < a.Length; k++)
                {
                    Assert.AreEqual(a[k].Beat, b[k].Beat, 1e-3); Assert.AreEqual(a[k].Duration, b[k].Duration, 1e-3);
                    Assert.AreEqual(a[k].Key, b[k].Key); Assert.AreEqual(a[k].Velocity, b[k].Velocity, 1 / 127f);
                }
            }
            Assert.AreEqual(0, written.Warnings.Count, string.Join("; ", written.Warnings));
        });

        [Test]
        public void ConductorTrackRoundTrips() => Run((song, midi, written) =>
        {
            Assert.AreEqual(100, midi.Bpm, .01);
            var tempos = midi.Tempos.OrderBy(t => t.Beat).ToArray();
            Assert.AreEqual(140, tempos.Last().Bpm, .01); Assert.AreEqual(12, tempos.Last().Beat, 1e-3);
            Assert.IsTrue(tempos.Where(t => t.Beat > 8 && t.Beat < 12).All(t => t.Bpm > 100 && t.Bpm < 140), "ramp written as steps");
            Assert.AreEqual(4, midi.BeatsPerBar);
            Assert.IsTrue(midi.Meters.Select(m => (m.Beat, m.Numerator, m.Denominator)).SequenceEqual(new[] { (0.0, 4, 4), (16.0, 7, 8) }));
            Assert.AreEqual("E minor", midi.Key);
            Assert.AreEqual("Intro@0 사비@16", string.Join(" ", midi.Markers.Select(m => $"{m.Name}@{m.Beat:0.##}")));
        });

        [Test]
        public void NoteBendsAndVibratoExportAsPitchBend()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                song.name = "Bends"; song.Bpm = 60; song.LoopBeats = 8; song.PlaybackVersion = MusicPlaybackVersion.V3;
                song.Parts = new[] { new MusicCueDefinition.Part { Name = "Lead", Layer = MusicLayer.Melody, VibratoRateHz = 4, Notes = new[] {
                    new MusicNote(0, 2, 69) { BendTo = 2, BendStart = .5f, BendEnd = 1 }, new MusicNote(3, 2, 71) { Vibrato = .5f }, new MusicNote(6, 1, 72) } } };
                var written = MidiMusicWriter.Write(song);
                var midi = MidiMusicReader.Read(written.Bytes);
                var bend = midi.Parts[0].Expression[0];
                Assert.IsNotNull(bend, "pitch bend written");
                double At(double beat) => bend.Where(x => x.Beat <= beat + 1e-6).Last().Value;
                Assert.AreEqual(0, At(.4), .05, "before the bend");
                Assert.AreEqual(2, At(1.9), .05, "bent a whole tone");
                Assert.AreEqual(0, At(2.5), .05, "back after the note");
                var vibrato = Enumerable.Range(0, 40).Select(i => At(3.3 + i * .04)).ToArray();
                Assert.IsTrue(vibrato.Max() > .4 && vibrato.Min() < -.4, "vibrato ±0.5: " + vibrato.Max().ToString("0.00") + " / " + vibrato.Min().ToString("0.00"));
                Assert.AreEqual(0, At(6.5), .05, "plain note");
                Assert.IsFalse(written.Warnings.Any(w => w.Contains("overlapping")));
                song.Parts[0].Notes = new[] { new MusicNote(0, 2, 69) { BendTo = 1, BendEnd = 1 }, new MusicNote(1, 2, 72) { Vibrato = .2f } };
                Assert.IsTrue(MidiMusicWriter.Write(song).Warnings.Any(w => w.Contains("overlapping")), "overlap warning");
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        [Test]
        public void ExpressionLanesRoundTrip() => Run((song, midi, written) =>
        {
            var lead = midi.Parts[0];
            var pitch = lead.Expression[0]; var mod = lead.Expression[1]; var bright = lead.Expression[2];
            Assert.IsNotNull(pitch); Assert.IsNotNull(mod); Assert.IsNotNull(bright);
            double At(MusicAutomationPoint[] p, double beat) => p.Where(x => x.Beat <= beat + 1e-6).Last().Value;
            Assert.AreEqual(5, At(pitch, 2.5), .01, "bend range 5 written as RPN 0"); Assert.AreEqual(-5, At(pitch, 3.5), .01); Assert.AreEqual(0, At(pitch, 4.5), .01);
            Assert.AreEqual(.5, At(mod, 2), 1 / 127.0 + 1e-3, "linear ramp written as steps"); Assert.AreEqual(1, At(mod, 4), 1e-3);
            Assert.AreEqual(2, At(bright, 1), 4 / 64.0);
        });

        [Test]
        public void PatternPlacementsAreExported()
        {
            var song = Song();
            try
            {
                song.Patterns = new[] { new MusicPattern { Name = "Riff", LengthBeats = 2, Notes = new[] { new MusicNote(0, .5, 50, .8f), new MusicNote(1, .5, 53, .8f) } } };
                song.Parts[1].Placements = new[] { new MusicPatternPlacement("Riff", 8, 0, 2) };
                var midi = MidiMusicReader.Read(MidiMusicWriter.Write(song).Bytes);
                var bass = midi.Parts.First(p => p.Name == "Bass").Notes.Where(n => n.Beat >= 8 && n.Beat < 12).OrderBy(n => n.Beat).ToArray();
                Assert.AreEqual("8:50 9:53 10:50 11:53", string.Join(" ", bass.Select(n => $"{n.Beat:0}:{n.Key}")));
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        [Test]
        public void KeySignatureNamesAndModes()
        {
            Assert.AreEqual("C major", MidiMusicWriter.KeyName(0, false));
            Assert.AreEqual("Bb major", MidiMusicWriter.KeyName(-2, false));
            Assert.AreEqual("F# minor", MidiMusicWriter.KeyName(3, true));
            Assert.AreEqual("Eb minor", MidiMusicWriter.KeyName(-6, true));
            var song = Song(); song.Key = "D dorian";
            try
            {
                var written = MidiMusicWriter.Write(song);
                Assert.IsNull(MidiMusicReader.Read(written.Bytes).Key);
                Assert.IsTrue(written.Warnings.Any(w => w.Contains("dorian")), string.Join("; ", written.Warnings));
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        [Test]
        public void ManyPartsStillSplitOnReimport()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                song.name = "Many"; song.LoopBeats = 8;
                song.Parts = Enumerable.Range(0, 18).Select(i => new MusicCueDefinition.Part { Name = "P" + i, Notes = new[] { new MusicNote(i * .25, .2, 60 + i, .8f) } }).ToArray();
                var written = MidiMusicWriter.Write(song);
                Assert.AreEqual(18, MidiMusicReader.Read(written.Bytes).Parts.Count, "channel + program pairs stay distinct");
                Assert.AreEqual(3, written.Warnings.Count, "three parts share a channel");
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }
    }
}
