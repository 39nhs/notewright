using System;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicGlideTests
    {
        const int Rate = 8000;

        static MusicCue Cue(int version, double glide, params MusicNote[] notes) =>
            new MusicCue("Glide", 120, 4, 8, new[] { new MusicPart(MusicLayer.Bass, new MusicInstrument(MusicVoice.Bass, .002, .05), .5, "Sub", notes, 0, 0, glide) },
                Array.Empty<MusicMixEffect>(), null, null, version);

        static (float[] pcm, AdaptiveMusicEngine engine) Render(MusicCue cue, double seconds)
        {
            var engine = new AdaptiveMusicEngine(cue, Rate);
            var pcm = new float[(int)(seconds * Rate) * 2];
            engine.RenderStereo(pcm);
            return (pcm, engine);
        }

        /// <summary>Zero crossings per second of the left channel in [from, to) seconds → about 2 × frequency.</summary>
        static double Crossings(float[] stereo, double from, double to)
        {
            int a = (int)(from * Rate), b = (int)(to * Rate), n = 0;
            for (int f = a + 1; f < b; f++) if ((stereo[f * 2] >= 0) != (stereo[(f - 1) * 2] >= 0)) n++;
            return n / (to - from);
        }

        [Test]
        public void LegatoNoteSlidesTheHeldVoiceInV2()
        {
            // A2 (110 Hz) held into E3 (164.8 Hz) at beat 1 (0.5 s); 0.1 s glide.
            var notes = new[] { new MusicNote(0, 1.5, 45), new MusicNote(1, 1, 52) };
            var (v2, e2) = Render(Cue(MusicPlaybackVersion.V2, .1, notes), 1.2);
            Assert.AreEqual(1, e2.PeakVoices, "The legato note reuses the held voice");
            Assert.AreEqual(220, Crossings(v2, .1, .45), 8, "Before the slide: A2");
            Assert.AreEqual(329.6, Crossings(v2, .7, 1.0), 8, "After the slide: E3");
            double mid = Crossings(v2, .5, .6);
            Assert.IsTrue(mid > 225 && mid < 325, "During the slide the pitch is between the two notes: " + mid);

            var (v1, e1) = Render(Cue(MusicPlaybackVersion.V1, .1, notes), 1.2);
            Assert.AreEqual(2, e1.PeakVoices, "V1 ignores glide: both notes sound");
            Assert.IsTrue(!System.Linq.Enumerable.SequenceEqual(v1, v2), "V1 and V2 render differently when glide is set");
        }

        [Test]
        public void SeparatedNotesAndChordsDoNotGlide()
        {
            var gap = Cue(MusicPlaybackVersion.V2, .2, new MusicNote(0, .5, 45), new MusicNote(1, 1, 52));
            var (pcm, _) = Render(gap, 1.0);
            Assert.AreEqual(329.6, Crossings(pcm, .52, .62), 8, "A note after a gap starts at its own pitch");

            var chord = Cue(MusicPlaybackVersion.V2, .2, new MusicNote(0, 2, 45), new MusicNote(0, 2, 52));
            var (_, engine) = Render(chord, .5);
            Assert.AreEqual(2, engine.PeakVoices, "Notes that start together are a chord, not a slide");
        }

        [Test]
        public void GlideIsRenderedIdenticallyWithoutGlideTime()
        {
            var notes = new[] { new MusicNote(0, 1.5, 45), new MusicNote(1, 1, 52) };
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V1, 0, notes), 1).pcm, Render(Cue(MusicPlaybackVersion.V2, 0, notes), 1).pcm,
                "Without glide V2 sounds exactly like V1 (below 128 voices)");
        }

        [Test]
        public void InvalidGlideTimeIsRejected()
        {
            var instrument = new MusicInstrument(MusicVoice.Bass);
            Assert.Throws<ArgumentException>(() => new MusicPart(MusicLayer.Bass, instrument, .5, "x", new[] { new MusicNote(0, 1, 40) }, 0, 0, -.1));
            Assert.Throws<ArgumentException>(() => new MusicPart(MusicLayer.Bass, instrument, .5, "x", new[] { new MusicNote(0, 1, 40) }, 0, 0, MusicPart.MaxGlideSeconds + 1));
            Assert.Throws<ArgumentException>(() => new MusicPart(MusicLayer.Bass, instrument, .5, "x", new[] { new MusicNote(0, 1, 40) }, 0, 0, double.NaN));
        }
    }
}
