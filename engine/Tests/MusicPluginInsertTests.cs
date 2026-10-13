using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicPluginInsertTests
    {
        const string Id = "0123456789ABCDEF0123456789ABCDEF";
        static MusicPluginInsertDefinition Insert(float gain = .5f, float mix = 1) => new MusicPluginInsertDefinition { ClassId = Id, Mix = mix,
            Parameters = new[] { new MusicPluginInsertParameter { Key = "gain", Value = gain } } };
        static IMusicPluginInsertProcessor Factory(MusicPluginInsert insert, int rate, int block) => new DelayGain(insert.Parameters["gain"], 17);
        sealed class DelayGain : IMusicPluginInsertProcessor
        {
            readonly float[] ring; int at; double gain;
            public int LatencySamples { get; }
            public DelayGain(double gain, int delay) { this.gain = gain; LatencySamples = delay; ring = new float[Math.Max(1, delay) * 2]; }
            public uint ParameterId(string key) => key == "gain" ? 0U : throw new ArgumentException("Unknown gain parameter.");
            public void Process(float[] input, float[] output, int frames, long firstFrame, double bpm, double beat, double loop,
                int meter, IReadOnlyList<MusicPluginParameterPoint> automation)
            {
                int point = 0;
                for (int f = 0; f < frames; f++)
                {
                    while (point < automation.Count && automation[point].Offset <= f) gain = automation[point++].Value;
                    for (int c = 0; c < 2; c++)
                    { float value = (float)(input[f * 2 + c] * gain); output[f * 2 + c] = LatencySamples == 0 ? value : ring[at * 2 + c]; ring[at * 2 + c] = value; }
                    at = (at + 1) % Math.Max(1, LatencySamples);
                }
            }
            public void Dispose() { }
        }
        static double Difference(float[] a, float[] b)
        { double max = 0; for (int i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i])); return max; }
        static MusicCueDefinition OnePart(MusicCueDefinition d)
        { d.PlaybackVersion = MusicPlaybackVersion.V3; d.Parts = new[] { d.Parts[1] }; d.Parts[0].TrackEffects = true; return d; }
        static float[] Dry(MusicCueDefinition d)
        {
            var pcm = new float[32000]; new AdaptiveMusicEngine(d.Compile(), 8000, playOnce: true).RenderStereo(pcm); return pcm;
        }

        [Test]
        public void ReleasedInstrumentCatalogSignatureRemainsAvailable()
        {
            Assert.IsNotNull(typeof(MusicPluginCatalog).GetMethod("Scan", new[] { typeof(string), typeof(IEnumerable<string>), typeof(IList<string>), typeof(int) }),
                "already compiled v0.2.4 clients must retain their catalog entry point");
        }

        [Test]
        public void TrackBusAndMasterInsertsComposeAndCompensateLatency()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = OnePart(song.Definition); d.Buses = new[] { new MusicBusDefinition { Name = "Group" } }; d.Parts[0].Output = "Group";
                d.Parts[0].Gain *= .125f; var expected = Dry(d); d.Parts[0].Gain *= 8;
                d.Parts[0].Effects.Inserts = new[] { Insert() }; d.Buses[0].Effects.Inserts = new[] { Insert() }; d.Mixer.Inserts = new[] { Insert() };
                var result = MusicPluginInsertRenderer.Prepare(d.Compile(), 8000, Factory, 2);
                Assert.AreEqual(0, result.Diagnostics.Count); Assert.Less(Difference(expected, result.CopyStereo()), 3e-6,
                    "three 17-frame delays are compensated on their nodes, including the final partial block");
            }
        }

        [Test]
        public void ADelayedIdentityInsertDoesNotShiftStereoOrTheFirstNote()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = OnePart(song.Definition); d.Parts[0].Pan = -.4f; var expected = Dry(d);
                d.Parts[0].Effects.Inserts = new[] { Insert(1) };
                var prepared = MusicPluginInsertRenderer.Prepare(d.Compile(), 8000, Factory, 2);
                Assert.Less(Difference(expected, prepared.CopyStereo()), 3e-6);
                d.Parts[0].Effects.Inserts[0].Mix = .5f;
                Assert.Less(Difference(expected, MusicPluginInsertRenderer.Prepare(d.Compile(), 8000, Factory, 2).CopyStereo()), 3e-6, "dry/wet are aligned before mixing");
            }
        }

        [Test]
        public void BypassAndOldVersionsDoNotLoadEffects()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = OnePart(song.Definition); var expected = Dry(d); d.Mixer.Inserts = new[] { Insert() }; d.Mixer.Inserts[0].Bypass = true;
                Assert.IsFalse(d.Compile().HasPluginInserts);
                CollectionAssert.AreEqual(expected, MusicPluginInsertRenderer.Prepare(d.Compile(), 8000, (i, r, b) => throw new Exception("Must not load"), 2).CopyStereo());
                d.Mixer.Inserts[0].Bypass = false; d.PlaybackVersion = MusicPlaybackVersion.V2;
                Assert.IsFalse(d.Compile().HasPluginInserts);
                Assert.DoesNotThrow(() => new AdaptiveMusicEngine(d.Compile(), 8000));
                Assert.AreEqual(0, MusicPluginInsertRenderer.Prepare(d.Compile(), 8000, (i, r, b) => throw new Exception("Must not load"), 2).Diagnostics.Count);
                d.PlaybackVersion = MusicPlaybackVersion.V3;
                Assert.Throws<InvalidOperationException>(() => new AdaptiveMusicEngine(d.Compile(), 8000), "active effects require preparation, never silently ignored");
            }
        }

        [Test]
        public void FailedEffectReturnsDryInputAndDiagnostic()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = OnePart(song.Definition); var expected = Dry(d); d.Mixer.Inserts = new[] { Insert() };
                var result = MusicPluginInsertRenderer.Prepare(d.Compile(), 8000, (i, r, b) => throw new System.IO.IOException("worker crashed"), 2);
                Assert.AreEqual(1, result.Diagnostics.Count); Assert.IsTrue(result.Diagnostics[0].Contains("worker crashed"));
                Assert.Less(Difference(expected, result.CopyStereo()), 3e-6);
            }
        }

        [Test]
        public void ParameterAutomationFollowsTheCueClock()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = OnePart(song.Definition); var insert = Insert(0);
                insert.Parameters[0].Points = new[] { new MusicAutomationPoint(0, 0, MusicAutomationCurve.Step), new MusicAutomationPoint(2, 1) };
                d.Mixer.Inserts = new[] { insert };
                var result = MusicPluginInsertRenderer.Prepare(d.Compile(), 8000, Factory, 2);
                var pcm = result.CopyStereo(); Assert.AreEqual(0, result.Diagnostics.Count);
                Assert.Less(pcm.Take(12000).Sum(x => Math.Abs(x)), 1e-8, "gain stays silent before beat 2");
                Assert.Greater(pcm.Skip(18000).Sum(x => Math.Abs(x)), 1, "gain turns on after beat 2");
            }
        }

        [Test]
        public void InvalidInsertDataAndOversizedPreparationAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new MusicPluginInsert(new MusicPluginInsertDefinition { ClassId = "bad" }));
            var insert = Insert(); insert.Parameters[0].Value = 2; Assert.Throws<ArgumentException>(() => new MusicPluginInsert(insert));
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = OnePart(song.Definition); insert = Insert(); insert.Parameters[0].Points = new[] { new MusicAutomationPoint(9, .5f) };
                d.Mixer.Inserts = new[] { insert }; Assert.Throws<ArgumentException>(() => d.Compile());
                d.Mixer.Inserts = Array.Empty<MusicPluginInsertDefinition>(); d.Bpm = 10; d.LoopBeats = 1000;
                Assert.Throws<ArgumentException>(() => MusicPluginInsertRenderer.Prepare(d.Compile(), 48000, Factory), "30 minute bound");
                Assert.Throws<ArgumentException>(() => MusicPluginInsertRenderer.Prepare(d.Compile(), 48000, Factory, 60, 32), "PCM budget");
                var cancel = new System.Threading.CancellationTokenSource(); cancel.Cancel();
                Assert.Throws<OperationCanceledException>(() => MusicPluginInsertRenderer.Prepare(d.Compile(), 8000, Factory, 1, cancellation: cancel.Token)); cancel.Dispose();
            }
        }

        [Test]
        public void PreparedReaderHandlesSeekLoopAndEndOfRange()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var prepared = MusicPluginInsertRenderer.Prepare(song.Definition.Compile(MusicPlaybackVersion.V3), 8000, Factory, 2);
                var pcm = prepared.CopyStereo(); var reader = prepared.CreateReader(10, 3); var block = new float[10]; reader.ReadStereo(block);
                CollectionAssert.AreEqual(pcm.Skip(20).Take(6).Concat(new float[4]).ToArray(), block);
                reader.Seek(0); reader.ReadStereo(block); Assert.AreEqual(3, reader.PositionFrames);
                var loop = prepared.CreateReader(10, 3, true); loop.ReadStereo(block);
                CollectionAssert.AreEqual(pcm.Skip(20).Take(6).Concat(pcm.Skip(20).Take(4)).ToArray(), block);
            }
        }

        [Test]
        public void InstalledNativeEffectReceivesStereoInputAndPreparesAllNodes()
        {
            var roots = Vst3TestHost.Roots("installed effects");
            string executable = Vst3TestHost.Executable;
            var catalog = MusicPluginCatalog.Scan(executable, roots, includeEffects: true);
            var plugin = catalog.Plugins.FirstOrDefault(p => !p.IsInstrument);
            if (plugin == null) Assert.Ignore("No installed effect found.");
            using (var host = new MusicPluginHost(executable))
            {
                host.LoadEffect(plugin, 48000, 512); host.Configure(new MusicPluginState(null));
                var input = new float[1024]; var output = new float[1024]; double energy = 0;
                for (int block = 0; block < 16; block++)
                {
                    for (int f = 0; f < 512; f++) { input[f * 2] = (float)(.1 * Math.Sin((block * 512 + f) * .05)); input[f * 2 + 1] = (float)(.05 * Math.Sin((block * 512 + f) * .08)); }
                    host.ProcessEffect(input, output, 512, block * 512, 120, block * 512 / 24000.0, 8);
                    foreach (float value in output) { Assert.IsFalse(float.IsNaN(value) || float.IsInfinity(value)); energy += value * value; }
                }
                Assert.Greater(energy, 1, "effect output is driven by input audio");
                var state = MusicPluginState.FromBase64(host.CaptureState().ToBase64());
                host.Configure(state); host.ProcessEffect(input, output, 512, 0, 120, 0, 8);
                Assert.Greater(output.Sum(x => Math.Abs(x)), .1, "effect state survives capture and restore");
                Assert.Throws<InvalidOperationException>(() => host.Process(output, 512, 0, 120, 0, 8));
            }
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = OnePart(song.Definition);
                MusicPluginInsertDefinition Fx() => new MusicPluginInsertDefinition { ClassId = plugin.Identity.ClassId, Name = plugin.Identity.Name, Vendor = plugin.Identity.Vendor, Version = plugin.Identity.Version };
                d.Parts[0].Effects.Inserts = new[] { Fx() }; d.Buses = new[] { new MusicBusDefinition { Name = "Group", Effects = new MusicMixerSettings { Inserts = new[] { Fx() } } } };
                d.Parts[0].Output = "Group"; d.Mixer.Inserts = new[] { Fx() };
                var context = new MusicPluginInsertContext(executable, catalog, new[] { plugin.Identity.ClassId });
                var result = MusicPluginInsertRenderer.PrepareAsync(d.Compile(), context, 48000, 1).GetAwaiter().GetResult();
                Assert.AreEqual(0, result.Diagnostics.Count, string.Join(";", result.Diagnostics)); Assert.Greater(result.CopyStereo().Sum(x => Math.Abs(x)), 1);
                var instrument = catalog.Plugins.FirstOrDefault(p => p.IsInstrument);
                if (instrument != null)
                {
                    var combinedContext = new MusicPluginInsertContext(executable, catalog, new[] { plugin.Identity.ClassId, instrument.Identity.ClassId });
                    var mod = new MusicPluginMod("Native instrument and inserts", new MusicPluginTarget("*", "Bass", instrument.Identity));
                    var combined = MusicPluginInsertRenderer.PrepareAsync(d.Compile(), combinedContext, 48000, 1, instruments: mod).GetAwaiter().GetResult();
                    Assert.AreEqual(0, combined.Diagnostics.Count, string.Join(";", combined.Diagnostics)); Assert.Greater(combined.CopyStereo().Sum(x => Math.Abs(x)), 1);
                }
                Assert.IsTrue(d.Compile().HasPluginInserts);
            }
        }
    }
}
