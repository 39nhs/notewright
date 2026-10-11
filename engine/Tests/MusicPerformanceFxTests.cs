using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>Master tape speed (varispeed, tape stop) and the performance lanes: gate depth, crush mix, drive, stutter, width.</summary>
    public sealed class MusicPerformanceFxTests
    {
        const int Rate = 16000;

        static AudioClip Sine(double hz, double seconds = 8)
        {
            int n = (int)(Rate * seconds);
            var clip = AudioClip.Create("Sine" + hz, n, 1, Rate, false);
            var pcm = new float[n];
            for (int i = 0; i < n; i++) pcm[i] = (float)(.5 * Math.Sin(2 * Math.PI * hz * i / Rate));
            clip.SetData(pcm, 0);
            return clip;
        }

        // 60 BPM (1 beat = 1 s), one track on a 440 Hz recording with its own effect chain.
        static MusicCueDefinition Song(MusicMixerSettings effects, params MusicNote[] notes)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Fx"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = 60; song.LoopBeats = 16;
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Tone", Sample = Sine(440), SampleRootKey = 69, Gain = .2f, AttackSeconds = .002f, ReleaseSeconds = .02f,
                TrackEffects = effects != null, Effects = effects ?? new MusicMixerSettings(), Notes = notes.Length > 0 ? notes : new[] { new MusicNote(0, 8, 69) } } };
            return song;
        }

        static void Run(MusicCueDefinition song, Action<MusicCueDefinition> test)
        {
            try { test(song); }
            finally { foreach (var p in song.Parts) UnityEngine.Object.DestroyImmediate(p.Sample); UnityEngine.Object.DestroyImmediate(song); }
        }

        static float[] Render(MusicCueDefinition song, double seconds)
        {
            var pcm = new float[(int)(seconds * Rate) * 2];
            new AdaptiveMusicEngine(song.Compile(), Rate, playOnce: true).RenderStereo(pcm);
            return pcm;
        }

        static double Hz(float[] pcm, double from, double to)
        {
            int a = (int)(from * Rate), b = (int)(to * Rate), first = -1, last = -1, count = 0;
            for (int i = a + 1; i < b; i++)
                if (pcm[(i - 1) * 2] < 0 && pcm[i * 2] >= 0) { if (first < 0) first = i; else count++; last = i; }
            return count == 0 ? 0 : count * Rate / (double)(last - first);
        }

        static double Rms(float[] pcm, double from, double to, int channel = 0)
        {
            double sum = 0; int a = (int)(from * Rate), b = (int)(to * Rate);
            for (int i = a; i < b; i++) sum += pcm[i * 2 + channel] * pcm[i * 2 + channel];
            return Math.Sqrt(sum / Math.Max(1, b - a));
        }

        static MusicAutomationLane Lane(MusicAutomationTarget target, params (double beat, float value, MusicAutomationCurve curve)[] points) =>
            new MusicAutomationLane(target, points.Select(p => new MusicAutomationPoint(p.beat, p.value, p.curve)).ToArray());

        [Test]
        public void SpeedScalesTempoAndPitchTogether() => Run(Song(null, new MusicNote(0, 1, 69), new MusicNote(2, 1, 69)), s =>
        {
            s.MasterAutomation = new[] { Lane(MusicAutomationTarget.Speed, (0, .5f, MusicAutomationCurve.Linear)) };
            var pcm = Render(s, 6);
            Assert.AreEqual(220, Hz(pcm, .2, 1.8), 2, "half speed: an octave down");
            Assert.IsTrue(Rms(pcm, 2.3, 3.7) < .001, "the first note (1 beat) lasts 2 s, then the gap");
            Assert.IsTrue(Rms(pcm, 4.2, 5.8) > .02, "the second note starts at beat 2 = 4 s");
            var engine = new AdaptiveMusicEngine(s.Compile(), Rate, playOnce: true);
            engine.RenderStereo(new float[Rate * 2]); // 1 s
            Assert.AreEqual(.5, engine.PositionBeats, .01);
        });

        [Test]
        public void GateDepthLaneOpensAndClosesTheGate() => Run(Song(new MusicMixerSettings { Gate = true, GateBeats = .25f, GateOpen = .5f }), s =>
        {
            s.Parts[0].Automation = new[] { Lane(MusicAutomationTarget.GateDepth, (0, 0, MusicAutomationCurve.Step), (4, 1, MusicAutomationCurve.Step)) };
            var pcm = Render(s, 8);
            double Chop(double from) => Rms(pcm, from + .15, from + .24) / Rms(pcm, from + .02, from + .1); // closed half vs open half of a step
            Assert.AreEqual(1, Chop(1), .05, "depth 0: no chopping");
            Assert.IsTrue(Chop(5) < .05, "depth 1: chopped");
        });

        [Test]
        public void CrushAndDriveLanesBlendTheEffect()
        {
            float[] With(MusicMixerSettings fx, MusicAutomationLane lane)
            {
                var song = Song(fx);
                try { if (lane != null) song.Parts[0].Automation = new[] { lane }; return Render(song, 2); }
                finally { foreach (var p in song.Parts) UnityEngine.Object.DestroyImmediate(p.Sample); UnityEngine.Object.DestroyImmediate(song); }
            }
            double Difference(float[] a, float[] b) => Math.Sqrt(a.Zip(b, (x, y) => (double)(x - y) * (x - y)).Average());
            var dry = With(new MusicMixerSettings(), null);
            Assert.IsTrue(Difference(dry, With(new MusicMixerSettings { CrushBits = 3, CrushDownsample = 4 }, Lane(MusicAutomationTarget.CrushMix, (0, 0, 0)))) < 1e-6, "crush mix 0 = dry");
            Assert.IsTrue(Difference(dry, With(new MusicMixerSettings { CrushBits = 3, CrushDownsample = 4 }, Lane(MusicAutomationTarget.CrushMix, (0, 1, 0)))) > .005, "crush mix 1 = crushed");
            Assert.IsTrue(Difference(dry, With(new MusicMixerSettings { Drive = true, DriveDb = 30 }, Lane(MusicAutomationTarget.DriveMix, (0, 0, 0)))) < 1e-6, "drive mix 0 = dry");
            var soft = With(new MusicMixerSettings { Drive = true, DriveDb = 30 }, Lane(MusicAutomationTarget.DriveDb, (0, 0, 0)));
            var hard = With(new MusicMixerSettings { Drive = true, DriveDb = 0 }, Lane(MusicAutomationTarget.DriveDb, (0, 30, 0)));
            Assert.IsTrue(Difference(soft, With(new MusicMixerSettings { Drive = true, DriveDb = 0 }, null)) < 1e-6, "the lane replaces the authored drive");
            Assert.IsTrue(Difference(hard, With(new MusicMixerSettings { Drive = true, DriveDb = 30 }, null)) < 1e-6);
        }

        [Test]
        public void StutterRepeatsTheSliceWhileEngaged() => Run(Song(new MusicMixerSettings { Stutter = true, StutterBeats = .25f },
            Enumerable.Range(0, 32).Select(i => new MusicNote(i * .125, .1, 57 + i % 12)).ToArray()), s =>
        {
            s.Parts[0].Automation = new[] { Lane(MusicAutomationTarget.Stutter, (0, 0, MusicAutomationCurve.Step), (1, 1, MusicAutomationCurve.Step), (2, 0, MusicAutomationCurve.Step)) };
            var pcm = Render(s, 3);
            int slice = Rate / 4, a = Rate + slice, b = Rate + 2 * slice; // repeats start one slice after engaging (beat 1.25)
            double same = 0, total = 0;
            for (int i = 64; i < slice - 64; i++) { double x = pcm[(a + i) * 2], y = pcm[(b + i) * 2]; same += (x - y) * (x - y); total += x * x; }
            Assert.IsTrue(total > 0 && same / total < 1e-6, "the repeats are identical");
            var dry = Render(Song(null, s.Parts[0].Notes), 3);
            double Diff(int from, int to) { double d = 0; for (int i = from; i < to; i++) d += Math.Abs(pcm[i * 2] - dry[i * 2]); return d; }
            Assert.IsTrue(Diff(Rate / 2, Rate) < 1e-3 && Diff((int)(2.1 * Rate), (int)(2.9 * Rate)) < 1e-3, "live before and after");
            Assert.IsTrue(Diff((int)(1.3 * Rate), (int)(1.9 * Rate)) > .1, "repeating in between");
        });

        [Test]
        public void WidthLaneNarrowsTheStereoImage() => Run(Song(new MusicMixerSettings { Chorus = true, ChorusMix = .5f }), s =>
        {
            s.Parts[0].Automation = new[] { Lane(MusicAutomationTarget.Width, (0, 0, 0)) };
            var pcm = Render(s, 1);
            double side = 0; for (int i = 0; i < pcm.Length; i += 2) side += Math.Abs(pcm[i] - pcm[i + 1]);
            Assert.IsTrue(side < 1e-6, "width 0 = mono");
            s.MasterAutomation = new[] { Lane(MusicAutomationTarget.Width, (0, 2, 0)) };
            Assert.DoesNotThrow(() => s.Compile());
        });

        [Test]
        public void LanesNeedTheirStage() => Run(Song(new MusicMixerSettings()), s =>
        {
            void Fails(MusicAutomationLane lane, string text, bool master = false)
            {
                if (master) { s.MasterAutomation = new[] { lane }; s.Parts[0].Automation = Array.Empty<MusicAutomationLane>(); }
                else { s.Parts[0].Automation = new[] { lane }; s.MasterAutomation = Array.Empty<MusicAutomationLane>(); }
                string message = null;
                try { s.Compile(); } catch (ArgumentException e) { message = e.Message; } catch (InvalidOperationException e) { message = e.Message; }
                Assert.IsTrue(message != null && message.Contains(text), message ?? "compiled");
            }
            Fails(Lane(MusicAutomationTarget.GateDepth, (0, 1, 0)), "needs Gate on");
            Fails(Lane(MusicAutomationTarget.CrushMix, (0, 1, 0)), "Crush");
            Fails(Lane(MusicAutomationTarget.DriveDb, (0, 10, 0)), "needs Drive on");
            Fails(Lane(MusicAutomationTarget.Stutter, (0, 1, 0)), "needs Stutter on");
            Fails(Lane(MusicAutomationTarget.Speed, (0, 1, 0)), "master only");
            Fails(Lane(MusicAutomationTarget.Speed, (0, 3, 0)), "Speed value", true);
            s.Parts[0].Automation = Array.Empty<MusicAutomationLane>(); s.MasterAutomation = Array.Empty<MusicAutomationLane>();
            s.Parts[0].Effects.Drive = true;
            s.Parts[0].Modulators = new[] { new MusicModulator { Enabled = true, Target = MusicAutomationTarget.DriveDb, Amount = 12, SyncBeats = 1, RateHz = 1 } };
            Assert.DoesNotThrow(() => s.Compile(), "an LFO can move the drive");
            Assert.AreEqual(MusicAutomationTarget.Speed, MusicAutomation.JsonNames["speed"]);
        });

        [Test]
        public void SpeedLaneTapeStopSlowsToSilenceAndResumes() => Run(Song(null, new MusicNote(0, 8, 69)), s =>
        {
            // Tape stop over beats 2–3, stopped until beat 4.
            s.MasterAutomation = new[] { new MusicAutomationLane(MusicAutomationTarget.Speed,
                new MusicAutomationPoint(2, 1), new MusicAutomationPoint(3, 0, MusicAutomationCurve.Step), new MusicAutomationPoint(4, 1)) };
            // Beats 2–3 slow to a stop: down to 1/6 speed ln(6) / 0.95 ≈ 1.89 s, then the tempo map's 10 BPM floor for the last 0.12 beat
            // (0.74 s); beat 3–4 is stopped at the normal tempo (1 s).
            var compiled = s.Compile();
            Assert.AreEqual(2 + Math.Log(6) / .95 + .74 + 1, compiled.Tempo.SecondsAt(4), .15, "durations follow the tape speed");
            var pcm = Render(s, 8);
            Assert.AreEqual(440, Hz(pcm, .5, 1.9), 2);
            double slowing = Hz(pcm, 2.6, 2.9);
            Assert.IsTrue(slowing > 100 && slowing < 400, "pitch falls during the stop: " + slowing);
            Assert.IsTrue(Rms(pcm, 4.9, 5.6) < 1e-4, "silent while stopped");
            Assert.AreEqual(440, Hz(pcm, 6, 7.5), 2, "running again on time");
            // Tape start: from a stop at beat 0 up to full speed by beat 2.
            s.MasterAutomation = new[] { new MusicAutomationLane(MusicAutomationTarget.Speed, new MusicAutomationPoint(0, 0), new MusicAutomationPoint(2, 1)) };
            pcm = Render(s, 9); // from a stop, 2 beats take about ln(20) / 0.475 ≈ 6.3 s
            Assert.IsTrue(Rms(pcm, .05, .2) < Rms(pcm, 7, 7.5) * .5, "starts quiet");
            Assert.AreEqual(440, Hz(pcm, 7, 8.5), 2, "up to speed");
        });
    }
}
