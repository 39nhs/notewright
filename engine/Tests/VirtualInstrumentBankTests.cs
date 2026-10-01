using System;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class VirtualInstrumentBankTests
    {
        [Test]
        public void BoundInstrumentUsesVstPcmWithoutChangingNotesAndUnboundPartKeepsFallback()
        {
            var bank = ScriptableObject.CreateInstance<VirtualInstrumentBank>();
            var cue = ScriptableObject.CreateInstance<MusicCueDefinition>();
            var clip = AudioClip.Create("Rendered VST", 8000, 1, 8000, false);
            try
            {
                var pcm = new float[8000]; Array.Fill(pcm, .25f); clip.SetData(pcm, 0);
                bank.Bindings = new[] { new VirtualInstrumentBank.Binding { InstrumentId = "Keyboard-60", RootKey = 60, Sample = clip } };
                cue.name = "Binding test"; cue.InstrumentBank = bank;
                cue.Parts = new[] {
                    new MusicCueDefinition.Part { Name = "Keyboard", InstrumentId = "Keyboard-60", Notes = new[] { new MusicNote(0, 1, 64) } },
                    new MusicCueDefinition.Part { Name = "Other", InstrumentId = "Other", FallbackVoice = MusicVoice.Bass }
                };
                var result = cue.Compile();
                var engine = new AdaptiveMusicEngine(result, 8000);
                var audio = new float[2000]; engine.Render(audio);
                Assert.AreEqual(Math.Tanh(.25 * .12), audio[1000], 1e-6);
                Assert.AreEqual(60, result.GetPart(0).Instrument.RootKey);
                Assert.AreEqual(64, result.GetPart(0).GetNote(0).Key);
                Assert.AreEqual("Keyboard-60", result.GetPart(0).InstrumentId);
                Assert.AreEqual(0, result.GetPart(1).Instrument.SampleRate);
                Assert.AreEqual(MusicVoice.Bass, result.GetPart(1).Instrument.Voice);
            }
            finally { UnityEngine.Object.DestroyImmediate(cue); UnityEngine.Object.DestroyImmediate(bank); UnityEngine.Object.DestroyImmediate(clip); }
        }

        [Test]
        public void BrokenBindingFailsInsteadOfSilentlyUsingOldSound()
        {
            var bank = ScriptableObject.CreateInstance<VirtualInstrumentBank>();
            try
            {
                bank.Bindings = new[] { new VirtualInstrumentBank.Binding { InstrumentId = "Bass-40" } };
                Assert.Throws<InvalidOperationException>(() => bank.Find("Bass-40"));
                Assert.IsNull(bank.Find("Trumpet-60"));
            }
            finally { UnityEngine.Object.DestroyImmediate(bank); }
        }
    }
}
