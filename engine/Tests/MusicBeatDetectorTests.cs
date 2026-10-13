using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>Tempo and beat detection on synthesized drum loops, a drifting take and an engine render.</summary>
    public sealed class MusicBeatDetectorTests
    {
        const int Rate = 22050;

        // Kick on beat 1 (and 3 when busy), snare on 2 and 4, hats on eighths; beats at the given times.
        static float[] Drums(double[] beatTimes, double seconds, bool busy = true)
        {
            var pcm = new float[(int)(seconds * Rate)];
            var rng = new System.Random(1);
            void Hit(double at, Func<double, double> sound, double length)
            {
                int a = (int)(at * Rate);
                for (int i = 0; i < length * Rate && a + i < pcm.Length; i++) if (a + i >= 0) pcm[a + i] += (float)sound(i / (double)Rate);
            }
            for (int b = 0; b < beatTimes.Length; b++)
            {
                double t = beatTimes[b], next = b + 1 < beatTimes.Length ? beatTimes[b + 1] : t + (t - beatTimes[Math.Max(0, b - 1)]);
                if (b % 4 == 0 || (busy && b % 4 == 2)) Hit(t, x => .8 * Math.Sin(2 * Math.PI * (50 * x + 60 * (1 - Math.Exp(-x * 25)) / 25)) * Math.Exp(-x * 9), .4);
                if (b % 4 == 1 || b % 4 == 3) Hit(t, x => (rng.NextDouble() * 2 - 1) * .4 * Math.Exp(-x * 18) + .2 * Math.Sin(2 * Math.PI * 190 * x) * Math.Exp(-x * 20), .25);
                // Hats: noise minus its 7 kHz low-pass (two one-pole stages), bright like a real hi-hat.
                double lp1 = 0, lp2 = 0, k = 1 - Math.Exp(-2 * Math.PI * 7000.0 / Rate);
                foreach (double h in new[] { t, (t + next) / 2 })
                    Hit(h, x => { double w = rng.NextDouble() * 2 - 1; lp1 += k * (w - lp1); lp2 += k * (lp1 - lp2); return (w - lp2) * .2 * Math.Exp(-x * 60); }, .06);
            }
            return pcm;
        }

        static double[] Steady(double bpm, double first, int count) => Enumerable.Range(0, count).Select(i => first + i * 60 / bpm).ToArray();

        [Test]
        public void FindsTempoBeatsAndDownbeatOfASteadyLoop()
        {
            var truth = Steady(128, .37, 42);
            var result = MusicBeatDetector.Detect(Drums(truth, 21), Rate);
            Assert.AreEqual(128, result.Bpm, .6, "tempo");
            Assert.IsTrue(result.Confidence > .3, "confidence " + result.Confidence);
            Assert.IsTrue(result.Drift < .03, "steady: " + result.Drift);
            int matched = result.BeatSeconds.Count(b => truth.Any(t => Math.Abs(t - b) < .025));
            Assert.IsTrue(matched >= truth.Length - 3 && result.BeatSeconds.Length <= truth.Length + 2, $"{matched} of {truth.Length} beats within 25 ms ({result.BeatSeconds.Length} found)");
            double down = result.DownbeatSeconds;
            int index = Array.FindIndex(truth, t => Math.Abs(t - down) < .025);
            Assert.IsTrue(index >= 0 && index % 4 == 0, $"downbeat {down:0.000} s = beat {index}");
        }

        [Test]
        public void SlowAndFastTempiAreNotOctaveErrors()
        {
            foreach (double bpm in new[] { 84.0, 96, 150, 174 })
            {
                var truth = Steady(bpm, .2, (int)(bpm / 3));
                var result = MusicBeatDetector.Detect(Drums(truth, 20.5, busy: false), Rate);
                // A sparse loop under 100 BPM with eighth-note hats is ambiguous (84 or 168 both tap along); faster ones must be exact.
                bool ok = Math.Abs(result.Bpm - bpm) < bpm * .01 || (bpm < 100 && Math.Abs(result.Bpm - 2 * bpm) < bpm * .02);
                Assert.IsTrue(ok, $"at {bpm}: {result.Bpm:0.##} ({result.Trace})");
            }
        }

        [Test]
        public void ADriftingTakeGivesATempoMap()
        {
            // 110 → 128 BPM over 48 beats (double time would be past the 200 BPM range).
            var truth = new double[48]; double t = .25;
            for (int i = 0; i < truth.Length; i++) { truth[i] = t; t += 60 / (110 + 18.0 * i / truth.Length); }
            var result = MusicBeatDetector.Detect(Drums(truth, t + .5), Rate);
            Assert.IsTrue(result.Drift > .05, "drifts: " + result.Drift);
            var changes = result.ToTempoChanges(out double first);
            Assert.AreEqual(111, first, 3, "first bar: " + result.Trace);
            Assert.IsTrue(changes.Length >= 3 && changes.Last().Bpm > 120, "speeds up: " + string.Join(", ", changes.Select(c => $"{c.Beat}:{c.Bpm:0}")));
            Assert.IsTrue(changes.Select(c => c.Bpm).Zip(changes.Select(c => c.Bpm).Skip(1), (a, b) => b > a).All(x => x), "rising");
        }

        [Test]
        public void DetectsAnEngineRender()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                song.name = "Det"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = 140; song.LoopBeats = 32;
                song.Parts = new[]
                {
                    new MusicCueDefinition.Part { Name = "Kick", Gain = .4f, Layer = MusicLayer.Drums, FallbackVoice = MusicVoice.Kick, Notes = Enumerable.Range(0, 32).Where(b => b % 2 == 0).Select(b => new MusicNote(b, .2, 36)).ToArray() },
                    new MusicCueDefinition.Part { Name = "Bass", Gain = .12f, Synth = MusicSynthPresets.Create("Acid bass"), Notes = Enumerable.Range(0, 64).Select(i => new MusicNote(i * .5, .4, 40 + (i % 8 == 6 ? 7 : 0), .8f)).ToArray() },
                };
                var cue = song.Compile(); var pcm = new float[(int)(cue.Tempo.SecondsAt(32) * Rate) * 2];
                new AdaptiveMusicEngine(cue, Rate, playOnce: true).RenderStereo(pcm);
                var mono = new float[pcm.Length / 2]; for (int i = 0; i < mono.Length; i++) mono[i] = (pcm[i * 2] + pcm[i * 2 + 1]) * .5f;
                var result = MusicBeatDetector.Detect(mono, Rate);
                Assert.AreEqual(140, result.Bpm, 1, "tempo of the song");
                Assert.IsTrue(result.BeatSeconds.Any(b => Math.Abs(b - 60.0 / 140 * 4) < .03), "on the song's beats");
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        [Test]
        public void SilenceAndBadInputs()
        {
            var silent = MusicBeatDetector.Detect(new float[Rate * 5], Rate);
            Assert.AreEqual(0, silent.Confidence, 1e-9); Assert.IsTrue(silent.BeatSeconds.Length < 3 || silent.Bpm >= 0);
            Assert.AreEqual(0, MusicBeatDetector.Detect(new float[100], Rate).BeatSeconds.Length, "too short");
            Assert.Throws<ArgumentException>(() => MusicBeatDetector.Detect(null, Rate));
            Assert.Throws<ArgumentOutOfRangeException>(() => MusicBeatDetector.Detect(new float[Rate], Rate, 100, 120));
        }
    }
}
