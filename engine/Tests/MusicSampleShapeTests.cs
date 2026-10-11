using System;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>Sample voice shaping: decay/sustain, filter, filter envelope, velocity brightness, key tracking.</summary>
    public sealed class MusicSampleShapeTests
    {
        const int Rate = 32000;

        // A 3 s steady saw-like tone (220 Hz, many harmonics) recorded at A3.
        static AudioClip Tone()
        {
            var pcm = new float[Rate * 3];
            for (int i = 0; i < pcm.Length; i++) { double ph = 220.0 * i / Rate; pcm[i] = (float)(.4 * (2 * (ph - Math.Floor(ph)) - 1)); }
            var clip = AudioClip.Create("Saw", pcm.Length, 1, Rate, false); clip.SetData(pcm, 0); return clip;
        }

        static float[] Render(MusicSampleShapeSettings shape, int key = 57, float velocity = 1, int version = MusicPlaybackVersion.V3)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>(); var clip = Tone();
            try
            {
                song.name = "Shape"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = 60; song.LoopBeats = 4;
                song.Parts = new[] { new MusicCueDefinition.Part { Name = "S", Sample = clip, SampleRootKey = 57, Gain = .5f, AttackSeconds = .002f, ReleaseSeconds = .05f,
                    SampleShape = shape, Notes = new[] { new MusicNote(0, 2.5, key, velocity) } } };
                var pcm = new float[Rate * 3 * 2]; new AdaptiveMusicEngine(song.Compile(version), Rate, playOnce: true).RenderStereo(pcm); return pcm;
            }
            finally { UnityEngine.Object.DestroyImmediate(song); UnityEngine.Object.DestroyImmediate(clip); }
        }

        static double Rms(float[] pcm, double from, double to) { double s = 0; int a = (int)(from * Rate), b = (int)(to * Rate); for (int i = a; i < b; i++) s += pcm[i * 2] * pcm[i * 2]; return Math.Sqrt(s / (b - a)); }

        static double Magnitude(float[] pcm, double hz, double from, double to)
        {
            int a = (int)(from * Rate), b = (int)(to * Rate); double w = 2 * Math.PI * hz / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = a; i < b; i++) { double s0 = pcm[i * 2] + c * s1 - s2; s2 = s1; s1 = s0; }
            return Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2) / (b - a);
        }

        [Test]
        public void DecaysToTheSustainLevel()
        {
            var plain = Render(new MusicSampleShapeSettings());
            var shaped = Render(new MusicSampleShapeSettings { DecaySeconds = .3f, Sustain = .25f });
            Assert.AreEqual(.25 + .75 * Math.Exp(-6.9078 * .0035 / .3), Rms(shaped, .003, .008) / Rms(plain, .003, .008), .05, "near full level just after the attack");
            Assert.AreEqual(.25, Rms(shaped, 1.2, 2.0) / Rms(plain, 1.2, 2.0), .02, "held at the sustain level");
            Assert.IsTrue(Rms(shaped, 2.7, 2.9) < .01 * Rms(shaped, 1.2, 2), "releases at the note end");
        }

        [Test]
        public void FilterEnvelopeVelocityAndKeyTracking()
        {
            var dark = Render(new MusicSampleShapeSettings { Filter = MusicSynthFilter.LowPass, CutoffHz = 400 });
            Assert.IsTrue(Magnitude(dark, 2200, .5, 1.5) < .05 * Magnitude(dark, 220, .5, 1.5), "low-pass removes the high harmonics");
            var swept = Render(new MusicSampleShapeSettings { Filter = MusicSynthFilter.LowPass, CutoffHz = 400, EnvelopeOctaves = 4, FilterDecay = .4f });
            Assert.IsTrue(Magnitude(swept, 2200, .01, .08) / Magnitude(swept, 220, .01, .08) > 5 * Magnitude(swept, 2200, 1.2, 1.5) / Magnitude(swept, 220, 1.2, 1.5), "bright attack, dark sustain");
            var soft = Render(new MusicSampleShapeSettings { Filter = MusicSynthFilter.LowPass, CutoffHz = 3000, VelocityOctaves = 3 }, velocity: .2f);
            var hard = Render(new MusicSampleShapeSettings { Filter = MusicSynthFilter.LowPass, CutoffHz = 3000, VelocityOctaves = 3 }, velocity: 1);
            Assert.IsTrue(Magnitude(soft, 1760, .5, 1.5) / Magnitude(soft, 220, .5, 1.5) < .5 * Magnitude(hard, 1760, .5, 1.5) / Magnitude(hard, 220, .5, 1.5), "soft notes are darker");
            // A5 plays the 3 s recording 4× faster (0.75 s): measure early. Tracked cutoff 600 → 2.0 kHz.
            var tracked = Render(new MusicSampleShapeSettings { Filter = MusicSynthFilter.HighPass, CutoffHz = 600, KeyTracking = 1 }, key: 81);
            var fixedCut = Render(new MusicSampleShapeSettings { Filter = MusicSynthFilter.HighPass, CutoffHz = 600 }, key: 81);
            Assert.IsTrue(Magnitude(tracked, 880, .1, .6) / Magnitude(tracked, 3520, .1, .6) < .5 * Magnitude(fixedCut, 880, .1, .6) / Magnitude(fixedCut, 3520, .1, .6),
                "at A5 the tracked high-pass sits higher and thins the fundamental");
        }

        [Test]
        public void OlderVersionsIgnoreItAndBadValuesAreRejected()
        {
            var shape = new MusicSampleShapeSettings { Sustain = .1f, DecaySeconds = .1f, Filter = MusicSynthFilter.LowPass, CutoffHz = 200 };
            var v2 = Render(shape, version: MusicPlaybackVersion.V2); var plain = Render(new MusicSampleShapeSettings(), version: MusicPlaybackVersion.V2);
            for (int i = 0; i < v2.Length; i += 97) Assert.AreEqual(plain[i], v2[i], 1e-7, "V2 plays the sample as before");
            Assert.Throws<ArgumentException>(() => new MusicSampleShapeSettings { Sustain = .5f, CutoffHz = 5 }.Compile());
            Assert.IsNull(new MusicSampleShapeSettings().Compile(), "nothing on = no shaping");
        }
    }
}
