using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// Per-voice shaping of a sample track's notes (playback V3+; older versions play the sample with Attack / Release only):
    /// a decay to a sustain level after the attack, and a resonant filter with its own envelope, key tracking and velocity brightness.
    /// </summary>
    [Serializable]
    public sealed class MusicSampleShapeSettings
    {
        [Tooltip("Seconds to fall 60 dB toward Sustain after the attack.")]
        [Range(.001f, 20)] public float DecaySeconds = .5f;
        [Tooltip("Level held after the decay (1 = no decay: the sample as recorded).")]
        [Range(0, 1)] public float Sustain = 1;
        public MusicSynthFilter Filter;
        [Range(20, 20000)] public float CutoffHz = 8000;
        [Range(0, 1)] public float Resonance = .2f;
        [Tooltip("1 = the cutoff follows the note pitch (relative to middle C).")]
        [Range(0, 1)] public float KeyTracking;
        [Tooltip("Octaves the filter envelope moves the cutoff at its peak.")]
        [Range(-8, 8)] public float EnvelopeOctaves;
        [Tooltip("Octaves darker at velocity 0 than at velocity 1.")]
        [Range(0, 4)] public float VelocityOctaves;
        [Range(.0005f, 10)] public float FilterAttack = .005f;
        [Range(.001f, 20)] public float FilterDecay = .3f;
        [Range(0, 1)] public float FilterSustain;
        [Range(.001f, 20)] public float FilterRelease = .2f;

        public bool Active => Sustain < 1 || Filter != MusicSynthFilter.Off;
        public MusicSampleShape Compile() => Active ? new MusicSampleShape(this) : null;
    }

    /// <summary>Validated <see cref="MusicSampleShapeSettings"/> for the audio thread.</summary>
    public sealed class MusicSampleShape
    {
        public readonly double Decay, Sustain, CutoffHz, Resonance, KeyTracking, EnvelopeOctaves, VelocityOctaves, FilterAttack, FilterDecay, FilterSustain, FilterRelease;
        public readonly MusicSynthFilter Filter;

        public MusicSampleShape(MusicSampleShapeSettings s)
        {
            bool In(double v, double lo, double hi) => MusicTrack.Finite(v) && v >= lo && v <= hi;
            if (!In(s.DecaySeconds, .001, 20) || !In(s.Sustain, 0, 1) || !Enum.IsDefined(typeof(MusicSynthFilter), s.Filter) || !In(s.CutoffHz, 20, 20000) || !In(s.Resonance, 0, 1) ||
                !In(s.KeyTracking, 0, 1) || !In(s.EnvelopeOctaves, -8, 8) || !In(s.VelocityOctaves, 0, 4) || !In(s.FilterAttack, .0005, 10) || !In(s.FilterDecay, .001, 20) ||
                !In(s.FilterSustain, 0, 1) || !In(s.FilterRelease, .001, 20))
                throw new ArgumentException("Invalid sample shape (decay 0.001–20 s, sustain 0–1, cutoff 20–20000 Hz, filter envelope as on the synth).");
            Decay = s.DecaySeconds; Sustain = s.Sustain; Filter = s.Filter; CutoffHz = s.CutoffHz; Resonance = s.Resonance; KeyTracking = s.KeyTracking;
            EnvelopeOctaves = s.EnvelopeOctaves; VelocityOctaves = s.VelocityOctaves; FilterAttack = s.FilterAttack; FilterDecay = s.FilterDecay; FilterSustain = s.FilterSustain; FilterRelease = s.FilterRelease;
        }
    }
}
