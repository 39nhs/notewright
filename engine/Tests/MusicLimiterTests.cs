using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicLimiterTests
    {
        const int Rate = 48000;

        static MusicCue Cue(MusicMixerSettings mixer, float gain, int version = MusicPlaybackVersion.V3, double seconds = 2)
        {
            var synth = MusicSynthPresets.Create("Saw lead"); synth.LfoPitchCents = 0;
            var notes = Enumerable.Range(0, 4).Select(i => new MusicNote(i, .9, 57 + i * 3, 1f)).ToArray();
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(synth.Compile()), gain, "Lead", notes, 0);
            var kick = new MusicPart(MusicLayer.Drums, new MusicInstrument(MusicVoice.Kick), Math.Min(1, gain * 2), "Kick", Enumerable.Range(0, 4).Select(i => new MusicNote(i, .5, 36)).ToArray(), 0);
            return new MusicCue("Limiter", 120, 4, 4, new[] { part, kick }, Array.Empty<MusicMixEffect>(), null, mixer.Compile(), version);
        }

        static float[] Render(MusicCue cue, double seconds = 2.2)
        {
            var engine = new AdaptiveMusicEngine(cue, Rate, playOnce: true);
            var pcm = new float[(int)(seconds * Rate) * 2]; engine.RenderStereo(pcm); return pcm;
        }

        [Test]
        public void QuietSignalsPassUnchangedApartFromTheLookahead()
        {
            var limited = Render(Cue(new MusicMixerSettings { Limiter = true, LimiterCeilingDb = 0 }, .004f));
            var soft = Render(Cue(new MusicMixerSettings(), .004f));
            int latency = (int)Math.Round(MusicMixerSettings.LimiterLookaheadSeconds * Rate);
            Assert.Less(soft.Max(Math.Abs), .02f, "quiet enough for tanh to be linear");
            double worst = 0;
            for (int i = Rate / 50 * 2; i < soft.Length - latency * 2; i++) worst = Math.Max(worst, Math.Abs(limited[i + latency * 2] - soft[i]));
            Assert.Less(worst, 2e-6, "a pure 2 ms delay at unity gain");
        }

        [Test]
        public void LoudSignalsStayUnderTheCeiling()
        {
            var mixer = new MusicMixerSettings { Limiter = true, LimiterCeilingDb = -1, OutputDb = 18 };
            var pcm = Render(Cue(mixer, .5f));
            Assert.LessOrEqual(pcm.Max(Math.Abs), Math.Pow(10, -1 / 20.0) + 1e-6, "no sample above -1 dBFS");
            double lufs = MusicLoudness.Measure(pcm, Rate, out double truePeak);
            Assert.LessOrEqual(truePeak, -.5, "true peak within half a dB of the ceiling");
            Assert.Greater(lufs, -12, "and loud");
            var soft = Render(Cue(new MusicMixerSettings { OutputDb = 18 }, .5f));
            Assert.Greater(soft.Max(Math.Abs), .95f, "the soft limiter alone lets the mix reach full scale");
            CollectionAssert.AreEqual(Render(Cue(new MusicMixerSettings { OutputDb = 18 }, .5f, MusicPlaybackVersion.V2)),
                Render(Cue(new MusicMixerSettings { OutputDb = 18, Limiter = true }, .5f, MusicPlaybackVersion.V2)), "V2 ignores the limiter");
        }

        [Test]
        public void ReleaseSetsHowFastTheGainRecovers()
        {
            // A loud bar then a quiet one: a short release lets the quiet bar come back sooner.
            MusicCue Song(float release)
            {
                var loud = new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bass), 1, "Loud", new[] { new MusicNote(0, 1, 45) }, 0);
                var quiet = new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Pad), .05, "Quiet", new[] { new MusicNote(1, 3, 69) }, 0);
                return new MusicCue("Release", 120, 4, 4, new[] { loud, quiet }, Array.Empty<MusicMixEffect>(), null,
                    new MusicMixerSettings { Limiter = true, LimiterCeilingDb = -6, LimiterInputDb = 12, LimiterReleaseMs = release }.Compile(), MusicPlaybackVersion.V3);
            }
            double Level(float[] x, double from, double to) { double e = 0; int a = (int)(from * Rate) * 2, b = (int)(to * Rate) * 2; for (int i = a; i < b; i++) e += x[i] * x[i]; return Math.Sqrt(e / (b - a)); }
            var fast = Render(Song(20)); var slow = Render(Song(1500));
            Assert.Greater(Level(fast, .65, .75), Level(slow, .65, .75) * 2, "shortly after the loud note the fast release is louder");
            Assert.AreEqual(Level(fast, 1.5, 1.9), Level(slow, 1.5, 1.9) * 1, Level(fast, 1.5, 1.9) * .6, "both recover in the end");
        }

        [Test]
        public void TruePeakFindsPeaksBetweenSamples()
        {
            var tp = new MusicTruePeak(); double peak = 0, samplePeak = 0;
            for (int i = 0; i < 400; i++)
            {
                double x = Math.Sin(2 * Math.PI * i / 4 + Math.PI / 4); // a quarter-rate sine sampled at ±0.707
                peak = Math.Max(peak, tp.Process(x)); samplePeak = Math.Max(samplePeak, Math.Abs(x));
            }
            Assert.AreEqual(.7071, samplePeak, 1e-3);
            Assert.AreEqual(1, peak, .03, "the true peak is 0 dBTP");
            var dc = new MusicTruePeak(); double last = 0; for (int i = 0; i < 40; i++) last = dc.Process(.5);
            Assert.AreEqual(.5, last, 1e-9);
            var meter = new MusicLevelMeter(Rate);
            var pcm = new float[8000]; for (int i = 0; i < 4000; i++) pcm[i * 2] = pcm[i * 2 + 1] = (float)Math.Sin(2 * Math.PI * i / 4 + Math.PI / 4);
            meter.ProcessStereo(pcm); meter.TryRead(out var levels);
            Assert.Greater(levels.TruePeakLeft, levels.PeakLeft + .2);
        }

        [Test]
        public void MatchLoudnessReachesTheTarget()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                song.name = "Loudness"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.LoopBeats = 8;
                song.Parts = new[]
                {
                    new MusicCueDefinition.Part { Name = "Pad", Gain = .1f, Synth = MusicSynthPresets.Create("Warm pad"), Notes = new[] { new MusicNote(0, 8, 57), new MusicNote(0, 8, 64) } },
                    new MusicCueDefinition.Part { Name = "Kick", Gain = .3f, FallbackVoice = MusicVoice.Kick, Notes = Enumerable.Range(0, 8).Select(i => new MusicNote(i, .5, 36)).ToArray() },
                };
                float[] RenderSong(MusicCueDefinition s) => Render(s.Compile(), 4);
                foreach (double target in new[] { -14.0, -20 })
                {
                    double result = MusicLoudness.Match(song, target, RenderSong);
                    Assert.IsTrue(song.Mixer.Limiter);
                    Assert.AreEqual(target, MusicLoudness.Measure(RenderSong(song), Rate, out double tp), .5, $"target {target}");
                    Assert.AreEqual(target, result, .5);
                    Assert.LessOrEqual(tp, -.5);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }
    }
}
