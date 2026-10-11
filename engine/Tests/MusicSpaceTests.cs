using System;
using System.Linq;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicSpaceTests
    {
        const int Rate = 8000;

        static MusicCue Cue(int version, MusicMixerSettings master, MusicVoice voice = MusicVoice.Pad, int key = 60, double length = 1, double gain = .05, MusicMixerSettings track = null) =>
            new MusicCue("Space", 120, 4, 8, new[] { new MusicPart(MusicLayer.Harmony, new MusicInstrument(voice, .002, .02), gain, "A",
                new[] { new MusicNote(0, length, key) }, 0, 0, 0, 0, MusicDriveMode.Soft, track?.Compile()) },
                Array.Empty<MusicMixEffect>(), null, master?.Compile(), version);

        static float[] Render(MusicCue cue, double seconds)
        {
            var pcm = new float[(int)(seconds * Rate) * 2];
            new AdaptiveMusicEngine(cue, Rate, playOnce: true).RenderStereo(pcm);
            return pcm;
        }

        static double Rms(float[] stereo, double from, double to)
        {
            int a = (int)(from * Rate), b = (int)(to * Rate); double e = 0;
            for (int f = a; f < b; f++) e += stereo[f * 2] * (double)stereo[f * 2] + stereo[f * 2 + 1] * (double)stereo[f * 2 + 1];
            return Math.Sqrt(e / (2 * (b - a)));
        }

        [Test]
        public void SwitchedOffStagesChangeNothing()
        {
            var settings = new MusicMixerSettings { EqLowDb = 12, EqHighDb = -12, ChorusMix = 1, ChorusDepthMs = 10, ReverbMix = 1, ReverbSize = 1, ReverbPreDelayMs = 50 };
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V2, null), 2), Render(Cue(MusicPlaybackVersion.V2, settings), 2), "Values without Eq/Chorus/Reverb on are inert");
        }

        [Test]
        public void V1IgnoresTheStage()
        {
            var on = new MusicMixerSettings { Eq = true, EqLowDb = 12, Chorus = true, Reverb = true, ReverbMix = .5f };
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V1, null), 2), Render(Cue(MusicPlaybackVersion.V1, on), 2));
        }

        [Test]
        public void ReverbLeavesATailAfterTheNote()
        {
            var dry = Render(Cue(MusicPlaybackVersion.V2, null, length: .5), 2.5);
            var wet = Render(Cue(MusicPlaybackVersion.V2, new MusicMixerSettings { Reverb = true, ReverbMix = .5f, ReverbSize = .8f }, length: .5), 2.5);
            Assert.Less(Rms(dry, .6, 1.2), 1e-6, "Dry: silence after the note");
            Assert.Greater(Rms(wet, .6, 1.2), 1e-3, "Reverb tail rings on");
            Assert.Greater(Rms(wet, .6, .9), Rms(wet, 1.5, 2.4), "and decays");
            var delayed = Render(Cue(MusicPlaybackVersion.V2, new MusicMixerSettings { Reverb = true, ReverbMix = 1, ReverbPreDelayMs = 150 }, length: .5), .5);
            Assert.Less(Rms(delayed, .001, .14), 1e-6, "Pre-delay: nothing before 150 ms when fully wet");
            Assert.Greater(Rms(delayed, .16, .4), 1e-4);
        }

        [Test]
        public void EqShelvesAndPeakChangeTheirBand()
        {
            // Bass voice at key 36 (65 Hz): a +12 dB low shelf at 200 Hz raises it about 4x; a -12 dB high shelf barely touches it.
            double plain = Rms(Render(Cue(MusicPlaybackVersion.V2, null, MusicVoice.Bass, 36, gain: .02), 1), .2, .45);
            double low = Rms(Render(Cue(MusicPlaybackVersion.V2, new MusicMixerSettings { Eq = true, EqLowDb = 12, EqLowHz = 200 }, MusicVoice.Bass, 36, gain: .02), 1), .2, .45);
            double high = Rms(Render(Cue(MusicPlaybackVersion.V2, new MusicMixerSettings { Eq = true, EqHighDb = -12, EqHighHz = 3000 }, MusicVoice.Bass, 36, gain: .02), 1), .2, .45);
            Assert.AreEqual(4, low / plain, .6, "Low shelf +12 dB");
            Assert.AreEqual(1, high / plain, .05, "High shelf leaves the bass alone");
            // Pad at key 69 (440 Hz + 660 Hz): a -12 dB peak at 440 Hz with narrow Q lowers the mix.
            double pad = Rms(Render(Cue(MusicPlaybackVersion.V2, null, MusicVoice.Pad, 69, gain: .02), 1), .2, .45);
            double notch = Rms(Render(Cue(MusicPlaybackVersion.V2, new MusicMixerSettings { Eq = true, EqMidDb = -12, EqMidHz = 440, EqMidQ = 4 }, MusicVoice.Pad, 69, gain: .02), 1), .2, .45);
            Assert.Less(notch, pad * .85);
        }

        [Test]
        public void ChorusMovesTheSoundButKeepsItsLevel()
        {
            var dry = Render(Cue(MusicPlaybackVersion.V2, null, gain: .02), 1);
            var chorus = Render(Cue(MusicPlaybackVersion.V2, new MusicMixerSettings { Chorus = true, ChorusMix = .5f, ChorusDepthMs = 5, ChorusRateHz = 2 }, gain: .02), 1);
            double difference = dry.Zip(chorus, (a, b) => Math.Abs(a - b)).Max();
            Assert.Greater(difference, 1e-3, "The modulated copy changes the waveform");
            Assert.AreEqual(Rms(dry, .2, .45), Rms(chorus, .2, .45), Rms(dry, .2, .45) * .4, "Level within about 3 dB");
            Assert.AreNotEqual(chorus.Where((x, i) => i % 2 == 0).Take(4000).Sum(), chorus.Where((x, i) => i % 2 == 1).Take(4000).Sum(), "Left and right differ (stereo)");
        }

        [Test]
        public void TrackReverbStaysOnItsTrack()
        {
            var wetTrack = Render(Cue(MusicPlaybackVersion.V2, null, length: .5, track: new MusicMixerSettings { Reverb = true, ReverbMix = .5f }), 1.5);
            Assert.Greater(Rms(wetTrack, .6, 1.0), 1e-4, "The track's own reverb tail");
        }

        [Test]
        public void InvalidValuesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { ReverbMix = 2 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { EqMidQ = 0 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { ChorusDelayMs = 50 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { EqLowDb = 30 }.Compile());
        }
    }
}
