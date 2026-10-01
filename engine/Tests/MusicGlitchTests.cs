using System;
using System.Linq;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public class MusicGlitchTests
    {
        [TestCase(MusicNoiseKind.SlowingGlitch)]
        [TestCase(MusicNoiseKind.AcceleratingGlitch)]
        [TestCase(MusicNoiseKind.PitchGlitch)]
        [TestCase(MusicNoiseKind.StutterGlitch)]
        public void GlitchesRespectRangeAndBufferBoundaries(MusicNoiseKind kind)
        {
            var region = new MusicNoiseRegion { Kind = kind, StartBeat = 2, DurationBeats = 4, FadeInBeats = .125, FadeOutBeats = .25 };
            var cue = new MusicCue("Glitch", 120, 4, 8, region.Compile(8));
            var a = new AdaptiveMusicEngine(cue, 8000, playOnce:true);
            var b = new AdaptiveMusicEngine(cue, 8000, playOnce:true);
            var output = new float[32000]; a.Render(output);
            var block = new float[125];
            for (int offset = 0; offset < output.Length; offset += block.Length)
            { b.Render(block); for (int i = 0; i < block.Length; i++) Assert.AreEqual(output[offset+i], block[i]); }
            Assert.IsTrue(output.Take(8000).All(x => x == 0));
            Assert.IsTrue(output.Skip(24002).All(x => x == 0));
            Assert.Greater(output.Sum(x => x*x), .01);
            Assert.AreEqual(0, a.DroppedNotes);
        }
        [Test]
        public void RateAndPitchDirectionsAreDistinct()
        {
            var slow = new NoisePlayback(0,0,true,true,MusicNoiseKind.SlowingGlitch);
            var fast = new NoisePlayback(0,0,true,true,MusicNoiseKind.AcceleratingGlitch);
            var pitch = new NoisePlayback(0,0,true,true,MusicNoiseKind.PitchGlitch);
            Assert.Greater(slow.Speed(.1), slow.Speed(.9));
            Assert.Less(fast.Speed(.1), fast.Speed(.9));
            Assert.Greater(pitch.Speed(1.0/26), 1);
            Assert.Less(pitch.Speed(3.0/26), 1);
            Assert.Greater(Pulses(slow,0,1), Pulses(slow,3,4));
            Assert.Less(Pulses(fast,0,1), Pulses(fast,3,4));
        }
        static int Pulses(NoisePlayback playback, double from, double to)
        {
            int count = 0; bool last = false;
            for (double t = from; t < to; t += .0005) { bool on = playback.Gate(t,4) > .5; if(on && !last) count++; last = on; }
            return count;
        }
    }
}
