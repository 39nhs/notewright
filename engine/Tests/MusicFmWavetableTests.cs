using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>Synth FM (2-operator) and wavetable oscillators, their mod-matrix destinations and band-limited tables.</summary>
    public sealed class MusicFmWavetableTests
    {
        const int Rate = 32000;

        static MusicSynthSettings Plain(MusicSynthWave wave)
        {
            var s = MusicSynthPresets.Create("Init"); s.Osc1Wave = wave; s.AmpAttack = .001f; s.AmpSustain = 1; s.AmpRelease = .01f; s.CutoffHz = 20000; s.Filter = MusicSynthFilter.Off;
            return s;
        }

        static float[] Render(MusicSynthSettings synth, int key, double seconds)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                song.name = "Fm"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = 60; song.LoopBeats = 8;
                song.Parts = new[] { new MusicCueDefinition.Part { Name = "Synth", Gain = .3f, Synth = synth, Notes = new[] { new MusicNote(0, 6, key) } } };
                var pcm = new float[(int)(seconds * Rate) * 2];
                new AdaptiveMusicEngine(song.Compile(), Rate, playOnce: true).RenderStereo(pcm);
                return pcm;
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        static double Magnitude(float[] pcm, double hz, double from, double to)
        {
            int a = (int)(from * Rate), b = (int)(to * Rate);
            double w = 2 * Math.PI * hz / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = a; i < b; i++) { double s0 = pcm[i * 2] + c * s1 - s2; s2 = s1; s1 = s0; }
            return Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2) / (b - a);
        }

        [Test]
        public void FmMakesSidebandsThatTheIndexControls()
        {
            var s = Plain(MusicSynthWave.Fm); s.FmRatio = 2; s.FmIndex = 0;
            var pure = Render(s, 57, 1); // 220 Hz
            Assert.IsTrue(Magnitude(pure, 660, .2, 1) < .01 * Magnitude(pure, 220, .2, 1), "index 0 = sine");
            s = Plain(MusicSynthWave.Fm); s.FmRatio = 2; s.FmIndex = 1.5f;
            var fm = Render(s, 57, 1);
            Assert.IsTrue(Magnitude(fm, 660, .2, 1) > .3 * Magnitude(fm, 220, .2, 1), "carrier ± modulator: 660 Hz sideband");
            Assert.IsTrue(Magnitude(fm, 1100, .2, 1) > .05 * Magnitude(fm, 220, .2, 1), "and the next one");
            Assert.IsTrue(Magnitude(fm, 440, .2, 1) < .01 * Magnitude(fm, 220, .2, 1), "ratio 2: only odd multiples");
        }

        [Test]
        public void FmEnvelopeDecaysTheBrightness()
        {
            var s = Plain(MusicSynthWave.Fm); s.FmRatio = 1; s.FmIndex = 0; s.FmEnvelope = 5; s.FilterAttack = .001f; s.FilterDecay = .3f; s.FilterSustain = 0;
            var pcm = Render(s, 57, 2);
            double Bright(double t) => Magnitude(pcm, 660, t, t + .1) / Magnitude(pcm, 220, t, t + .1);
            Assert.IsTrue(Bright(.02) > 5 * Bright(1.5), $"bright at the start, mellow later: {Bright(.02):0.00} → {Bright(1.5):0.000}");
            var bell = Render(MusicSynthPresets.Create("FM bell"), 69, 1);
            Assert.IsTrue(Magnitude(bell, 440 * 4.5, .02, .3) > .02 * Magnitude(bell, 440, .02, .3), "the bell's inharmonic 4.5× partial");
        }

        [Test]
        public void WavetablePositionMorphsTheShape()
        {
            var s = Plain(MusicSynthWave.Wavetable); s.Wavetable = "Basic"; s.WavetablePosition = 0;
            var sine = Render(s, 57, 1);
            Assert.IsTrue(Magnitude(sine, 660, .2, 1) < .01 * Magnitude(sine, 220, .2, 1) && Magnitude(sine, 440, .2, 1) < .01 * Magnitude(sine, 220, .2, 1), "position 0 = sine");
            s.WavetablePosition = 1;
            var square = Render(s, 57, 1);
            Assert.AreEqual(1 / 3.0, Magnitude(square, 660, .2, 1) / Magnitude(square, 220, .2, 1), .05, "position 1 = square: third harmonic 1/3");
            Assert.IsTrue(Magnitude(square, 440, .2, 1) < .02 * Magnitude(square, 220, .2, 1), "no even harmonics");
            s.WavetablePosition = 2 / 3f;
            var saw = Render(s, 57, 1);
            Assert.AreEqual(.5, Magnitude(saw, 440, .2, 1) / Magnitude(saw, 220, .2, 1), .06, "position 2/3 = saw: second harmonic 1/2");
            foreach (string name in MusicWavetable.BuiltInNames) Assert.IsTrue(MusicWavetable.BuiltIn(name).FrameCount >= 5, name);
        }

        [Test]
        public void TablesStayBandLimitedOnHighNotes()
        {
            var s = Plain(MusicSynthWave.Wavetable); s.WavetablePosition = 1; // square
            var pcm = Render(s, 100, 1); // E7 2637 Hz: harmonics past 16 kHz must be gone, so no fold-back below the fundamental
            double fundamental = Magnitude(pcm, 2637.02, .2, 1);
            double aliasRegion = new[] { 500.0, 1000, 1500, 2000 }.Max(hz => Magnitude(pcm, hz, .2, 1));
            Assert.IsTrue(aliasRegion < .01 * fundamental, $"no aliasing below the note: {aliasRegion / fundamental:0.0000}");
        }

        [Test]
        public void ModMatrixMovesTheTableAndCustomFramesLoad()
        {
            // A slow sweep (the Basic table's triangle has its 3rd harmonic in opposite phase to the saw's, so a fast sweep cancels in a window).
            var s = Plain(MusicSynthWave.Wavetable); s.WavetablePosition = 0; s.FilterAttack = .001f; s.FilterDecay = 5f; s.FilterSustain = 0;
            s.ModMatrix = new[] { new MusicSynthModSlot(MusicSynthModSource.FilterEnvelope, MusicSynthModDestination.WavetablePosition, 1) };
            var pcm = Render(s, 57, 2);
            double Square(double t) => Magnitude(pcm, 660, t, t + .1) / Magnitude(pcm, 220, t, t + .1);
            Assert.IsTrue(Square(.02) > .25 && Square(1.5) < .08, $"the envelope sweeps square → toward sine: {Square(.02):0.00} → {Square(1.5):0.000}");
            // Two custom frames: a sine and its second harmonic.
            var frames = new float[MusicWavetable.FrameSize * 2];
            for (int i = 0; i < MusicWavetable.FrameSize; i++) { frames[i] = (float)Math.Sin(2 * Math.PI * i / MusicWavetable.FrameSize); frames[MusicWavetable.FrameSize + i] = (float)Math.Sin(4 * Math.PI * i / MusicWavetable.FrameSize); }
            var clip = AudioClip.Create("Table", frames.Length, 1, 48000, false); clip.SetData(frames, 0);
            try
            {
                var custom = Plain(MusicSynthWave.Wavetable); custom.WavetableClip = clip; custom.WavetablePosition = 1;
                var octave = Render(custom, 57, 1);
                Assert.IsTrue(Magnitude(octave, 440, .2, 1) > 20 * Magnitude(octave, 220, .2, 1), "frame 2 = the octave");
                Assert.Throws<ArgumentException>(() => MusicWavetable.FromFrames("bad", new float[3000]));
                Assert.Throws<ArgumentException>(() => MusicWavetable.BuiltIn("Nope"));
                var bad = Plain(MusicSynthWave.Fm); bad.FmRatio = 40;
                Assert.Throws<ArgumentException>(() => bad.Compile());
            }
            finally { UnityEngine.Object.DestroyImmediate(clip); }
        }
    }
}
