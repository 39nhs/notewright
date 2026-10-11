using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// Oscillator waveforms. Organ = tonewheel drawbars (<see cref="MusicSynthSettings.Drawbars"/>): nine sine footages from 16' to 1'.
    /// Fm = a sine carrier phase-modulated by a sine at <see cref="MusicSynthSettings.FmRatio"/> × the note (2-operator FM).
    /// Wavetable = <see cref="MusicSynthSettings.Wavetable"/> frames at <see cref="MusicSynthSettings.WavetablePosition"/>.
    /// </summary>
    public enum MusicSynthWave { Sine, Triangle, Saw, Square, Organ, Fm, Wavetable }
    public enum MusicSynthFilter { Off, LowPass, HighPass, BandPass }
    /// <summary>Hammond percussion on the Organ waveform: a decaying 4' (second) or 2⅔' (third) partial at each note start.</summary>
    public enum MusicOrganPercussion { Off, Second, Third }
    public enum MusicLfoWave { Sine, Triangle, Square, SawUp, SawDown, SampleAndHold }
    /// <summary>Mod-matrix sources: LFOs and envelopes swing -1..1 / 0..1, velocity 0..1, key (note - 60) / 60, mod wheel = the Modulation lane (0..1).</summary>
    public enum MusicSynthModSource { Lfo1, Lfo2, AmpEnvelope, FilterEnvelope, Velocity, Key, ModWheel }
    /// <summary>Mod-matrix destinations and the unit of Amount: cents, octaves, level (+1 = double), pulse width, cents (oscillator 2), pan.</summary>
    public enum MusicSynthModDestination { PitchCents, CutoffOctaves, Level, PulseWidth, Osc2Cents, Pan, FmIndex, WavetablePosition }

    [Serializable]
    public struct MusicSynthModSlot
    {
        public MusicSynthModSource Source;
        public MusicSynthModDestination Destination;
        [Tooltip("Destination units per unit of source: cents, octaves, level, pulse width, cents, pan.")]
        public float Amount;
        public MusicSynthModSlot(MusicSynthModSource source, MusicSynthModDestination destination, float amount) { Source = source; Destination = destination; Amount = amount; }
    }

    /// <summary>
    /// Built-in subtractive synth for a track (playback V3+): two band-limited oscillators plus sub and noise, unison with detune and
    /// stereo spread, an ADSR amp envelope, a resonant filter with its own ADSR, key tracking and velocity, and one LFO on pitch,
    /// filter and level. When enabled it replaces the track's sample or fallback voice in V3; older playback versions ignore it and
    /// play the track as before. Authoring data; <see cref="Compile"/> validates it.
    /// </summary>
    [Serializable]
    public sealed class MusicSynthSettings
    {
        [Tooltip("Playback V3+: play this track with the built-in synth instead of its sample or fallback voice.")]
        public bool Enabled;

        [Header("Oscillators")]
        public MusicSynthWave Osc1Wave = MusicSynthWave.Saw;
        [Range(0, 1)] public float Osc1Level = 1;
        public MusicSynthWave Osc2Wave = MusicSynthWave.Saw;
        [Range(0, 1)] public float Osc2Level;
        [Range(-24, 24)] public int Osc2Semitones;
        [Range(-100, 100)] public float Osc2Cents = 7;
        [Tooltip("Organ waveform registration: nine drawbars 0–8 for 16' 5⅓' 8' 4' 2⅔' 2' 1⅗' 1⅓' 1' (\"888000000\" = classic jazz; spaces ignored).")]
        public string Drawbars = "888000000";
        [Tooltip("Organ waveform: Hammond percussion — a decaying second (4') or third (2⅔') harmonic at each note start.")]
        public MusicOrganPercussion OrganPercussion;
        [Range(0, 1)] public float PercussionLevel = .5f;
        [Tooltip("Seconds the percussion takes to fall 60 dB (about 0.25 fast, 1 slow).")]
        [Range(.05f, 3)] public float PercussionDecaySeconds = .3f;
        [Tooltip("Organ waveform: key click — a few milliseconds of bright noise at each note start (0 = off).")]
        [Range(0, 1)] public float KeyClick;
        [Tooltip("Fm waveform: modulator frequency as a multiple of the note (1 = e-piano, 3.5 = bell, 0.5 = bass).")]
        [Range(.25f, 16)] public float FmRatio = 1;
        [Tooltip("Fm waveform: modulation index (0 = sine; higher = brighter).")]
        [Range(0, 20)] public float FmIndex = 2;
        [Tooltip("Fm waveform: index added at the filter envelope's peak (the usual way FM tones decay from bright to soft).")]
        [Range(-20, 20)] public float FmEnvelope;
        [Tooltip("Wavetable waveform: built-in table (Basic, Pulse, Harmonic, Vocal); ignored when Wavetable Clip is set.")]
        public string Wavetable = "Basic";
        [Tooltip("Wavetable waveform: frames of 2048 samples back to back (the common single-cycle wavetable format), up to 64.")]
        public UnityEngine.AudioClip WavetableClip;
        [Tooltip("Wavetable waveform: position across the frames (0..1); the mod matrix can move it.")]
        [Range(0, 1)] public float WavetablePosition;
        [Tooltip("Square-wave duty cycle for both oscillators (0.5 = square).")]
        [Range(.05f, .95f)] public float PulseWidth = .5f;
        [Tooltip("Sine one octave below the note.")]
        [Range(0, 1)] public float SubLevel;
        [Range(0, 1)] public float NoiseLevel;

        [Header("Unison")]
        [Range(1, MusicSynthPatch.MaxUnison)] public int Unison = 1;
        [Range(0, 100)] public float UnisonDetuneCents = 12;
        [Tooltip("Spreads unison voices across the stereo field around the track pan.")]
        [Range(0, 1)] public float UnisonSpread = .5f;

        [Header("Amp envelope (seconds)")]
        [Range(.0005f, 10)] public float AmpAttack = .005f;
        [Range(.001f, 20)] public float AmpDecay = .3f;
        [Range(0, 1)] public float AmpSustain = .8f;
        [Range(.001f, 20)] public float AmpRelease = .2f;

        [Header("Filter")]
        public MusicSynthFilter Filter = MusicSynthFilter.LowPass;
        [Range(20, 20000)] public float CutoffHz = 8000;
        [Range(0, 1)] public float Resonance = .2f;
        [Tooltip("1 = the cutoff follows the note pitch (relative to middle C).")]
        [Range(0, 1)] public float KeyTracking;
        [Tooltip("Octaves the filter envelope moves the cutoff at its peak (negative = downwards).")]
        [Range(-8, 8)] public float EnvelopeOctaves;
        [Tooltip("Octaves darker at velocity 0 than at velocity 1.")]
        [Range(0, 4)] public float VelocityOctaves;
        [Range(.0005f, 10)] public float FilterAttack = .005f;
        [Range(.001f, 20)] public float FilterDecay = .3f;
        [Range(0, 1)] public float FilterSustain;
        [Range(.001f, 20)] public float FilterRelease = .2f;

        [Header("LFO")]
        public MusicLfoWave LfoWave = MusicLfoWave.Sine;
        [Range(.01f, 50)] public float LfoRateHz = 5;
        [Tooltip("Tempo sync: one LFO cycle per this many beats (0 = use Rate Hz).")]
        [Range(0, 64)] public float LfoSyncBeats;
        [Tooltip("Seconds over which the LFO fades in after each note starts (delayed vibrato).")]
        [Range(0, 10)] public float LfoFadeSeconds;
        [Range(0, 2400)] public float LfoPitchCents;
        [Range(0, 8)] public float LfoFilterOctaves;
        [Tooltip("Tremolo depth (0 = none, 1 = full).")]
        [Range(0, 1)] public float LfoLevel;

        [Header("LFO 2 (a mod-matrix source)")]
        public MusicLfoWave Lfo2Wave = MusicLfoWave.Triangle;
        [Range(.01f, 50)] public float Lfo2RateHz = .5f;
        [Tooltip("Tempo sync: one cycle per this many beats (0 = use Rate Hz).")]
        [Range(0, 64)] public float Lfo2SyncBeats;

        [Header("Mod matrix")]
        [Tooltip("Up to 4 routings: source × amount → destination, on top of the fixed LFO routes.")]
        public MusicSynthModSlot[] ModMatrix = Array.Empty<MusicSynthModSlot>();

        /// <summary>Validated runtime patch, or null when disabled.</summary>
        public MusicSynthPatch Compile() => Enabled ? new MusicSynthPatch(this) : null;

        public MusicSynthSettings Clone() { var c = (MusicSynthSettings)MemberwiseClone(); c.ModMatrix = (MusicSynthModSlot[])(ModMatrix ?? Array.Empty<MusicSynthModSlot>()).Clone(); return c; }
    }

    /// <summary>Immutable, validated synth settings for the audio thread (see <see cref="MusicSynthSettings"/>).</summary>
    public sealed class MusicSynthPatch
    {
        public const int MaxUnison = 7;
        public readonly MusicSynthWave Osc1Wave, Osc2Wave;
        public readonly double Osc1Level, Osc2Level, Osc2Ratio, PulseWidth, SubLevel, NoiseLevel;
        /// <summary>Organ drawbar amplitudes by harmonic of the 16' footage (index = harmonic 1…16, 0 unused; 3 dB per drawbar step).</summary>
        internal readonly double[] OrganHarmonics = new double[17];
        public readonly double FmRatio, FmIndex, FmEnvelope, WavetablePosition;
        public readonly MusicOrganPercussion OrganPercussion;
        public readonly double PercussionLevel, PercussionDecay, KeyClick;
        /// <summary>The Wavetable oscillator's table (null unless a Wavetable waveform is used).</summary>
        public readonly MusicWavetable Table;
        internal readonly bool UsesFm, UsesTable;
        public static readonly int[] DrawbarHarmonics = { 1, 3, 2, 4, 6, 8, 10, 12, 16 }; // 16' 5⅓' 8' 4' 2⅔' 2' 1⅗' 1⅓' 1' as multiples of 16'
        /// <summary>Parses a 9-digit drawbar registration (0–8 each; spaces, dashes and | ignored) into levels.</summary>
        public static int[] ParseDrawbars(string registration)
        {
            var levels = new System.Collections.Generic.List<int>();
            foreach (char c in registration ?? "")
            {
                if (c >= '0' && c <= '8') levels.Add(c - '0');
                else if (!char.IsWhiteSpace(c) && c != '-' && c != '|') throw new ArgumentException("Invalid synth drawbars: digits 0–8 only.");
            }
            if (levels.Count != 9) throw new ArgumentException("Invalid synth drawbars: nine digits (16' to 1').");
            return levels.ToArray();
        }
        public readonly int Unison;
        public readonly double AmpAttack, AmpDecay, AmpSustain, AmpRelease;
        public readonly MusicSynthFilter Filter;
        public readonly double CutoffHz, Resonance, KeyTracking, EnvelopeOctaves, VelocityOctaves;
        public readonly double FilterAttack, FilterDecay, FilterSustain, FilterRelease;
        public readonly MusicLfoWave LfoWave;
        public readonly double LfoRateHz, LfoSyncBeats, LfoFadeSeconds, LfoPitchSemitones, LfoFilterOctaves, LfoLevel;
        public const int MaxModSlots = 4;
        public readonly MusicLfoWave Lfo2Wave;
        public readonly double Lfo2RateHz, Lfo2SyncBeats;
        internal readonly MusicSynthModSource[] ModSource; internal readonly MusicSynthModDestination[] ModDestination; internal readonly double[] ModAmount;
        public int ModCount => ModSource.Length;
        internal readonly bool UsesLfo2, UsesFilterEnvelope;
        /// <summary>Per unison voice: frequency ratio (detune) and pan offset (-1..1 times the spread).</summary>
        internal readonly double[] UnisonRatio, UnisonPan;
        internal readonly double UnisonGain;

        public MusicSynthPatch(MusicSynthSettings s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            void Check(bool ok, string what) { if (!ok) throw new ArgumentException("Invalid synth " + what + "."); }
            bool In(double v, double lo, double hi) => MusicTrack.Finite(v) && v >= lo && v <= hi;
            Check(Enum.IsDefined(typeof(MusicSynthWave), s.Osc1Wave) && Enum.IsDefined(typeof(MusicSynthWave), s.Osc2Wave), "waveform");
            Check(In(s.Osc1Level, 0, 1) && In(s.Osc2Level, 0, 1) && In(s.SubLevel, 0, 1) && In(s.NoiseLevel, 0, 1), "oscillator level (0–1)");
            Check(s.Osc2Semitones >= -24 && s.Osc2Semitones <= 24 && In(s.Osc2Cents, -100, 100), "oscillator 2 tuning");
            Check(In(s.PulseWidth, .05, .95), "pulse width (0.05–0.95)");
            Check(s.Unison >= 1 && s.Unison <= MaxUnison && In(s.UnisonDetuneCents, 0, 100) && In(s.UnisonSpread, 0, 1), "unison");
            Check(In(s.AmpAttack, .0005, 10) && In(s.AmpDecay, .001, 20) && In(s.AmpSustain, 0, 1) && In(s.AmpRelease, .001, 20), "amp envelope");
            Check(Enum.IsDefined(typeof(MusicSynthFilter), s.Filter) && In(s.CutoffHz, 20, 20000) && In(s.Resonance, 0, 1), "filter");
            Check(In(s.KeyTracking, 0, 1) && In(s.EnvelopeOctaves, -8, 8) && In(s.VelocityOctaves, 0, 4), "filter modulation");
            Check(In(s.FilterAttack, .0005, 10) && In(s.FilterDecay, .001, 20) && In(s.FilterSustain, 0, 1) && In(s.FilterRelease, .001, 20), "filter envelope");
            Check(Enum.IsDefined(typeof(MusicLfoWave), s.LfoWave) && In(s.LfoRateHz, .01, 50) && In(s.LfoSyncBeats, 0, 64) && In(s.LfoFadeSeconds, 0, 10), "LFO");
            Check(In(s.LfoPitchCents, 0, 2400) && In(s.LfoFilterOctaves, 0, 8) && In(s.LfoLevel, 0, 1), "LFO depth");
            Check(s.Osc1Level + s.Osc2Level + s.SubLevel + s.NoiseLevel > 0, "level: every source is off");
            Check(Enum.IsDefined(typeof(MusicLfoWave), s.Lfo2Wave) && In(s.Lfo2RateHz, .01, 50) && In(s.Lfo2SyncBeats, 0, 64), "LFO 2");
            var slots = s.ModMatrix ?? Array.Empty<MusicSynthModSlot>();
            Check(slots.Length <= MaxModSlots, "mod matrix (at most 4 slots)");
            // FmIndex: index units (±20); WavetablePosition: position (±1).
            foreach (var slot in slots)
            {
                Check(Enum.IsDefined(typeof(MusicSynthModSource), slot.Source) && Enum.IsDefined(typeof(MusicSynthModDestination), slot.Destination), "mod matrix routing");
                double limit = slot.Destination == MusicSynthModDestination.PitchCents || slot.Destination == MusicSynthModDestination.Osc2Cents ? 2400 :
                    slot.Destination == MusicSynthModDestination.CutoffOctaves ? 8 : slot.Destination == MusicSynthModDestination.PulseWidth ? .45 : slot.Destination == MusicSynthModDestination.Level ? 4 :
                    slot.Destination == MusicSynthModDestination.FmIndex ? 20 : 1;
                Check(In(slot.Amount, -limit, limit), $"mod matrix amount for {slot.Destination} (±{limit})");
            }
            Lfo2Wave = s.Lfo2Wave; Lfo2RateHz = s.Lfo2RateHz; Lfo2SyncBeats = s.Lfo2SyncBeats;
            var used = Array.FindAll(slots, x => x.Amount != 0);
            ModSource = Array.ConvertAll(used, x => x.Source); ModDestination = Array.ConvertAll(used, x => x.Destination); ModAmount = Array.ConvertAll(used, x => (double)x.Amount);
            UsesLfo2 = Array.IndexOf(ModSource, MusicSynthModSource.Lfo2) >= 0; UsesFilterEnvelope = Array.IndexOf(ModSource, MusicSynthModSource.FilterEnvelope) >= 0;
            Osc1Wave = s.Osc1Wave; Osc2Wave = s.Osc2Wave; Osc1Level = s.Osc1Level; Osc2Level = s.Osc2Level;
            Check(In(s.FmRatio, .25, 16) && In(s.FmIndex, 0, 20) && In(s.FmEnvelope, -20, 20) && In(s.WavetablePosition, 0, 1), "FM / wavetable setting");
            Check(Enum.IsDefined(typeof(MusicOrganPercussion), s.OrganPercussion) && In(s.PercussionLevel, 0, 1) && In(s.PercussionDecaySeconds, .05, 3) && In(s.KeyClick, 0, 1), "organ percussion / key click");
            bool organ = Osc1Wave == MusicSynthWave.Organ || Osc2Wave == MusicSynthWave.Organ;
            OrganPercussion = organ ? s.OrganPercussion : MusicOrganPercussion.Off; PercussionLevel = s.PercussionLevel; PercussionDecay = s.PercussionDecaySeconds; KeyClick = organ ? s.KeyClick : 0;
            FmRatio = s.FmRatio; FmIndex = s.FmIndex; FmEnvelope = s.FmEnvelope; WavetablePosition = s.WavetablePosition;
            UsesFm = Osc1Wave == MusicSynthWave.Fm || Osc2Wave == MusicSynthWave.Fm;
            UsesTable = Osc1Wave == MusicSynthWave.Wavetable || Osc2Wave == MusicSynthWave.Wavetable;
            if (UsesTable)
            {
                if (s.WavetableClip != null)
                {
                    var data = new float[s.WavetableClip.samples * s.WavetableClip.channels]; s.WavetableClip.GetData(data, 0);
                    var mono = new float[s.WavetableClip.samples];
                    for (int i = 0; i < mono.Length; i++) { double sum = 0; for (int c = 0; c < s.WavetableClip.channels; c++) sum += data[i * s.WavetableClip.channels + c]; mono[i] = (float)(sum / s.WavetableClip.channels); }
                    Table = MusicWavetable.FromFrames(s.WavetableClip.name, mono);
                }
                else Table = MusicWavetable.BuiltIn(string.IsNullOrWhiteSpace(s.Wavetable) ? "Basic" : s.Wavetable.Trim());
            }
            if (Osc1Wave == MusicSynthWave.Organ || Osc2Wave == MusicSynthWave.Organ)
            {
                var levels = ParseDrawbars(s.Drawbars);
                double total = 0;
                for (int i = 0; i < 9; i++) { double a = levels[i] == 0 ? 0 : Math.Pow(10, (levels[i] - 8) * 3 / 20.0); OrganHarmonics[DrawbarHarmonics[i]] += a; total += a; }
                Check(total > 0, "drawbars: all at 0");
                for (int h = 1; h <= 16; h++) OrganHarmonics[h] *= .5; // fixed scale keeps the drawbar law: all nine out ≈ 1.5 RMS
            }
            Osc2Ratio = Math.Pow(2, (s.Osc2Semitones + s.Osc2Cents / 100.0) / 12); PulseWidth = s.PulseWidth; SubLevel = s.SubLevel; NoiseLevel = s.NoiseLevel;
            Unison = s.Unison;
            AmpAttack = s.AmpAttack; AmpDecay = s.AmpDecay; AmpSustain = s.AmpSustain; AmpRelease = s.AmpRelease;
            Filter = s.Filter; CutoffHz = s.CutoffHz; Resonance = s.Resonance; KeyTracking = s.KeyTracking; EnvelopeOctaves = s.EnvelopeOctaves; VelocityOctaves = s.VelocityOctaves;
            FilterAttack = s.FilterAttack; FilterDecay = s.FilterDecay; FilterSustain = s.FilterSustain; FilterRelease = s.FilterRelease;
            LfoWave = s.LfoWave; LfoRateHz = s.LfoRateHz; LfoSyncBeats = s.LfoSyncBeats; LfoFadeSeconds = s.LfoFadeSeconds;
            LfoPitchSemitones = s.LfoPitchCents / 100.0; LfoFilterOctaves = s.LfoFilterOctaves; LfoLevel = s.LfoLevel;
            // Unison voices spread evenly from -detune to +detune (one voice = centred, no detune) and across the stereo spread.
            UnisonRatio = new double[Unison]; UnisonPan = new double[Unison];
            for (int i = 0; i < Unison; i++)
            {
                double position = Unison == 1 ? 0 : 2.0 * i / (Unison - 1) - 1;
                UnisonRatio[i] = Math.Pow(2, position * s.UnisonDetuneCents / 1200);
                UnisonPan[i] = position * s.UnisonSpread;
            }
            UnisonGain = 1 / Math.Sqrt(Unison);
        }
    }

    /// <summary>Starting points for the built-in synth (presets by name in song.json, code).</summary>
    public static class MusicSynthPresets
    {
        public static readonly string[] Names = { "Init", "Saw lead", "Square lead", "Pluck", "Warm pad", "Sub bass", "Acid bass", "Brass", "Bell keys", "Noise sweep", "Jazz organ", "Jazz organ perc", "Rock organ", "Gospel organ", "FM e-piano", "FM bell", "FM bass", "Wavetable pad", "Wavetable vox" };

        public static MusicSynthSettings Create(string name)
        {
            var s = new MusicSynthSettings { Enabled = true };
            switch (name)
            {
                case "Init": break;
                case "Saw lead":
                    s.Osc2Level = .7f; s.Osc2Cents = 9; s.Unison = 3; s.UnisonDetuneCents = 10; s.UnisonSpread = .4f;
                    s.CutoffHz = 2200; s.Resonance = .25f; s.EnvelopeOctaves = 2; s.FilterDecay = .4f; s.FilterSustain = .3f; s.KeyTracking = .5f;
                    s.LfoPitchCents = 18; s.LfoRateHz = 5.5f; s.LfoFadeSeconds = .6f; break;
                case "Square lead":
                    s.Osc1Wave = MusicSynthWave.Square; s.PulseWidth = .35f; s.Osc2Wave = MusicSynthWave.Square; s.Osc2Level = .4f; s.Osc2Semitones = 12; s.Osc2Cents = 0;
                    s.CutoffHz = 3500; s.Resonance = .15f; s.KeyTracking = .6f; s.AmpRelease = .12f; s.LfoPitchCents = 12; s.LfoFadeSeconds = .4f; break;
                case "Pluck":
                    s.Osc2Level = .5f; s.Osc2Cents = 5; s.AmpAttack = .001f; s.AmpDecay = .45f; s.AmpSustain = 0; s.AmpRelease = .25f;
                    s.CutoffHz = 600; s.Resonance = .3f; s.EnvelopeOctaves = 4; s.FilterAttack = .001f; s.FilterDecay = .18f; s.FilterSustain = 0;
                    s.VelocityOctaves = 1.5f; s.KeyTracking = .5f; break;
                case "Warm pad":
                    s.Osc2Level = .8f; s.Osc2Cents = -6; s.Unison = 5; s.UnisonDetuneCents = 18; s.UnisonSpread = .9f;
                    s.AmpAttack = .8f; s.AmpDecay = 1; s.AmpSustain = .85f; s.AmpRelease = 1.5f;
                    s.CutoffHz = 1400; s.Resonance = .1f; s.EnvelopeOctaves = 1; s.FilterAttack = 1.2f; s.FilterDecay = 2; s.FilterSustain = .5f; s.FilterRelease = 1.5f;
                    s.LfoWave = MusicLfoWave.Triangle; s.LfoRateHz = .3f; s.LfoFilterOctaves = .5f; break;
                case "Sub bass":
                    s.Osc1Wave = MusicSynthWave.Sine; s.Osc2Wave = MusicSynthWave.Triangle; s.Osc2Level = .25f; s.Osc2Semitones = 12; s.Osc2Cents = 0;
                    s.AmpAttack = .003f; s.AmpSustain = 1; s.AmpRelease = .08f; s.Filter = MusicSynthFilter.LowPass; s.CutoffHz = 900; s.Resonance = 0; break;
                case "Acid bass":
                    s.Osc1Wave = MusicSynthWave.Saw; s.AmpAttack = .002f; s.AmpDecay = .3f; s.AmpSustain = .6f; s.AmpRelease = .06f;
                    s.CutoffHz = 300; s.Resonance = .8f; s.EnvelopeOctaves = 4.5f; s.FilterAttack = .002f; s.FilterDecay = .22f; s.FilterSustain = 0; s.VelocityOctaves = 2; break;
                case "Brass":
                    s.Osc2Level = .9f; s.Osc2Cents = 4; s.Unison = 2; s.UnisonDetuneCents = 6; s.AmpAttack = .06f; s.AmpSustain = .9f; s.AmpRelease = .2f;
                    s.CutoffHz = 900; s.Resonance = .15f; s.EnvelopeOctaves = 2.5f; s.FilterAttack = .08f; s.FilterDecay = .5f; s.FilterSustain = .4f; s.VelocityOctaves = 1; s.KeyTracking = .4f;
                    s.LfoPitchCents = 8; s.LfoFadeSeconds = .5f; break;
                case "Bell keys":
                    s.Osc1Wave = MusicSynthWave.Sine; s.Osc2Wave = MusicSynthWave.Sine; s.Osc2Level = .5f; s.Osc2Semitones = 19; s.Osc2Cents = 0;
                    s.AmpAttack = .002f; s.AmpDecay = 1.6f; s.AmpSustain = 0; s.AmpRelease = 1; s.Filter = MusicSynthFilter.Off; s.LfoLevel = .1f; s.LfoRateHz = 4; break;
                case "FM e-piano":
                    s.Osc1Wave = MusicSynthWave.Fm; s.FmRatio = 1; s.FmIndex = .6f; s.FmEnvelope = 3.5f; s.FilterAttack = .001f; s.FilterDecay = 1.2f; s.FilterSustain = 0;
                    s.AmpAttack = .002f; s.AmpDecay = 2.5f; s.AmpSustain = .25f; s.AmpRelease = .35f; s.CutoffHz = 14000; s.EnvelopeOctaves = 0; break;
                case "FM bell":
                    s.Osc1Wave = MusicSynthWave.Fm; s.FmRatio = 3.5f; s.FmIndex = 1.5f; s.FmEnvelope = 4; s.FilterAttack = .001f; s.FilterDecay = 1.5f; s.FilterSustain = 0;
                    s.AmpAttack = .002f; s.AmpDecay = 3; s.AmpSustain = 0; s.AmpRelease = 1.5f; s.CutoffHz = 16000; s.EnvelopeOctaves = 0; break;
                case "FM bass":
                    s.Osc1Wave = MusicSynthWave.Fm; s.FmRatio = 1; s.FmIndex = 1.2f; s.FmEnvelope = 4; s.FilterAttack = .001f; s.FilterDecay = .25f; s.FilterSustain = .1f;
                    s.AmpAttack = .002f; s.AmpDecay = .6f; s.AmpSustain = .6f; s.AmpRelease = .08f; s.CutoffHz = 9000; s.EnvelopeOctaves = 0; break;
                case "Wavetable pad":
                    s.Osc1Wave = MusicSynthWave.Wavetable; s.Wavetable = "Harmonic"; s.WavetablePosition = .2f; s.Unison = 3; s.UnisonDetuneCents = 14; s.AmpAttack = .6f; s.AmpRelease = 1.2f;
                    s.Lfo2SyncBeats = 8; s.Lfo2Wave = MusicLfoWave.Triangle; s.ModMatrix = new[] { new MusicSynthModSlot(MusicSynthModSource.Lfo2, MusicSynthModDestination.WavetablePosition, .5f) };
                    s.CutoffHz = 9000; break;
                case "Wavetable vox":
                    s.Osc1Wave = MusicSynthWave.Wavetable; s.Wavetable = "Vocal"; s.AmpAttack = .03f; s.AmpRelease = .3f; s.CutoffHz = 12000;
                    s.ModMatrix = new[] { new MusicSynthModSlot(MusicSynthModSource.FilterEnvelope, MusicSynthModDestination.WavetablePosition, 1) }; s.FilterAttack = .4f; s.FilterDecay = .8f; s.FilterSustain = .3f; break;
                case "Jazz organ":
                    s.Osc1Wave = MusicSynthWave.Organ; s.Drawbars = "888000000"; s.AmpAttack = .004f; s.AmpDecay = .1f; s.AmpSustain = 1; s.AmpRelease = .03f;
                    s.CutoffHz = 12000; s.EnvelopeOctaves = 0; break;
                case "Jazz organ perc":
                    s.Osc1Wave = MusicSynthWave.Organ; s.Drawbars = "888000000"; s.OrganPercussion = MusicOrganPercussion.Third; s.PercussionLevel = .55f; s.PercussionDecaySeconds = .25f; s.KeyClick = .35f;
                    s.AmpAttack = .002f; s.AmpDecay = .1f; s.AmpSustain = 1; s.AmpRelease = .03f; s.CutoffHz = 12000; s.EnvelopeOctaves = 0; break;
                case "Rock organ":
                    s.Osc1Wave = MusicSynthWave.Organ; s.Drawbars = "888888888"; s.AmpAttack = .004f; s.AmpDecay = .1f; s.AmpSustain = 1; s.AmpRelease = .03f;
                    s.CutoffHz = 9000; s.EnvelopeOctaves = 0; break;
                case "Gospel organ":
                    s.Osc1Wave = MusicSynthWave.Organ; s.Drawbars = "868000006"; s.AmpAttack = .004f; s.AmpDecay = .1f; s.AmpSustain = 1; s.AmpRelease = .05f;
                    s.CutoffHz = 12000; s.EnvelopeOctaves = 0; break;
                case "Noise sweep":
                    s.Osc1Level = 0; s.NoiseLevel = 1; s.Filter = MusicSynthFilter.BandPass; s.CutoffHz = 400; s.Resonance = .6f; s.EnvelopeOctaves = 5;
                    s.AmpAttack = .5f; s.AmpSustain = 1; s.AmpRelease = 1; s.FilterAttack = 2; s.FilterSustain = 1; break;
                default: throw new ArgumentException("Unknown synth preset: " + name);
            }
            return s;
        }
    }
}
