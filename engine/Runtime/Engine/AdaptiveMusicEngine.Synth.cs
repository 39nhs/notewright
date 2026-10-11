using System;
using System.Threading;

namespace Graze.Presentation.Audio
{
    // ---- V3 built-in synth (MusicSynthPatch): per voice, unison oscillators (PolyBLEP saw/square, naive triangle, sine) + sub + noise,
    // panned per unison voice into a stereo pair, a TPT state-variable filter per channel, drive, ADSR amp envelope and one LFO.
    // Envelopes are closed-form in the voice age; filter coefficients and LFO values update every SynthControl samples.
    // State lives in a preallocated pool (one stride per voice slot): no allocation, locks or Unity calls on the audio thread. ----
    public sealed partial class AdaptiveMusicEngine
    {
        private const int SynthStride = 57, SynthControl = 16;
        // Pool layout per voice: [0..6] osc 1 phases, [7..13] osc 2 phases, 14 sub phase, 15 noise state, 16..19 filter state (L ic1, ic2, R ic1, ic2),
        // 20..22 filter a1 a2 a3, 23 filter k, 24 control countdown, 25 LFO phase, 26 amp level at release, 27 filter envelope at release,
        // 28 sample-and-hold value, 29 pitch multiplier, 30 tremolo gain.
        private const int SynOscB = 7, SynSub = 14, SynNoise = 15, SynFilter = 16, SynCoef = 20, SynCountdown = 24, SynLfoPhase = 25, SynAmpAtRelease = 26, SynFilterAtRelease = 27,
            SynHeld = 28, SynPitchMul = 29, SynTremolo = 30,
            // Mod matrix (used only when the patch has slots): 31 LFO 2 phase, 32 LFO 2 held value, 33 pulse width, 34 oscillator 2 ratio multiplier,
            // 35 pan offset, 36 level multiplier, 37 cutoff octaves.
            SynLfo2Phase = 31, SynHeld2 = 32, SynModWidth = 33, SynModOsc2 = 34, SynModPan = 35, SynModLevel = 36, SynModCutoff = 37,
            // FM / wavetable: 40..46 osc 1 modulator phases, 47..53 osc 2 modulator phases, 54 FM index now, 55 wavetable position now (control rate).
            SynFmA = 40, SynFmB = 47, SynFmIndex = 54, SynTablePos = 55, SynModFm = 38, SynModTable = 39,
            SynClickPrevious = 56; // organ key click: the previous noise sample
        private double[] _synth; // allocated on a caller thread the first time a cue with a synth part enters the engine, never replaced
        private const double SixtyDb = 6.907755278982137; // ln(1000): exponential segments fall 60 dB over their time

        private void EnsureSynth(MusicCue cue)
        {
            if (Volatile.Read(ref _synth) != null || cue == null) return;
            foreach (var part in cue.Parts)
                if (part.Instrument.Synth != null) { Volatile.Write(ref _synth, new double[VoiceCapacity * SynthStride]); return; }
        }

        private void StartSynthV3(ref Voice voice, int slot, MusicNote note)
        {
            var s = _synth; int o = slot * SynthStride;
            Array.Clear(s, o, SynthStride);
            var patch = voice.Instrument.Synth;
            // Unison phases start spread over the cycle (deterministic) so stacked voices do not start as one loud peak.
            for (int u = 0; u < patch.Unison; u++) { s[o + u] = patch.Unison == 1 ? 0 : (u * .618033988749895) % 1; s[o + SynOscB + u] = (u * .381966011250105 + .25) % 1; }
            s[o + SynNoise] = 1 + ((note.Key * 7919L + (long)(voice.StartBeat * 4096)) & 0x7fffffff) % 2147483646; // deterministic per note, 1..2^31-2
            s[o + SynPitchMul] = 1; s[o + SynTremolo] = 1; s[o + SynAmpAtRelease] = -1;
            s[o + SynModWidth] = patch.PulseWidth; s[o + SynModOsc2] = 1; s[o + SynModLevel] = 1;
            s[o + SynFmIndex] = patch.FmIndex + patch.FmEnvelope * (patch.FilterAttack > 0 ? 0 : 1); s[o + SynTablePos] = patch.WavetablePosition;
        }

        // FM (2-operator phase modulation) and wavetable oscillators; m = the modulator phase slot for this unison voice.
        private double ShapedWave(MusicSynthPatch patch, MusicSynthWave wave, double[] s, int o, int m, double phase, double dt, double width)
        {
            if (wave == MusicSynthWave.Fm)
            {
                double mod = s[o + m]; double value = Math.Sin(2 * Math.PI * phase + s[o + SynFmIndex] * Math.Sin(2 * Math.PI * mod));
                mod += dt * patch.FmRatio; if (mod >= 1) mod -= Math.Floor(mod); s[o + m] = mod;
                return value;
            }
            if (wave == MusicSynthWave.Wavetable) return patch.Table.Read(phase, s[o + SynTablePos], dt);
            return Wave(wave, phase, dt, width);
        }

        private static double Adsr(double attack, double decay, double sustain, double age)
        {
            if (age < attack) return age / attack;
            return sustain + (1 - sustain) * Math.Exp(-SixtyDb * (age - attack) / decay);
        }

        private static double Blep(double phase, double dt)
        {
            if (phase < dt) { double t = phase / dt; return t + t - t * t - 1; }
            if (phase > 1 - dt) { double t = (phase - 1) / dt; return t * t + t + t + 1; }
            return 0;
        }

        // Organ: drawbar sines over the 16' phase (the caller advances it at half the note frequency), harmonics past 0.45 × the rate left out.
        private static double OrganWave(MusicSynthPatch patch, double phase, double dt)
        {
            double sum = 0, angle = 2 * Math.PI * phase;
            for (int h = 1; h <= 16; h++) { double a = patch.OrganHarmonics[h]; if (a > 0 && h * dt < .45) sum += a * Math.Sin(h * angle); }
            return sum;
        }

        private static double Wave(MusicSynthWave wave, double phase, double dt, double width)
        {
            switch (wave)
            {
                case MusicSynthWave.Sine: return Math.Sin(2 * Math.PI * phase);
                case MusicSynthWave.Triangle: return 1 - 4 * Math.Abs(phase - .5);
                case MusicSynthWave.Square:
                    double square = phase < width ? 1 : -1;
                    double fall = phase - width; if (fall < 0) fall += 1;
                    return square + Blep(phase, dt) - Blep(fall, dt);
                default: return 2 * phase - 1 - Blep(phase, dt);
            }
        }

        private double Lfo(MusicSynthPatch patch, double phase, int o) => LfoShape(patch.LfoWave, phase, _synth[o + SynHeld]);

        private static double LfoShape(MusicLfoWave wave, double phase, double held)
        {
            switch (wave)
            {
                case MusicLfoWave.Triangle: return 1 - 4 * Math.Abs(phase - .5);
                case MusicLfoWave.Square: return phase < .5 ? 1 : -1;
                case MusicLfoWave.SawUp: return 2 * phase - 1;
                case MusicLfoWave.SawDown: return 1 - 2 * phase;
                case MusicLfoWave.SampleAndHold: return held;
                default: return Math.Sin(2 * Math.PI * phase);
            }
        }

        private void MixSynthV3(ref Voice voice, double gain, ref double left, ref double right, double pitch, double modulation, double brightness)
        {
            var patch = voice.Instrument.Synth; var s = _synth; int o = voice.Slot * SynthStride;
            double age = voice.Age;
            if (_beat + 1e-9 >= voice.EndBeat && voice.ReleaseAge < 0)
            {
                voice.ReleaseAge = age;
                s[o + SynAmpAtRelease] = Adsr(patch.AmpAttack, patch.AmpDecay, patch.AmpSustain, age);
                s[o + SynFilterAtRelease] = Adsr(patch.FilterAttack, patch.FilterDecay, patch.FilterSustain, age);
            }
            double amp;
            if (voice.ReleaseAge >= 0)
            {
                double t = age - voice.ReleaseAge;
                if (t >= patch.AmpRelease) { voice.Active = false; return; }
                amp = s[o + SynAmpAtRelease] * Math.Exp(-SixtyDb * t / patch.AmpRelease);
            }
            else
            {
                amp = Adsr(patch.AmpAttack, patch.AmpDecay, patch.AmpSustain, age);
                // A decayed pluck (sustain 0) is inaudible after twice its decay (-120 dB): free the voice early.
                if (patch.AmpSustain == 0 && age > patch.AmpAttack + 2 * patch.AmpDecay) { voice.Active = false; return; }
            }
            double bend = GlideMultiplier(ref voice);
            if (voice.GlideSeconds > 0) voice.GlideAge += 1.0 / _sampleRate;

            // LFO clock (tempo-synced cycles follow the current tempo).
            double rate = patch.LfoSyncBeats > 0 ? _bpm / 60 / patch.LfoSyncBeats : patch.LfoRateHz;
            double lfoPhase = s[o + SynLfoPhase] + rate / _sampleRate;
            if (lfoPhase >= 1)
            {
                lfoPhase -= Math.Floor(lfoPhase);
                if (patch.LfoWave == MusicLfoWave.SampleAndHold)
                {
                    double state = s[o + SynNoise]; state = (state * 48271) % 2147483647; s[o + SynNoise] = state;
                    s[o + SynHeld] = state / 1073741823.5 - 1;
                }
            }
            s[o + SynLfoPhase] = lfoPhase;
            if (patch.UsesLfo2)
            {
                double phase2 = s[o + SynLfo2Phase] + (patch.Lfo2SyncBeats > 0 ? _bpm / 60 / patch.Lfo2SyncBeats : patch.Lfo2RateHz) / _sampleRate;
                if (phase2 >= 1)
                {
                    phase2 -= Math.Floor(phase2);
                    if (patch.Lfo2Wave == MusicLfoWave.SampleAndHold) { double state = s[o + SynNoise]; state = (state * 48271) % 2147483647; s[o + SynNoise] = state; s[o + SynHeld2] = state / 1073741823.5 - 1; }
                }
                s[o + SynLfo2Phase] = phase2;
            }

            // Control rate: LFO depth, pitch multiplier, tremolo and filter coefficients.
            if (--s[o + SynCountdown] < 0)
            {
                s[o + SynCountdown] = SynthControl - 1;
                double depth = (patch.LfoFadeSeconds > 0 ? Math.Min(1, age / patch.LfoFadeSeconds) : 1) * modulation;
                double lfo = Lfo(patch, lfoPhase, o) * depth;
                s[o + SynPitchMul] = patch.LfoPitchSemitones > 0 ? Math.Pow(2, lfo * patch.LfoPitchSemitones / 12) : 1;
                s[o + SynTremolo] = 1 - patch.LfoLevel * depth * .5 * (1 - lfo); // swings between 1 and 1 - depth
                if (patch.ModCount > 0) ModMatrix(patch, ref voice, s, o, lfoPhase, amp, age, modulation);
                if (patch.UsesFm || patch.UsesTable)
                {
                    double env = voice.ReleaseAge >= 0
                        ? s[o + SynFilterAtRelease] * Math.Exp(-SixtyDb * (age - voice.ReleaseAge) / patch.FilterRelease)
                        : Adsr(patch.FilterAttack, patch.FilterDecay, patch.FilterSustain, age);
                    s[o + SynFmIndex] = Math.Max(0, patch.FmIndex + patch.FmEnvelope * env + (patch.ModCount > 0 ? s[o + SynModFm] : 0));
                    s[o + SynTablePos] = Math.Max(0, Math.Min(1, patch.WavetablePosition + (patch.ModCount > 0 ? s[o + SynModTable] : 0)));
                }
                if (patch.Filter != MusicSynthFilter.Off)
                {
                    double env = voice.ReleaseAge >= 0
                        ? s[o + SynFilterAtRelease] * Math.Exp(-SixtyDb * (age - voice.ReleaseAge) / patch.FilterRelease)
                        : Adsr(patch.FilterAttack, patch.FilterDecay, patch.FilterSustain, age);
                    double velocity = voice.Part != null && voice.Part.Gain > 0 ? Math.Min(1, voice.Gain / voice.Part.Gain) : 1;
                    double octaves = brightness + patch.KeyTracking * (voice.Key - 60) / 12 + patch.EnvelopeOctaves * env + patch.LfoFilterOctaves * lfo
                                   + patch.VelocityOctaves * (velocity - 1) + (patch.ModCount > 0 ? s[o + SynModCutoff] : 0);
                    double cutoff = Math.Max(20, Math.Min(Math.Min(20000, .45 * _sampleRate), patch.CutoffHz * Math.Pow(2, octaves)));
                    double g = Math.Tan(Math.PI * cutoff / _sampleRate), k = 2 - 1.96 * patch.Resonance;
                    double a1 = 1 / (1 + g * (g + k)), a2 = g * a1;
                    s[o + SynCoef] = a1; s[o + SynCoef + 1] = a2; s[o + SynCoef + 2] = g * a2; s[o + SynCoef + 3] = k;
                }
            }

            // Oscillators: each unison voice panned around the voice pan (V2 linear balance law).
            double dt = _frequency[voice.Key] * bend * pitch * s[o + SynPitchMul] / _sampleRate;
            double l = 0, r = 0, width = patch.ModCount > 0 ? s[o + SynModWidth] : patch.PulseWidth, panOffset = patch.ModCount > 0 ? s[o + SynModPan] : 0;
            double osc2Ratio = patch.ModCount > 0 ? patch.Osc2Ratio * s[o + SynModOsc2] : patch.Osc2Ratio;
            for (int u = 0; u < patch.Unison; u++)
            {
                double d1 = dt * patch.UnisonRatio[u], p1 = s[o + u];
                bool organ1 = patch.Osc1Wave == MusicSynthWave.Organ; if (organ1) d1 *= .5; // organ phase runs at the 16' footage
                double tone = patch.Osc1Level > 0 ? patch.Osc1Level * (organ1 ? OrganWave(patch, p1, d1) : ShapedWave(patch, patch.Osc1Wave, s, o, SynFmA + u, p1, d1, width)) : 0;
                p1 += d1; if (p1 >= 1) p1 -= Math.Floor(p1); s[o + u] = p1;
                if (patch.Osc2Level > 0)
                {
                    double d2 = dt * patch.UnisonRatio[u] * osc2Ratio, p2 = s[o + SynOscB + u];
                    bool organ2 = patch.Osc2Wave == MusicSynthWave.Organ; if (organ2) d2 *= .5;
                    tone += patch.Osc2Level * (organ2 ? OrganWave(patch, p2, d2) : ShapedWave(patch, patch.Osc2Wave, s, o, SynFmB + u, p2, d2, width));
                    p2 += d2; if (p2 >= 1) p2 -= Math.Floor(p2); s[o + SynOscB + u] = p2;
                }
                double pan = Math.Max(-1, Math.Min(1, voice.Pan + patch.UnisonPan[u] + panOffset));
                l += tone * (pan > 0 ? 1 - pan : 1); r += tone * (pan < 0 ? 1 + pan : 1);
            }
            l *= patch.UnisonGain; r *= patch.UnisonGain;
            double centre = 0;
            if (patch.SubLevel > 0)
            {
                double ps = s[o + SynSub]; centre += patch.SubLevel * Math.Sin(2 * Math.PI * ps);
                ps += dt * .5; if (ps >= 1) ps -= Math.Floor(ps); s[o + SynSub] = ps;
            }
            if (patch.NoiseLevel > 0)
            {
                double state = s[o + SynNoise]; state = (state * 48271) % 2147483647; s[o + SynNoise] = state; // Park–Miller, exact in doubles
                centre += patch.NoiseLevel * (state / 1073741823.5 - 1);
            }
            if (patch.OrganPercussion != MusicOrganPercussion.Off)
            {
                // The 16' phase of the first unison voice: the 2nd harmonic is 4 × 16', the 3rd 6 × 16'.
                double phase16 = patch.Osc1Wave == MusicSynthWave.Organ ? s[o] : s[o + SynOscB]; int h = patch.OrganPercussion == MusicOrganPercussion.Second ? 4 : 6;
                double percussion = patch.PercussionLevel * Math.Exp(-SixtyDb * age / patch.PercussionDecay);
                if (percussion > 1e-5 && h * dt * .5 < .45) centre += .5 * percussion * Math.Sin(2 * Math.PI * h * phase16);
            }
            double click = 0;
            if (patch.KeyClick > 0 && age < .01)
            {
                // Contact bounce: bright noise (first difference) at full level, not under the amp attack.
                double state = s[o + SynNoise]; state = (state * 48271) % 2147483647; s[o + SynNoise] = state;
                double noise = state / 1073741823.5 - 1, previous = s[o + SynClickPrevious]; s[o + SynClickPrevious] = noise;
                click = patch.KeyClick * .35 * (noise - previous) * Math.Exp(-age / .0015);
            }
            if (centre != 0) { double cp = Math.Max(-1, Math.Min(1, voice.Pan + panOffset)); l += centre * (cp > 0 ? 1 - cp : 1); r += centre * (cp < 0 ? 1 + cp : 1); }

            if (patch.Filter != MusicSynthFilter.Off)
            {
                double a1 = s[o + SynCoef], a2 = s[o + SynCoef + 1], a3 = s[o + SynCoef + 2], k = s[o + SynCoef + 3];
                l = Svf(patch.Filter, l, a1, a2, a3, k, s, o + SynFilter);
                r = Svf(patch.Filter, r, a1, a2, a3, k, s, o + SynFilter + 2);
            }
            var part = voice.Part;
            if (part != null && part.DriveDb > 0) { l = MusicDrive.Shape(part.DriveMode, l * part.DriveGain); r = MusicDrive.Shape(part.DriveMode, r * part.DriveGain); }
            double level = amp * s[o + SynTremolo] * voice.Gain * gain * (patch.ModCount > 0 ? s[o + SynModLevel] : 1);
            voice.Age += 1.0 / _sampleRate;
            left += l * level; right += r * level;
            if (click != 0) { double cg = voice.Gain * gain, cp = Math.Max(-1, Math.Min(1, voice.Pan + panOffset)); left += click * cg * (cp > 0 ? 1 - cp : 1); right += click * cg * (cp < 0 ? 1 + cp : 1); }
        }

        // Mod matrix at control rate: sums each slot's source × amount per destination into the voice's modulation slots.
        private void ModMatrix(MusicSynthPatch patch, ref Voice voice, double[] s, int o, double lfoPhase, double amp, double age, double modulation)
        {
            double pitch = 0, cutoff = 0, level = 0, width = 0, osc2 = 0, pan = 0, fm = 0, table = 0;
            double fade = patch.LfoFadeSeconds > 0 ? Math.Min(1, age / patch.LfoFadeSeconds) : 1;
            for (int i = 0; i < patch.ModCount; i++)
            {
                double v;
                switch (patch.ModSource[i])
                {
                    case MusicSynthModSource.Lfo1: v = LfoShape(patch.LfoWave, lfoPhase, s[o + SynHeld]) * fade; break;
                    case MusicSynthModSource.Lfo2: v = LfoShape(patch.Lfo2Wave, s[o + SynLfo2Phase], s[o + SynHeld2]); break;
                    case MusicSynthModSource.AmpEnvelope: v = amp; break;
                    case MusicSynthModSource.FilterEnvelope:
                        v = voice.ReleaseAge >= 0 ? s[o + SynFilterAtRelease] * Math.Exp(-SixtyDb * (age - voice.ReleaseAge) / patch.FilterRelease) : Adsr(patch.FilterAttack, patch.FilterDecay, patch.FilterSustain, age); break;
                    case MusicSynthModSource.Velocity: v = voice.Part != null && voice.Part.Gain > 0 ? Math.Min(1, voice.Gain / voice.Part.Gain) : 1; break;
                    case MusicSynthModSource.Key: v = (voice.Key - 60) / 60.0; break;
                    default: v = modulation; break;
                }
                double x = v * patch.ModAmount[i];
                switch (patch.ModDestination[i])
                {
                    case MusicSynthModDestination.PitchCents: pitch += x; break;
                    case MusicSynthModDestination.CutoffOctaves: cutoff += x; break;
                    case MusicSynthModDestination.Level: level += x; break;
                    case MusicSynthModDestination.PulseWidth: width += x; break;
                    case MusicSynthModDestination.Osc2Cents: osc2 += x; break;
                    case MusicSynthModDestination.FmIndex: fm += x; break;
                    case MusicSynthModDestination.WavetablePosition: table += x; break;
                    default: pan += x; break;
                }
            }
            if (pitch != 0) s[o + SynPitchMul] *= Math.Pow(2, pitch / 1200);
            s[o + SynModCutoff] = cutoff; s[o + SynModLevel] = Math.Max(0, 1 + level);
            s[o + SynModWidth] = Math.Max(.05, Math.Min(.95, patch.PulseWidth + width));
            s[o + SynModOsc2] = Math.Pow(2, osc2 / 1200); s[o + SynModPan] = Math.Max(-1, Math.Min(1, pan));
            s[o + SynModFm] = fm; s[o + SynModTable] = table;
        }

        // Topology-preserving-transform state-variable filter (Zavalishin / Simper): stable under fast cutoff modulation.
        private static double Svf(MusicSynthFilter mode, double input, double a1, double a2, double a3, double k, double[] s, int at)
        {
            double ic1 = s[at], ic2 = s[at + 1];
            double v3 = input - ic2, v1 = a1 * ic1 + a2 * v3, v2 = ic2 + a2 * ic1 + a3 * v3;
            s[at] = 2 * v1 - ic1; s[at + 1] = 2 * v2 - ic2;
            switch (mode)
            {
                case MusicSynthFilter.HighPass: return input - k * v1 - v2;
                case MusicSynthFilter.BandPass: return k * v1; // unity gain at the centre for any resonance
                default: return v2;
            }
        }
    }
}
