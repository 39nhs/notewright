using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicPluginNativeTests
    {
        static MusicPluginCatalog Catalog()
        {
            var roots = Vst3TestHost.Roots("installed instrument folders");
            var errors = new List<string>(); var result = MusicPluginCatalog.Scan(Vst3TestHost.Executable, roots, errors);
            Assert.IsTrue(result.Plugins.Count > 0, string.Join("\n", errors)); return result;
        }
        [Test]
        public void InstalledInstrumentsAcceptMidiStateAndProduceFiniteStereo()
        {
            var catalog = Catalog();
            foreach (var plugin in catalog.Plugins)
            using (var host = new MusicPluginHost(Vst3TestHost.Executable))
            {
                host.Load(plugin, 48000, 512); var state = host.CaptureState();
                var roundtrip = MusicPluginState.FromBase64(state.ToBase64()); host.Configure(roundtrip);
                double energy = 0; var pcm = new float[1024];
                for (int b = 0; b < 100; b++)
                {
                    var notes = b == 0 ? new[] { new MusicPluginMidiEvent(0, 60, .8f, 1, true) } : b == 80 ? new[] { new MusicPluginMidiEvent(0, 60, 0, 1, false) } : null;
                    host.Process(pcm, 512, b * 512, 1000, b * 512.0 * 1000 / (60 * 48000), 64, notes);
                    foreach (float value in pcm) { Assert.IsFalse(float.IsNaN(value) || float.IsInfinity(value), plugin.Identity.Name); energy += value * value; }
                }
                Assert.IsTrue(energy > .001, plugin.Identity.Name + " was silent.");
                Console.WriteLine(plugin.Identity.Name + " / " + plugin.Identity.ClassId + " energy=" + energy);
            }
        }
        [Test]
        public void RuntimeMixChangesSelectedPartAndReturnsToStockOnWorkerFailure()
        {
            var catalog = Catalog(); var plugin = catalog.Plugins[0];
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .2, "Lead", new MusicNote(0, 3, 60));
            var cue = new MusicCue("Plugin runtime", 120, 4, 4, new[] { part }, Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V2);
            var mod = new MusicPluginMod("Integration", new MusicPluginTarget("*", "Lead", plugin.Identity));
            using (var session = new MusicPluginSession(Vst3TestHost.Executable, catalog, new[] { plugin.Identity.ClassId }, mod, 48000))
            {
                session.PrepareAsync(cue).GetAwaiter().GetResult(); Assert.AreEqual(0, session.Diagnostics.Length, string.Join("\n", session.Diagnostics));
                var engine = new AdaptiveMusicEngine(cue, 48000, plugins: session); var stock = new AdaptiveMusicEngine(cue, 48000);
                var buffer = new float[2048]; var reference = new float[2048]; double difference = 0;
                for (int b = 0; b < 20; b++) { engine.RenderStereo(buffer); stock.RenderStereo(reference); for (int i = 0; i < buffer.Length; i++) difference += Math.Abs(buffer[i] - reference[i]); }
                Assert.IsTrue(difference > 1, "Plugin never replaced the stock part.");
                // Crash this session's own native child. The test process remains alive and stock voices stayed phase-correct.
                using (var child = System.Diagnostics.Process.GetProcessById(session.WorkerProcessId(cue, 0)))
                { child.Kill(); Assert.IsTrue(child.WaitForExit(2000), "Killed worker did not terminate."); }
                for (int i = 0; i < 200 && session.Failure(cue, 0) == null; i++) Thread.Sleep(10);
                Assert.IsNotNull(session.Failure(cue, 0));
                for (int b = 0; b < 4; b++) { engine.RenderStereo(buffer); stock.RenderStereo(reference); }
                CollectionAssert.AreEqual(reference, buffer);
            }
        }
        [Test]
        public void OfflineLoopsAndTempoRampsKeepBufferedAndAudioCallbackAllocatesNothing()
        {
            var catalog = Catalog(); var plugin = catalog.Plugins[0];
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .2, "Lead", new MusicNote(0, 6, 60), new MusicNote(2, .5, 67));
            var cue = new MusicCue("Ramp loop", 1000, 4, 4, new[] { part }, Array.Empty<MusicMixEffect>(),
                new[] { new MusicTempoChange { Beat = 1, Bpm = 60000, RampBeats = 1 }, new MusicTempoChange { Beat = 3, Bpm = 1000 } }, playbackVersion: MusicPlaybackVersion.V2);
            using (var session = new MusicPluginSession(Vst3TestHost.Executable, catalog, new[] { plugin.Identity.ClassId },
                new MusicPluginMod("Loops", new MusicPluginTarget("*", "Lead", plugin.Identity, mono: true)), 48000))
            {
                session.PrepareAsync(cue).GetAwaiter().GetResult(); Assert.AreEqual(0, session.Diagnostics.Length);
                var engine = new AdaptiveMusicEngine(cue, 48000, plugins: session); var buffer = new float[1024]; double energy = 0;
                engine.RenderStereo(buffer); // warm JIT
                for (int b = 1; b < 400; b++)
                {
                    session.WaitForBuffered(cue, b * 512, 512);
                    engine.RenderStereo(buffer);
                    foreach (var sample in buffer) { Assert.IsFalse(float.IsNaN(sample) || float.IsInfinity(sample)); energy += sample * sample; }
                }
                Assert.IsTrue(energy > 1);
                Assert.IsNull(session.Failure(cue, 0)); Assert.AreEqual(0, session.Underruns(cue, 0));
            }
        }
        [Test]
        public void V3ParameterAutomationFollowsTheLaneSampleAccurately()
        {
            // A parameter that changes the level (e.g. Surge XT "a_volume") makes the check plugin-independent.
            string parameter = Environment.GetEnvironmentVariable("NOTEWRIGHT_TEST_VST3_LEVEL_PARAMETER");
            if (string.IsNullOrEmpty(parameter)) Assert.Ignore("Set NOTEWRIGHT_TEST_VST3_LEVEL_PARAMETER to a level parameter of the first installed instrument.");
            var catalog = Catalog(); var plugin = catalog.Plugins[0];
            var cue = new MusicCue("Lane", 120, 4, 8, new[] { new MusicPart(MusicLayer.Harmony, new MusicInstrument(MusicVoice.Pad), .3, "Pad",
                new[] { new MusicNote(0, 7.5, 48), new MusicNote(0, 7.5, 55) }, 0) }, Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V3);
            double Rms(float[] pcm, double from, double to) { double e = 0; int a = (int)(from * 48000) * 2, b = (int)(to * 48000) * 2; for (int i = a; i < b; i++) e += pcm[i] * pcm[i]; return 10 * Math.Log10(e / (b - a) + 1e-24); }
            float[] Render(MusicPluginTarget target)
            {
                using (var session = new MusicPluginSession(Vst3TestHost.Executable, catalog, new[] { plugin.Identity.ClassId }, new MusicPluginMod("Lane", target), 48000))
                {
                    session.PrepareAsync(cue).GetAwaiter().GetResult(); Assert.AreEqual(0, session.Diagnostics.Length);
                    var engine = new AdaptiveMusicEngine(cue, 48000, plugins: session); var block = new float[1024]; var pcm = new float[48000 * 4 * 2];
                    for (int b = 0; b * 1024 < pcm.Length; b++) { session.WaitForBuffered(cue, b * 512L, 512); engine.RenderStereo(block); Array.Copy(block, 0, pcm, b * 1024, 1024); }
                    Assert.IsNull(session.Failure(cue, 0)); return pcm;
                }
            }
            var automated = Render(new MusicPluginTarget("*", "Pad", plugin.Identity, automation: new Dictionary<string, MusicAutomationPoint[]> {
                [parameter] = new[] { new MusicAutomationPoint(0, .15f, MusicAutomationCurve.Step), new MusicAutomationPoint(4, .9f) } }));
            var low = Render(new MusicPluginTarget("*", "Pad", plugin.Identity, parameters: new Dictionary<string, double> { [parameter] = .15 }));
            var high = Render(new MusicPluginTarget("*", "Pad", plugin.Identity, parameters: new Dictionary<string, double> { [parameter] = .9 }));
            Assert.AreEqual(Rms(low, .3, 1.9), Rms(automated, .3, 1.9), 1, "before beat 4 the lane holds .15");
            Assert.AreEqual(Rms(high, 2.6, 3.8), Rms(automated, 2.6, 3.8), 1, "after beat 4 the lane holds .9");
            Assert.IsTrue(Rms(high, 2.6, 3.8) - Rms(low, 2.6, 3.8) > 6, "the parameter must change the level for this test to mean anything");
        }

        [Test]
        public void ParametersAreSavedAndRepeatRestartsKeepTheSameWorkers()
        {
            var catalog = Catalog(); var plugin = catalog.Plugins[0]; string parameter;
            using (var probe = new MusicPluginHost(Vst3TestHost.Executable))
            { probe.Load(plugin); Assert.IsTrue(probe.Parameters.Count > 0); parameter = probe.Parameters[0].Id.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            var cue = new MusicCue("Repeat", 1000, 4, 4, new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .2, "Lead", new MusicNote(0, 3, 60)));
            using (var session = new MusicPluginSession(Vst3TestHost.Executable, catalog, new[] { plugin.Identity.ClassId },
                new MusicPluginMod("Repeat", new MusicPluginTarget("*", "Lead", plugin.Identity)), 48000))
            {
                session.PrepareAsync(cue).GetAwaiter().GetResult(); Assert.AreEqual(0, session.Diagnostics.Length);
                string before = session.CaptureState(cue, 0).ToBase64(); session.SetParameter(cue, 0, parameter, .77);
                Assert.AreNotEqual(before, session.CaptureState(cue, 0).ToBase64(), "Queued parameter changes must reach saved state.");
                int first = session.WorkerProcessId(cue, 0, 0), second = session.WorkerProcessId(cue, 0, 1);
                var engine = new AdaptiveMusicEngine(cue, 48000, repeatFadeSeconds: .04, plugins: session); var pcm = new float[1024];
                for (int b = 0; b < 140; b++) { engine.RenderStereo(pcm); Thread.Sleep(11); foreach (var value in pcm) Assert.IsFalse(float.IsNaN(value) || float.IsInfinity(value)); }
                Assert.IsTrue(engine.RepeatTransitions > 2); Assert.IsNull(session.Failure(cue, 0));
                Assert.AreEqual(first, session.WorkerProcessId(cue, 0, 0)); Assert.AreEqual(second, session.WorkerProcessId(cue, 0, 1));
            }
        }
        [Test]
        public void OneShotPluginNotesDoNotRestartAtLoopBoundary()
        {
            var catalog = Catalog(); var plugin = catalog.Plugins[0];
            var cue = new MusicCue("One shot", 120, 4, 4, new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .2, "Lead", new MusicNote(0, .25, 60)));
            using (var session = new MusicPluginSession(Vst3TestHost.Executable, catalog, new[] { plugin.Identity.ClassId },
                new MusicPluginMod("One shot", new MusicPluginTarget("*", "Lead", plugin.Identity)), 48000, loop: false))
            {
                session.PrepareAsync(cue).GetAwaiter().GetResult(); Assert.AreEqual(0, session.Diagnostics.Length);
                var engine = new AdaptiveMusicEngine(cue, 48000, playOnce: true, plugins: session); var pcm = new float[1024]; double tail = 0;
                for (int b = 0; b < 250; b++) { session.WaitForBuffered(cue, b * 512, 512); engine.RenderStereo(pcm); if (b > 190) foreach (var sample in pcm) tail += sample * sample; }
                Assert.IsTrue(tail < .000001, "One-shot plugin retriggered at the nominal loop."); Assert.IsNull(session.Failure(cue, 0));
            }
        }
        [Test]
        public void PreparedCueTransitionKeepsPartMixingAndReturnsFromTempoFallback()
        {
            var catalog = Catalog(); var plugin = catalog.Plugins[0];
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .2, "Lead", new MusicNote(0, 3, 60));
            var first = new MusicCue("First", 1000, 4, 4, part); var next = new MusicCue("Next", 2000, 4, 4, part);
            using (var session = new MusicPluginSession(Vst3TestHost.Executable, catalog, new[] { plugin.Identity.ClassId },
                new MusicPluginMod("Transition", new MusicPluginTarget("*", "Lead", plugin.Identity)), 48000))
            {
                session.PrepareAsync(first).GetAwaiter().GetResult(); session.PrepareAsync(next).GetAwaiter().GetResult();
                Assert.AreEqual(0, session.Diagnostics.Length);
                var engine = new AdaptiveMusicEngine(first, 48000, plugins: session); var stock = new AdaptiveMusicEngine(first, 48000); var pcm = new float[1024]; var reference = new float[1024];
                engine.TransitionTo(next, .25); stock.TransitionTo(next, .25); double difference = 0;
                for (int b = 0; b < 100; b++) { engine.RenderStereo(pcm); stock.RenderStereo(reference); Thread.Sleep(11); if (b > 80) for (int i = 0; i < pcm.Length; i++) difference += Math.Abs(pcm[i] - reference[i]); }
                Assert.AreEqual(next, engine.CurrentCue); Assert.IsFalse(engine.IsTransitioning); Assert.IsTrue(difference > 1, "Plugin did not resume after tempo transition.");
                Assert.IsNull(session.Failure(next, 0));
            }
        }
    }
}

