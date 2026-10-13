using System;
using System.Collections.Generic;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    [Serializable]
    public sealed class MusicPluginInsertParameter
    {
        public string Key;
        [Range(0, 1)] public float Value;
        [Tooltip("Normalized parameter points over cue beats. Empty = static Value.")]
        public MusicAutomationPoint[] Points = Array.Empty<MusicAutomationPoint>();
    }

    [Serializable]
    public sealed class MusicPluginInsertDefinition
    {
        public string ClassId, Name, Vendor, Version;
        public bool Bypass;
        [Range(0, 1)] public float Mix = 1;
        [Tooltip("Optional JUCE VC2! raw_state from MusicPluginState.ToBase64().")]
        [TextArea] public string State;
        public MusicPluginInsertParameter[] Parameters = Array.Empty<MusicPluginInsertParameter>();
    }

    /// <summary>Immutable insert settings. No DLL paths or self-approval in song data.</summary>
    public sealed class MusicPluginInsert
    {
        public readonly MusicPluginIdentity Plugin;
        public readonly MusicPluginState State;
        public readonly double Mix;
        public readonly IReadOnlyDictionary<string, double> Parameters;
        public readonly IReadOnlyDictionary<string, MusicAutomation> Automation;
        public MusicPluginInsert(MusicPluginInsertDefinition source)
        {
            if (source == null || !MusicTrack.Finite(source.Mix) || source.Mix < 0 || source.Mix > 1) throw new ArgumentException("Invalid insert mix.");
            Plugin = new MusicPluginIdentity(source.ClassId, source.Name, source.Vendor, source.Version);
            State = MusicPluginState.FromBase64(source.State); Mix = source.Mix;
            var values = new Dictionary<string, double>(StringComparer.Ordinal); var lanes = new Dictionary<string, MusicAutomation>(StringComparer.Ordinal);
            if ((source.Parameters?.Length ?? 0) > 128) throw new ArgumentException("At most 128 authored insert parameters.");
            foreach (var parameter in source.Parameters ?? Array.Empty<MusicPluginInsertParameter>())
            {
                if (parameter == null || string.IsNullOrWhiteSpace(parameter.Key) || !MusicTrack.Finite(parameter.Value) || parameter.Value < 0 || parameter.Value > 1 || values.ContainsKey(parameter.Key))
                    throw new ArgumentException("Insert parameters need unique keys and values 0..1.");
                values.Add(parameter.Key, parameter.Value);
                if (parameter.Points != null && parameter.Points.Length > 0)
                    lanes.Add(parameter.Key, new MusicAutomation(MusicAutomationTarget.PluginParameter, parameter.Points, MusicCue.MaxLoopBeats));
            }
            Parameters = new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(values);
            Automation = new System.Collections.ObjectModel.ReadOnlyDictionary<string, MusicAutomation>(lanes);
        }
        internal static MusicPluginInsert[] Compile(MusicPluginInsertDefinition[] definitions)
        {
            if (definitions == null || definitions.Length == 0) return Array.Empty<MusicPluginInsert>();
            if (definitions.Length > 4) throw new ArgumentException("At most four inserts per chain.");
            var result = new List<MusicPluginInsert>();
            foreach (var definition in definitions)
            {
                if (definition == null) throw new ArgumentException("Null insert definition.");
                if (!definition.Bypass && definition.Mix != 0) result.Add(new MusicPluginInsert(definition));
            }
            return result.ToArray();
        }
        internal static void CheckLoop(MusicMixerSnapshot mixer, double loop)
        {
            if (mixer == null) return;
            foreach (var insert in mixer.Inserts) foreach (var lane in insert.Automation.Values)
                if (lane.LastBeat > loop + 1e-9) throw new ArgumentException("Insert automation point outside cue loop.");
        }
    }
}
