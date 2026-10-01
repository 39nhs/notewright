using System;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicSynthTests
    {
        [Test]
        public void ChangeWaitsForBarAndLatestRequestWins()
        {
            var synth = new MusicSynth(MusicScore.CreateDemo(), 8000);
            synth.Render(new float[4000]);
            synth.RequestMood(MusicMood.Battle);
            synth.RequestMood(MusicMood.Boss);
            synth.Render(new float[12000]);
            Assert.AreEqual(MusicMood.Explore, synth.CurrentMood);
            synth.Render(new float[1]);
            Assert.AreEqual(MusicMood.Boss, synth.CurrentMood);
        }

        [Test]
        public void OutputDoesNotDependOnAudioBufferSize()
        {
            var a = new MusicSynth(MusicScore.CreateDemo(), 8000);
            var b = new MusicSynth(MusicScore.CreateDemo(), 8000);
            a.RequestMood(MusicMood.Battle); b.RequestMood(MusicMood.Battle);
            var whole = new float[32000]; a.Render(whole);
            var part = new float[125];
            for (int offset = 0; offset < whole.Length; offset += part.Length)
            {
                b.Render(part);
                for (int i = 0; i < part.Length; i++) Assert.AreEqual(whole[offset + i], part[i]);
            }
            Assert.AreEqual(32000, b.RenderedSamples);
        }

        [Test]
        public void TransitionsAndLoopsStayFiniteAudibleAndWithoutLargeJumps()
        {
            var synth = new MusicSynth(MusicScore.CreateDemo(), 48000);
            var buffer = new float[48000];
            float previous = 0, maxJump = 0;
            double energy = 0;
            bool valid = true;
            for (int second = 0; second < 26; second++)
            {
                if (second == 7) synth.RequestMood(MusicMood.Battle);
                if (second == 15) synth.RequestMood(MusicMood.Boss);
                if (second == 23) synth.RequestMood(MusicMood.Explore);
                synth.Render(buffer);
                foreach (float sample in buffer)
                {
                    valid &= !float.IsNaN(sample) && !float.IsInfinity(sample) && Math.Abs(sample) <= 1;
                    maxJump = Math.Max(maxJump, Math.Abs(sample - previous));
                    energy += sample * sample;
                    previous = sample;
                }
            }
            Assert.IsTrue(valid);
            Assert.Greater(energy, 100);
            Assert.Less(maxJump, .08f);
            Assert.AreEqual(MusicMood.Explore, synth.CurrentMood);
        }

        [Test]
        public void RejectsInvalidTrackAndCopiesCallerNotes()
        {
            Assert.Throws<ArgumentException>(() => new MusicTrack(MusicVoice.Bell, double.NaN, .1, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MusicTrack(MusicVoice.Bell, 1, .1, 128));
            var notes = new[] { 60 };
            var track = new MusicTrack(MusicVoice.Bell, 1, .1, notes);
            var score = new MusicScore(120, 4, 1, new[] { track }, new[] { track }, new[] { track });
            var before = new float[1000]; new MusicSynth(score, 8000).Render(before);
            notes[0] = -1;
            var after = new float[1000]; new MusicSynth(score, 8000).Render(after);
            CollectionAssert.AreEqual(before, after);
        }
    }
}
