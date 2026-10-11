using System;
using System.Linq;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicAmpTests
    {
        const int Rate = 48000;

        static float[] Sine(double hz, double amplitude = .3)
        {
            var pcm = new float[Rate];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
            return pcm;
        }

        // A one-second sine held for the whole render, through the master (or the part's own bus).
        static float[] Render(double hz, MusicMixerSettings master, MusicMixerSettings track = null, double amplitude = .3)
        {
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(Sine(hz, amplitude), Rate, 60, .002, .05), .8, "Tone",
                new[] { new MusicNote(0, 2, 60) }, 0, 0, 0, 0, MusicDriveMode.Soft, track?.Compile());
            var cue = new MusicCue("Amp", 120, 4, 4, new[] { part }, Array.Empty<MusicMixEffect>(), null, (master ?? new MusicMixerSettings()).Compile(), MusicPlaybackVersion.V2);
            var pcm = new float[Rate / 2 * 2];
            new AdaptiveMusicEngine(cue, Rate, playOnce: true).RenderStereo(pcm);
            return pcm;
        }

        // Power (dB) at one frequency in the left channel from .1 s to .5 s (Hann-windowed Goertzel).
        static double Db(float[] stereo, double hz)
        {
            int a = Rate / 10, n = Rate * 4 / 10;
            double w = 2 * Math.PI * hz / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++)
            {
                double x = stereo[(a + i) * 2] * (.5 - .5 * Math.Cos(2 * Math.PI * i / (n - 1)));
                double s0 = x + c * s1 - s2; s2 = s1; s1 = s0;
            }
            return 10 * Math.Log10(s1 * s1 + s2 * s2 - c * s1 * s2 + 1e-30);
        }

        static MusicMixerSettings Amp(MusicAmpModel model, float gainDb = 30, float levelDb = -12) =>
            new MusicMixerSettings { Amp = true, AmpModel = model, AmpGainDb = gainDb, AmpLevelDb = levelDb, AmpTightHz = 0 };

        [Test]
        public void HighGainAddsHarmonics()
        {
            var clean = Render(220, null);
            var driven = Render(220, Amp(MusicAmpModel.HighGain));
            Assert.Less(Db(clean, 660) - Db(clean, 220), -40, "The engine alone is nearly clean (only its soft limiter)");
            Assert.Greater(Db(driven, 660) - Db(driven, 220), -15, "Clipping: strong third harmonic");
            Assert.Greater(Db(driven, 440) - Db(driven, 220), -40, "Asymmetric stages add even harmonics");
            Assert.LessOrEqual(driven.Max(x => Math.Abs(x)), 1f);
        }

        [Test]
        public void CleanModelStaysCleanAtLowGain()
        {
            var output = Render(220, Amp(MusicAmpModel.Clean, 0, 0), null, .05);
            Assert.Less(Db(output, 660) - Db(output, 220), -30);
        }

        [Test]
        public void OversamplingKeepsAliasesDown()
        {
            // A clipped 1.9 kHz tone has harmonics far above 24 kHz; whatever folds back lands between the harmonics.
            var output = Render(1900, Amp(MusicAmpModel.HighGain, 30, -20));
            double fundamental = Db(output, 1900);
            double worst = Enumerable.Range(1, 10).Max(k => Db(output, (k + .5) * 1900)) - fundamental;
            Assert.Less(worst, -50, "Inharmonic (aliased) energy between the harmonics");
        }

        [Test]
        public void CabinetShapesTheTone()
        {
            var cab = new MusicMixerSettings { Cabinet = MusicCabinet.Closed4x12 };
            double Gain(double hz) => Db(Render(hz, cab), hz) - Db(Render(hz, null), hz);
            double low = Gain(40), body = Gain(110), bite = Gain(2800), top = Gain(9000);
            Assert.Less(low, -15, "Speaker low-end roll-off");
            Assert.Greater(bite, body - 3, "Upper-mid bite");
            Assert.Less(top, bite - 20, "Steep top roll-off");
            foreach (var model in new[] { MusicCabinet.Open2x12, MusicCabinet.Combo1x12, MusicCabinet.Bass4x10 })
            {
                var settings = new MusicMixerSettings { Cabinet = model };
                Assert.Less(Db(Render(10000, settings), 10000) - Db(Render(1000, settings), 1000), -15, model + " rolls off the top");
            }
        }

        [Test]
        public void SilenceStaysSilent()
        {
            var settings = Amp(MusicAmpModel.HighGain, 60, 12);
            settings.Cabinet = MusicCabinet.Closed4x12;
            var cue = new MusicCue("Silent", 120, 4, 4, new[] { new MusicPart(MusicLayer.Melody, new MusicInstrument(Sine(220), Rate, 60), .8, "Tone", new MusicNote(0, 1, 60, 0)) },
                Array.Empty<MusicMixEffect>(), null, settings.Compile(), MusicPlaybackVersion.V2);
            var pcm = new float[Rate];
            new AdaptiveMusicEngine(cue, Rate, playOnce: true).RenderStereo(pcm);
            Assert.AreEqual(0, pcm.Max(x => Math.Abs(x)), "No offset or noise from the bias");
        }

        [Test]
        public void OffChangesNothing()
        {
            // Every amp/cabinet field changed but both switches off: the same output as the defaults.
            var off = new MusicMixerSettings { AmpModel = MusicAmpModel.Bass, AmpGainDb = 50, AmpTightHz = 300, AmpBassDb = 6, AmpMidDb = -6, AmpTrebleDb = 3,
                AmpPresenceDb = 9, AmpLevelDb = 6, CabinetMix = .3f };
            CollectionAssert.AreEqual(Render(220, null), Render(220, off));
            Assert.IsFalse(off.Compile().AmpStage);
        }

        [Test]
        public void TrackAmpDrivesOnlyItsTrack()
        {
            var tracked = Render(220, null, Amp(MusicAmpModel.Crunch, 36, -10));
            Assert.Greater(Db(tracked, 660) - Db(tracked, 220), -25);
        }

        [Test]
        public void BassModelKeepsTheLowsClean()
        {
            var output = Render(55, Amp(MusicAmpModel.Bass, 30, -12));
            var high = Render(55, Amp(MusicAmpModel.HighGain, 30, -12));
            Assert.Less(Db(output, 165) - Db(output, 55), Db(high, 165) - Db(high, 55), "Less low-end distortion than high gain");
        }

        [Test]
        public void InvalidSettingsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { AmpGainDb = 61 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { AmpTightHz = -1 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { CabinetMix = 2 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { Cabinet = (MusicCabinet)99 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { AmpModel = (MusicAmpModel)99 }.Compile());
        }
    }
}
