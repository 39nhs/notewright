using System;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicAutomationTests
    {
        static MusicAutomationPoint P(double beat, float value, MusicAutomationCurve curve = MusicAutomationCurve.Linear) => new MusicAutomationPoint(beat, value, curve);

        [Test]
        public void EvaluatesLinearStepSmoothAndHoldsOutsideThePoints()
        {
            var lane = new MusicAutomation(MusicAutomationTarget.VolumeDb,
                new[] { P(4, -6, MusicAutomationCurve.Smooth), P(1, 0), P(2, -12, MusicAutomationCurve.Step), P(6, 6), P(6, -80) }, 8);
            Assert.AreEqual(0, lane.Evaluate(0), 1e-12, "before the first point");
            Assert.AreEqual(-6, lane.Evaluate(1.5), 1e-12, "linear");
            Assert.AreEqual(-12, lane.Evaluate(3.99), 1e-12, "step holds");
            Assert.AreEqual(-6, lane.Evaluate(4), 1e-12);
            Assert.AreEqual(0, lane.Evaluate(5), 1e-12, "smoothstep midpoint");
            Assert.AreEqual(-6 + 12 * .25 * .25 * (3 - 2 * .25), lane.Evaluate(4.5), 1e-12, "smoothstep");
            Assert.AreEqual(-80, lane.Evaluate(6), 1e-12, "equal beats: the later point wins from that beat on");
            Assert.AreEqual(-80, lane.Evaluate(8), 1e-12, "after the last point");
            Assert.AreEqual(0, MusicAutomation.Gain(-80)); Assert.AreEqual(1, MusicAutomation.Gain(0), 1e-12);
        }

        [Test]
        public void CompileRejectsInvalidLanesAndSkipsDisabledOnes()
        {
            Assert.Throws<ArgumentException>(() => new MusicAutomation(MusicAutomationTarget.Pan, new[] { P(0, 1.5f) }, 4));
            Assert.Throws<ArgumentException>(() => new MusicAutomation(MusicAutomationTarget.VolumeDb, new[] { P(0, 30) }, 4));
            Assert.Throws<ArgumentException>(() => new MusicAutomation(MusicAutomationTarget.VolumeDb, new[] { P(5, 0) }, 4), "beyond the loop");
            Assert.Throws<ArgumentException>(() => new MusicAutomation(MusicAutomationTarget.VolumeDb, new[] { P(double.NaN, 0) }, 4));
            Assert.Throws<ArgumentException>(() => new MusicAutomation(MusicAutomationTarget.VolumeDb, Array.Empty<MusicAutomationPoint>(), 4));
            var volume = new MusicAutomationLane(MusicAutomationTarget.VolumeDb, P(0, 0));
            Assert.Throws<ArgumentException>(() => MusicAutomation.Compile(new[] { volume, new MusicAutomationLane(MusicAutomationTarget.VolumeDb, P(1, 0)) }, 4, "Bell", false));
            Assert.Throws<ArgumentException>(() => MusicAutomation.Compile(new[] { new MusicAutomationLane(MusicAutomationTarget.Pan, P(0, 0)) }, 4, "Master", true));
            Assert.IsNull(MusicAutomation.Compile(new[] { new MusicAutomationLane(MusicAutomationTarget.VolumeDb, P(9, 99)) { Bypass = true } }, 4, "Bell", false));
            Assert.AreEqual(1, MusicAutomation.Compile(new[] { volume }, 4, "Bell", false).Length);
        }

        [Test]
        public void OlderVersionsIgnoreLanesAndV3WithoutLanesIsV2()
        {
            using (var plain = MusicPlaybackScenarios.Build("Oscillators"))
            using (var automated = MusicPlaybackScenarios.Build("Automation"))
            {
                for (int version = MusicPlaybackVersion.V1; version <= MusicPlaybackVersion.V2; version++)
                    CollectionAssert.AreEqual(MusicPlaybackScenarios.Render(plain.Definition.Compile(version)),
                        MusicPlaybackScenarios.Render(automated.Definition.Compile(version)), "V" + version + " plays without automation");
                CollectionAssert.AreEqual(MusicPlaybackScenarios.Render(plain.Definition.Compile(MusicPlaybackVersion.V2)),
                    MusicPlaybackScenarios.Render(plain.Definition.Compile(MusicPlaybackVersion.V3)), "V3 without lanes = V2");
                var a = MusicPlaybackScenarios.Render(plain.Definition.Compile(MusicPlaybackVersion.V3));
                var b = MusicPlaybackScenarios.Render(automated.Definition.Compile(MusicPlaybackVersion.V3));
                double difference = 0;
                for (int i = 0; i < a.Length; i++) difference += Math.Abs(a[i] - b[i]);
                Assert.IsTrue(difference > 1, "V3 follows the lanes");
            }
        }

        [Test]
        public void SilentTrackLaneEqualsMutingAndMasterLaneSilencesTheSong()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = song.Definition;
                d.Parts[0].Automation = new[] { new MusicAutomationLane(MusicAutomationTarget.VolumeDb, P(0, -80)) };
                var silenced = MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3));
                d.Parts[0].Automation = Array.Empty<MusicAutomationLane>(); d.Parts[0].Mute = true;
                CollectionAssert.AreEqual(MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3)), silenced);
                d.Parts[0].Mute = false;
                d.MasterAutomation = new[] { new MusicAutomationLane(MusicAutomationTarget.VolumeDb, P(0, -80)) };
                foreach (float x in MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3))) Assert.AreEqual(0f, x);
            }
        }

        [Test]
        public void PanLaneReplacesTheAuthoredPan()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = song.Definition;
                d.Parts = new[] { d.Parts[1] };
                d.Parts[0].Automation = new[] { new MusicAutomationLane(MusicAutomationTarget.Pan, P(0, -1)) };
                var pcm = MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3));
                double left = 0, right = 0;
                for (int i = 0; i < pcm.Length; i += 2) { left += Math.Abs(pcm[i]); right += Math.Abs(pcm[i + 1]); }
                Assert.IsTrue(left > 1); Assert.AreEqual(0, right, 1e-9, "hard left");
            }
        }

        [Test]
        public void MashupMovesTrackAutomationWithItsLayer()
        {
            var a = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                a.name = "A"; a.Bpm = 120; a.LoopBeats = 8; a.PlaybackVersion = MusicPlaybackVersion.V3;
                a.Parts = new[] { new MusicCueDefinition.Part { Name = "Bell", Notes = new[] { new MusicNote(0, 1, 60), new MusicNote(4, 1, 62) },
                    Automation = new[] { new MusicAutomationLane(MusicAutomationTarget.VolumeDb, P(0, -24), P(8, 0)) } } };
                var m = MusicMashup.Combine(new[] { new MusicMashupLayer { Song = a, AtBeat = 2, FromBeat = 4, LengthBeats = 4, Repeat = 2 } }, "M", 12).Song;
                try
                {
                    var lane = new MusicAutomation(MusicAutomationTarget.VolumeDb, m.Parts[0].Automation[0].Points, m.LoopBeats);
                    Assert.AreEqual(-12, lane.Evaluate(2), 1e-6, "excerpt start = source beat 4");
                    Assert.AreEqual(-6, lane.Evaluate(4), 1e-6, "source beat 6");
                    Assert.AreEqual(-12, lane.Evaluate(6), 1e-6, "the repeat starts over");
                    Assert.AreEqual(-3, lane.Evaluate(9), 1e-6);
                    Assert.DoesNotThrow(() => m.Compile());
                }
                finally { UnityEngine.Object.DestroyImmediate(m); }
            }
            finally { UnityEngine.Object.DestroyImmediate(a); }
        }

        [Test]
        public void CutoffRampsMoveInOctaves()
        {
            var lane = new MusicAutomation(MusicAutomationTarget.LowPassHz, new[] { P(0, 100), P(2, 400), P(4, 0) }, 4);
            Assert.AreEqual(200, lane.Evaluate(1), 1e-9, "halfway between 100 and 400 Hz is one octave up");
            Assert.AreEqual(200, lane.Evaluate(3), 1e-9, "a ramp to 0 (off) is linear in Hz");
            Assert.AreEqual(.5, new MusicAutomation(MusicAutomationTarget.EchoWet, new[] { P(0, 0), P(2, 1) }, 4).Evaluate(1), 1e-12);
            Assert.Throws<ArgumentException>(() => new MusicAutomation(MusicAutomationTarget.HighPassHz, new[] { P(0, 2500) }, 4));
            Assert.Throws<ArgumentException>(() => new MusicAutomation(MusicAutomationTarget.ReverbMix, new[] { P(0, 1.2f) }, 4));
        }

        [Test]
        public void EffectLanesNeedTheirStage()
        {
            using (var song = MusicPlaybackScenarios.Build("TrackFx"))
            {
                var d = song.Definition;
                void Fails(int part, MusicAutomationTarget target)
                {
                    d.Parts[part].Automation = new[] { new MusicAutomationLane(target, P(0, .5f)) };
                    Assert.Throws<ArgumentException>(() => d.Compile(MusicPlaybackVersion.V3), part + " " + target);
                    d.Parts[part].Automation = Array.Empty<MusicAutomationLane>();
                }
                d.Parts[0].TrackEffects = false; Fails(0, MusicAutomationTarget.LowPassHz); d.Parts[0].TrackEffects = true;
                Fails(0, MusicAutomationTarget.ChorusMix); // Chorus off
                Fails(0, MusicAutomationTarget.ReverbMix); // Reverb off
                d.MasterAutomation = new[] { new MusicAutomationLane(MusicAutomationTarget.ReverbMix, P(0, .5f)) };
                Assert.Throws<ArgumentException>(() => d.Compile(MusicPlaybackVersion.V3), "master reverb off");
                d.MasterAutomation = Array.Empty<MusicAutomationLane>();
                Assert.DoesNotThrow(() => d.Compile(MusicPlaybackVersion.V3));
            }
        }

        [Test]
        public void ConstantEffectLanesEqualTheAuthoredValues()
        {
            using (var song = MusicPlaybackScenarios.Build("TrackFx"))
            {
                var d = song.Definition;
                d.Parts[0].Effects.Chorus = true; d.Parts[0].Effects.ChorusMix = .3f; d.Parts[0].Effects.Reverb = true; d.Parts[0].Effects.ReverbMix = .25f;
                d.Mixer.LowPassHz = 6000; d.Mixer.Reverb = true; d.Mixer.ReverbMix = .15f;
                var authored = MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3));
                var fx = d.Parts[0].Effects;
                d.Parts[0].Automation = new[] {
                    new MusicAutomationLane(MusicAutomationTarget.LowPassHz, P(0, fx.LowPassHz)), new MusicAutomationLane(MusicAutomationTarget.HighPassHz, P(0, fx.HighPassHz)),
                    new MusicAutomationLane(MusicAutomationTarget.EchoWet, P(0, fx.EchoWet)), new MusicAutomationLane(MusicAutomationTarget.ChorusMix, P(0, fx.ChorusMix)),
                    new MusicAutomationLane(MusicAutomationTarget.ReverbMix, P(0, fx.ReverbMix)) };
                d.MasterAutomation = new[] { new MusicAutomationLane(MusicAutomationTarget.LowPassHz, P(0, 6000)), new MusicAutomationLane(MusicAutomationTarget.ReverbMix, P(0, .15f)),
                    new MusicAutomationLane(MusicAutomationTarget.VolumeDb, P(0, 0)) };
                CollectionAssert.AreEqual(authored, MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3)), "a constant lane at the authored value changes nothing");
                foreach (var target in new[] { MusicAutomationTarget.LowPassHz, MusicAutomationTarget.EchoWet, MusicAutomationTarget.ChorusMix, MusicAutomationTarget.ReverbMix })
                {
                    var lanes = (MusicAutomationLane[])d.Parts[0].Automation.Clone();
                    int i = Array.FindIndex(lanes, l => l.Target == target);
                    var original = lanes[i];
                    d.Parts[0].Automation[i] = new MusicAutomationLane(target, P(0, target == MusicAutomationTarget.LowPassHz ? 500 : .9f));
                    var changed = MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3));
                    d.Parts[0].Automation[i] = original;
                    double difference = 0;
                    for (int s = 0; s < changed.Length; s++) difference += Math.Abs(changed[s] - authored[s]);
                    Assert.IsTrue(difference > .1, target + " lane reaches the track chain");
                }
            }
        }

#if UNITY_5_3_OR_NEWER
        [Test]
        public void AutomatedPlaybackAllocatesNothingInTheAudioCallback()
        {
            foreach (string scenario in new[] { "Automation", "AutomationFx" })
            using (var song = MusicPlaybackScenarios.Build(scenario))
            {
                var engine = new AdaptiveMusicEngine(song.Definition.Compile(MusicPlaybackVersion.V3), 8000);
                var pcm = new float[2000];
                for (int i = 0; i < 20; i++) engine.RenderStereo(pcm);
                long allocated = 0;
                for (int i = 0; i < 60; i++) { long before = GC.GetAllocatedBytesForCurrentThread(); engine.RenderStereo(pcm); allocated += GC.GetAllocatedBytesForCurrentThread() - before; }
                Assert.AreEqual(0L, allocated);
            }
        }
#endif
    }
}
