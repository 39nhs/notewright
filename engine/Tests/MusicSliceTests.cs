using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicSliceTests
    {
        const int Rate = 8000;

        // Two quarter-second tones: 200 Hz then 800 Hz.
        static float[] TwoTones()
        {
            var pcm = new float[Rate / 2];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(.5 * Math.Sin(2 * Math.PI * (i < Rate / 4 ? 200 : 800) * i / Rate));
            return pcm;
        }

        static MusicCue Cue(int version, MusicInstrument instrument, params MusicNote[] notes) =>
            new MusicCue("Slices", 120, 4, 8, new[] { new MusicPart(MusicLayer.Melody, instrument, .5, "Chop", notes) }, Array.Empty<MusicMixEffect>(), null, null, version);

        static float[] Render(MusicCue cue, double seconds, out AdaptiveMusicEngine engine)
        {
            var pcm = new float[(int)(seconds * Rate) * 2];
            engine = new AdaptiveMusicEngine(cue, Rate, playOnce: true);
            engine.RenderStereo(pcm);
            return pcm;
        }

        // Frequency from zero crossings of the left channel between two times.
        static double Frequency(float[] stereo, double from, double to)
        {
            int crossings = 0;
            for (int f = (int)(from * Rate) + 1; f < (int)(to * Rate); f++) if ((stereo[f * 2] >= 0) != (stereo[(f - 1) * 2] >= 0)) crossings++;
            return crossings / 2.0 / (to - from);
        }

        static double Rms(float[] stereo, double from, double to)
        {
            double e = 0; int a = (int)(from * Rate), b = (int)(to * Rate);
            for (int f = a; f < b; f++) e += stereo[f * 2] * (double)stereo[f * 2];
            return Math.Sqrt(e / (b - a));
        }

        static MusicInstrument Sliced() => new MusicInstrument(TwoTones(), Rate, 60, .002, .01, null, new[] { 0, .25 });

        [Test]
        public void EachKeyPlaysItsSliceAtTheRecordedPitch()
        {
            // Root 60 = slice 1 (200 Hz), 61 = slice 2 (800 Hz). One-beat notes (0.5 s) are longer than the slices.
            var output = Render(Cue(MusicPlaybackVersion.V2, Sliced(), new MusicNote(0, 1, 61), new MusicNote(2, 1, 60)), 2, out var engine);
            Assert.AreEqual(800, Frequency(output, .05, .2), 15, "Key 61: slice 2, not slice 1 pitched up");
            Assert.Less(Rms(output, .28, .45), 1e-4, "A slice ends where the next one starts, even under a longer note");
            Assert.AreEqual(200, Frequency(output, 1.05, 1.2), 10, "Key 60: slice 1");
            Assert.Less(Rms(output, 1.28, 1.45), 1e-4, "Slice 1 stops at slice 2's start");
            Assert.AreEqual(0, engine.DroppedNotes);
        }

        [Test]
        public void KeysWithoutASliceAreSilent()
        {
            var output = Render(Cue(MusicPlaybackVersion.V2, Sliced(), new MusicNote(0, 1, 59), new MusicNote(0, 1, 62)), 1, out var engine);
            Assert.Less(output.Max(x => Math.Abs(x)), 1e-6);
            Assert.AreEqual(0, engine.DroppedNotes, "Not counted as dropped");
        }

        [Test]
        public void V1IgnoresSlices()
        {
            var plain = new MusicInstrument(TwoTones(), Rate, 60, .002, .01);
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V1, plain, new MusicNote(0, 1, 62)), 1, out _),
                Render(Cue(MusicPlaybackVersion.V1, Sliced(), new MusicNote(0, 1, 62)), 1, out _), "V1 plays the whole sample, pitched");
        }

        [Test]
        public void UnslicedSamplesSoundAsBefore()
        {
            var plain = new MusicInstrument(TwoTones(), Rate, 60, .002, .01);
            var empty = new MusicInstrument(TwoTones(), Rate, 60, .002, .01, null, new double[0]);
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V2, plain, new MusicNote(0, 2, 64)), 1.5, out _),
                Render(Cue(MusicPlaybackVersion.V2, empty, new MusicNote(0, 2, 64)), 1.5, out _));
        }

        [Test]
        public void InvalidSlicesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new MusicInstrument(TwoTones(), Rate, 60, .002, .01, null, new[] { .2, .1 }));
            Assert.Throws<ArgumentException>(() => new MusicInstrument(TwoTones(), Rate, 60, .002, .01, null, new[] { 0, .6 }));
            Assert.Throws<ArgumentException>(() => new MusicInstrument(TwoTones(), Rate, 60, .002, .01, null, Enumerable.Range(0, 129).Select(i => i * .001).ToArray()));
        }

        [Test]
        public void DefinitionCompilesSlices()
        {
            var clip = AudioClip.Create("chop", TwoTones().Length, 1, Rate, false);
            clip.SetData(TwoTones(), 0);
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Chop"; song.PlaybackVersion = MusicPlaybackVersion.V2; song.LoopBeats = 4;
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Chop", Sample = clip, SampleRootKey = 60, SliceSeconds = new[] { 0f, .25f }, Notes = new[] { new MusicNote(0, 1, 61) } } };
            Assert.AreEqual(2, song.Compile().GetPart(0).Instrument.SliceCount);
            song.Parts[0].SliceSeconds = new[] { .3f, .1f };
            var e = Assert.Throws<InvalidOperationException>(() => song.Compile());
            Assert.IsTrue(e.Message.StartsWith("Chop:"), e.Message);
        }

        [Test]
        public void SliceHelpers()
        {
            CollectionAssert.AreEqual(new[] { 0f, .25f, .5f, .75f }, MusicSlices.Even(1, 4));
            CollectionAssert.AreEqual(new[] { 0f, .5f, 1f, 1.5f }, MusicSlices.EveryBeats(2, 120, 1));
            // Clicks at 0.1, 0.35 and 0.6 s in silence.
            var pcm = new float[Rate];
            foreach (double t in new[] { .1, .35, .6 })
                for (int i = 0; i < 200; i++) pcm[(int)(t * Rate) + i] = (float)(.8 * Math.Exp(-i / 40.0) * Math.Sin(i * .9));
            var found = MusicSlices.AtTransients(pcm, Rate);
            Assert.AreEqual(3, found.Length, string.Join(", ", found));
            for (int i = 0; i < 3; i++) Assert.AreEqual(new[] { .1, .35, .6 }[i], found[i], .011, "Starts just before each click");
            Assert.IsTrue(found.Zip(found.Skip(1), (a, b) => b > a).All(x => x), "Ascending");
        }
    }
}
