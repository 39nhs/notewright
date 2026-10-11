using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicModulatorTests
    {
        const int Rate = 8000;

        static MusicSynthSettings Sine() => new MusicSynthSettings { Enabled = true, Osc1Wave = MusicSynthWave.Sine, Filter = MusicSynthFilter.Off, AmpAttack = .001f, AmpSustain = 1, AmpRelease = .01f };

        // 120 BPM, 8 beats: "Pad" holds A4 the whole time; "Kick" hits on beats 2 and 6 (loud short bursts).
        static MusicCueDefinition Song(int version = MusicPlaybackVersion.V3)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Mods"; song.PlaybackVersion = version; song.Bpm = 120; song.LoopBeats = 8;
            var kick = Sine(); kick.AmpSustain = 1;
            song.Parts = new[]
            {
                new MusicCueDefinition.Part { Name = "Pad", InstrumentId = "Pad", Gain = .03f, Synth = Sine(), Notes = new[] { new MusicNote(0, 8, 69) } }, // quiet: the soft limiter stays linear
                new MusicCueDefinition.Part { Name = "Kick", InstrumentId = "Kick", Gain = .9f, Pan = 1, Synth = kick, Notes = new[] { new MusicNote(2, .5, 45), new MusicNote(6, .5, 45) } }, // right only
            };
            return song;
        }

        static float[] Render(MusicCueDefinition song, double seconds = 4)
        {
            var pcm = new float[(int)(seconds * Rate) * 2];
            new AdaptiveMusicEngine(song.Compile(), Rate, playOnce: true).RenderStereo(pcm);
            return pcm;
        }

        static double Rms(float[] stereo, double from, double to, int channel = 0)
        {
            double e = 0; int a = (int)(from * Rate), b = (int)(to * Rate);
            for (int f = a; f < b; f++) e += stereo[f * 2 + channel] * (double)stereo[f * 2 + channel];
            return Math.Sqrt(e / (b - a));
        }

        static void Using(MusicCueDefinition song, Action<MusicCueDefinition> test) { try { test(song); } finally { UnityEngine.Object.DestroyImmediate(song); } }

        [Test]
        public void SyncedLfoMovesVolumeAndPan() => Using(Song(), s =>
        {
            s.Parts[1].Notes = Array.Empty<MusicNote>();
            s.Parts[0].Modulators = new[] { MusicModulator.Lfo(MusicAutomationTarget.VolumeDb, 12, syncBeats: 2, wave: MusicLfoWave.Square) };
            var pcm = Render(s);
            // Square, one cycle per 2 beats (1 s): +12 dB in the first half second, -12 dB in the second.
            Assert.AreEqual(24, 20 * Math.Log10(Rms(pcm, .05, .45) / Rms(pcm, .55, .95)), .5);
            s.Parts[0].Modulators = new[] { MusicModulator.Lfo(MusicAutomationTarget.Pan, 1, syncBeats: 2, wave: MusicLfoWave.Square) };
            pcm = Render(s);
            Assert.Less(Rms(pcm, .05, .45, 0), 1e-4, "pan +1: left closed"); Assert.Less(Rms(pcm, .55, .95, 1), 1e-4, "pan -1: right closed");
        });

        [Test]
        public void LfoRidesOnTheExistingLane() => Using(Song(), s =>
        {
            s.Parts[1].Notes = Array.Empty<MusicNote>();
            s.Parts[0].Automation = new[] { new MusicAutomationLane(MusicAutomationTarget.VolumeDb, new MusicAutomationPoint(0, -20, MusicAutomationCurve.Step), new MusicAutomationPoint(4, 0, MusicAutomationCurve.Step)) };
            s.Parts[0].Modulators = new[] { MusicModulator.Lfo(MusicAutomationTarget.VolumeDb, 6, syncBeats: 2, wave: MusicLfoWave.Square) };
            var pcm = Render(s);
            Assert.AreEqual(-14 - 6, 20 * Math.Log10(Rms(pcm, .05, .45) / Rms(pcm, 2.05, 2.45)), .6, "-20 + 6 against 0 + 6");
            Assert.AreEqual(1, s.Parts[0].Automation.Length, "the song's own lane is not changed");
            Assert.AreEqual(2, s.Parts[0].Automation[0].Points.Length);
        });

        [Test]
        public void FollowerDucksToAnotherTrack() => Using(Song(), s =>
        {
            s.Parts[0].Pan = -1; // the pad on the left, the kick on the right: the left channel is the pad alone
            s.Parts[0].Modulators = new[] { MusicModulator.Follower(MusicAutomationTarget.VolumeDb, -40, "Kick", attackMs: 2, releaseMs: 150) };
            var ducked = Render(s);
            s.Parts[1].Gain = 0; // no kick sound → no ducking (the pad alone)
            var free = Render(s);
            Assert.Less(Rms(ducked, 1.05, 1.2) / Rms(free, 1.05, 1.2), .2, "the pad ducks while the kick sounds");
            Assert.AreEqual(1, Rms(ducked, .3, .9) / Rms(free, .3, .9), .05, "before the kick it is untouched");
            s.Parts[0].Modulators = new[] { MusicModulator.Follower(MusicAutomationTarget.VolumeDb, -40, "Nobody") };
            var e = Assert.Throws<InvalidOperationException>(() => s.Compile());
            Assert.IsTrue(e.Message.Contains("Nobody"), e.Message);
        });

        [Test]
        public void OlderVersionsIgnoreModulators() => Using(Song(MusicPlaybackVersion.V2), s =>
        {
            s.Parts[0].Synth = new MusicSynthSettings(); s.Parts[1].Synth = new MusicSynthSettings();
            var plain = Render(s);
            s.Parts[0].Modulators = new[] { MusicModulator.Lfo(MusicAutomationTarget.VolumeDb, 12, syncBeats: 1) };
            CollectionAssert.AreEqual(plain, Render(s));
        });

        [Test]
        public void InvalidModulatorsAreRejected() => Using(Song(), s =>
        {
            s.Parts[0].Modulators = new[] { MusicModulator.Lfo(MusicAutomationTarget.VolumeDb, 6, rateHz: 99) };
            Assert.Throws<InvalidOperationException>(() => s.Compile());
            s.Parts[0].Modulators = Enumerable.Range(0, 9).Select(i => MusicModulator.Lfo(MusicAutomationTarget.Pan, .1f)).ToArray();
            Assert.Throws<InvalidOperationException>(() => s.Compile());
            s.Parts[0].Modulators = new[] { MusicModulator.Lfo(MusicAutomationTarget.VolumeDb, 6, rateHz: 2) };
            s.Parts[0].Modulators[0].Enabled = false;
            Assert.DoesNotThrow(() => s.Compile(), "disabled modulators are ignored");
        });

        static double Frequency(float[] stereo, double from, double to)
        {
            int crossings = 0;
            for (int f = (int)(from * Rate) + 1; f < (int)(to * Rate); f++) if (stereo[(f - 1) * 2] < 0 && stereo[f * 2] >= 0) crossings++;
            return crossings / (to - from);
        }

        [Test]
        public void SynthModMatrixRoutesSources() => Using(Song(), s =>
        {
            s.Parts[1].Notes = Array.Empty<MusicNote>();
            var synth = Sine(); synth.Lfo2Wave = MusicLfoWave.Square; synth.Lfo2SyncBeats = 2; // a 1 s cycle
            synth.ModMatrix = new[] { new MusicSynthModSlot(MusicSynthModSource.Lfo2, MusicSynthModDestination.PitchCents, 1200) };
            s.Parts[0].Synth = synth;
            var pcm = Render(s);
            Assert.AreEqual(880, Frequency(pcm, .05, .45), 10, "LFO 2 +1 → an octave up");
            Assert.AreEqual(220, Frequency(pcm, .55, .95), 6, "-1 → an octave down");
            synth.ModMatrix = new[] { new MusicSynthModSlot(MusicSynthModSource.Velocity, MusicSynthModDestination.Level, -1) };
            s.Parts[0].Notes = new[] { new MusicNote(0, 1.8, 69, 1), new MusicNote(2, 1.8, 69, .5f) };
            pcm = Render(s, 2);
            // Level × (1 - velocity): velocity 1 is silenced by the matrix, velocity .5 plays at .5 × .5.
            Assert.Less(Rms(pcm, .1, .8), 1e-5); Assert.Greater(Rms(pcm, 1.1, 1.8), .002);
            synth.ModMatrix = new[] { new MusicSynthModSlot(MusicSynthModSource.Lfo1, MusicSynthModDestination.PitchCents, 9999) };
            Assert.Throws<InvalidOperationException>(() => s.Compile(), "amount limit");
        });
    }
}
