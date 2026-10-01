using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>Audio rendered by an external instrument host. No plugin runs on the audio thread.</summary>
    [CreateAssetMenu(menuName = "Graze/Audio/Virtual Instrument Bank")]
    public sealed class VirtualInstrumentBank : ScriptableObject
    {
        [Serializable]
        public sealed class Binding
        {
            public string InstrumentId;
            public AudioClip Sample;
            public int RootKey;
            public string Patch;
        }
        [TextArea] public string Provenance;
        public Binding[] Bindings = Array.Empty<Binding>();

        public Binding Find(string instrumentId)
        {
            Binding found = null;
            foreach (var binding in Bindings)
            {
                if (binding == null || binding.InstrumentId != instrumentId) continue;
                if (found != null) throw new InvalidOperationException("Duplicate virtual instrument: " + instrumentId);
                if (binding.Sample == null || binding.RootKey < 0 || binding.RootKey > 127)
                    throw new InvalidOperationException("Invalid virtual instrument: " + instrumentId);
                found = binding;
            }
            return found;
        }
    }
}
