using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicVariantTests
    {
        const int Rate = 8000;

        // One-second sine at a fixed frequency: the frequency tells which take played (all takes share root key 60).
        static float[] Tone(double hz, double amplitude = .5)
        {
            var pcm = new float[Rate];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
            return pcm;
        }

        static MusicInstrument Instrument(params MusicSampleVariant[] variants) =>
            new MusicInstrument(Tone(200), Rate, 60, .002, .01, null, null, variants);

        static MusicSampleVariant Take(double hz, float minVelocity, float gain = 1) => new MusicSampleVariant(Tone(hz), Rate, minVelocity, gain);

        static MusicCue Cue(int version, MusicInstrument instrument, params MusicNote[] notes) =>
            new MusicCue("Takes", 120, 4, 16, new[] { new MusicPart(MusicLayer.Melody, instrument, .5, "Takes", notes) }, Array.Empty<MusicMixEffect>(), null, null, version);

        static float[] Render(MusicCue cue, double seconds)
        {
            var pcm = new float[(int)(seconds * Rate) * 2];
            new AdaptiveMusicEngine(cue, Rate, playOnce: true).RenderStereo(pcm);
            return pcm;
        }

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

        // Note n (0-based) at beat 2n: half a second of sound, then half a second of silence (120 BPM).
        static MusicNote[] Notes(params float[] velocities) => velocities.Select((v, i) => new MusicNote(i * 2, .8, 60, v)).ToArray();
        static double FrequencyOfNote(float[] output, int n) => Frequency(output, n + .05, n + .35);

        [Test]
        public void VelocityPicksTheLayer()
        {
            var output = Render(Cue(MusicPlaybackVersion.V2, Instrument(Take(800, .6f)), Notes(.3f, .9f, .6f, .59f)), 4);
            Assert.AreEqual(200, FrequencyOfNote(output, 0), 10, "Below .6: the Sample");
            Assert.AreEqual(800, FrequencyOfNote(output, 1), 15, "Above .6: the loud layer");
            Assert.AreEqual(800, FrequencyOfNote(output, 2), 15, "Exactly the minimum velocity reaches the layer");
            Assert.AreEqual(200, FrequencyOfNote(output, 3), 10);
        }

        [Test]
        public void RoundRobinTakesPlayInTurn()
        {
            var output = Render(Cue(MusicPlaybackVersion.V2, Instrument(Take(400, 0), Take(800, 0)), Notes(.5f, .5f, .5f, .5f)), 4);
            CollectionAssert.AreEqual(new double[] { 200, 400, 800, 200 }, Enumerable.Range(0, 4).Select(n => Math.Round(FrequencyOfNote(output, n) / 100) * 100).ToArray(),
                "Sample, then the takes in order, then the Sample again");
        }

        [Test]
        public void EachLayerKeepsItsOwnRoundRobin()
        {
            // Soft: the Sample only. Loud: two takes. Alternating soft/loud notes still alternate the loud takes.
            var output = Render(Cue(MusicPlaybackVersion.V2, Instrument(Take(600, .5f), Take(900, .5f)), Notes(.2f, .9f, .2f, .9f, .2f, .9f)), 6);
            CollectionAssert.AreEqual(new double[] { 200, 600, 200, 900, 200, 600 }, Enumerable.Range(0, 6).Select(n => Math.Round(FrequencyOfNote(output, n) / 100) * 100).ToArray());
        }

        [Test]
        public void TakeGainBalancesTakes()
        {
            var output = Render(Cue(MusicPlaybackVersion.V2, Instrument(Take(200, .5f, .5f)), Notes(.4f, .8f)), 2);
            // Note 2 is twice as loud by velocity and half as loud by its take gain.
            Assert.AreEqual(1, Rms(output, 1.05, 1.35) / Rms(output, .05, .35), .02);
        }

        [Test]
        public void V1PlaysOnlyTheSample()
        {
            var plain = new MusicInstrument(Tone(200), Rate, 60, .002, .01);
            var notes = Notes(.3f, .9f, .9f);
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V1, plain, notes), 3), Render(Cue(MusicPlaybackVersion.V1, Instrument(Take(800, .6f), Take(400, 0)), notes), 3));
        }

        [Test]
        public void InstrumentsWithoutVariantsSoundAsBefore()
        {
            var notes = Notes(.3f, .9f);
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V2, new MusicInstrument(Tone(200), Rate, 60, .002, .01), notes), 2),
                Render(Cue(MusicPlaybackVersion.V2, Instrument(), notes), 2));
        }

        [Test]
        public void RendersAreRepeatable()
        {
            var cue = Cue(MusicPlaybackVersion.V2, Instrument(Take(400, 0), Take(800, .7f), Take(900, .7f)), Notes(.5f, .8f, .5f, .8f));
            CollectionAssert.AreEqual(Render(cue, 4), Render(cue, 4), "Round robin restarts with each engine");
        }

        [Test]
        public void LayoutIsReported()
        {
            var instrument = Instrument(Take(400, 0), Take(800, .7f), Take(900, .7f), Take(1000, .3f));
            Assert.AreEqual(4, instrument.VariantCount);
            Assert.AreEqual(3, instrument.VelocityLayerCount, "0, .3 and .7");
            Assert.AreEqual(0, new MusicInstrument(Tone(200), Rate, 60).VariantCount);
            Assert.AreEqual(1, new MusicInstrument(Tone(200), Rate, 60).VelocityLayerCount);
        }

        [Test]
        public void InvalidVariantsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => Instrument(Take(400, 1.5f)), "Velocity above 1");
            Assert.Throws<ArgumentException>(() => Instrument(Take(400, .5f, -1)), "Negative gain");
            Assert.Throws<ArgumentException>(() => Instrument(new MusicSampleVariant(new float[1], Rate, 0)), "Too short");
            Assert.Throws<ArgumentException>(() => new MusicInstrument(Tone(200), Rate, 60, .002, .01, null, new[] { 0, .5 }, new[] { Take(400, 0) }), "Not with slices");
            Assert.Throws<ArgumentException>(() => Instrument(Enumerable.Range(1, 16).Select(i => Take(400, i / 20f)).ToArray()), "17 layers");
            Assert.DoesNotThrow(() => Instrument(Enumerable.Range(1, 15).Select(i => Take(400, i / 20f)).ToArray()), "16 layers");
            Assert.Throws<ArgumentException>(() => Instrument(Enumerable.Range(0, 65).Select(i => Take(400, 0)).ToArray()), "65 variants");
        }

        static AudioClip Clip(double hz)
        {
            var clip = AudioClip.Create("tone" + hz, Rate, 1, Rate, false);
            clip.SetData(Tone(hz), 0);
            return clip;
        }

        static MusicCueDefinition Song(params MusicCueDefinition.SampleVariant[] variants)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Takes"; song.Bpm = 120; song.LoopBeats = 8; song.PlaybackVersion = MusicPlaybackVersion.V2;
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Guitar", Layer = MusicLayer.Melody, Gain = .5f, Sample = Clip(200), SampleRootKey = 60,
                AttackSeconds = .002f, ReleaseSeconds = .01f, Variants = variants, Notes = Notes(.3f, .9f) } };
            return song;
        }

        [Test]
        public void SongVariantsCompileAndPlay()
        {
            var song = Song(new MusicCueDefinition.SampleVariant { Sample = Clip(800), MinVelocity = .6f, GainDb = -6 });
            var output = Render(song.Compile(), 2);
            Assert.AreEqual(200, FrequencyOfNote(output, 0), 10);
            Assert.AreEqual(800, FrequencyOfNote(output, 1), 15);
            Assert.AreEqual(.9 / .3 * Math.Pow(10, -6 / 20.0), Rms(output, 1.05, 1.35) / Rms(output, .05, .35), .05, "GainDb");
        }

        [Test]
        public void SongValidationNamesTheTrack()
        {
            var e = Assert.Throws<InvalidOperationException>(() => Song(new MusicCueDefinition.SampleVariant { MinVelocity = .5f }).Compile());
            Assert.IsTrue(e.Message.Contains("Guitar") && e.Message.Contains("variant 1"), e.Message);
            e = Assert.Throws<InvalidOperationException>(() => Song(new MusicCueDefinition.SampleVariant { Sample = Clip(800), MinVelocity = 2 }).Compile());
            Assert.IsTrue(e.Message.StartsWith("Guitar: "), e.Message);
        }
    }
}
