using System;
using System.Linq;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicDriveTests
    {
        const int Rate = 8000;

        static float[] Render(MusicCue cue, double seconds)
        {
            var engine = new AdaptiveMusicEngine(cue, Rate);
            var pcm = new float[(int)(seconds * Rate) * 2];
            engine.RenderStereo(pcm);
            return pcm;
        }

        static MusicCue Cue(int version, MusicMixerSettings mixer, double trackDriveDb = 0, MusicDriveMode mode = MusicDriveMode.Soft, int key = 33, float gain = .3f) =>
            new MusicCue("Drive", 120, 4, 8, new[] { new MusicPart(MusicLayer.Bass, new MusicInstrument(MusicVoice.Bass, .002, .05), gain, "Sub",
                new[] { new MusicNote(0, 8, key) }, 0, 0, 0, trackDriveDb, mode) }, Array.Empty<MusicMixEffect>(), null, (mixer ?? new MusicMixerSettings()).Compile(), version);

        static double Rms(float[] stereo, double from, double to)
        {
            int a = (int)(from * Rate), b = (int)(to * Rate); double e = 0;
            for (int f = a; f < b; f++) e += stereo[f * 2] * (double)stereo[f * 2];
            return Math.Sqrt(e / (b - a));
        }

        [Test]
        public void ShapersStayWithinFullScale()
        {
            foreach (MusicDriveMode mode in Enum.GetValues(typeof(MusicDriveMode)))
                for (double u = -300; u <= 300; u += .37)
                    Assert.IsTrue(Math.Abs(MusicDrive.Shape(mode, u)) <= 1 + 1e-12, mode + " at " + u);
            Assert.AreEqual(0, MusicDrive.Shape(MusicDriveMode.Fuzz, 0), 1e-12, "Fuzz is centred: silence stays silent");
        }

        [Test]
        public void OutputGainGoesTo30Decibels()
        {
            Assert.DoesNotThrow(() => new MusicMixerSettings { OutputDb = 30 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { OutputDb = 31 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { DriveDb = 49 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicMixerSettings { DriveMode = (MusicDriveMode)9 }.Compile());
            Assert.Throws<ArgumentException>(() => new MusicPart(MusicLayer.Bass, new MusicInstrument(MusicVoice.Bass), .5, "x", new[] { new MusicNote(0, 1, 40) }, 0, 0, 0, -1));
        }

        [Test]
        public void MasterDriveIsLouderAndV1IgnoresIt()
        {
            var drive = new MusicMixerSettings { Drive = true, DriveMode = MusicDriveMode.Hard, DriveDb = 30, DriveTrimDb = 6 };
            double clean = Rms(Render(Cue(MusicPlaybackVersion.V2, null), 1), .2, 1), driven = Rms(Render(Cue(MusicPlaybackVersion.V2, drive), 1), .2, 1);
            Assert.IsTrue(driven > clean * 2, "Hard drive pushes the level up: " + clean + " -> " + driven);
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V1, null), 1), Render(Cue(MusicPlaybackVersion.V1, drive), 1), "V1 ignores the master drive");
            CollectionAssert.AreEqual(Render(Cue(MusicPlaybackVersion.V1, null), 1), Render(Cue(MusicPlaybackVersion.V2, null), 1), "Without drive V2 renders like V1");
        }

        [Test]
        public void KeepBassLeavesTheSubClean()
        {
            // 55 Hz sine: with the split at 300 Hz almost all of it bypasses the shaper; without it a huge drive squares it.
            var squared = new MusicMixerSettings { Drive = true, DriveMode = MusicDriveMode.Hard, DriveDb = 40 };
            var kept = new MusicMixerSettings { Drive = true, DriveMode = MusicDriveMode.Hard, DriveDb = 40, DriveKeepBassHz = 300 };
            double dry = Rms(Render(Cue(MusicPlaybackVersion.V2, null, key: 33, gain: .1f), 1), .2, 1);
            double sq = Rms(Render(Cue(MusicPlaybackVersion.V2, squared, key: 33, gain: .1f), 1), .2, 1);
            double kp = Rms(Render(Cue(MusicPlaybackVersion.V2, kept, key: 33, gain: .1f), 1), .2, 1);
            Assert.IsTrue(sq > dry * 3, "Without the split the sub is squared: " + dry + " -> " + sq);
            Assert.IsTrue(kp < sq * .6, "With the split the sub mostly bypasses the shaper: " + kp + " vs " + sq);
        }

        [Test]
        public void TrackDriveChangesOnlyV2()
        {
            var v1Clean = Render(Cue(MusicPlaybackVersion.V1, null), 1);
            CollectionAssert.AreEqual(v1Clean, Render(Cue(MusicPlaybackVersion.V1, null, 30, MusicDriveMode.Fold), 1), "V1 ignores track drive");
            var v2 = Render(Cue(MusicPlaybackVersion.V2, null, 30, MusicDriveMode.Fold), 1);
            Assert.IsFalse(v2.SequenceEqual(v1Clean), "V2 applies track drive");
            Assert.IsTrue(v2.All(x => Math.Abs(x) <= 1), "Output stays within full scale");
        }
    }
}
