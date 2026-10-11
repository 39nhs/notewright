using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicSampleLoopTests
    {
        const int Rate = 8000;

        // A decaying 400 Hz tone: 0.25 s long, so without a loop a held note stops after 0.25 s.
        static float[] Tone(double seconds = .25, double hz = 400, double decay = 2)
        {
            var pcm = new float[(int)(seconds * Rate)];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(.5 * Math.Sin(2 * Math.PI * hz * i / Rate) * Math.Exp(-decay * i / Rate));
            return pcm;
        }

        static float[] Render(int version, MusicInstrument instrument, double seconds, params MusicNote[] notes)
        {
            var cue = new MusicCue("Loop", 120, 4, 8, new[] { new MusicPart(MusicLayer.Melody, instrument, .5, "Loop", notes) }, Array.Empty<MusicMixEffect>(), null, null, version);
            var pcm = new float[(int)(seconds * Rate) * 2];
            new AdaptiveMusicEngine(cue, Rate, playOnce: true).RenderStereo(pcm);
            return pcm;
        }

        static double Rms(float[] stereo, double from, double to)
        {
            double e = 0; int a = (int)(from * Rate), b = (int)(to * Rate);
            for (int f = a; f < b; f++) e += stereo[f * 2] * (double)stereo[f * 2];
            return Math.Sqrt(e / (b - a));
        }

        static double Frequency(float[] stereo, double from, double to)
        {
            int crossings = 0;
            for (int f = (int)(from * Rate) + 1; f < (int)(to * Rate); f++) if (stereo[(f - 1) * 2] < 0 && stereo[f * 2] >= 0) crossings++;
            return crossings / (to - from);
        }

        [Test]
        public void HeldNotesSustainInV3Only()
        {
            var looped = new MusicInstrument(Tone(), Rate, 69, .002, .02).WithSustainLoop(.1, .2, .01);
            Assert.IsTrue(looped.HasSustainLoop);
            var note = new MusicNote(0, 2, 69, 1); // 1 s at 120 BPM
            var v3 = Render(MusicPlaybackVersion.V3, looped, 1.3, note);
            var v2 = Render(MusicPlaybackVersion.V2, looped, 1.3, note);
            Assert.Greater(Rms(v3, .5, .95), .05, "V3 keeps sounding through the loop");
            Assert.AreEqual(400, Frequency(v3, .3, .95), 8, "the loop keeps the pitch");
            Assert.AreEqual(0, Rms(v2, .3, .95), 1e-9, "V2 stops at the end of the sample");
            Assert.AreEqual(0, Rms(v3, 1.1, 1.3), 1e-9, "the release ends the loop");
            var plain = Render(MusicPlaybackVersion.V3, new MusicInstrument(Tone(), Rate, 69, .002, .02), 1.3, note);
            Assert.AreEqual(0, Rms(plain, .3, .95), 1e-9, "no loop = as before");
            var a = Render(MusicPlaybackVersion.V3, new MusicInstrument(Tone(), Rate, 69, .002, .02), .2, note); var b = Render(MusicPlaybackVersion.V3, looped, .2, note);
            Assert.Less(a.Zip(b, (x, y) => Math.Abs(x - y)).Take((int)(.18 * Rate) * 2).Max(), 1e-6, "before the crossfade (last 10 ms of the loop) the looped instrument plays the same samples");
        }

        [Test]
        public void TheLoopJumpIsSeamless()
        {
            // 400 Hz = 20 frames per cycle: the loop runs from a peak (frame 805) to a trough (frame 2415), so a hard jump steps by the full
            // peak-to-peak; the crossfade keeps every step within the sine's own slope.
            var tone = Tone(.5, 400, 0);
            float Jump(MusicInstrument instrument)
            {
                var pcm = Render(MusicPlaybackVersion.V3, instrument, 1, new MusicNote(0, 2, 69, 1));
                float worst = 0; for (int f = (int)(.3 * Rate); f < (int)(.9 * Rate); f++) worst = Math.Max(worst, Math.Abs(pcm[f * 2] - pcm[(f - 1) * 2]));
                return worst;
            }
            float smooth = Jump(new MusicInstrument(tone, Rate, 69, .002, .02).WithSustainLoop(805.0 / Rate, 2415.0 / Rate, .02));
            float hard = Jump(new MusicInstrument(tone, Rate, 69, .002, .02).WithSustainLoop(805.0 / Rate, 2415.0 / Rate, 0));
            double slope = 2 * Math.PI * 400 / Rate * .25; // largest step of the sine itself (amplitude .5 × gain .5)
            Assert.Less(smooth, slope * 1.05, "crossfaded");
            Assert.Greater(hard, .3, "a hard jump steps by about the peak-to-peak");
        }

        [Test]
        public void BakeAndFind()
        {
            var tone = Tone(.5, 400, 0);
            var baked = MusicSampleLoop.Bake(tone, 800, 1600, 80, out int used);
            Assert.AreEqual(80, used);
            Assert.IsTrue(baked.Take(1520).SequenceEqual(tone.Take(1520)), "only the crossfade frames change");
            Assert.IsTrue(baked.Skip(1600).SequenceEqual(tone.Skip(1600)));
            MusicSampleLoop.Bake(tone, 10, 1600, 80, out used); Assert.AreEqual(10, used, "limited to the frames before the start");
            var (s, e) = MusicSampleLoop.Find(tone, Rate, 803, 1611);
            Assert.AreEqual(0, (e - s) % 20, "found points are a whole number of 400 Hz periods apart (20 frames)");
            Assert.IsTrue(tone[s - 1] < 0 && tone[s] >= 0, "the start is a rising zero crossing");
            Assert.Throws<ArgumentOutOfRangeException>(() => MusicSampleLoop.Bake(tone, 100, tone.Length, 10, out _));
        }

        [Test]
        public void ZonesLoopTheirOwnRecordings()
        {
            var instrument = new MusicInstrument(Tone(), Rate, 69, .002, .02, new[]
            {
                new MusicSampleZone(Tone(), Rate, 69, 0, 72, loopStart: .1, loopEnd: .2),
                new MusicSampleZone(Tone(), Rate, 81, 73, 127),
            });
            var pcm = Render(MusicPlaybackVersion.V3, instrument, 2.3, new MusicNote(0, 2, 69, 1), new MusicNote(2, 2, 81, 1));
            Assert.Greater(Rms(pcm, .5, .9), .05, "the looped zone sustains");
            Assert.AreEqual(0, Rms(pcm, 1.4, 1.9), 1e-9, "the other zone stops at its end");
        }

        static AudioClip Clip(float[] pcm)
        {
            var clip = AudioClip.Create("tone", pcm.Length, 1, Rate, false);
            clip.SetData(pcm, 0);
            return clip;
        }

        [Test]
        public void SongLoopFieldsCompileAndValidate()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                song.name = "Loops"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.LoopBeats = 8;
                song.Parts = new[] { new MusicCueDefinition.Part { Name = "Pad", Sample = Clip(Tone()), SampleRootKey = 69, LoopStartSeconds = .1f, LoopEndSeconds = .2f, Notes = new[] { new MusicNote(0, 2, 69) } } };
                Assert.IsTrue(song.Compile().GetPart(0).Instrument.HasSustainLoop);
                song.Parts[0].LoopEndSeconds = 5;
                var e = Assert.Throws<InvalidOperationException>(() => song.Compile());
                Assert.IsTrue(e.Message.StartsWith("Pad: ") && e.Message.Contains("inside"), e.Message);
                song.Parts[0].LoopEndSeconds = .2f; song.Parts[0].SliceSeconds = new[] { 0f, .1f };
                e = Assert.Throws<InvalidOperationException>(() => song.Compile());
                Assert.IsTrue(e.Message.Contains("slices"), e.Message);
                song.Parts[0].SliceSeconds = Array.Empty<float>(); song.Parts[0].LoopEndSeconds = 0;
                Assert.IsFalse(song.Compile().GetPart(0).Instrument.HasSustainLoop, "end 0 = off");
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
            Assert.Throws<InvalidOperationException>(() => new MusicInstrument(MusicVoice.Bell).WithSustainLoop(0, .1));
        }
    }
}
