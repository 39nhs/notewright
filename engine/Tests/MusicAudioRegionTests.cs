using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>Audio regions: source offset, gain, fades, transposition, warp (tempo, markers), edits and structure edits.</summary>
    public sealed class MusicAudioRegionTests
    {
        const int Rate = 16000;

        // 0–1 s: 440 Hz, 1–2 s: 660 Hz, 2–4 s: 880 Hz.
        static AudioClip Steps()
        {
            var pcm = new float[Rate * 4];
            double phase = 0;
            for (int i = 0; i < pcm.Length; i++) { double hz = i < Rate ? 440 : i < 2 * Rate ? 660 : 880; phase += hz / Rate; pcm[i] = (float)(.5 * Math.Sin(2 * Math.PI * phase)); }
            var clip = AudioClip.Create("Steps", pcm.Length, 1, Rate, false); clip.SetData(pcm, 0); return clip;
        }

        // A click every 0.5 s (a 120 BPM recording), or at the given times.
        static AudioClip Clicks(double[] times, double seconds)
        {
            var pcm = new float[(int)(seconds * Rate)];
            foreach (double t in times) for (int i = 0; i < 80; i++) { int at = (int)(t * Rate) + i; if (at < pcm.Length) pcm[at] = (float)(.8 * Math.Exp(-i / 15.0)); }
            var clip = AudioClip.Create("Clicks", pcm.Length, 1, Rate, false); clip.SetData(pcm, 0); return clip;
        }

        static MusicCueDefinition Song(double bpm, AudioClip sample, params MusicAudioRegion[] regions)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Regions"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = bpm; song.LoopBeats = 16;
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Audio", Gain = 1, Sample = sample, SampleRootKey = 60, Regions = regions } };
            return song;
        }

        static void Run(MusicCueDefinition song, Action<MusicCueDefinition> test)
        {
            try { test(song); }
            finally { foreach (var p in song.Parts) { if (p.Sample != null) UnityEngine.Object.DestroyImmediate(p.Sample); } UnityEngine.Object.DestroyImmediate(song); }
        }

        static float[] Render(MusicCueDefinition song, double seconds, int version = MusicPlaybackVersion.V3)
        {
            var pcm = new float[(int)(seconds * Rate) * 2];
            new AdaptiveMusicEngine(song.Compile(version), Rate, playOnce: true).RenderStereo(pcm);
            return pcm;
        }

        static double Hz(float[] pcm, double from, double to)
        {
            int a = (int)(from * Rate), b = (int)(to * Rate), first = -1, last = -1, count = 0;
            for (int i = a + 1; i < b; i++)
                if (pcm[(i - 1) * 2] < 0 && pcm[i * 2] >= 0) { if (first < 0) first = i; else count++; last = i; }
            return count == 0 ? 0 : count * Rate / (double)(last - first);
        }

        static double Rms(float[] pcm, double from, double to)
        {
            double sum = 0; int a = (int)(from * Rate), b = (int)(to * Rate);
            for (int i = a; i < b; i++) sum += pcm[i * 2] * pcm[i * 2];
            return Math.Sqrt(sum / Math.Max(1, b - a));
        }

        // Click times in the left channel (rising over 0.2).
        static double[] ClickTimes(float[] pcm)
        {
            var times = new System.Collections.Generic.List<double>(); int hold = 0;
            for (int i = 1; i < pcm.Length / 2; i++) { if (hold > 0) { hold--; continue; } if (Math.Abs(pcm[i * 2]) > .2) { times.Add(i / (double)Rate); hold = Rate / 20; } }
            return times.ToArray();
        }

        [Test]
        public void RegionsPlayFromTheirSourceOffsetWithGainAndFades() => Run(Song(60, Steps(),
            new MusicAudioRegion { StartBeat = 1, LengthBeats = 2, SourceStartSeconds = 1.2f },
            new MusicAudioRegion { StartBeat = 4, LengthBeats = 2, SourceStartSeconds = 0, GainDb = -6, FadeInBeats = 1 }), s =>
        {
            var pcm = Render(s, 7);
            Assert.IsTrue(Rms(pcm, 0, .9) < 1e-4, "silent before the region");
            Assert.AreEqual(660, Hz(pcm, 1.05, 1.75), 3, "starts 1.2 s into the recording");
            Assert.AreEqual(880, Hz(pcm, 2.1, 2.9), 3, "then plays on at its own speed");
            Assert.IsTrue(Rms(pcm, 3.1, 3.9) < 1e-3, "ends with the region");
            Assert.IsTrue(Rms(pcm, 4.0, 4.2) < .5 * Rms(pcm, 4.8, 5.0), "fades in");
            Assert.AreEqual(.5, Rms(pcm, 5.1, 5.6) / Rms(pcm, 1.1, 1.6), .03, "−6 dB");
        });

        [Test]
        public void TransposeAndTempoWarp() => Run(Song(90, Clicks(Enumerable.Range(0, 16).Select(i => .05 + i * .5).ToArray(), 8.2),
            new MusicAudioRegion { StartBeat = 0, LengthBeats = 12, Warp = MusicRegionWarp.Tempo, SourceBpm = 120 }), s =>
        {
            var clicks = ClickTimes(Render(s, 8.5));
            // Source clicks at 0.05 + k/2 s (120 BPM, 0.1 beat late) → song beat k + 0.1 at 90 BPM.
            var expected = Enumerable.Range(0, 12).Select(i => (i + .1) * 60 / 90.0).ToArray();
            Assert.IsTrue(clicks.Length >= 11, "clicks: " + clicks.Length);
            for (int i = 0; i < 11; i++) Assert.AreEqual(expected[i], clicks[i], .02, $"click {i} on beat {i} of the 90 BPM song");
            s.Parts[0].Regions[0] = new MusicAudioRegion { StartBeat = 0, LengthBeats = 4, SourceStartSeconds = 0, Semitones = 12 };
            s.Parts[0].Sample = Steps();
            var pcm = Render(s, 3);
            Assert.AreEqual(880, Hz(pcm, .05, .45), 4, "warp off: an octave up and twice as fast (0–0.5 s = the first second)");
            Assert.AreEqual(1320, Hz(pcm, .55, .95), 6);
            s.Parts[0].Regions[0] = new MusicAudioRegion { StartBeat = 0, LengthBeats = 4, Warp = MusicRegionWarp.Tempo, SourceBpm = 90, Semitones = 12 };
            pcm = Render(s, 3);
            Assert.AreEqual(880, Hz(pcm, .1, .9), 6, "tempo warp: pitch only — still the first second's tone");
        });

        static string Describe(MusicCueDefinition s) => string.Join(" ", s.Parts[0].Regions.Select(r => $"{r.StartBeat:0.##}+{r.LengthBeats:0.##}@{r.SourceStartSeconds:0.##}"));

        [Test]
        public void OlderVersionsIgnoreRegionsAndBadRegionsAreRejected() => Run(Song(60, Steps(), new MusicAudioRegion { StartBeat = 0, LengthBeats = 2 }), s =>
        {
            Assert.IsTrue(Rms(Render(s, 2, MusicPlaybackVersion.V2), 0, 2) < 1e-6, "V2: no regions (no notes either)");
            s.Parts[0].Regions[0].FadeInBeats = 3;
            Assert.Throws<InvalidOperationException>(() => s.Compile(), "fades longer than the region");
            s.Parts[0].Regions[0].FadeInBeats = 0; s.Parts[0].Regions[0].Warp = MusicRegionWarp.Markers;
            Assert.Throws<InvalidOperationException>(() => s.Compile(), "markers needed");
            s.Parts[0].Regions[0].Warp = MusicRegionWarp.Off; s.Parts[0].Sample = null;
            Assert.Throws<InvalidOperationException>(() => s.Compile(), "no recording");
        });
    }
}
