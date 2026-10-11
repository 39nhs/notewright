using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>Song-level data: key changes, the chord track, swing and the loudness target.</summary>
    public sealed class MusicSongDataTests
    {
        // Eight 4/4 bars in A minor, modulating to C# minor at bar 5; one chord per bar.
        static MusicCueDefinition Song()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Data"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = 120; song.LoopBeats = 32; song.Key = "A minor";
            song.KeyChanges = new[] { new MusicKeyChange(16, "C# minor") };
            song.Chords = new[] { "i", "VI", "III", "VII", "i", "iv", "V7", "i" }.Select((c, i) => new MusicChordMarker(i * 4, c)).ToArray();
            song.Parts = new[]
            {
                new MusicCueDefinition.Part { Name = "Lead", Gain = .1f, Notes = Enumerable.Range(0, 16).Select(i => new MusicNote(i * .5, .5, 69 + i % 5, .8f)).ToArray() },
                new MusicCueDefinition.Part { Name = "Pad", Gain = .1f },
            };
            return song;
        }

        static void Run(Action<MusicCueDefinition> test)
        {
            var song = Song();
            try { test(song); }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        [Test]
        public void HarmonyTrackAnswersKeyAndChordAtABeat() => Run(s =>
        {
            var track = s.HarmonyTrack();
            Assert.AreEqual("A minor", track.KeyNameAt(0)); Assert.AreEqual("A minor", track.KeyNameAt(15.9));
            Assert.AreEqual("C# minor", track.KeyNameAt(16)); Assert.AreEqual("C# minor", track.KeyNameAt(31));
            Assert.AreEqual(8, track.ChordCount);
            Assert.AreEqual("Am", track.ChordNameAt(0), "Roman numerals in A minor");
            Assert.AreEqual("F", track.ChordNameAt(5)); Assert.AreEqual("G", track.ChordNameAt(13));
            Assert.AreEqual("C#m", track.ChordNameAt(16), "numerals after the key change follow the new key");
            Assert.AreEqual("F#m", track.ChordNameAt(20));
            Assert.AreEqual("G#7", track.ChordNameAt(24));
            Assert.AreEqual(32, track.ChordEnd(7), "the last chord runs to the loop end");
            var steps = track.Steps(6, 14);
            Assert.AreEqual("F@6+2 C@8+4 G@12+2", string.Join(" ", steps.Select(x => $"{x.Chord}@{x.Beat:0.##}+{x.Beats:0.##}")));
            Assert.AreEqual("Db", MusicHarmonyTrack.Spell(new MusicChord(1, MusicChordQuality.Major), MusicKey.Parse("F minor")));
            Assert.AreEqual("A#dim/C#", MusicHarmonyTrack.Spell(new MusicChord(10, MusicChordQuality.Diminished, 1), MusicKey.Parse("B major")));
            Assert.AreEqual("", MusicHarmonyTrack.Empty.ChordNameAt(3)); Assert.IsNull(MusicHarmonyTrack.Empty.KeyAt(0));
        });

        [Test]
        public void ChordNamesWorkWithoutAKey()
        {
            var track = new MusicHarmonyTrack(8, null, null, new[] { new MusicChordMarker(4, "Dm7"), new MusicChordMarker(0, "C/E") });
            Assert.AreEqual(-1, new MusicHarmonyTrack(8, null, null, new[] { new MusicChordMarker(2, "C") }).ChordIndexAt(1), "before the first chord");
            Assert.AreEqual("C/E", track.ChordNameAt(1)); Assert.AreEqual("Dm7", track.ChordNameAt(7.5)); Assert.IsNull(track.KeyAt(0));
        }

        [Test]
        public void InvalidHarmonyDataIsRejectedAtCompile()
        {
            Assert.Throws<ArgumentException>(() => new MusicHarmonyTrack(16, "C", new[] { new MusicKeyChange(4, "H minor") }, null), "unknown key");
            Assert.Throws<ArgumentException>(() => new MusicHarmonyTrack(16, "C", new[] { new MusicKeyChange(16, "G") }, null), "at the loop end");
            Assert.Throws<ArgumentException>(() => new MusicHarmonyTrack(16, "C", null, new[] { new MusicChordMarker(0, "Xq9") }), "unknown chord");
            Assert.Throws<ArgumentException>(() => new MusicHarmonyTrack(16, "C", null, new[] { new MusicChordMarker(0, "C"), new MusicChordMarker(0, "F") }), "same beat");
            Assert.Throws<ArgumentException>(() => new MusicHarmonyTrack(16, "C", null, new[] { new MusicChordMarker(0, " ") }), "empty");
            Run(s =>
            {
                s.Chords = new[] { new MusicChordMarker(40, "C") };
                var e = Assert.Throws<InvalidOperationException>(() => s.Compile());
                Assert.IsTrue(e.Message.StartsWith("Data: "), e.Message);
            });
        }

        [Test]
        public void EngineReportsTheCurrentKeyAndChord() => Run(s =>
        {
            var engine = new AdaptiveMusicEngine(s.Compile(), 8000);
            var block = new float[800]; // 0.05 s = 0.1 beat at 120 BPM
            var seen = new List<string>();
            for (int i = 0; i < 20 * 16; i++) { engine.RenderStereo(block); if (i % 20 == 9) seen.Add(engine.CurrentChordName); } // every 2 beats, at beats 1, 3, 5…
            Assert.AreEqual("Am Am F F C C G G C#m C#m F#m F#m G#7 G#7 C#m C#m", string.Join(" ", seen));
            Assert.AreEqual("A minor", engine.CurrentKey?.ToString(), "wrapped back to the start");
            for (int i = 0; i < 190; i++) engine.RenderStereo(block); // beat 19
            Assert.AreEqual("C# minor", engine.CurrentKey?.ToString());
            Assert.AreEqual("C#m", engine.CurrentChord?.ToString());
        });

        [Test]
        public void HarmonyDataDoesNotChangeTheSound() => Run(s =>
        {
            float[] Render(MusicCueDefinition song) { var pcm = new float[16000]; new AdaptiveMusicEngine(song.Compile(), 8000).RenderStereo(pcm); return pcm; }
            var with = Render(s);
            s.KeyChanges = Array.Empty<MusicKeyChange>(); s.Chords = Array.Empty<MusicChordMarker>();
            Assert.IsTrue(Render(s).SequenceEqual(with));
        });

        [Test]
        public void SwingDelaysOffbeatsOnTheGrid()
        {
            var notes = new[] { new MusicNote(0, .5, 60), new MusicNote(.5, .5, 62), new MusicNote(1, .5, 64), new MusicNote(1.5, .25, 65), new MusicNote(1.7, .2, 67) };
            var swung = MusicGroove.Apply(notes, 1 / 3.0, .5);
            var expected = new[] { 0, .5 + 1 / 6.0, 1, 1.5 + 1 / 6.0, 1.7 };
            for (int i = 0; i < expected.Length; i++) Assert.AreEqual(expected[i], swung[i].Beat, 1e-9, "every second eighth; off-grid notes stay");
            Assert.AreEqual(.5 - 1 / 6.0, swung[1].Duration, 1e-9, "shortened so it still ends on the next downbeat");
            Assert.AreEqual(.125, swung[3].Duration, 1e-9, "never shorter than half its length");
            Assert.AreEqual(.5, notes[1].Beat, "the input is not changed");
            Assert.IsTrue(ReferenceEquals(notes, MusicGroove.Apply(notes, 0, .5)), "no swing = the same notes");
            Assert.AreEqual(.25 + .1, MusicGroove.Apply(notes.Concat(new[] { new MusicNote(.25, .25, 70) }).ToArray(), .4, .25).Last().Beat, 1e-9, "sixteenth grid");
            Assert.Throws<ArgumentException>(() => MusicGroove.Apply(notes, .9, .5));
            Assert.Throws<ArgumentException>(() => MusicGroove.Apply(notes, .3, 0));
        }

        [Test]
        public void SongSwingReachesPlaybackAndMidiButNotStraightTracks() => Run(s =>
        {
            s.Swing = .25f; s.SwingGrid = .5f;
            var lead = s.PerformedNotes(s.Parts[0]);
            Assert.AreEqual(.625, lead[1].Beat, 1e-9);
            Assert.AreEqual(.5, s.Parts[0].Notes[1].Beat, "stored notes stay straight");
            var cue = s.Compile();
            var midi = MidiMusicReader.Read(MidiMusicWriter.Write(s).Bytes);
            Assert.AreEqual(.625, midi.Parts[0].Notes.OrderBy(n => n.Beat).ElementAt(1).Beat, 1e-3, "MIDI exports what plays");
            s.Parts[0].IgnoreSwing = true;
            Assert.AreEqual(.5, s.PerformedNotes(s.Parts[0])[1].Beat, 1e-9);
            s.Parts[0].IgnoreSwing = false; s.Swing = 2;
            Assert.Throws<InvalidOperationException>(() => s.Compile());
        });

        [Test]
        public void MidiCarriesKeyChanges() => Run(s =>
        {
            var midi = MidiMusicReader.Read(MidiMusicWriter.Write(s).Bytes);
            Assert.AreEqual("A minor", midi.Key);
            Assert.AreEqual(1, midi.KeyChanges.Count);
            Assert.AreEqual(16, midi.KeyChanges[0].Beat, 1e-6); Assert.AreEqual("C# minor", midi.KeyChanges[0].Key);
        });

        [Test]
        public void LoudnessTargetIsMatched() => Run(s =>
        {
            s.Parts[1].Synth = MusicSynthPresets.Create("Warm pad"); s.Parts[1].Gain = .05f;
            s.Parts[1].Notes = new[] { new MusicNote(0, 16, 57, .8f), new MusicNote(0, 16, 64, .8f) };
            s.LoopBeats = 8; s.KeyChanges = Array.Empty<MusicKeyChange>(); s.Chords = Array.Empty<MusicChordMarker>();
            Assert.IsNull(MusicLoudness.MatchTarget(s), "no target");
            s.TargetLufs = -16;
            double reached = MusicLoudness.MatchTarget(s, 16000).Value;
            Assert.AreEqual(-16, reached, .5);
            Assert.IsTrue(s.Mixer.Limiter);
            Assert.AreEqual(-16, MusicLoudness.Measure(MusicLoudness.RenderSong(s, 16000), 16000, out _), .5);
        });
    }
}
