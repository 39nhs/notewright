using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicColorTests
    {
        const int Rate = 16000;

        // A 2 s mono sample: a sine at `hz` (or a sum), played at its root so the voice reproduces it.
        static float[] Tone(params double[] hz)
        {
            var pcm = new float[Rate * 2];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(hz.Sum(f => Math.Sin(2 * Math.PI * f * i / Rate)) * .3 / hz.Length);
            return pcm;
        }

        static float[] Render(MusicColorSettings color, int version = MusicPlaybackVersion.V3, double seconds = 1.5, float[] tone = null, double pan = 0)
        {
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(tone ?? Tone(1000), Rate, 60, .005, .01), .1, "Tone", new[] { new MusicNote(0, 3.5, 60) }, pan);
            var mixer = new MusicMixerSettings { Color = color ?? new MusicColorSettings() }.Compile();
            var cue = new MusicCue("Color", 120, 4, 4, new[] { part }, Array.Empty<MusicMixEffect>(), null, mixer, version);
            var pcm = new float[(int)(seconds * Rate) * 2];
            new AdaptiveMusicEngine(cue, Rate, playOnce: true).RenderStereo(pcm);
            return pcm;
        }

        static double Rms(float[] stereo, int channel, double from = .3, double to = 1.3)
        {
            double e = 0; int a = (int)(from * Rate), b = (int)(to * Rate);
            for (int f = a; f < b; f++) e += stereo[f * 2 + channel] * (double)stereo[f * 2 + channel];
            return Math.Sqrt(e / (b - a));
        }
        static double Db(double ratio) => 20 * Math.Log10(ratio);

        [Test]
        public void OffMeansUnchangedAndV2IgnoresIt()
        {
            Assert.IsNull(new MusicMixerSettings().Compile().Color, "nothing on = no stage");
            var plain = Render(null);
            var colored = new MusicColorSettings { Phaser = true, StereoWidth = 0 };
            CollectionAssert.AreEqual(Render(null, MusicPlaybackVersion.V2), Render(colored, MusicPlaybackVersion.V2), "V2 ignores the color stage");
            Assert.AreNotEqual(Rms(plain, 0), Rms(Render(colored), 0), "V3 plays it");
        }

        [Test]
        public void ParametricEqBands()
        {
            double Level(MusicEqBand band, double hz) => Rms(Render(new MusicColorSettings { EqBands = new[] { band } }, tone: Tone(hz)), 0) / Rms(Render(null, tone: Tone(hz)), 0);
            Assert.Less(Db(Level(new MusicEqBand(MusicEqBandType.LowCut, 1000), 125)), -30, "low cut");
            Assert.AreEqual(0, Db(Level(new MusicEqBand(MusicEqBandType.LowCut, 1000), 6000)), .5, "passes above");
            Assert.AreEqual(12, Db(Level(new MusicEqBand(MusicEqBandType.Peak, 1000, 12, 1), 1000)), .5, "peak +12 dB");
            Assert.Less(Db(Level(new MusicEqBand(MusicEqBandType.Notch, 1000, 0, 4), 1000)), -30, "notch");
            Assert.AreEqual(-6, Db(Level(new MusicEqBand(MusicEqBandType.HighShelf, 3000, -6), 7000)), .7, "high shelf");
            var off = new MusicEqBand(MusicEqBandType.Peak, 1000, 12) { Enabled = false };
            Assert.IsNull(new MusicMixerSettings { Color = new MusicColorSettings { EqBands = new[] { off } } }.Compile().Color, "disabled bands do not turn the stage on");
        }

        [Test]
        public void WidthAndAutoPan()
        {
            var panned = Render(new MusicColorSettings { StereoWidth = 0 }, pan: .8);
            Assert.AreEqual(Rms(panned, 0), Rms(panned, 1), 1e-6, "width 0 = mono");
            var wide = Render(new MusicColorSettings { StereoWidth = 2 }, pan: .4); var normal = Render(null, pan: .4);
            Assert.Greater(Math.Abs(Rms(wide, 0) - Rms(wide, 1)), Math.Abs(Rms(normal, 0) - Rms(normal, 1)) * 1.5, "width 2 widens");
            // Auto-pan synced to 1 beat (0.5 s at 120 BPM), full depth, square: left loud in the first half of each beat, right in the second.
            var auto = Render(new MusicColorSettings { Tremolo = true, TremoloPan = true, TremoloBeats = 1, TremoloDepth = 1, TremoloShape = MusicLfoShape.Square });
            Assert.Less(Rms(auto, 0, .52, .72) / Rms(auto, 1, .52, .72), .05, "first half of a beat: panned right (left closed)");
            Assert.Less(Rms(auto, 1, .77, .97) / Rms(auto, 0, .77, .97), .05, "second half: panned left");
        }

        [Test]
        public void TremoloFollowsTheTempo()
        {
            var t = Render(new MusicColorSettings { Tremolo = true, TremoloBeats = .5f, TremoloDepth = 1, TremoloShape = MusicLfoShape.Square });
            Assert.Greater(Rms(t, 0, .26, .37), .01); Assert.Less(Rms(t, 0, .38, .49), 1e-4, "a quarter-second cycle: half open, half closed");
        }

        [Test]
        public void ModulationEffectsStayBounded()
        {
            var noise = new float[Rate * 2]; var rng = new System.Random(1); for (int i = 0; i < noise.Length; i++) noise[i] = (float)((rng.NextDouble() * 2 - 1) * .3);
            foreach (var c in new[] { new MusicColorSettings { Phaser = true, PhaserFeedback = .95f, PhaserStages = 12 }, new MusicColorSettings { Flanger = true, FlangerFeedback = .95f, FlangerMix = 1 } })
            {
                var pcm = Render(c, tone: noise);
                Assert.IsTrue(pcm.All(x => !float.IsNaN(x) && Math.Abs(x) <= 1), "bounded");
                Assert.Greater(Rms(pcm, 0), .01);
            }
            var dry = Render(null, tone: noise); var flanged = Render(new MusicColorSettings { Flanger = true }, tone: noise);
            Assert.Greater(dry.Zip(flanged, (a, b) => Math.Abs(a - b)).Max(), .01, "the flanger changes the sound");
        }

        [Test]
        public void MultibandAndDeEsser()
        {
            var noise = new float[Rate * 2]; var rng = new System.Random(2); for (int i = 0; i < noise.Length; i++) noise[i] = (float)((rng.NextDouble() * 2 - 1) * .3);
            var flat = new MusicColorSettings { Multiband = true, MultibandLowRatio = 1, MultibandMidRatio = 1, MultibandHighRatio = 1 };
            Assert.AreEqual(0, Db(Rms(Render(flat, tone: noise), 0) / Rms(Render(null, tone: noise), 0)), .5, "ratio 1 everywhere: the bands sum back flat");
            var squash = new MusicColorSettings { Multiband = true, MultibandLowThresholdDb = -40, MultibandLowRatio = 20, MultibandMidRatio = 1, MultibandHighRatio = 1 };
            var bass = Tone(80); var treble = Tone(6000);
            Assert.Less(Db(Rms(Render(squash, tone: bass), 0) / Rms(Render(null, tone: bass), 0)), -6, "the low band is compressed");
            Assert.AreEqual(0, Db(Rms(Render(squash, tone: treble), 0) / Rms(Render(null, tone: treble), 0)), .7, "the high band is not");
            var deEss = new MusicColorSettings { DeEsser = true, DeEsserHz = 4000, DeEsserThresholdDb = -50, DeEsserRangeDb = 10 };
            Assert.AreEqual(-10, Db(Rms(Render(deEss, tone: Tone(6500)), 0) / Rms(Render(null, tone: Tone(6500)), 0)), 1.5, "sibilance band turned down by the range");
            Assert.AreEqual(0, Db(Rms(Render(deEss, tone: Tone(300)), 0) / Rms(Render(null, tone: Tone(300)), 0)), .5, "the rest untouched");
        }

        [Test]
        public void InvalidSettingsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { Color = new MusicColorSettings { Phaser = true, PhaserRateHz = 50 } }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { Color = new MusicColorSettings { Multiband = true, MultibandLowHz = 900, MultibandHighHz = 1000 } }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { Color = new MusicColorSettings { EqBands = Enumerable.Range(0, 9).Select(i => new MusicEqBand(MusicEqBandType.Peak, 1000)).ToArray() } }.Compile());
        }

        static MusicCueDefinition RoutedSong(float gain, bool preFader, float busPan = 0)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Routing"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.LoopBeats = 4;
            var clip = AudioClip.Create("tone", Rate * 2, 1, Rate, false); clip.SetData(Tone(500).Select(x => x * .1f).ToArray(), 0); // quiet: the master soft limiter stays linear
            song.Buses = new[] { new MusicBusDefinition { Name = "Fx", Pan = busPan } };
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Lead", Sample = clip, SampleRootKey = 60, Gain = gain, Output = "", Notes = new[] { new MusicNote(0, 3.5, 60) },
                Sends = new[] { new MusicSendDefinition("Fx", 0, null, preFader) } } };
            return song;
        }

        [Test]
        public void PreFaderSendsIgnoreTheTrackLevelAndBusesPan()
        {
            double Total(float gain, bool pre, float pan = 0, int channel = 0)
            {
                var song = RoutedSong(gain, pre, pan);
                try { var pcm = new float[(int)(1.5 * Rate) * 2]; new AdaptiveMusicEngine(song.Compile(), Rate, playOnce: true).RenderStereo(pcm); return Rms(pcm, channel); }
                finally { UnityEngine.Object.DestroyImmediate(song); }
            }
            // Output = track (gain g) + send at 0 dB (post-fader: × g; pre-fader: the unity-level track signal). Post: halving g halves everything;
            // pre: (0.25 + 1) / (0.5 + 1).
            Assert.AreEqual(.5, Total(.25f, false) / Total(.5f, false), .02, "post-fader follows the track gain");
            Assert.AreEqual(1.25 / 1.5, Total(.25f, true) / Total(.5f, true), .02, "pre-fader send stays at its level");
            Assert.Less(Total(.5f, false, 1, 0), Total(.5f, false, 0, 0) * .55, "bus pan 1: the bus leaves the left channel");
        }
    }
}
