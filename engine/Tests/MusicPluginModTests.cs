using System;
using System.Collections.Generic;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicPluginModTests
    {
        const string Id = "00112233445566778899AABBCCDDEEFF";
        [Test]
        public void StateAndManifestRoundTripPreserveBinaryPatchAndControls()
        {
            byte[] component = new byte[257], controller = new byte[73]; for (int i = 0; i < component.Length; i++) component[i] = (byte)i;
            for (int i = 0; i < controller.Length; i++) controller[i] = (byte)(255 - i);
            var state = new MusicPluginState(component, controller);
            var mod = new MusicPluginMod("Patch \"한글\"", new MusicPluginTarget("*", "Lead", new MusicPluginIdentity(Id, "Example Synth"), state,
                new Dictionary<string, double> { ["Vowel"] = .375 }, gain: .9, pan: -.1, transpose: 12, velocityCurve: 1.5, mono: true));
            var copy = MusicPluginMod.FromJson(mod.ToJson());
            Assert.AreEqual(mod.Name, copy.Name); Assert.AreEqual(.375, copy.Targets[0].Parameters["Vowel"]);
            Assert.AreEqual(12, copy.Targets[0].Transpose); Assert.IsTrue(copy.Targets[0].Mono);
            CollectionAssert.AreEqual(component, copy.Targets[0].State.Component); CollectionAssert.AreEqual(controller, copy.Targets[0].State.Controller);
        }
        [Test]
        public void ParameterAutomationRoundTripsAndIsValidated()
        {
            var lanes = new Dictionary<string, MusicAutomationPoint[]> {
                ["cutoff"] = new[] { new MusicAutomationPoint(0, .1f), new MusicAutomationPoint(4, .9f, MusicAutomationCurve.Smooth), new MusicAutomationPoint(6, .5f, MusicAutomationCurve.Step) },
                ["1234"] = new[] { new MusicAutomationPoint(2, 1) } };
            var mod = new MusicPluginMod("Sweep", new MusicPluginTarget("*", "Lead", new MusicPluginIdentity(Id), automation: lanes));
            var copy = MusicPluginMod.FromJson(mod.ToJson()).Targets[0].Automation;
            Assert.AreEqual(2, copy.Count);
            Assert.AreEqual(3, copy["cutoff"].Count); Assert.AreEqual(4, copy["cutoff"][1].Beat); Assert.AreEqual(.9f, copy["cutoff"][1].Value);
            Assert.AreEqual(MusicAutomationCurve.Smooth, copy["cutoff"][1].Curve); Assert.AreEqual(MusicAutomationCurve.Step, copy["cutoff"][2].Curve);
            Assert.AreEqual(0, new MusicPluginTarget("*", "Lead", new MusicPluginIdentity(Id)).Automation.Count);
            Assert.Throws<ArgumentException>(() => new MusicPluginTarget("*", "Lead", new MusicPluginIdentity(Id),
                automation: new Dictionary<string, MusicAutomationPoint[]> { ["cutoff"] = new[] { new MusicAutomationPoint(0, 1.5f) } }), "normalized values only");
            string json = "{\"format\":\"graze-plugin-mod/1\",\"name\":\"a\",\"targets\":[{\"song\":\"*\",\"instrument\":\"Lead\",\"plugin\":{\"classId\":\"" + Id + "\"},\"automation\":{\"cutoff\":";
            Assert.AreEqual(.25f, MusicPluginMod.FromJson(json + "[0,0.25,0, 8,1,0]}}]}").Targets[0].Automation["cutoff"][0].Value);
            Assert.Throws<ArgumentException>(() => MusicPluginMod.FromJson(json + "[0,0.25]}}]}"), "beat,value,curve triples");
            Assert.Throws<ArgumentException>(() => MusicPluginMod.FromJson(json + "[0,0.25,0.5]}}]}"), "integer curve");
            Assert.Throws<ArgumentException>(() => MusicAutomation.Compile(new[] { new MusicAutomationLane(MusicAutomationTarget.PluginParameter, new MusicAutomationPoint(0, .5f)) }, 4, "Lead", false),
                "plugin parameters are not song lanes");
        }

        [Test]
        public void InvalidAndExecutableManifestDataAreRejected()
        {
            Assert.Throws<ArgumentException>(() => MusicPluginMod.FromJson("{\"format\":\"graze-plugin-mod/1\",\"name\":\"a\",\"targets\":[{\"song\":\"*\",\"instrument\":\"Lead\",\"plugin\":{\"classId\":\"" + Id + "\",\"path\":\"evil.vst3\"}}]}"));
            Assert.Throws<ArgumentException>(() => MusicPluginMod.FromJson("{\"name\":\"a\",\"name\":\"b\"}"));
            Assert.Throws<ArgumentException>(() => new MusicPluginTarget("*", "Lead", new MusicPluginIdentity(Id), gain: double.NaN));
            Assert.Throws<ArgumentException>(() => new MusicPluginMod("duplicate", new MusicPluginTarget("*", "Lead", new MusicPluginIdentity(Id)), new MusicPluginTarget("*", "Lead", new MusicPluginIdentity(Id))));
            Assert.Throws<ArgumentException>(() => MusicPluginState.FromBase64(Convert.ToBase64String(new byte[16])));
        }
        [Test]
        public void UnapprovedPluginIdentityNeverResolves()
        {
            var empty = new MusicPluginCatalog();
            Assert.Throws<UnauthorizedAccessException>(() => empty.Resolve(new MusicPluginIdentity(Id), Array.Empty<string>()));
        }
        [Test]
        public void EmptyDisabledAndMissingModsPreserveV2PcmExactly()
        {
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .2, "Lead", new MusicNote(0, 3, 60));
            var cue = new MusicCue("Compatibility", 1000, 4, 4, new[] { part }, Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V2);
            var baseline = new float[24000]; new AdaptiveMusicEngine(cue, 48000).RenderStereo(baseline);
            foreach (bool disabled in new[] { false, true })
            using (var session = new MusicPluginSession("unused.exe", new MusicPluginCatalog(), Array.Empty<string>(),
                new MusicPluginMod("Missing", new MusicPluginTarget("*", "Lead", new MusicPluginIdentity(Id))), 48000))
            {
                session.Enabled = !disabled; session.PrepareAsync(cue).GetAwaiter().GetResult();
                var actual = new float[baseline.Length]; new AdaptiveMusicEngine(cue, 48000, plugins: session).RenderStereo(actual);
                CollectionAssert.AreEqual(baseline, actual);
            }
        }
    }
}
