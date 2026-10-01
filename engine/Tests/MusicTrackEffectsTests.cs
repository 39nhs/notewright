using System;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicTrackEffectsTests
    {
        const int Rate = 8000;

        static float[] Render(MusicCue cue, double seconds)
        {
            var engine = new AdaptiveMusicEngine(cue, Rate);
            var pcm = new float[(int)(seconds * Rate) * 2];
            engine.RenderStereo(pcm);
            return pcm;
        }

        static MusicPart Part(string id, int key, double gain, MusicMixerSettings fx, double length = 1, MusicVoice voice = MusicVoice.Bass) =>
            new MusicPart(MusicLayer.Harmony, new MusicInstrument(voice, .002, .05), gain, id, new[] { new MusicNote(0, length, key) }, 0, 0, 0, 0,
                MusicDriveMode.Soft, fx?.Compile());

        static MusicCue Cue(int version, params MusicPart[] parts) =>
            new MusicCue("TrackFx", 120, 4, 8, parts, Array.Empty<MusicMixEffect>(), null, null, version);

        static double Rms(float[] stereo, double from, double to)
        {
            int a = (int)(from * Rate), b = (int)(to * Rate); double e = 0;
            for (int f = a; f < b; f++) e += stereo[f * 2] * (double)stereo[f * 2];
            return Math.Sqrt(e / (b - a));
        }

        [Test]
        public void DefaultTrackEffectsAreTransparent()
        {
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V2, Part("A", 45, .3, null)), 1),
                Render(Cue(MusicPlaybackVersion.V2, Part("A", 45, .3, new MusicMixerSettings())), 1), "A default chain changes nothing");
        }

        [Test]
        public void EffectsStayOnTheirTrack()
        {
            // Quiet levels keep the master limiter nearly linear, so the mix is the sum of the tracks.
            var crush = new MusicMixerSettings { CrushBits = 8, CrushDownsample = 8, LowPassHz = 300 };
            var both = Render(Cue(MusicPlaybackVersion.V2, Part("A", 45, .02, crush), Part("B", 69, .02, null, 1, MusicVoice.Pad)), 1);
            var aOnly = Render(Cue(MusicPlaybackVersion.V2, Part("A", 45, .02, crush)), 1);
            var bOnly = Render(Cue(MusicPlaybackVersion.V2, Part("B", 69, .02, null, 1, MusicVoice.Pad)), 1);
            double worst = 0;
            for (int i = 0; i < both.Length; i++) worst = Math.Max(worst, Math.Abs(both[i] - aOnly[i] - bOnly[i]));
            Assert.IsTrue(worst < 2e-4, "Track B is untouched by track A's crusher and filter: max difference " + worst);
            Assert.IsTrue(Rms(aOnly, .1, .9) > 1e-3, "Track A still sounds");
            var aDry = Render(Cue(MusicPlaybackVersion.V2, Part("A", 45, .02, null)), 1);
            double changed = 0;
            for (int i = 0; i < aDry.Length; i++) changed = Math.Max(changed, Math.Abs(aDry[i] - aOnly[i]));
            Assert.IsTrue(changed > 1e-3, "Track A's own chain is applied: " + changed);
        }

        [Test]
        public void EchoTailRingsAfterTheNoteEnds()
        {
            var echo = new MusicMixerSettings { EchoWet = .5f, EchoSeconds = .25f, EchoFeedback = .6f };
            var dry = Render(Cue(MusicPlaybackVersion.V2, Part("A", 57, .3, null, .5)), 2);
            var wet = Render(Cue(MusicPlaybackVersion.V2, Part("A", 57, .3, echo, .5)), 2);
            Assert.AreEqual(0, Rms(dry, .6, 1.4), 1e-9, "Without effects the track is silent after its note");
            Assert.IsTrue(Rms(wet, .6, 1.4) > 1e-3, "The track's echo keeps ringing without voices");
        }

        [Test]
        public void V1IgnoresTrackEffects()
        {
            var fx = new MusicMixerSettings { CrushBits = 3, Drive = true, DriveDb = 24 };
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V1, Part("A", 45, .3, null)), 1),
                Render(Cue(MusicPlaybackVersion.V1, Part("A", 45, .3, fx)), 1));
        }

        [Test]
        public void InvalidTrackEffectSettingsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { OutputDb = 40 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { EchoFeedback = .95f }.Compile());
        }
    }
}
