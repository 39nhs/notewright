using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicExpressionTests
    {
        const int Rate = 48000;

        static MusicAutomation Lane(MusicAutomationTarget target, params (double beat, float value)[] points) =>
            new MusicAutomation(target, points.Select(p => new MusicAutomationPoint(p.beat, p.value, MusicAutomationCurve.Step)).ToArray(), 8);

        static float[] SinePcm(double hz, double seconds)
        {
            var pcm = new float[(int)(seconds * Rate)];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(.8 * Math.Sin(2 * Math.PI * hz * i / Rate));
            return pcm;
        }

        static MusicCue Cue(MusicInstrument instrument, int version, params MusicAutomation[] lanes) => Cue(instrument, version, 69, lanes);
        static MusicCue Cue(MusicInstrument instrument, int version, int key, params MusicAutomation[] lanes)
        {
            var part = new MusicPart(MusicLayer.Melody, instrument, .5, "Lead", new[] { new MusicNote(0, 4, key) }, 0, automation: lanes.Length == 0 ? null : lanes);
            return new MusicCue("Expression", 120, 4, 8, new[] { part }, Array.Empty<MusicMixEffect>(), playbackVersion: version);
        }

        static double[] Render(MusicCue cue, double seconds)
        {
            var engine = new AdaptiveMusicEngine(cue, Rate, playOnce: true);
            var pcm = new float[(int)(seconds * Rate) * 2]; var block = new float[960];
            for (int at = 0; at < pcm.Length; at += block.Length) { engine.RenderStereo(block); Array.Copy(block, 0, pcm, at, Math.Min(block.Length, pcm.Length - at)); }
            var left = new double[pcm.Length / 2]; for (int i = 0; i < left.Length; i++) left[i] = pcm[i * 2];
            return left;
        }

        static double Frequency(double[] x, int from, int to)
        {
            int crossings = 0; double first = -1, last = 0;
            for (int i = from + 1; i < to; i++)
                if (x[i - 1] < 0 && x[i] >= 0) { double t = i - 1 + x[i - 1] / (x[i - 1] - x[i]); if (first < 0) first = t; last = t; crossings++; }
            return (crossings - 1) / ((last - first) / Rate);
        }

        static double Brightness(double[] x, int from, int to) { double d = 0, e = 0; for (int i = from + 1; i < to; i++) { d += (x[i] - x[i - 1]) * (x[i] - x[i - 1]); e += x[i] * x[i]; } return d / e; }

        static MusicSynthPatch Sine(float vibratoCents = 0) => new MusicSynthSettings { Enabled = true, Osc1Wave = MusicSynthWave.Sine, Filter = MusicSynthFilter.Off,
            AmpAttack = .001f, AmpSustain = 1, LfoPitchCents = vibratoCents, LfoRateHz = 2 }.Compile();

        [Test]
        public void PitchLaneBendsSamplesOscillatorsAndSynthVoices()
        {
            var bendUp = Lane(MusicAutomationTarget.PitchSemitones, (0, 0), (1, 12)); // beat 1 = 0.5 s at 120 BPM
            var instruments = new Dictionary<string, MusicInstrument>
            {
                ["sample"] = new MusicInstrument(SinePcm(440, 3), Rate, 69),
                ["oscillator"] = new MusicInstrument(MusicVoice.Bass),
                ["synth"] = new MusicInstrument(Sine()),
            };
            foreach (var kv in instruments)
            {
                var x = Render(Cue(kv.Value, MusicPlaybackVersion.V3, bendUp), 1);
                Assert.AreEqual(440, Frequency(x, Rate / 10, Rate * 4 / 10), 2, kv.Key + " before the bend");
                Assert.AreEqual(880, Frequency(x, Rate * 6 / 10, Rate * 9 / 10), 4, kv.Key + " an octave up");
            }
            var down = Render(Cue(instruments["synth"], MusicPlaybackVersion.V3, Lane(MusicAutomationTarget.PitchSemitones, (0, -12))), 1);
            Assert.AreEqual(220, Frequency(down, Rate / 10, Rate), 1);
        }

        [Test]
        public void ExpressionLanesAreV3OnlyAndANeutralLaneChangesNothing()
        {
            var instrument = new MusicInstrument(SinePcm(440, 3), Rate, 69);
            var bend = Lane(MusicAutomationTarget.PitchSemitones, (0, 0), (1, 7));
            foreach (int version in new[] { MusicPlaybackVersion.V1, MusicPlaybackVersion.V2 })
                CollectionAssert.AreEqual(Render(Cue(instrument, version), 1), Render(Cue(instrument, version, bend), 1), "V" + version + " ignores the lane");
            CollectionAssert.AreEqual(Render(Cue(instrument, MusicPlaybackVersion.V3), 1),
                Render(Cue(instrument, MusicPlaybackVersion.V3, Lane(MusicAutomationTarget.PitchSemitones, (0, 0))), 1), "pitch 0 renders exactly like no lane");
            var synth = new MusicInstrument(Sine());
            CollectionAssert.AreEqual(Render(Cue(synth, MusicPlaybackVersion.V3), 1),
                Render(Cue(synth, MusicPlaybackVersion.V3, Lane(MusicAutomationTarget.Modulation, (0, 1)), Lane(MusicAutomationTarget.Brightness, (0, 0))), 1), "full modulation, brightness 0");
        }

        [Test]
        public void ModulationScalesSynthVibratoAndBrightnessMovesTheFilter()
        {
            var vibrato = new MusicInstrument(Sine(100));
            var wobbly = Render(Cue(vibrato, MusicPlaybackVersion.V3), 1);
            var still = Render(Cue(vibrato, MusicPlaybackVersion.V3, Lane(MusicAutomationTarget.Modulation, (0, 0))), 1);
            Assert.AreEqual(440 * Math.Pow(2, 1 / 12.0), Frequency(wobbly, Rate / 8 - 1200, Rate / 8 + 1200), 6, "LFO peak at full modulation");
            Assert.AreEqual(440, Frequency(still, Rate / 8 - 1200, Rate / 8 + 1200), 1, "modulation 0 stops the vibrato");
            var saw = new MusicInstrument(new MusicSynthSettings { Enabled = true, Osc1Wave = MusicSynthWave.Saw, CutoffHz = 1500, Resonance = 0, AmpSustain = 1 }.Compile());
            double neutral = Brightness(Render(Cue(saw, MusicPlaybackVersion.V3, 45), .5), Rate / 10, Rate / 2);
            double dark = Brightness(Render(Cue(saw, MusicPlaybackVersion.V3, 45, Lane(MusicAutomationTarget.Brightness, (0, -3))), .5), Rate / 10, Rate / 2);
            double bright = Brightness(Render(Cue(saw, MusicPlaybackVersion.V3, 45, Lane(MusicAutomationTarget.Brightness, (0, 3))), .5), Rate / 10, Rate / 2);
            Assert.Less(dark, neutral * .5); Assert.Greater(bright, neutral * 2);
        }

        [Test]
        public void ExpressionTargetsAreTrackOnlyAndValidated()
        {
            foreach (var target in new[] { MusicAutomationTarget.PitchSemitones, MusicAutomationTarget.Modulation, MusicAutomationTarget.Brightness })
            {
                Assert.IsTrue(MusicAutomation.IsVoice(target)); Assert.IsFalse(MusicAutomation.IsEffect(target));
                var lane = new MusicAutomationLane(target, new MusicAutomationPoint(0, 0));
                Assert.Throws<ArgumentException>(() => MusicAutomation.Compile(new[] { lane }, 8, "Master", true), target + " on the master");
                Assert.IsNotNull(MusicAutomation.Compile(new[] { lane }, 8, "Lead", false));
            }
            Assert.Throws<ArgumentException>(() => new MusicAutomation(MusicAutomationTarget.PitchSemitones, new[] { new MusicAutomationPoint(0, 25) }, 8));
            Assert.Throws<ArgumentException>(() => new MusicAutomation(MusicAutomationTarget.Brightness, new[] { new MusicAutomationPoint(0, -9) }, 8));
            Assert.AreEqual(MusicAutomationTarget.PitchSemitones, MusicAutomation.JsonNames["pitch"]);
        }

        [Test]
        public void SmfImportReadsBendRangeBendAndControllersPerChannel()
        {
            // One track, 480 PPQN: RPN 0 = 12 semitones, note at 0, bend +12 at beat 1, CC1 at beat 1, centre at beat 2, note off at beat 3.
            var events = new List<byte>();
            void Ev(int delta, params int[] bytes) { Var(events, delta); foreach (int b in bytes) events.Add((byte)b); }
            Ev(0, 0xB0, 101, 0); Ev(0, 0xB0, 100, 0); Ev(0, 0xB0, 6, 12);
            Ev(0, 0x90, 60, 100);
            Ev(480, 0xE0, 127, 127); Ev(0, 0xB0, 1, 64);
            Ev(480, 0xE0, 0, 64);
            Ev(480, 0x80, 60, 0);
            Ev(0, 0xFF, 0x2F, 0);
            var file = new List<byte>(); file.AddRange(System.Text.Encoding.ASCII.GetBytes("MThd")); file.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xE0 });
            file.AddRange(System.Text.Encoding.ASCII.GetBytes("MTrk")); file.AddRange(BitConverter.GetBytes(events.Count).Reverse()); file.AddRange(events);
            var midi = MidiMusicReader.Read(file.ToArray());
            var part = midi.Parts.Single();
            var pitch = part.Expression[0];
            CollectionAssert.AreEqual(new[] { 0.0, 1, 2 }, pitch.Select(p => p.Beat).ToArray());
            Assert.AreEqual(0, pitch[0].Value); Assert.AreEqual(12, pitch[1].Value, .01, "full bend at a 12-semitone range"); Assert.AreEqual(0, pitch[2].Value);
            Assert.AreEqual(64 / 127f, part.Expression[1][0].Value, 1e-6);
            Assert.IsNull(part.Expression[2]);
        }

        static void Var(List<byte> output, int value)
        {
            var stack = new Stack<byte>(); stack.Push((byte)(value & 127));
            while ((value >>= 7) > 0) stack.Push((byte)(value & 127 | 128));
            output.AddRange(stack);
        }
    }
}
