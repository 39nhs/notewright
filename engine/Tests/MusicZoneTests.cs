using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicZoneTests
    {
        const int Rate = 8000;

        // One-second sine: each zone's recording has its own frequency, so the pitch heard tells which zone played (and from which root).
        static float[] Tone(double hz, double amplitude = .5)
        {
            var pcm = new float[Rate];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
            return pcm;
        }

        static MusicSampleZone Zone(double hz, int root, int low, int high, float minVelocity = 0, float gain = 1) =>
            new MusicSampleZone(Tone(hz), Rate, root, low, high, minVelocity, gain);

        // The instrument's own sample (V1/V2) is a 300 Hz tone at key 60.
        static MusicInstrument Instrument(params MusicSampleZone[] zones) => new MusicInstrument(Tone(300), Rate, 60, .002, .01, zones);

        static MusicCue Cue(int version, MusicInstrument instrument, params MusicNote[] notes) =>
            new MusicCue("Zones", 120, 4, 16, new[] { new MusicPart(MusicLayer.Melody, instrument, .5, "Zones", notes) }, Array.Empty<MusicMixEffect>(), null, null, version);

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

        // Note n (0-based) at second n (beat 2n, 120 BPM), 0.4 s long.
        static MusicNote[] Notes(params (int key, float velocity)[] notes) => notes.Select((n, i) => new MusicNote(i * 2, .8, n.key, n.velocity)).ToArray();
        static double FrequencyOfNote(float[] output, int n) => Frequency(output, n + .05, n + .35);
        static double Semitones(double hz, int semitones) => hz * Math.Pow(2, semitones / 12.0);

        static readonly MusicSampleZone[] TwoZones = { Zone(100, 48, 0, 60), Zone(500, 72, 61, 127) };

        [Test]
        public void EachKeyPlaysItsZoneFromThatZonesRoot()
        {
            var output = Render(Cue(MusicPlaybackVersion.V3, Instrument(TwoZones), Notes((48, .5f), (60, .5f), (61, .5f), (72, .5f))), 4);
            Assert.AreEqual(100, FrequencyOfNote(output, 0), 5, "root of the low zone");
            Assert.AreEqual(200, FrequencyOfNote(output, 1), 8, "low zone, an octave up");
            Assert.AreEqual(Semitones(500, -11), FrequencyOfNote(output, 2), 10, "the high zone starts at 61");
            Assert.AreEqual(500, FrequencyOfNote(output, 3), 15, "root of the high zone");
        }

        [Test]
        public void VelocityLayersShareKeys()
        {
            var instrument = Instrument(Zone(200, 60, 0, 127), Zone(800, 60, 0, 127, .6f));
            var output = Render(Cue(MusicPlaybackVersion.V3, instrument, Notes((60, .3f), (60, .9f), (60, .6f), (60, .59f))), 4);
            CollectionAssert.AreEqual(new double[] { 200, 800, 800, 200 }, Enumerable.Range(0, 4).Select(n => Math.Round(FrequencyOfNote(output, n) / 100) * 100).ToArray());
        }

        [Test]
        public void KeysOutsideEveryZoneUseTheNearest()
        {
            var instrument = Instrument(Zone(100, 45, 40, 50), Zone(500, 75, 70, 80, .5f), Zone(700, 75, 70, 80));
            Assert.AreEqual(0, instrument.ZoneFor(30, .5f), "below every zone");
            Assert.AreEqual(0, instrument.ZoneFor(55, .5f));
            Assert.AreEqual(1, instrument.ZoneFor(66, .8f), "nearer the high zones: the loud layer for a loud note");
            Assert.AreEqual(2, instrument.ZoneFor(90, .2f), "a soft note above: the soft layer");
            Assert.AreEqual(1, instrument.ZoneFor(75, .9f));
            var soft = Instrument(Zone(400, 60, 0, 127, .5f));
            Assert.AreEqual(0, soft.ZoneFor(60, .1f), "quieter than every layer: the softest still plays");
            Assert.AreEqual(-1, new MusicInstrument(Tone(300), Rate, 60).ZoneFor(60, 1), "no zones");
            var output = Render(Cue(MusicPlaybackVersion.V3, instrument, Notes((33, .5f))), 1);
            Assert.AreEqual(Semitones(100, -12), FrequencyOfNote(output, 0), 4, "pitched from the nearest zone's root");
        }

        [Test]
        public void OlderPlaybackVersionsPlayTheOwnSample()
        {
            foreach (int version in new[] { MusicPlaybackVersion.V1, MusicPlaybackVersion.V2 })
            {
                var output = Render(Cue(version, Instrument(TwoZones), Notes((60, .5f), (72, .5f))), 2);
                Assert.AreEqual(300, FrequencyOfNote(output, 0), 10, "V" + version);
                Assert.AreEqual(600, FrequencyOfNote(output, 1), 15, "V" + version);
            }
        }

        [Test]
        public void ZoneGainScalesTheVoice()
        {
            var instrument = Instrument(Zone(200, 60, 0, 60), Zone(200, 72, 61, 127, 0, .5f));
            var output = Render(Cue(MusicPlaybackVersion.V3, instrument, Notes((60, .5f), (72, .5f))), 2);
            Assert.AreEqual(.5, Rms(output, 1.05, 1.35) / Rms(output, .05, .35), .03);
        }

        [Test]
        public void InvalidZonesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => Instrument(Zone(100, 60, 70, 60)), "low above high");
            Assert.Throws<ArgumentException>(() => Instrument(Zone(100, 60, 0, 128)), "key 128");
            Assert.Throws<ArgumentException>(() => Instrument(Zone(100, 60, 0, 127, 2)), "velocity 2");
            Assert.Throws<ArgumentException>(() => Instrument(Zone(100, 60, 0, 127, 0, float.NaN)), "NaN gain");
            Assert.Throws<ArgumentException>(() => Instrument(new MusicSampleZone(Array.Empty<float>(), Rate, 60, 0, 127)), "empty recording");
            Assert.Throws<ArgumentException>(() => Instrument(Enumerable.Range(0, MusicInstrument.MaxZones + 1).Select(i => Zone(100, 60, 0, 127)).ToArray()));
            Assert.AreEqual(0, Instrument().ZoneCount, "no zones = the plain sampler");
        }

        static AudioClip Clip(double hz)
        {
            var clip = AudioClip.Create("tone" + hz, Rate, 1, Rate, false);
            clip.SetData(Tone(hz), 0);
            return clip;
        }

        static MusicCueDefinition Song(AudioClip sample, params MusicCueDefinition.SampleZone[] zones)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Zones"; song.Bpm = 120; song.LoopBeats = 8; song.PlaybackVersion = MusicPlaybackVersion.V3;
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Piano", Layer = MusicLayer.Melody, Gain = .5f, Sample = sample, SampleRootKey = 60,
                AttackSeconds = .002f, ReleaseSeconds = .01f, Zones = zones, Notes = Notes((48, .5f), (72, .5f)) } };
            return song;
        }

        [Test]
        public void SongZonesCompileWithoutATrackSample()
        {
            var song = Song(null, new MusicCueDefinition.SampleZone { Sample = Clip(100), RootKey = 48, LowKey = 0, HighKey = 60 },
                new MusicCueDefinition.SampleZone { Sample = Clip(500), RootKey = 72, LowKey = 61, HighKey = 127, GainDb = -6 });
            var output = Render(song.Compile(), 2);
            Assert.AreEqual(100, FrequencyOfNote(output, 0), 5);
            Assert.AreEqual(500, FrequencyOfNote(output, 1), 15);
            Assert.AreEqual(Math.Pow(10, -6 / 20.0), Rms(output, 1.05, 1.35) / Rms(output, .05, .35), .03, "GainDb");
            output = Render(song.Compile(MusicPlaybackVersion.V2), 2);
            Assert.AreEqual(100, FrequencyOfNote(output, 0), 5, "V2 without a Sample: the zone nearest middle C (a tie goes to the first listed)");
            Assert.AreEqual(Semitones(100, 24), Frequency(output, 1.05, 1.2), 20, "the 1 s recording lasts 0.25 s two octaves up");
        }

        [Test]
        public void SongValidationNamesTheTrackAndZone()
        {
            var e = Assert.Throws<InvalidOperationException>(() => Song(Clip(300), new MusicCueDefinition.SampleZone { RootKey = 60, HighKey = 127 }).Compile());
            Assert.IsTrue(e.Message.Contains("Piano") && e.Message.Contains("zone 1"), e.Message);
            e = Assert.Throws<InvalidOperationException>(() => Song(Clip(300), new MusicCueDefinition.SampleZone { Sample = Clip(200), RootKey = 60, LowKey = 90, HighKey = 10 }).Compile());
            Assert.IsTrue(e.Message.StartsWith("Piano: "), e.Message);
            var withVariants = Song(Clip(300), new MusicCueDefinition.SampleZone { Sample = Clip(200), RootKey = 60, HighKey = 127 });
            withVariants.Parts[0].Variants = new[] { new MusicCueDefinition.SampleVariant { Sample = Clip(800), MinVelocity = .5f } };
            e = Assert.Throws<InvalidOperationException>(() => withVariants.Compile());
            Assert.IsTrue(e.Message.Contains("variants"), e.Message);
        }

        [Test]
        public void RootKeysComeFromFileNames()
        {
            Assert.AreEqual(60, MusicSampleZones.RootFromName("Piano_C4"));
            Assert.AreEqual(54, MusicSampleZones.RootFromName("strings F#3 sus"));
            Assert.AreEqual(73, MusicSampleZones.RootFromName("Db5"));
            Assert.AreEqual(9, MusicSampleZones.RootFromName("sub_A-1"));
            Assert.AreEqual(45, MusicSampleZones.RootFromName("Cello_A2_ff"));
            Assert.AreEqual(60, MusicSampleZones.RootFromName("piano-60"));
            Assert.AreEqual(59, MusicSampleZones.RootFromName("take2_B3"), "a note name wins over numbers");
            Assert.IsNull(MusicSampleZones.RootFromName("kick"));
            Assert.IsNull(MusicSampleZones.RootFromName("hit-300"));
        }

        [Test]
        public void RangesMeetHalfwayBetweenRoots()
        {
            Assert.IsTrue(new[] { (0, 54), (55, 66), (67, 127) }.SequenceEqual(MusicSampleZones.Ranges(new[] { 48, 60, 72 })));
            Assert.IsTrue(new[] { (61, 127), (0, 60) }.SequenceEqual(MusicSampleZones.Ranges(new[] { 72, 48 })), "kept in input order");
            Assert.IsTrue(new[] { (0, 127) }.SequenceEqual(MusicSampleZones.Ranges(new[] { 60 })));
            Assert.Throws<ArgumentException>(() => MusicSampleZones.Ranges(Array.Empty<int>()));
        }
    }
}
