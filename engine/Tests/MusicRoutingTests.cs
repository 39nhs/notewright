using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicRoutingTests
    {
        static MusicBusDefinition Bus(string name, string output = "", MusicMixerSettings effects = null) =>
            new MusicBusDefinition { Name = name, Output = output, Effects = effects ?? new MusicMixerSettings() };

        static double MaxDifference(float[] a, float[] b)
        {
            double max = 0; for (int i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i])); return max;
        }

        [Test]
        public void BusesCompileFeedersFirstAndRejectBadGraphs()
        {
            var order = MusicRouting.CompileBuses(new[] { Bus("Master glue"), Bus("Verb", "Master glue"), Bus("Drums", "Master glue"), Bus("Room", "Verb") }, out var index);
            foreach (var bus in order) Assert.IsTrue(bus.Output == -1 || bus.Output > index[bus.Name], bus.Name + " must come before the bus it feeds");
            Assert.AreEqual(-1, order[index["Master glue"]].Output);
            Assert.Throws<ArgumentException>(() => MusicRouting.CompileBuses(new[] { Bus("A", "B"), Bus("B", "A") }, out _), "loop");
            Assert.Throws<ArgumentException>(() => MusicRouting.CompileBuses(new[] { Bus("A", "A") }, out _), "self loop");
            Assert.Throws<ArgumentException>(() => MusicRouting.CompileBuses(new[] { Bus("A", "Nope") }, out _), "unknown output");
            Assert.Throws<ArgumentException>(() => MusicRouting.CompileBuses(new[] { Bus("A"), Bus("A") }, out _), "duplicate");
            Assert.Throws<ArgumentException>(() => MusicRouting.CompileTrack("", new[] { new MusicSendDefinition("Nope", 0) }, index, "Bell", out _), "unknown send bus");
            Assert.Throws<ArgumentException>(() => MusicRouting.CompileTrack("Verb", new[] { new MusicSendDefinition("Verb", 0) }, index, "Bell", out _), "send to own output");
            Assert.Throws<ArgumentException>(() => MusicRouting.CompileTrack("", new[] { new MusicSendDefinition("Verb", 0), new MusicSendDefinition("Verb", -6) }, index, "Bell", out _), "two sends to one bus");
            Assert.Throws<ArgumentException>(() => MusicRouting.CompileTrack("", new[] { new MusicSendDefinition("Verb", 20) }, index, "Bell", out _), "send level");
            Assert.AreEqual(index["Drums"], MusicRouting.CompileTrack("Drums", new[] { new MusicSendDefinition("Verb", -80) }, index, "Kick", out var sends));
            Assert.IsNull(sends, "a -80 dB send is off");
        }

        [Test]
        public void OlderVersionsIgnoreRoutingAndANeutralGroupBusIsTransparent()
        {
            using (var plain = MusicPlaybackScenarios.Build("Oscillators"))
            using (var routed = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = routed.Definition;
                d.Buses = new[] { Bus("Group") };
                foreach (var part in d.Parts) part.Output = "Group";
                for (int version = MusicPlaybackVersion.V1; version <= MusicPlaybackVersion.V2; version++)
                    CollectionAssert.AreEqual(MusicPlaybackScenarios.Render(plain.Definition.Compile(version)), MusicPlaybackScenarios.Render(d.Compile(version)), "V" + version + " ignores routing");
                Assert.Less(MaxDifference(MusicPlaybackScenarios.Render(plain.Definition.Compile(MusicPlaybackVersion.V3)), MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3))), 1e-5,
                    "a group bus with neutral settings changes nothing but summing order");
            }
        }

        [Test]
        public void SendsAddACopyAndBusEffectsApplyToTheirInputs()
        {
            using (var dry = MusicPlaybackScenarios.Build("Oscillators"))
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                foreach (var s in new[] { dry.Definition, song.Definition }) s.Parts = new[] { s.Parts[1] }; // the bass alone stays clear of the limiter
                var d = song.Definition;
                d.Buses = new[] { Bus("Copy", "", new MusicMixerSettings { OutputDb = -36 }) };
                d.Parts[0].Sends = new[] { new MusicSendDefinition("Copy", 0) };
                var a = MusicPlaybackScenarios.Render(dry.Definition.Compile(MusicPlaybackVersion.V3));
                var quiet = MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3));
                Assert.Less(MaxDifference(a, quiet), 1e-2, "a -36 dB bus adds almost nothing");
                d.Buses[0].Effects.OutputDb = 0;
                var doubled = MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3));
                double ratio = Math.Sqrt(doubled.Sum(x => (double)x * x) / a.Sum(x => (double)x * x));
                Assert.AreEqual(2, ratio, .1, "a 0 dB send to a neutral bus doubles the track (before the soft limiter)");
                d.Buses[0].Effects.LowPassHz = 150;
                var filtered = MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3));
                Assert.Greater(MaxDifference(doubled, filtered), .01, "the bus chain processes what is sent to it");
            }
        }

        [Test]
        public void CodeBuiltCuesValidateBusIndexes()
        {
            var bell = new MusicInstrument(MusicVoice.Bell);
            Assert.Throws<ArgumentException>(() => new MusicCue("Bad", 120, 4, 4, new[] { new MusicPart(MusicLayer.Melody, bell, .2, "Bell", new[] { new MusicNote(0, 1, 60) }, 0, outputBus: 0) },
                Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V3), "output bus without buses");
            var mixer = new MusicMixerSettings().Compile();
            Assert.Throws<ArgumentException>(() => new MusicCue("Bad", 120, 4, 4, new[] { new MusicPart(MusicLayer.Melody, bell, .2, "Bell", new[] { new MusicNote(0, 1, 60) }, 0) },
                Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V3, buses: new[] { new MusicBus("A", mixer, 0) }), "a bus feeding itself");
            Assert.DoesNotThrow(() => new MusicCue("Good", 120, 4, 4, new[] { new MusicPart(MusicLayer.Melody, bell, .2, "Bell", new[] { new MusicNote(0, 1, 60) }, 0, sends: new[] { new MusicSend(1, .5) }) },
                Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V3, buses: new[] { new MusicBus("A", mixer, 1), new MusicBus("B", mixer) }));
        }

        [Test]
        public void MashupKeepsTheBaseSongsBusesAndDropsUnknownRoutes()
        {
            var a = ScriptableObject.CreateInstance<MusicCueDefinition>(); var b = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                a.name = "A"; a.Bpm = 120; a.LoopBeats = 4; a.PlaybackVersion = MusicPlaybackVersion.V3; a.Buses = new[] { Bus("Verb") };
                a.Parts = new[] { new MusicCueDefinition.Part { Name = "Bell", Notes = new[] { new MusicNote(0, 1, 60) }, Sends = new[] { new MusicSendDefinition("Verb", -6) } } };
                b.name = "B"; b.Bpm = 120; b.LoopBeats = 4; b.PlaybackVersion = MusicPlaybackVersion.V3; b.Buses = new[] { Bus("Drums") };
                b.Parts = new[] { new MusicCueDefinition.Part { Name = "Kick", Layer = MusicLayer.Drums, FallbackVoice = MusicVoice.Kick, Notes = new[] { new MusicNote(0, .5, 36) }, Output = "Drums",
                    Sends = new[] { new MusicSendDefinition("Verb", -3) } } };
                var result = MusicMashup.Combine(new[] { new MusicMashupLayer { Song = a }, new MusicMashupLayer { Song = b } });
                var m = result.Song;
                try
                {
                    Assert.AreEqual(1, m.Buses.Length); Assert.AreEqual("Verb", m.Buses[0].Name);
                    var kick = m.Parts.First(p => p.Name == "Kick");
                    Assert.AreEqual("", kick.Output, "B's Drums bus is not in the mashup"); Assert.AreEqual(1, kick.Sends.Length, "the Verb send matches a base bus");
                    Assert.IsTrue(result.Warnings.Any(w => w.Contains("Kick")));
                    Assert.DoesNotThrow(() => m.Compile());
                }
                finally { UnityEngine.Object.DestroyImmediate(m); }
            }
            finally { UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); }
        }

#if UNITY_5_3_OR_NEWER
        [Test]
        public void RoutedPlaybackAllocatesNothingInTheAudioCallback()
        {
            using (var song = MusicPlaybackScenarios.Build("Routing"))
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
