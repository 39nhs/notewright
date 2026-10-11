using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicRoutingAutomationTests
    {
        static MusicAutomationLane Lane(MusicAutomationTarget target, float value) => new MusicAutomationLane(target, new MusicAutomationPoint(0, value));
        static float[] Render(MusicCueDefinition song) => MusicPlaybackScenarios.Render(song.Compile(MusicPlaybackVersion.V3));
        static double Difference(float[] a, float[] b) => a.Zip(b, (x, y) => Math.Abs((double)x - y)).Sum();

        [Test]
        public void ConstantBusAndSendLanesMatchAuthoredSettings()
        {
            using (var song = MusicPlaybackScenarios.Build("Routing"))
            {
                var d = song.Definition;
                foreach (var bus in d.Buses)
                {
                    bus.Effects.Chorus = true; bus.Effects.ChorusMix = .2f;
                    bus.Effects.Reverb = true; bus.Effects.ReverbMix = .15f;
                }
                var authored = Render(d);
                foreach (var bus in d.Buses)
                    bus.Automation = new[] { Lane(MusicAutomationTarget.VolumeDb, 0), Lane(MusicAutomationTarget.LowPassHz, bus.Effects.LowPassHz),
                        Lane(MusicAutomationTarget.HighPassHz, bus.Effects.HighPassHz), Lane(MusicAutomationTarget.EchoWet, bus.Effects.EchoWet),
                        Lane(MusicAutomationTarget.ChorusMix, bus.Effects.ChorusMix), Lane(MusicAutomationTarget.ReverbMix, bus.Effects.ReverbMix) };
                foreach (var part in d.Parts)
                    for (int s = 0; s < part.Sends.Length; s++) part.Sends[s].LevelAutomation = new[] { Lane(MusicAutomationTarget.VolumeDb, part.Sends[s].LevelDb) };
                CollectionAssert.AreEqual(authored, Render(d), "constant automation at the authored values must not change PCM");
            }
        }

        [Test]
        public void BusVolumeMutesOnlyItsOutputAndSendLaneReplacesTheAuthoredLevel()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = song.Definition; d.Parts = new[] { d.Parts[1] };
                var dry = Render(d);
                d.Buses = new[] { new MusicBusDefinition { Name = "Copy" } };
                var part = d.Parts[0]; part.Sends = new[] { new MusicSendDefinition("Copy", -80, Lane(MusicAutomationTarget.VolumeDb, 0)) };
                var awake = Render(d);
                part.Sends = new[] { new MusicSendDefinition("Copy", 0) };
                CollectionAssert.AreEqual(awake, Render(d), "automation at 0 dB turns an initially off send on");
                d.Buses[0].Automation = new[] { Lane(MusicAutomationTarget.VolumeDb, -80) };
                CollectionAssert.AreEqual(dry, Render(d), "muting the shared bus preserves the dry track");
                part.Output = "Copy"; part.Sends = Array.Empty<MusicSendDefinition>();
                foreach (float sample in Render(d)) Assert.AreEqual(0f, sample, "a muted group bus silences its routed track");
            }
        }

        [Test]
        public void BusEffectLanesReachTheChainAndOlderVersionsIgnoreThem()
        {
            using (var plain = MusicPlaybackScenarios.Build("Oscillators"))
            using (var song = MusicPlaybackScenarios.Build("RoutingAutomation"))
            {
                for (int v = MusicPlaybackVersion.V1; v <= MusicPlaybackVersion.V2; v++)
                    CollectionAssert.AreEqual(MusicPlaybackScenarios.Render(plain.Definition.Compile(v)), MusicPlaybackScenarios.Render(song.Definition.Compile(v)));
                var d = song.Definition;
                var automated = Render(d);
                foreach (var bus in d.Buses) bus.Automation = Array.Empty<MusicAutomationLane>();
                Assert.Greater(Difference(automated, Render(d)), 1, "bus lanes reach playback");
                foreach (var bus in d.Buses) bus.Automation = new[] { Lane(MusicAutomationTarget.LowPassHz, 100) };
                Assert.Greater(Difference(automated, Render(d)), 1, "the bus cutoff is audible");
            }
        }

        [Test]
        public void RejectsInvalidBusAndSendAutomationIncludingCueBounds()
        {
            var bus = new MusicBusDefinition { Name = "Group" };
            void BadBus(MusicAutomationLane lane)
            {
                bus.Automation = new[] { lane };
                Assert.Throws<ArgumentException>(() => MusicRouting.CompileBuses(new[] { bus }, out _, 4));
            }
            BadBus(Lane(MusicAutomationTarget.Pan, 0));
            BadBus(Lane(MusicAutomationTarget.ReverbMix, .5f));
            BadBus(new MusicAutomationLane(MusicAutomationTarget.VolumeDb, new MusicAutomationPoint(5, 0)));
            bus.Automation = Array.Empty<MusicAutomationLane>();
            MusicRouting.CompileBuses(new[] { bus }, out var index, 4);
            void BadSend(MusicAutomationLane lane) => Assert.Throws<ArgumentException>(() =>
                MusicRouting.CompileTrack("", new[] { new MusicSendDefinition("Group", 0, lane) }, index, "Bell", out _, 4));
            BadSend(Lane(MusicAutomationTarget.Pan, 0)); BadSend(Lane(MusicAutomationTarget.VolumeDb, 13));
            BadSend(new MusicAutomationLane(MusicAutomationTarget.VolumeDb, new MusicAutomationPoint(5, 0)));
            Assert.Throws<ArgumentException>(() => MusicRouting.CompileTrack("", new[] { new MusicSendDefinition("Group", -80), new MusicSendDefinition("Group", 0) }, index, "Bell", out _, 4));
            var tooLong = new MusicAutomation(MusicAutomationTarget.VolumeDb, new[] { new MusicAutomationPoint(5, 0) }, 8);
            Assert.Throws<ArgumentException>(() => new MusicCue("Bad bus", 120, 4, 4, Array.Empty<MusicPart>(), Array.Empty<MusicMixEffect>(),
                playbackVersion: MusicPlaybackVersion.V3, buses: new[] { new MusicBus("Group", new MusicMixerSettings().Compile(), automation: new[] { tooLong }) }));
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .1, "Bell", new[] { new MusicNote(0, 1, 60) }, 0,
                sends: new[] { new MusicSend(0, 0, tooLong) });
            Assert.Throws<ArgumentException>(() => new MusicCue("Bad send", 120, 4, 4, new[] { part }, Array.Empty<MusicMixEffect>(),
                playbackVersion: MusicPlaybackVersion.V3, buses: new[] { new MusicBus("Group", new MusicMixerSettings().Compile()) }));
        }

        [Test]
        public void BypassedLanesPreserveTheAuthoredMix()
        {
            using (var song = MusicPlaybackScenarios.Build("Routing"))
            {
                var d = song.Definition; var plain = Render(d);
                foreach (var bus in d.Buses) { var lane = Lane(MusicAutomationTarget.VolumeDb, -80); lane.Bypass = true; bus.Automation = new[] { lane }; }
                foreach (var part in d.Parts)
                    for (int s = 0; s < part.Sends.Length; s++)
                    {
                        var lane = Lane(MusicAutomationTarget.VolumeDb, -80); lane.Bypass = true; part.Sends[s].LevelAutomation = new[] { lane };
                    }
                CollectionAssert.AreEqual(plain, Render(d));
            }
        }

        [Test]
        public void MashupMovesAndCopiesBusAndSendLanesWithTheExcerpt()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = song.Definition; d.PlaybackVersion = MusicPlaybackVersion.V3;
                var lane = new MusicAutomationLane(MusicAutomationTarget.VolumeDb, new MusicAutomationPoint(0, -24), new MusicAutomationPoint(8, 0));
                d.Buses = new[] { new MusicBusDefinition { Name = "Verb", Automation = new[] { lane } } };
                d.Parts[0].Sends = new[] { new MusicSendDefinition("Verb", 0, lane) };
                var mashup = MusicMashup.Combine(new[] { new MusicMashupLayer { Song = d, FromBeat = 4, LengthBeats = 4, AtBeat = 2, Repeat = 2 } }, "Excerpt", 12).Song;
                try
                {
                    var compiled = mashup.Compile();
                    Assert.AreEqual(-12, compiled.GetBus(0).VolumeAutomation.Evaluate(2), 1e-9);
                    Assert.AreEqual(-12, compiled.GetBus(0).VolumeAutomation.Evaluate(6), 1e-9, "repeated excerpt restarts the lane");
                    var send = mashup.Parts.First(p => p.Sends.Length > 0).Sends[0];
                    Assert.AreEqual(-6, new MusicAutomation(send.LevelAutomation[0].Target, send.LevelAutomation[0].Points, 12).Evaluate(4), 1e-9);
                    mashup.Buses[0].Automation[0].Points[0].Value = -80;
                    Assert.AreEqual(-24f, lane.Points[0].Value, "mashup edits cannot mutate the source");
                }
                finally { UnityEngine.Object.DestroyImmediate(mashup); }
            }
        }

        [Test]
        public void TempoRampsAndLoopsDoNotDependOnAudioBlockSize()
        {
            using (var song = MusicPlaybackScenarios.Build("RoutingAutomation"))
            {
                var cue = song.Definition.Compile(MusicPlaybackVersion.V3);
                var whole = new float[128000]; new AdaptiveMusicEngine(cue, 8000).RenderStereo(whole);
                var chunks = new float[whole.Length]; var engine = new AdaptiveMusicEngine(cue, 8000);
                int offset = 0;
                while (offset < chunks.Length)
                {
                    var block = new float[Math.Min(274, chunks.Length - offset)];
                    engine.RenderStereo(block); Array.Copy(block, 0, chunks, offset, block.Length); offset += block.Length;
                }
                CollectionAssert.AreEqual(whole, chunks, "automation follows cue beats across tempo ramps and repeated loops, not callback blocks");
            }
        }

        [Test]
        public void RoutingAutomationHasRecordedFingerprints()
        {
            var missing = new System.Text.StringBuilder();
            for (int v = MusicPlaybackVersion.V1; v <= MusicPlaybackVersion.V3; v++)
                using (var song = MusicPlaybackScenarios.Build("RoutingAutomation"))
                {
                    var actual = MusicPlaybackVersionTests.Fingerprint(MusicPlaybackScenarios.Render(song.Definition.Compile(v)));
                    string key = MusicPlaybackFingerprints.Key(v, "RoutingAutomation");
                    if (!MusicPlaybackFingerprints.Values.ContainsKey(key))
                    {
                        missing.AppendLine("            [\"" + key + "\"] = new[] { " + string.Join(", ", actual.Select(x => x.ToString("G9", System.Globalization.CultureInfo.InvariantCulture))) + " },");
                        continue;
                    }
                    var expected = MusicPlaybackFingerprints.Values[key];
                    for (int i = 0; i < actual.Length; i++) Assert.AreEqual(expected[i], actual[i], 2e-6 + 1e-5 * Math.Abs(expected[i]), key);
                }
            Assert.IsTrue(missing.Length == 0, "Record new fingerprints:\n" + missing);
        }

#if UNITY_5_3_OR_NEWER
        [Test]
        public void SerializationPreservesAutomatedAndPlainSends()
        {
            using (var song = MusicPlaybackScenarios.Build("RoutingAutomation"))
            {
                var d = song.Definition;
                d.Parts[0].Sends = new[] { new MusicSendDefinition("Verb", -6) };
                var restored = ScriptableObject.CreateInstance<MusicCueDefinition>();
                try
                {
                    JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(d), restored); restored.name = d.name;
                    Assert.AreEqual(0, restored.Parts[0].Sends[0].LevelAutomation.Length, "an unautomated send remains unautomated");
                    Assert.AreEqual(3, restored.Parts[2].Sends[0].LevelAutomation[0].Points.Length);
                    CollectionAssert.AreEqual(Render(d), Render(restored));
                }
                finally { UnityEngine.Object.DestroyImmediate(restored); }
            }
        }

        [Test]
        public void RoutingAutomationAllocatesNothingInTheAudioCallback()
        {
            using (var song = MusicPlaybackScenarios.Build("RoutingAutomation"))
            {
                var engine = new AdaptiveMusicEngine(song.Definition.Compile(MusicPlaybackVersion.V3), 8000);
                var pcm = new float[2000]; for (int i = 0; i < 20; i++) engine.RenderStereo(pcm);
                long allocated = 0;
                for (int i = 0; i < 60; i++) { long before = GC.GetAllocatedBytesForCurrentThread(); engine.RenderStereo(pcm); allocated += GC.GetAllocatedBytesForCurrentThread() - before; }
                Assert.AreEqual(0L, allocated);
            }
        }
#endif
    }
}
