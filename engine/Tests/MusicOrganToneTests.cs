using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>Synth drawbar organ and the rotary speaker (Color stage) with its RotarySpeed lane.</summary>
    public sealed class MusicOrganToneTests
    {
        const int Rate = 16000;

        static MusicCueDefinition Song(MusicSynthSettings synth, MusicColorSettings color, params MusicNote[] notes)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Organ"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = 60; song.LoopBeats = 16;
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Organ", Gain = .2f, Synth = synth, Notes = notes.Length > 0 ? notes : new[] { new MusicNote(0, 8, 57) } } };
            song.Mixer = new MusicMixerSettings { Color = color ?? new MusicColorSettings() };
            return song;
        }

        static float[] Render(MusicCueDefinition song, double seconds)
        {
            try
            {
                var pcm = new float[(int)(seconds * Rate) * 2];
                new AdaptiveMusicEngine(song.Compile(), Rate, playOnce: true).RenderStereo(pcm);
                return pcm;
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        static MusicSynthSettings Organ(string drawbars)
        {
            var s = MusicSynthPresets.Create("Jazz organ"); s.Drawbars = drawbars; return s;
        }

        // Goertzel magnitude of the left channel at hz over [from, to) seconds.
        static double Magnitude(float[] pcm, double hz, double from, double to)
        {
            int a = (int)(from * Rate), b = (int)(to * Rate);
            double w = 2 * Math.PI * hz / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = a; i < b; i++) { double s0 = pcm[i * 2] + c * s1 - s2; s2 = s1; s1 = s0; }
            return Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2) / (b - a);
        }

        [Test]
        public void DrawbarsSetTheHarmonics()
        {
            // A3 = 220 Hz: 16' = 110, 5⅓' = 330, 8' = 220, 4' = 440, 1' = 1760.
            var eight = Render(Song(Organ("008000000"), null), 1);
            Assert.IsTrue(Magnitude(eight, 220, .2, 1) > 20 * Magnitude(eight, 110, .2, 1), "8' alone = the note");
            var sixteen = Render(Song(Organ("800000000"), null), 1);
            Assert.IsTrue(Magnitude(sixteen, 110, .2, 1) > 20 * Magnitude(sixteen, 220, .2, 1), "16' = an octave below");
            var jazz = Render(Song(Organ("888000000"), null), 1);
            double m110 = Magnitude(jazz, 110, .2, 1), m220 = Magnitude(jazz, 220, .2, 1), m330 = Magnitude(jazz, 330, .2, 1);
            Assert.AreEqual(1, m110 / m220, .15, "full drawbars are equal"); Assert.AreEqual(1, m330 / m220, .15);
            Assert.IsTrue(Magnitude(jazz, 440, .2, 1) < .05 * m220, "4' pushed in");
            var soft = Render(Song(Organ("004000000"), null), 1);
            Assert.AreEqual(Math.Pow(10, -12 / 20.0), Magnitude(soft, 220, .2, 1) / Magnitude(eight, 220, .2, 1), .02, "3 dB per drawbar step");
        }

        [Test]
        public void PercussionAndKeyClickColourTheAttack()
        {
            // A3 with only the 8' drawbar: percussion third = 660 Hz, decaying; key click = bright noise for a few ms.
            var plain = Render(Song(Organ("008000000"), null), 1.2);
            var perc = Organ("008000000"); perc.OrganPercussion = MusicOrganPercussion.Third; perc.PercussionLevel = 1; perc.PercussionDecaySeconds = .3f;
            var withPerc = Render(Song(perc, null), 1.2);
            Assert.IsTrue(Magnitude(plain, 660, .01, .06) < .01 * Magnitude(plain, 220, .01, .06), "no third without percussion");
            Assert.IsTrue(Magnitude(withPerc, 660, .01, .06) > .3 * Magnitude(withPerc, 220, .01, .06), "third harmonic at the attack");
            Assert.IsTrue(Magnitude(withPerc, 660, .6, 1) < .02 * Magnitude(withPerc, 220, .6, 1), "gone after the decay");
            var second = Organ("008000000"); second.OrganPercussion = MusicOrganPercussion.Second; second.PercussionLevel = 1;
            var withSecond = Render(Song(second, null), .2);
            Assert.IsTrue(Magnitude(withSecond, 440, .01, .06) > .3 * Magnitude(withSecond, 220, .01, .06), "second = the octave");
            var click = Organ("008000000"); click.KeyClick = 1;
            var withClick = Render(Song(click, null), .2);
            double Bright(float[] pcm, double from, double to) => Enumerable.Range((int)(from * Rate), (int)((to - from) * Rate)).Sum(i => Math.Abs(pcm[(i + 1) * 2] - pcm[i * 2]));
            Assert.IsTrue(Bright(withClick, 0, .004) > 3 * Bright(plain, 0, .004), "a bright click at the note start");
            Assert.AreEqual(Bright(plain, .05, .15), Bright(withClick, .05, .15), 1e-3, "nothing after it");
            var song = Song(Organ("888000000"), null); song.Parts[0].Synth.OrganPercussion = MusicOrganPercussion.Third;
            song.Parts[0].Synth.Osc1Wave = MusicSynthWave.Saw; // percussion only applies to the Organ waveform
            Assert.IsTrue(Render(song, .2).Length > 0);
            Assert.Throws<ArgumentException>(() => { var bad = Organ("888000000"); bad.PercussionDecaySeconds = 10; bad.Compile(); });
            Assert.IsTrue(MusicSynthPresets.Names.Contains("Jazz organ perc"));
        }

        [Test]
        public void HighHarmonicsAboveNyquistAreLeftOut()
        {
            var top = Render(Song(Organ("000000008"), null, new MusicNote(0, 2, 100)), 1); // E7 2637 Hz: 1' would be 10.5 kHz, above 0.45 × 16 kHz
            Assert.IsTrue(top.Max(Math.Abs) < 1e-6, "nothing aliases back");
        }

        [Test]
        public void BadRegistrationsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => MusicSynthPatch.ParseDrawbars("88800000"));
            Assert.Throws<ArgumentException>(() => MusicSynthPatch.ParseDrawbars("889000000"));
            Assert.AreEqual("8 8 8 0 0 0 0 0 0", string.Join(" ", MusicSynthPatch.ParseDrawbars("88 8000-000")));
            var song = Song(Organ("000000000"), null);
            try { Assert.Throws<InvalidOperationException>(() => song.Compile()); } finally { UnityEngine.Object.DestroyImmediate(song); }
            Assert.IsTrue(MusicSynthPresets.Names.Contains("Rock organ"));
        }

        // Level swirl: the strongest frequency (0.3–12 Hz) of the left channel's 5 ms RMS envelope over [from, to).
        static double SwirlRate(float[] pcm, double from, double to)
        {
            int window = Rate / 200, a = (int)(from * Rate), n = (int)((to - from) * Rate) / window;
            var env = new double[n];
            for (int k = 0; k < n; k++) { double s = 0; for (int i = 0; i < window; i++) { double x = pcm[(a + k * window + i) * 2]; s += x * x; } env[k] = Math.Sqrt(s / window); }
            double mean = env.Average(), best = 0, bestHz = 0;
            for (double hz = .3; hz <= 12; hz += .05)
            {
                double re = 0, im = 0;
                for (int k = 0; k < n; k++) { double t = k / 200.0; re += (env[k] - mean) * Math.Cos(2 * Math.PI * hz * t); im += (env[k] - mean) * Math.Sin(2 * Math.PI * hz * t); }
                double m = re * re + im * im; if (m > best) { best = m; bestHz = hz; }
            }
            return bestHz;
        }

        [Test]
        public void RotarySwirlsSlowOrFastAndSpreadsTheSound()
        {
            var tone = Organ("000000800"); // 1⅓' of A3 = 1320 Hz: the horn's band
            var slow = Render(Song(tone, new MusicColorSettings { Rotary = true, RotarySpeed = 0, RotaryDepth = 1 }), 4);
            var fast = Render(Song(tone.Clone(), new MusicColorSettings { Rotary = true, RotarySpeed = 1, RotaryDepth = 1 }), 4);
            double slowRate = SwirlRate(slow, 1, 4), fastRate = SwirlRate(fast, 1, 4);
            Assert.AreEqual(MusicColorSettings.RotaryHornSlowHz, slowRate, .5, "chorale");
            Assert.AreEqual(MusicColorSettings.RotaryHornFastHz, fastRate, 1.2, "tremolo");
            double side = 0, mid = 0; for (int i = Rate * 2; i < fast.Length; i += 2) { side += Math.Abs(fast[i] - fast[i + 1]); mid += Math.Abs(fast[i] + fast[i + 1]); }
            Assert.IsTrue(side > .2 * mid, "two mics: stereo");
            var dry = Render(Song(tone.Clone(), new MusicColorSettings { Rotary = true, RotaryMix = 0 }), 1);
            var plain = Render(Song(tone.Clone(), null), 1);
            Assert.IsTrue(dry.Zip(plain, (x, y) => Math.Abs(x - y)).Max() < 1e-6, "mix 0 = dry");
        }

        [Test]
        public void RotarySpeedLaneSpinsTheRotorsUp() => Assert.DoesNotThrow(() =>
        {
            var song = Song(Organ("000000800"), new MusicColorSettings { Rotary = true, RotaryDepth = 1 }, new MusicNote(0, 12, 57));
            song.MasterAutomation = new[] { new MusicAutomationLane(MusicAutomationTarget.RotarySpeed, new MusicAutomationPoint(0, 0, MusicAutomationCurve.Step), new MusicAutomationPoint(4, 1)) };
            var pcm = Render(song, 9);
            double before = SwirlRate(pcm, 1, 4), early = SwirlRate(pcm, 4, 4.5), late = SwirlRate(pcm, 6.5, 9);
            Assert.IsTrue(before < 2 && late > 5, $"slow then fast: {before:0.0} → {late:0.0} Hz");
            Assert.IsTrue(early < late - 1, $"the horn takes time to spin up: {early:0.0} Hz just after the switch");
            var bad = Song(Organ("888000000"), null);
            bad.MasterAutomation = song.MasterAutomation;
            try
            {
                string message = null;
                try { bad.Compile(); } catch (ArgumentException e) { message = e.Message; } catch (InvalidOperationException e) { message = e.Message; }
                Assert.IsTrue(message != null && message.Contains("Rotary"), message ?? "compiled");
            }
            finally { UnityEngine.Object.DestroyImmediate(bad); }
            Assert.AreEqual(MusicAutomationTarget.RotarySpeed, MusicAutomation.JsonNames["rotary"]);
        });
    }
}
