using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicNoiseTests
    {
        [TestCase(120)]
        [TestCase(60)]
        public void NoiseFollowsBeatRangeAndLoopsSourceForLongRegions(double bpm)
        {
            var region = new MusicNoiseRegion { StartBeat = 2, DurationBeats = 8, FadeInBeats = 6, FadeOutBeats = 2 };
            var cue = new MusicCue("Noise", bpm, 4, 16, region.Compile(16));
            var engine = new AdaptiveMusicEngine(cue, 8000, playOnce: true);
            var audio = new float[12 * 8000]; engine.Render(audio);
            int begin = (int)(2 * 60 / bpm * 8000), end = (int)(10 * 60 / bpm * 8000);
            Assert.IsTrue(audio.Take(begin).All(x => x == 0));
            Assert.IsTrue(audio.Skip(end + 2).All(x => x == 0));
            Assert.Greater(audio.Skip(begin + (end - begin) * 3 / 4).Take(1000).Sum(x => x * x), .001);
            Assert.AreEqual(0, MusicHarmony.MaskAt(cue, 5), "Noise has no pitch classes");
        }

        [TestCase(0)]
        [TestCase(2)]
        public void NonRepeatingNoiseDoesNotReturnOnLegacyOrFadedRepeat(double repeat)
        {
            var region = new MusicNoiseRegion { DurationBeats = 2, FadeInBeats = .1, FadeOutBeats = .2, RepeatWithCue = false };
            var cue = new MusicCue("One time FX", 120, 4, 8, region.Compile(8));
            var engine = new AdaptiveMusicEngine(cue, 8000, repeat);
            var audio = new float[7 * 8000]; engine.Render(audio);
            Assert.Greater(audio.Take(8000).Sum(x => x * x), .01);
            Assert.IsTrue(audio.Skip(4 * 8000).All(x => x == 0));
        }

        [Test]
        public void NoiseIsDeterministicAcrossBufferSizesAndMiddleEntryIsAudible()
        {
            var region = new MusicNoiseRegion { DurationBeats = 16, FadeInBeats = 8, FadeOutBeats = 2, Kind = MusicNoiseKind.Pink };
            var target = new MusicCue("Noise", 120, 4, 16, region.Compile(16));
            var source = new MusicCue("Silent", 120, 4, 16);
            var a = new AdaptiveMusicEngine(source, 8000); var b = new AdaptiveMusicEngine(source, 8000);
            a.Render(new float[32000]); b.Render(new float[32000]);
            a.TransitionTo(target, MusicTransitionMode.MatchProgress, 2, 4, 2, false, 5, 0, 5);
            b.TransitionTo(target, MusicTransitionMode.MatchProgress, 2, 4, 2, false, 5, 0, 5);
            var all = new float[16000]; var block = new float[125]; a.Render(all);
            for (int offset = 0; offset < all.Length; offset += block.Length)
            { b.Render(block); for (int i = 0; i < block.Length; i++) Assert.AreEqual(all[offset + i], block[i]); }
            Assert.Greater(all.Sum(x => x * x), .01);
            Assert.AreEqual(0, a.BridgeNotesScheduled);
        }

        [Test]
        public void InvalidRegionFailsAndDisabledRegionDoesNotCompile()
        {
            var region = new MusicNoiseRegion { StartBeat = 15, DurationBeats = 4 };
            Assert.Throws<ArgumentException>(() => region.Compile(16));
            var cue = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                cue.name = "Disabled"; region.Enabled = false; cue.NoiseRegions = new[] { region };
                Assert.AreEqual(0, cue.Compile().PartCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(cue); }
        }

        [Test]
        public void CustomSamplePlaysAndMissingSampleFails()
        {
            var region = new MusicNoiseRegion { Kind = MusicNoiseKind.Sample, DurationBeats = 4, FadeInBeats = 0, FadeOutBeats = .1 };
            Assert.Throws<InvalidOperationException>(() => region.Compile(8));
            var clip = AudioClip.Create("Custom noise", 8000, 1, 8000, false);
            try
            {
                var pcm = new float[8000]; Array.Fill(pcm, .2f); clip.SetData(pcm, 0); region.Sample = clip;
                var engine = new AdaptiveMusicEngine(new MusicCue("Custom", 120, 4, 8, region.Compile(8)), 8000);
                var audio = new float[12000]; engine.Render(audio);
                Assert.Greater(audio[11000], 0, "Custom sample loop continues past source end");
            }
            finally { UnityEngine.Object.DestroyImmediate(clip); }
        }
    }
}
