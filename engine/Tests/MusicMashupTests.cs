using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicMashupTests
    {
        static MusicCueDefinition Song(string name, double bpm, double loop, int version, params MusicCueDefinition.Part[] parts)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = name; song.Bpm = bpm; song.LoopBeats = loop; song.PlaybackVersion = version; song.Parts = parts;
            return song;
        }

        static MusicCueDefinition.Part Track(string name, MusicLayer layer, MusicVoice voice, params MusicNote[] notes) =>
            new MusicCueDefinition.Part { Name = name, InstrumentId = name, Layer = layer, FallbackVoice = voice, Gain = .5f, Notes = notes };

        static MusicNote[] Scale(int tonic, double beats) =>
            Enumerable.Range(0, (int)beats).Select(i => new MusicNote(i, 1, tonic + new[] { 0, 2, 4, 5, 7, 9, 11, 12 }[i % 8], .8f)).ToArray();

        [Test]
        public void LayersPlayTogetherOnTheBaseTempo()
        {
            var a = Song("A", 120, 8, MusicPlaybackVersion.V2, Track("Lead", MusicLayer.Melody, MusicVoice.Bell, new MusicNote(0, 1, 72), new MusicNote(4, 1, 74)));
            var b = Song("B", 90, 4, MusicPlaybackVersion.V2, Track("Kick", MusicLayer.Drums, MusicVoice.Kick, new MusicNote(0, .5, 36), new MusicNote(2, .5, 36)),
                Track("Lead", MusicLayer.Melody, MusicVoice.Pad, new MusicNote(1, 2, 60)));
            var r = MusicMashup.Combine(new[] { new MusicMashupLayer { Song = a }, new MusicMashupLayer { Song = b, Repeat = 2, Transpose = 3, Gain = .5f } });
            var m = r.Song;
            Assert.AreEqual(8, m.LoopBeats);
            Assert.AreEqual(120, m.Bpm);
            CollectionAssert.AreEqual(new[] { "Lead", "Kick", "Lead (B)" }, m.Parts.Select(p => p.Name).ToArray(), "Name clash gets the song name");
            CollectionAssert.AreEqual(new double[] { 0, 2, 4, 6 }, m.Parts[1].Notes.Select(n => n.Beat).ToArray(), "Repeat x2 back to back");
            Assert.IsTrue(m.Parts[1].Notes.All(n => n.Key == 36), "Drums are not transposed");
            CollectionAssert.AreEqual(new[] { 63, 63 }, m.Parts[2].Notes.Select(n => n.Key).ToArray(), "Transpose +3");
            Assert.AreEqual(.25f, m.Parts[2].Gain, 1e-6, "Layer gain scales track gain");
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("90")), "Tempo difference is reported");
            Assert.IsTrue(m.ImportNotes.StartsWith(MusicMashup.GeneratedTag));
            var cue = m.Compile();
            var engine = new AdaptiveMusicEngine(cue, 8000, playOnce: true);
            var pcm = new float[8000 * 4 * 2];
            engine.RenderStereo(pcm);
            Assert.AreEqual(0, engine.DroppedNotes);
            Assert.IsTrue(pcm.Any(x => Math.Abs(x) > 1e-3), "The mashup plays");
            Assert.AreEqual(1, a.Parts.Length, "Sources are not modified");
            Assert.AreEqual(1, b.Parts[1].Notes.Length);
        }

        [Test]
        public void ExcerptsMutedTracksAndLength()
        {
            var a = Song("A", 100, 16, MusicPlaybackVersion.V2, Track("Bass", MusicLayer.Bass, MusicVoice.Bass, Enumerable.Range(0, 16).Select(i => new MusicNote(i, 1, 40)).ToArray()));
            var b = Song("B", 100, 16, MusicPlaybackVersion.V2, Track("Arp", MusicLayer.Harmony, MusicVoice.Bell, Enumerable.Range(0, 16).Select(i => new MusicNote(i, 1.5, 60 + i)).ToArray()),
                Track("Pad", MusicLayer.Harmony, MusicVoice.Pad, new MusicNote(0, 16, 48)));
            var m = MusicMashup.Combine(new[] {
                new MusicMashupLayer { Song = a, FromBeat = 4, LengthBeats = 8 },
                new MusicMashupLayer { Song = b, AtBeat = 4, FromBeat = 8, LengthBeats = 4, MutedParts = new[] { "Pad" } } }).Song;
            Assert.AreEqual(8, m.LoopBeats, "Auto length = last layer end rounded up to bars");
            Assert.AreEqual(2, m.Parts.Length, "Muted track left out");
            Assert.AreEqual(0, m.Parts[0].Notes[0].Beat, 1e-9, "Base excerpt starts at beat 0");
            Assert.AreEqual(8, m.Parts[0].Notes.Length);
            var arp = m.Parts[1].Notes;
            CollectionAssert.AreEqual(new double[] { 4, 5, 6, 7 }, arp.Select(n => n.Beat).ToArray());
            CollectionAssert.AreEqual(new[] { 68, 69, 70, 71 }, arp.Select(n => n.Key).ToArray(), "Notes from source beats 8-11");
            Assert.AreEqual(1, arp[3].Duration, 1e-9, "A note crossing the excerpt end is cut at it");
            var shorter = MusicMashup.Combine(new[] { new MusicMashupLayer { Song = a }, new MusicMashupLayer { Song = b, AtBeat = 4 } }, "Short", 8).Song;
            Assert.AreEqual(8, shorter.LoopBeats);
            Assert.IsTrue(shorter.Parts.All(p => p.Notes.All(n => n.Beat < 8)), "Explicit length cuts later notes");
        }

        [Test]
        public void TempoMapRegionsAndPartBudget()
        {
            var a = Song("A", 100, 16, MusicPlaybackVersion.V2, Track("Lead", MusicLayer.Melody, MusicVoice.Bell, new MusicNote(4, 1, 72), new MusicNote(8, 1, 72)));
            a.TempoChanges = new[] { new MusicTempoChange { Beat = 8, Bpm = 140, RampBeats = 4 } };
            a.NoiseRegions = new[] { new MusicNoiseRegion { Name = "Riser", Kind = MusicNoiseKind.White, StartBeat = 10, DurationBeats = 4, FadeInBeats = 2, FadeOutBeats = 1 } };
            var m = MusicMashup.Combine(new[] { new MusicMashupLayer { Song = a, FromBeat = 4, LengthBeats = 8, Repeat = 2 } }).Song;
            Assert.AreEqual(100, m.Bpm);
            CollectionAssert.AreEqual(new double[] { 4, 8, 12 }, m.TempoChanges.Select(c => c.Beat).ToArray(), "Ramp moved, tempo restored at the repeat, ramp again");
            CollectionAssert.AreEqual(new double[] { 140, 100, 140 }, m.TempoChanges.Select(c => c.Bpm).ToArray());
            CollectionAssert.AreEqual(new double[] { 6, 14 }, m.NoiseRegions.Select(x => x.StartBeat).ToArray(), "Region repeats with its excerpt");
            Assert.AreEqual(2, m.NoiseRegions[1].DurationBeats, 1e-9, "Clipped at the excerpt end");
            m.Compile();
            var many = Enumerable.Range(0, 20).Select(i => Track("T" + i, MusicLayer.Harmony, MusicVoice.Pad, new MusicNote(0, 1, 60))).ToArray();
            var big = Song("Big", 120, 4, MusicPlaybackVersion.V2, many);
            var e = Assert.Throws<InvalidOperationException>(() => MusicMashup.Combine(new[] { new MusicMashupLayer { Song = big }, new MusicMashupLayer { Song = big } }));
            Assert.IsTrue(e.Message.Contains("40 tracks"), e.Message);
            Assert.DoesNotThrow(() => MusicMashup.Combine(new[] { new MusicMashupLayer { Song = big },
                new MusicMashupLayer { Song = big, MutedParts = many.Take(8).Select(p => p.Name).ToArray() } }));
        }

        [Test]
        public void KeyEstimateAndTransposeSuggestion()
        {
            var c = Song("C", 120, 32, MusicPlaybackVersion.V2, Track("Line", MusicLayer.Melody, MusicVoice.Bell, Scale(60, 32)));
            var d = Song("D", 120, 32, MusicPlaybackVersion.V2, Track("Line", MusicLayer.Melody, MusicVoice.Bell, Scale(62, 32)));
            var keyC = MusicMashup.EstimateKey(c);
            var keyD = MusicMashup.EstimateKey(d);
            Assert.AreEqual((0, false), (keyC.tonic, keyC.minor));
            Assert.AreEqual((2, false), (keyD.tonic, keyD.minor));
            Assert.AreEqual(-2, MusicMashup.SuggestTranspose((keyC.tonic, keyC.minor), (keyD.tonic, keyD.minor)));
            Assert.AreEqual(0, MusicMashup.SuggestTranspose((9, true), (0, false)), "A minor and C major share notes");
            Assert.AreEqual(5, MusicMashup.SuggestTranspose((5, false), (0, false)));
            Assert.AreEqual(-5, MusicMashup.SuggestTranspose((7, false), (0, false)), "Nearest direction: down a fourth, not up a fifth");
            Assert.AreEqual("A minor", MusicMashup.KeyName((9, true)));
        }

        [Test]
        public void InvalidLayersAreRejected()
        {
            var a = Song("A", 120, 4, MusicPlaybackVersion.V2, Track("Lead", MusicLayer.Melody, MusicVoice.Bell, new MusicNote(0, 1, 72)));
            Assert.Throws<ArgumentException>(() => MusicMashup.Combine(new MusicMashupLayer[0]));
            Assert.Throws<ArgumentException>(() => MusicMashup.Combine(new[] { new MusicMashupLayer() }));
            Assert.Throws<ArgumentException>(() => MusicMashup.Combine(new[] { new MusicMashupLayer { Song = a, FromBeat = 4 } }));
            Assert.Throws<ArgumentException>(() => MusicMashup.Combine(new[] { new MusicMashupLayer { Song = a, Repeat = 0 } }));
            Assert.Throws<ArgumentException>(() => MusicMashup.Combine(new[] { new MusicMashupLayer { Song = a, Transpose = 30 } }));
            Assert.Throws<InvalidOperationException>(() => MusicMashup.Combine(new[] { new MusicMashupLayer { Song = a, MutedParts = new[] { "Lead" } } }));
        }
    }
}
