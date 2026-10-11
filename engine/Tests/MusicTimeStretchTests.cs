using System;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicTimeStretchTests
    {
        const int Rate = 8000, SourceRate = 8000;

        /// <summary>Two seconds recorded at 120 BPM (four beats): a 440 Hz tone with a click on every beat.</summary>
        static float[] Loop()
        {
            var pcm = new float[SourceRate * 2];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(.4 * Math.Sin(2 * Math.PI * 440 * i / SourceRate));
            for (int beat = 0; beat < 4; beat++) for (int i = 0; i < 40; i++) pcm[beat * SourceRate / 2 + i] += .5f;
            return pcm;
        }

        static float[] Render(double songBpm, int version, double stretchBpm, int key = 69, double seconds = 6, double[] slices = null, MusicNote[] notes = null)
        {
            var instrument = new MusicInstrument(Loop(), SourceRate, 69, .001, .01, null, slices);
            var part = new MusicPart(MusicLayer.Melody, instrument, .5, "Loop", notes ?? new[] { new MusicNote(0, 16, key) }, 0, stretchBpm: stretchBpm);
            var cue = new MusicCue("Stretch", songBpm, 4, 32, new[] { part }, Array.Empty<MusicMixEffect>(), playbackVersion: version);
            var engine = new AdaptiveMusicEngine(cue, Rate, playOnce: true);
            var pcm = new float[(int)(seconds * Rate) * 2]; var block = new float[1000];
            for (int at = 0; at < pcm.Length; at += block.Length) { engine.RenderStereo(block); Array.Copy(block, 0, pcm, at, Math.Min(block.Length, pcm.Length - at)); }
            return pcm;
        }

        /// <summary>Seconds until the sound has ended (last sample above a small threshold).</summary>
        static double Length(float[] pcm) { int last = 0; for (int i = 0; i < pcm.Length; i += 2) if (Math.Abs(pcm[i]) > 1e-3) last = i / 2; return last / (double)Rate; }

        /// <summary>Dominant frequency between two times (zero crossings of the left channel, clicks are short enough not to matter).</summary>
        static double Frequency(float[] pcm, double from, double to)
        {
            int crossings = 0; int a = (int)(from * Rate), b = (int)(to * Rate);
            for (int i = a + 1; i < b; i++) if ((pcm[(i - 1) * 2] < 0) != (pcm[i * 2] < 0)) crossings++;
            return crossings / 2.0 / (to - from);
        }

        [Test]
        public void StretchIsValidated()
        {
            var bell = new MusicInstrument(MusicVoice.Bell);
            Assert.Throws<ArgumentException>(() => new MusicPart(MusicLayer.Melody, bell, .2, "Bell", new[] { new MusicNote(0, 1, 60) }, 0, stretchBpm: 120), "oscillators cannot stretch");
            var sample = new MusicInstrument(Loop(), SourceRate, 69);
            Assert.Throws<ArgumentException>(() => new MusicPart(MusicLayer.Melody, sample, .2, "Loop", new[] { new MusicNote(0, 1, 69) }, 0, stretchBpm: 5));
            Assert.Throws<ArgumentException>(() => new MusicPart(MusicLayer.Melody, sample, .2, "Loop", new[] { new MusicNote(0, 1, 69) }, 0, stretchBpm: 120, stretchGrainSeconds: 1));
        }

        [Test]
        public void TheSampleFollowsTheSongTempoAndKeepsItsPitch()
        {
            // Song at 60 BPM, sample recorded at 120: stretched, its four beats last four seconds instead of two, still at 440 Hz.
            var stretched = Render(60, MusicPlaybackVersion.V3, 120);
            Assert.AreEqual(4, Length(stretched), .1, "four song beats at 60 BPM");
            Assert.AreEqual(440, Frequency(stretched, .6, 3.4), 440 * .02, "pitch kept");
            var faster = Render(240, MusicPlaybackVersion.V3, 120);
            Assert.AreEqual(1, Length(faster), .06, "four beats at 240 BPM");
            Assert.AreEqual(440, Frequency(faster, .1, .9), 440 * .03);
            // Without stretch (and with V2, which ignores it) the sample plays at its own speed: two seconds.
            Assert.AreEqual(2, Length(Render(60, MusicPlaybackVersion.V3, 0)), .05);
            CollectionAssert.AreEqual(Render(60, MusicPlaybackVersion.V2, 0), Render(60, MusicPlaybackVersion.V2, 120), "V2 ignores the stretch");
        }

        [Test]
        public void NotesSetThePitchWithoutChangingTheTiming()
        {
            var octave = Render(60, MusicPlaybackVersion.V3, 120, key: 81);
            Assert.AreEqual(4, Length(octave), .1, "same duration an octave up");
            Assert.AreEqual(880, Frequency(octave, .6, 3.4), 880 * .02, "an octave up");
        }

        [Test]
        public void SlicesKeepTheirPitchAndLastTheirBeatsAtTheSongTempo()
        {
            // Two slices of one second (two beats at 120 BPM). At 60 BPM each slice lasts two seconds and stays at 440 Hz.
            var pcm = Render(60, MusicPlaybackVersion.V3, 120, slices: new[] { 0.0, 1.0 }, notes: new[] { new MusicNote(0, 8, 70) });
            Assert.AreEqual(2, Length(pcm), .1, "the second slice: two beats at 60 BPM");
            Assert.AreEqual(440, Frequency(pcm, .3, 1.7), 440 * .03);
        }

        [Test]
        public void ConstantOverlapKeepsTheLevelSteady()
        {
            // A steady tone through the grains: the RMS of every 100 ms window stays within 1.5 dB of the first.
            var pcm = Render(90, MusicPlaybackVersion.V3, 120);
            double Rms(int a) { double e = 0; for (int i = a; i < a + Rate / 10; i++) e += pcm[i * 2] * pcm[i * 2]; return Math.Sqrt(e / (Rate / 10)); }
            double reference = Rms(Rate / 10 * 3 + 300); // between the clicks
            for (double t = .3; t < 2.4; t += .5) Assert.AreEqual(0, 20 * Math.Log10(Rms((int)(t * Rate) + 300) / reference), 1.5, "at " + t);
        }

#if UNITY_5_3_OR_NEWER
        [Test]
        public void StretchedPlaybackAllocatesNothingInTheAudioCallback()
        {
            var instrument = new MusicInstrument(Loop(), SourceRate, 69);
            var cue = new MusicCue("Stretch", 100, 4, 8, new[] { new MusicPart(MusicLayer.Melody, instrument, .5, "Loop", new[] { new MusicNote(0, 4, 69), new MusicNote(4, 4, 72) }, 0, stretchBpm: 120) },
                Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V3);
            var engine = new AdaptiveMusicEngine(cue, Rate); var pcm = new float[2000];
            for (int i = 0; i < 20; i++) engine.RenderStereo(pcm);
            long allocated = 0;
            for (int i = 0; i < 60; i++) { long before = GC.GetAllocatedBytesForCurrentThread(); engine.RenderStereo(pcm); allocated += GC.GetAllocatedBytesForCurrentThread() - before; }
            Assert.AreEqual(0L, allocated);
        }
#endif
    }
}
