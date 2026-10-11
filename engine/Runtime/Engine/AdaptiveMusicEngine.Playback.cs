using System;
using System.Threading;

namespace Graze.Presentation.Audio
{
    // Playback-version dispatch and the per-version sound code (voice start, voice render, pan, master chain).
    // A released version's methods are frozen: to change the sound, add a version and new methods beside them.
    // See MusicPlaybackVersion and engine/playback-versions.md. Audio-thread rules apply to everything here.
    public sealed partial class AdaptiveMusicEngine
    {
        // Returns the voice slot, or -1 when no new voice started (silent note, missing slice, glide takeover, voice limit).
        private int StartVoice(int deckId, MusicCue cue, MusicPart part, MusicNote note, double start, double elapsedBeats = 0)
        {
            if (note.Velocity == 0 || part.Gain == 0) return -1;
            int version = cue.PlaybackVersionOf(part);
            if (note.Articulation < 0 && part.Regions != null && version >= MusicPlaybackVersion.V3) return StartRegionV3(deckId, cue, part, note, start, elapsedBeats);
            bool sliced = version >= MusicPlaybackVersion.V2 && part.Instrument.SliceStarts != null;
            if (sliced && (note.Key - part.Instrument.RootKey < 0 || note.Key - part.Instrument.RootKey >= part.Instrument.SliceStarts.Length)) return -1; // no slice on this key
            if (!sliced && version >= MusicPlaybackVersion.V2 && part.GlideSeconds > 0 && deckId >= 0 && GlideV2(deckId, part, note, start)) return -1;
            bool takes = version >= MusicPlaybackVersion.V2 && part.Instrument.Takes != null;
            // Polyphony follows the cue (a track pin changes only the voice's sound): a V1 cue searches only the first 128 slots, as before.
            int limit = MusicPlaybackVersion.VoiceLimit(cue.PlaybackVersion);
            for (int i = 0; i < limit; i++)
            {
                if (_voices[i].Active) continue;
                if (i >= _voiceEnd) _voiceEnd = i + 1;
                _voices[i] = new Voice { Active = true, Deck = deckId, Layer = part.Layer, Key = note.Key, Instrument = part.Instrument,
                    StartBeat = start - elapsedBeats, EndBeat = start + note.Duration, ReleaseAge = -1, Version = version, Part = part, TakeGain = 1,
                    Bus = version >= MusicPlaybackVersion.V2 ? TrackBus(part) : null }; // a bus exists only for V2 effects, V3 routing or a V3 sidechain key
                _voices[i].PartIndex = cue.HasPartAutomation ? Array.IndexOf(cue.Parts, part) : -1;
                switch (version)
                {
                    case MusicPlaybackVersion.V1:
                    case MusicPlaybackVersion.V2: // a V2 voice starts like V1 (V2 = more voices + glide)
                    case MusicPlaybackVersion.V3: // V3 = V2 + automation
                    default: // MusicCue and MusicPart reject versions this engine does not know.
                        StartVoiceV1(ref _voices[i], part, note, elapsedBeats); break;
                }
                // V3: a sample note entered part-way (preview from mid-song, a positioned transition) starts where the tempo map puts it;
                // V1/V2 assume the current tempo throughout. Matters for long samples such as frozen tracks in songs with tempo changes.
                if (version >= MusicPlaybackVersion.V3 && elapsedBeats > 0 && part.Instrument.Samples != null)
                    _voices[i].SamplePosition = (cue.Tempo.SecondsAt(note.Beat + elapsedBeats) - cue.Tempo.SecondsAt(note.Beat)) * part.Instrument.SampleRate;
                if (sliced) StartSliceV2(ref _voices[i], part, note, elapsedBeats);
                if (takes) StartTakeV2(ref _voices[i], part, note, elapsedBeats);
                if (version >= MusicPlaybackVersion.V3 && part.Instrument.Zones != null) StartZoneV3(ref _voices[i], cue, part, note, elapsedBeats);
                if (version >= MusicPlaybackVersion.V3) StartExpressionV3(ref _voices[i], part, note, start);
                if (version >= MusicPlaybackVersion.V3 && part.SampleShape != null && _voices[i].Instrument.Samples != null && _voices[i].Instrument.Noise == null)
                { _voices[i].Shape = part.SampleShape; _voices[i].ShEnvAtRelease = -1; }
                if (version >= MusicPlaybackVersion.V3 && part.StretchBpm > 0 && _voices[i].Instrument.Samples != null && _voices[i].Instrument.Noise == null)
                    StartStretchV3(ref _voices[i], part, note, sliced);
                if (version >= MusicPlaybackVersion.V3 && part.Instrument.Synth != null && _synth != null)
                { _voices[i].Synth = true; _voices[i].Slot = i; StartSynthV3(ref _voices[i], i, note); }
                return i;
            }
            Interlocked.Increment(ref _droppedNotes); // bounded work; never steal an audible voice abruptly
            return -1;
        }

        private void MixVoice(ref Voice voice, double gain, ref double left, ref double right)
        {
            switch (voice.Version)
            {
                case MusicPlaybackVersion.V3: MixVoiceV3(ref voice, gain, ref left, ref right); break;
                case MusicPlaybackVersion.V2: MixVoiceV2(ref voice, gain, ref left, ref right); break;
                case MusicPlaybackVersion.V1:
                default: MixVoiceV1(ref voice, gain, ref left, ref right); break;
            }
        }

        // The master chain follows the audible (current) cue, like its mixer settings and glitch regions.
        private void MixMaster(double left, double right, double localBeat, out float outLeft, out float outRight)
        {
            // V3 tempo-synced echo follows the current tempo; every other cue keeps its fixed echo time (no override).
            _masterMixer.DelayOverride = _currentCue.PlaybackVersion >= MusicPlaybackVersion.V3 && _currentCue.Mixer.EchoBeats > 0 ? _currentCue.Mixer.EchoDelayAt(_bpm) : -1;
            switch (_currentCue.PlaybackVersion)
            {
                case MusicPlaybackVersion.V3: MixMasterV3(left, right, localBeat, out outLeft, out outRight); break;
                case MusicPlaybackVersion.V2: MixMasterV2(left, right, localBeat, out outLeft, out outRight); break;
                case MusicPlaybackVersion.V1:
                default: MixMasterV1(left, right, localBeat, out outLeft, out outRight); break;
            }
        }

        // ---- V1: com.graze.music 0.1.0. Frozen. V2 voices start like V1; V2 renders with RenderVoiceV2 below. ----
        // Also V1: MusicCueDefinition.Compile (sample downmix), MusicNoiseRegion.Compile (generated noise), NoisePlayback,
        // MusicMixerSnapshot, MusicMasterMixer and MusicMixProcessor.

        private void StartVoiceV1(ref Voice voice, MusicPart part, MusicNote note, double elapsedBeats)
        {
            voice.Pan = part.Pan; voice.Gain = part.Gain * note.Velocity;
            voice.SamplePosition = elapsedBeats * 60 / _bpm * part.Instrument.SampleRate;
            voice.SampleStep = part.Instrument.SampleRate / (double)_sampleRate *
                (_frequency[note.Key] / _frequency[part.Instrument.RootKey]);
        }

        // Linear balance pan.
        private void MixVoiceV1(ref Voice voice, double gain, ref double left, ref double right)
        {
            double signal = RenderVoiceV1(ref voice) * gain;
            left += signal * (voice.Pan > 0 ? 1 - voice.Pan : 1);
            right += signal * (voice.Pan < 0 ? 1 + voice.Pan : 1);
        }

        private double RenderVoiceV1(ref Voice voice)
        {
            var instrument = voice.Instrument;
            if (_beat + 1e-9 >= voice.EndBeat && voice.ReleaseAge < 0) voice.ReleaseAge = voice.Age;
            double attack = Math.Min(1, voice.Age / instrument.Attack);
            double release = voice.ReleaseAge < 0 ? 1 : Math.Max(0, 1 - (voice.Age - voice.ReleaseAge) / instrument.Release);
            if (release <= 0) { voice.Active = false; return 0; }
            if (instrument.Noise != null)
            {
                var noise = instrument.Noise;
                double elapsed = Math.Max(0, _beat - voice.StartBeat), remaining = Math.Max(0, voice.EndBeat - _beat);
                double fadeIn = noise.FadeInBeats > 0 ? Smooth(Math.Min(1, elapsed / noise.FadeInBeats)) : 1;
                double fadeOut = noise.FadeOutBeats > 0 ? Smooth(Math.Min(1, remaining / noise.FadeOutBeats)) : 1;
                release *= fadeIn * fadeOut * Math.Min(1, remaining * 60 / _bpm / .005);
                if (_beat + 1e-9 >= voice.EndBeat) { voice.Active = false; return 0; }
            }
            double tone;
            if (instrument.Samples != null)
            {
                bool noiseLoop = instrument.Noise != null && instrument.Noise.LoopSource;
                int cross = noiseLoop ? Math.Min(instrument.Samples.Length / 4, (int)(instrument.SampleRate * .005)) : 0;
                if (noiseLoop && voice.SamplePosition >= instrument.Samples.Length - 1)
                    voice.SamplePosition = cross + (voice.SamplePosition - (instrument.Samples.Length - 1)) % (instrument.Samples.Length - 1 - cross);
                int index = (int)voice.SamplePosition;
                if (index >= instrument.Samples.Length - 1) { voice.Active = false; return 0; }
                double fraction = voice.SamplePosition - index;
                tone = instrument.Samples[index] * (1 - fraction) + instrument.Samples[index + 1] * fraction;
                // Short end taper avoids hard cuts in a one-shot source sample.
                if (noiseLoop && cross > 0 && voice.SamplePosition >= instrument.Samples.Length - 1 - cross)
                {
                    double head = voice.SamplePosition - (instrument.Samples.Length - 1 - cross);
                    int h = (int)head; double blend = head / cross;
                    double first = instrument.Samples[h] * (1 - (head - h)) + instrument.Samples[h + 1] * (head - h);
                    tone = tone * (1 - blend) + first * blend;
                }
                else if (!noiseLoop) tone *= Math.Min(1, (instrument.Samples.Length - 1 - voice.SamplePosition) / (instrument.SampleRate * .005));
                var glitch = instrument.Noise;
                double elapsed = Math.Max(0, _beat - voice.StartBeat);
                double duration = voice.EndBeat - voice.StartBeat;
                if (glitch != null) tone *= glitch.Gate(elapsed, duration);
                voice.SamplePosition += voice.SampleStep * (glitch != null ? glitch.Speed(Math.Min(1, elapsed / duration)) : 1);
            }
            else
            {
                switch (instrument.Voice)
                {
                    case MusicVoice.Bell: tone = (Math.Sin(voice.Phase) + .25 * Math.Sin(voice.Phase * 2)) * Math.Exp(-voice.Age * 3); break;
                    case MusicVoice.Bass: tone = Math.Sin(voice.Phase) + .2 * Math.Sin(voice.Phase * 2); break;
                    case MusicVoice.Kick: tone = Math.Sin(2 * Math.PI * (45 * voice.Age + 8 * (1 - Math.Exp(-voice.Age * 20)))) * Math.Exp(-voice.Age * 16); break;
                    default: tone = .5 * (Math.Sin(voice.Phase) + Math.Sin(voice.Phase * 1.5)); break;
                }
                voice.Phase += 2 * Math.PI * _frequency[voice.Key] / _sampleRate;
            }
            voice.Age += 1.0 / _sampleRate;
            return tone * attack * release * voice.Gain;
        }

        // Master mixer → startup ramp → tanh soft limiter → whole-mix glitches.
        private void MixMasterV1(double sum, double sumRight, double localBeat, out float left, out float right)
        {
            _masterMixer.Process(ref sum, ref sumRight, _currentCue.Mixer, localBeat);
            double startup = Math.Min(1, _samples / (_sampleRate * .02));
            left = _mixProcessor.Process((float)Math.Tanh(sum * startup), _currentCue, localBeat, _bpm, _decks[_current].Repeated, _playOnce);
            right = _mixRight.Process((float)Math.Tanh(sumRight * startup), _currentCue, localBeat, _bpm, _decks[_current].Repeated, _playOnce);
        }

        // ---- V2: 1024 voices + track glide. Voices start like V1; RenderVoiceV2 is RenderVoiceV1 with the glide pitch multiplier
        //      (bit-identical to V1 for a voice that never glides). ----

        /// <summary>
        /// Legato take-over: the most recently started voice of this part and deck that is still held (started earlier, not
        /// released, ends after this note starts) slides from its current pitch to the new key over part.GlideSeconds.
        /// Age and sample position continue (no new attack). Returns false when there is no such voice, so the note starts normally.
        /// </summary>
        private bool GlideV2(int deckId, MusicPart part, MusicNote note, double start)
        {
            int best = -1;
            for (int i = 0; i < _voiceEnd; i++)
            {
                ref var v = ref _voices[i];
                if (!v.Active || v.Deck != deckId || v.Part != part || v.Version < MusicPlaybackVersion.V2 || v.ReleaseAge >= 0 ||
                    v.Instrument.Noise != null || v.StartBeat >= start - 1e-9 || v.EndBeat <= start + 1e-9) continue;
                if (best < 0 || v.StartBeat > _voices[best].StartBeat) best = i;
            }
            if (best < 0) return false;
            ref var voice = ref _voices[best];
            double from = _frequency[voice.Key] * GlideMultiplier(ref voice);
            voice.Key = note.Key;
            voice.GlideRatio = from / _frequency[note.Key];
            voice.GlideAge = 0; voice.GlideSeconds = part.GlideSeconds;
            voice.EndBeat = start + note.Duration;
            voice.Gain = part.Gain * note.Velocity * voice.TakeGain; // legato keeps the take (velocity layer) it started with
            if (voice.Instrument.Samples != null)
                voice.SampleStep = voice.Instrument.SampleRate / (double)_sampleRate * (_frequency[note.Key] / _frequency[voice.Instrument.RootKey]);
            return true;
        }

        /// <summary>
        /// V2 velocity layers and round robin: the note's velocity picks the layer, then the layer's takes play in turn (per instrument
        /// and layer, from the first take when this engine first plays the instrument). Velocity still scales the gain as usual.
        /// </summary>
        private void StartTakeV2(ref Voice voice, MusicPart part, MusicNote note, double elapsedBeats)
        {
            var instrument = part.Instrument;
            int layer = instrument.LayerOf(note.Velocity);
            int first = instrument.LayerStart[layer], count = instrument.LayerStart[layer + 1] - first;
            int take = first;
            if (count > 1)
            {
                int slot = RobinSlot(instrument), index = slot * MusicInstrument.MaxVelocityLayers + layer;
                take = first + _robinNext[index];
                _robinNext[index] = (_robinNext[index] + 1) % count;
            }
            var chosen = instrument.Takes[take];
            voice.Instrument = chosen;
            voice.TakeGain = instrument.TakeGain[take];
            voice.Gain *= voice.TakeGain;
            voice.SamplePosition = elapsedBeats * 60 / _bpm * chosen.SampleRate;
            voice.SampleStep = chosen.SampleRate / (double)_sampleRate * (_frequency[note.Key] / _frequency[chosen.RootKey]);
        }

        // V3 multi-zone sampler: the voice plays the zone's own recording, pitched from that zone's root (position from the tempo map).
        private void StartZoneV3(ref Voice voice, MusicCue cue, MusicPart part, MusicNote note, double elapsedBeats)
        {
            var instrument = part.Instrument;
            int zone = instrument.ZoneFor(note.Key, note.Velocity, note.Articulation);
            var chosen = instrument.Zones[zone];
            voice.Instrument = chosen;
            voice.Gain *= instrument.ZoneGain[zone];
            voice.SamplePosition = elapsedBeats > 0 ? (cue.Tempo.SecondsAt(note.Beat + elapsedBeats) - cue.Tempo.SecondsAt(note.Beat)) * chosen.SampleRate : 0;
            voice.SampleStep = chosen.SampleRate / (double)_sampleRate * (_frequency[note.Key] / _frequency[chosen.RootKey]);
        }

        // V3 audio region segment: its recording from the segment's source position, at its own speed or stretched to the song.
        private int StartRegionV3(int deckId, MusicCue cue, MusicPart part, MusicNote note, double start, double elapsedBeats)
        {
            var segment = part.Regions[-note.Articulation - 1];
            int limit = MusicPlaybackVersion.VoiceLimit(cue.PlaybackVersion);
            for (int i = 0; i < limit; i++)
            {
                if (_voices[i].Active) continue;
                if (i >= _voiceEnd) _voiceEnd = i + 1;
                var instrument = segment.Instrument;
                _voices[i] = new Voice { Active = true, Deck = deckId, Layer = part.Layer, Key = note.Key, Instrument = instrument, StartBeat = start - elapsedBeats, EndBeat = start + note.Duration,
                    ReleaseAge = -1, Version = MusicPlaybackVersion.V3, Part = part, TakeGain = 1, Bus = TrackBus(part), Pan = part.Pan, Gain = part.Gain * segment.Gain,
                    Region = true, RegionStart = segment.RegionStart, RegionLength = segment.RegionLength, RegionFadeIn = segment.FadeIn, RegionFadeOut = segment.FadeOut };
                _voices[i].PartIndex = cue.HasPartAutomation ? Array.IndexOf(cue.Parts, part) : -1;
                double rate = instrument.SampleRate;
                if (segment.SecondsPerBeat > 0)
                {
                    // Stretch: the recording position follows the song beats (RenderStretchV3); Pitch shifts without changing that.
                    ref var v = ref _voices[i];
                    v.Stretch = true; v.StretchSpb = segment.SecondsPerBeat; v.StretchStart = segment.SourceStartFrames; v.StretchPitch = segment.Pitch;
                    if (segment.MarkerBeats != null) v.RegionMap = segment;
                    double period = part.StretchGrainSeconds * _sampleRate, step = rate / _sampleRate * v.StretchPitch;
                    v.GrainAge = period * .5; v.GrainStartA = v.StretchStart - period * .5 * step; v.GrainStartB = v.StretchStart;
                }
                else
                {
                    double elapsedSeconds = elapsedBeats > 0 ? cue.Tempo.SecondsAt(Math.Min(cue.LoopBeats, note.Beat + elapsedBeats)) - cue.Tempo.SecondsAt(note.Beat) : 0;
                    _voices[i].SamplePosition = segment.SourceStartFrames + elapsedSeconds * rate * segment.Pitch;
                    _voices[i].SampleStep = rate / _sampleRate * segment.Pitch;
                }
                return i;
            }
            Interlocked.Increment(ref _droppedNotes);
            return -1;
        }

        // V3 sample shaping: amp decay to sustain after the attack, then the filter (coefficients every 16 samples, like the synth).
        private double ShapeSample(ref Voice voice, double x)
        {
            var sh = voice.Shape; double age = voice.Age, attack = voice.Instrument.Attack;
            if (sh.Sustain < 1 && age > attack) x *= sh.Sustain + (1 - sh.Sustain) * Math.Exp(-6.907755278982137 * (age - attack) / sh.Decay);
            if (sh.Filter == MusicSynthFilter.Off) return x;
            if (--voice.ShCountdown < 0)
            {
                voice.ShCountdown = 15;
                double env;
                if (voice.ReleaseAge >= 0)
                {
                    if (voice.ShEnvAtRelease < 0) voice.ShEnvAtRelease = FilterAdsr(sh, voice.ReleaseAge);
                    env = voice.ShEnvAtRelease * Math.Exp(-6.907755278982137 * (age - voice.ReleaseAge) / sh.FilterRelease);
                }
                else env = FilterAdsr(sh, age);
                double velocity = voice.Part != null && voice.Part.Gain > 0 ? Math.Min(1, voice.Gain / voice.Part.Gain) : 1;
                double octaves = sh.KeyTracking * (voice.Key - 60) / 12 + sh.EnvelopeOctaves * env + sh.VelocityOctaves * (velocity - 1);
                double cutoff = Math.Max(20, Math.Min(Math.Min(20000, .45 * _sampleRate), sh.CutoffHz * Math.Pow(2, octaves)));
                double g = Math.Tan(Math.PI * cutoff / _sampleRate), k = 2 - 1.96 * sh.Resonance, a1 = 1 / (1 + g * (g + k)), a2 = g * a1;
                voice.ShA1 = a1; voice.ShA2 = a2; voice.ShA3 = g * a2; voice.ShK = k;
            }
            double ic1 = voice.ShIc1, ic2 = voice.ShIc2, v3 = x - ic2, v1 = voice.ShA1 * ic1 + voice.ShA2 * v3, v2 = ic2 + voice.ShA2 * ic1 + voice.ShA3 * v3;
            voice.ShIc1 = 2 * v1 - ic1; voice.ShIc2 = 2 * v2 - ic2;
            switch (sh.Filter)
            {
                case MusicSynthFilter.HighPass: return x - voice.ShK * v1 - v2;
                case MusicSynthFilter.BandPass: return voice.ShK * v1;
                default: return v2;
            }
        }

        private static double FilterAdsr(MusicSampleShape sh, double age) =>
            age < sh.FilterAttack ? age / sh.FilterAttack : sh.FilterSustain + (1 - sh.FilterSustain) * Math.Exp(-6.907755278982137 * (age - sh.FilterAttack) / sh.FilterDecay);

        // Region fades: smooth in and out over the region's own beats.
        private double RegionFade(ref Voice voice)
        {
            double t = _beat - voice.RegionStart, f = 1;
            if (voice.RegionFadeIn > 0) f *= Smooth(Math.Max(0, Math.Min(1, t / voice.RegionFadeIn)));
            if (voice.RegionFadeOut > 0) f *= Smooth(Math.Max(0, Math.Min(1, (voice.RegionLength - t) / voice.RegionFadeOut)));
            return f;
        }

        // V3 per-note articulation (gain, length, sample start; a zone's own start too) and expression (bend, vibrato).
        private void StartExpressionV3(ref Voice voice, MusicPart part, MusicNote note, double start)
        {
            var settings = part.Articulations != null && note.Articulation > 0 ? part.Articulations[note.Articulation] : null;
            if (settings != null)
            {
                voice.Gain *= settings.Gain;
                if (settings.LengthScale != 1) voice.EndBeat = start + note.Duration * settings.LengthScale;
            }
            var instrument = voice.Instrument;
            if (instrument.Samples != null && instrument.Noise == null && voice.SliceEnd == 0)
            {
                double skip = instrument.StartFrame + (settings != null ? settings.StartSeconds * instrument.SampleRate : 0);
                if (skip > 0) voice.SamplePosition = Math.Min(instrument.Samples.Length - 2, voice.SamplePosition + skip);
            }
            if (!note.HasExpression) return;
            voice.Expr = true; voice.ExprFrom = note.BendFrom; voice.ExprTo = note.BendTo; voice.ExprStart = note.BendStart; voice.ExprEnd = note.BendEnd;
            voice.ExprVibrato = note.Vibrato; voice.ExprRate = part.VibratoRateHz;
        }

        private const double VibratoFadeSeconds = .15;

        // Frequency ratio of a voice's per-note expression now.
        private double ExpressionPitch(ref Voice voice)
        {
            double t = _beat - voice.StartBeat, semitones = voice.ExprFrom;
            if (voice.ExprTo != voice.ExprFrom)
            {
                if (voice.ExprEnd > voice.ExprStart) semitones += (voice.ExprTo - voice.ExprFrom) * Smooth(Math.Max(0, Math.Min(1, (t - voice.ExprStart) / (voice.ExprEnd - voice.ExprStart))));
                else if (t >= voice.ExprStart) semitones = voice.ExprTo;
            }
            if (voice.ExprVibrato > 0) semitones += voice.ExprVibrato * Math.Sin(2 * Math.PI * voice.ExprRate * voice.Age) * Math.Min(1, voice.Age / VibratoFadeSeconds);
            return semitones == 0 ? 1 : Math.Pow(2, semitones / 12);
        }

        private int RobinSlot(MusicInstrument instrument)
        {
            for (int i = 0; i < RobinSlots; i++)
                if (ReferenceEquals(_robinOwner[i], instrument)) return i;
            int slot = _robinCursor;
            _robinCursor = (_robinCursor + 1) % RobinSlots;
            _robinOwner[slot] = instrument;
            Array.Clear(_robinNext, slot * MusicInstrument.MaxVelocityLayers, MusicInstrument.MaxVelocityLayers);
            return slot;
        }

        /// <summary>V2 slice sampler: the key picks a slice; it plays at its recorded pitch from the slice start to the next slice.</summary>
        private void StartSliceV2(ref Voice voice, MusicPart part, MusicNote note, double elapsedBeats)
        {
            var instrument = part.Instrument;
            int slice = note.Key - instrument.RootKey;
            var starts = instrument.SliceStarts;
            voice.SampleStep = instrument.SampleRate / (double)_sampleRate;
            voice.SamplePosition = starts[slice] + elapsedBeats * 60 / _bpm * instrument.SampleRate;
            voice.SliceEnd = slice + 1 < starts.Length ? starts[slice + 1] : instrument.Samples.Length - 1;
        }

        // Exponential in frequency = linear in pitch.
        private static double GlideMultiplier(ref Voice voice) =>
            voice.GlideSeconds > 0 && voice.GlideAge < voice.GlideSeconds ? Math.Pow(voice.GlideRatio, 1 - voice.GlideAge / voice.GlideSeconds) : 1;

        private void MixVoiceV2(ref Voice voice, double gain, ref double left, ref double right)
        {
            double signal = RenderVoiceV2(ref voice) * gain;
            left += signal * (voice.Pan > 0 ? 1 - voice.Pan : 1);
            right += signal * (voice.Pan < 0 ? 1 + voice.Pan : 1);
        }

        private double RenderVoiceV2(ref Voice voice)
        {
            var instrument = voice.Instrument;
            if (_beat + 1e-9 >= voice.EndBeat && voice.ReleaseAge < 0) voice.ReleaseAge = voice.Age;
            double attack = Math.Min(1, voice.Age / instrument.Attack);
            double release = voice.ReleaseAge < 0 ? 1 : Math.Max(0, 1 - (voice.Age - voice.ReleaseAge) / instrument.Release);
            if (release <= 0) { voice.Active = false; return 0; }
            double bend = GlideMultiplier(ref voice);
            if (voice.GlideSeconds > 0) voice.GlideAge += 1.0 / _sampleRate;
            if (instrument.Noise != null)
            {
                var noise = instrument.Noise;
                double elapsed = Math.Max(0, _beat - voice.StartBeat), remaining = Math.Max(0, voice.EndBeat - _beat);
                double fadeIn = noise.FadeInBeats > 0 ? Smooth(Math.Min(1, elapsed / noise.FadeInBeats)) : 1;
                double fadeOut = noise.FadeOutBeats > 0 ? Smooth(Math.Min(1, remaining / noise.FadeOutBeats)) : 1;
                release *= fadeIn * fadeOut * Math.Min(1, remaining * 60 / _bpm / .005);
                if (_beat + 1e-9 >= voice.EndBeat) { voice.Active = false; return 0; }
            }
            double tone;
            if (instrument.Samples != null)
            {
                bool noiseLoop = instrument.Noise != null && instrument.Noise.LoopSource;
                int cross = noiseLoop ? Math.Min(instrument.Samples.Length / 4, (int)(instrument.SampleRate * .005)) : 0;
                if (noiseLoop && voice.SamplePosition >= instrument.Samples.Length - 1)
                    voice.SamplePosition = cross + (voice.SamplePosition - (instrument.Samples.Length - 1)) % (instrument.Samples.Length - 1 - cross);
                int last = voice.SliceEnd > 0 ? voice.SliceEnd : instrument.Samples.Length - 1; // a slice ends where the next one starts
                int index = (int)voice.SamplePosition;
                if (index >= last) { voice.Active = false; return 0; }
                double fraction = voice.SamplePosition - index;
                tone = instrument.Samples[index] * (1 - fraction) + instrument.Samples[index + 1] * fraction;
                if (noiseLoop && cross > 0 && voice.SamplePosition >= instrument.Samples.Length - 1 - cross)
                {
                    double head = voice.SamplePosition - (instrument.Samples.Length - 1 - cross);
                    int h = (int)head; double blend = head / cross;
                    double first = instrument.Samples[h] * (1 - (head - h)) + instrument.Samples[h + 1] * (head - h);
                    tone = tone * (1 - blend) + first * blend;
                }
                else if (!noiseLoop) tone *= Math.Min(1, (last - voice.SamplePosition) / (instrument.SampleRate * .005));
                var glitch = instrument.Noise;
                double elapsed = Math.Max(0, _beat - voice.StartBeat);
                double duration = voice.EndBeat - voice.StartBeat;
                if (glitch != null) tone *= glitch.Gate(elapsed, duration);
                voice.SamplePosition += voice.SampleStep * bend * (glitch != null ? glitch.Speed(Math.Min(1, elapsed / duration)) : 1);
            }
            else
            {
                switch (instrument.Voice)
                {
                    case MusicVoice.Bell: tone = (Math.Sin(voice.Phase) + .25 * Math.Sin(voice.Phase * 2)) * Math.Exp(-voice.Age * 3); break;
                    case MusicVoice.Bass: tone = Math.Sin(voice.Phase) + .2 * Math.Sin(voice.Phase * 2); break;
                    case MusicVoice.Kick: tone = Math.Sin(2 * Math.PI * (45 * voice.Age + 8 * (1 - Math.Exp(-voice.Age * 20)))) * Math.Exp(-voice.Age * 16); break;
                    default: tone = .5 * (Math.Sin(voice.Phase) + Math.Sin(voice.Phase * 1.5)); break;
                }
                voice.Phase += 2 * Math.PI * _frequency[voice.Key] * bend / _sampleRate;
            }
            var part = voice.Part;
            if (part != null && part.DriveDb > 0) tone = MusicDrive.Shape(part.DriveMode, tone * part.DriveGain); // track drive, before the envelope
            voice.Age += 1.0 / _sampleRate;
            return tone * attack * release * voice.Gain;
        }

        // V2 master: V1 chain with drive, then amp/cabinet, then the tone/space stage (EQ → chorus → reverb) between the mixer's output
        // gain and the soft limiter (identical to V1 when Drive, Amp, Cabinet, Eq, Chorus and Reverb are off).
        private void MixMasterV2(double sum, double sumRight, double localBeat, out float left, out float right)
        {
            _masterMixer.Process(ref sum, ref sumRight, _currentCue.Mixer, localBeat);
            if (_currentCue.Mixer.Drive) _masterDrive.Process(ref sum, ref sumRight, _currentCue.Mixer);
            if (_currentCue.Mixer.AmpStage) _masterAmp.Process(ref sum, ref sumRight, _currentCue.Mixer);
            if (_currentCue.Mixer.Space) _masterSpace.Process(ref sum, ref sumRight, _currentCue.Mixer);
            double startup = Math.Min(1, _samples / (_sampleRate * .02));
            left = _mixProcessor.Process((float)Math.Tanh(sum * startup), _currentCue, localBeat, _bpm, _decks[_current].Repeated, _playOnce);
            right = _mixRight.Process((float)Math.Tanh(sumRight * startup), _currentCue, localBeat, _bpm, _decks[_current].Repeated, _playOnce);
        }

        // ---- V3: V2 sound plus automation. Without automation lanes a V3 cue renders exactly like V2. ----

        private void MixVoiceV3(ref Voice voice, double gain, ref double left, ref double right)
        {
            int slot = AutomationSlot(voice.Deck);
            var part = voice.Part;
            if (voice.Region) gain *= RegionFade(ref voice);
            double pitch = (voice.Expr ? ExpressionPitch(ref voice) : 1) * _speed;
            if (slot < 0 || voice.PartIndex < 0 || (part.VolumeAutomation == null && part.PanAutomation == null && part.PitchAutomation == null &&
                part.ModulationAutomation == null && part.BrightnessAutomation == null))
            { MixVoiceStretchOrV2(ref voice, gain, ref left, ref right, pitch, 1, 0); return; }
            int index = AutomationIndex(slot, voice.PartIndex);
            if (part.VolumeAutomation != null) gain *= _auto[index + (int)MusicAutomationTarget.VolumeDb];
            double authoredPan = voice.Pan;
            if (part.PanAutomation != null) voice.Pan = _auto[index + (int)MusicAutomationTarget.Pan];
            MixVoiceStretchOrV2(ref voice, gain, ref left, ref right,
                (part.PitchAutomation != null ? _auto[index + (int)MusicAutomationTarget.PitchSemitones] : 1) * pitch,
                part.ModulationAutomation != null ? _auto[index + (int)MusicAutomationTarget.Modulation] : 1,
                part.BrightnessAutomation != null ? _auto[index + (int)MusicAutomationTarget.Brightness] : 0);
            voice.Pan = authoredPan;
        }

        // A synth voice renders its patch; a stretched voice renders through its grains with the V2 pan law; every other voice is exactly
        // MixVoiceV2, or its V3 copy while a pitch lane bends it or its instrument has a sustain loop. pitch = frequency ratio; modulation/brightness drive the synth only.
        private void MixVoiceStretchOrV2(ref Voice voice, double gain, ref double left, ref double right, double pitch, double modulation, double brightness)
        {
            if (voice.Synth) { MixSynthV3(ref voice, gain, ref left, ref right, pitch, modulation, brightness); return; }
            if (!voice.Stretch)
            {
                if (pitch == 1 && voice.Instrument.LoopEnd == 0 && voice.Shape == null) { MixVoiceV2(ref voice, gain, ref left, ref right); return; } // sustain loops and shaped samples render in V3
                double bent = RenderVoiceV3(ref voice, pitch) * gain;
                if (voice.Shape != null) bent = ShapeSample(ref voice, bent);
                left += bent * (voice.Pan > 0 ? 1 - voice.Pan : 1);
                right += bent * (voice.Pan < 0 ? 1 + voice.Pan : 1);
                return;
            }
            double signal = RenderStretchV3(ref voice, pitch) * gain;
            if (voice.Shape != null) signal = ShapeSample(ref voice, signal);
            left += signal * (voice.Pan > 0 ? 1 - voice.Pan : 1);
            right += signal * (voice.Pan < 0 ? 1 + voice.Pan : 1);
        }

        // V3 pitch bend: RenderVoiceV2 with the pitch lane's ratio on the playback speed / oscillator frequency (V2 itself is unchanged).
        private double RenderVoiceV3(ref Voice voice, double pitch)
        {
            var instrument = voice.Instrument;
            if (_beat + 1e-9 >= voice.EndBeat && voice.ReleaseAge < 0) voice.ReleaseAge = voice.Age;
            double attack = Math.Min(1, voice.Age / instrument.Attack);
            double release = voice.ReleaseAge < 0 ? 1 : Math.Max(0, 1 - (voice.Age - voice.ReleaseAge) / instrument.Release);
            if (release <= 0) { voice.Active = false; return 0; }
            double bend = GlideMultiplier(ref voice) * pitch; // V3 pitch lane on top of the glide
            if (voice.GlideSeconds > 0) voice.GlideAge += 1.0 / _sampleRate;
            if (instrument.Noise != null)
            {
                var noise = instrument.Noise;
                double elapsed = Math.Max(0, _beat - voice.StartBeat), remaining = Math.Max(0, voice.EndBeat - _beat);
                double fadeIn = noise.FadeInBeats > 0 ? Smooth(Math.Min(1, elapsed / noise.FadeInBeats)) : 1;
                double fadeOut = noise.FadeOutBeats > 0 ? Smooth(Math.Min(1, remaining / noise.FadeOutBeats)) : 1;
                release *= fadeIn * fadeOut * Math.Min(1, remaining * 60 / _bpm / .005);
                if (_beat + 1e-9 >= voice.EndBeat) { voice.Active = false; return 0; }
            }
            double tone;
            if (instrument.Samples != null)
            {
                bool noiseLoop = instrument.Noise != null && instrument.Noise.LoopSource;
                int cross = noiseLoop ? Math.Min(instrument.Samples.Length / 4, (int)(instrument.SampleRate * .005)) : 0;
                if (noiseLoop && voice.SamplePosition >= instrument.Samples.Length - 1)
                    voice.SamplePosition = cross + (voice.SamplePosition - (instrument.Samples.Length - 1)) % (instrument.Samples.Length - 1 - cross);
                // Sustain loop: past LoopEnd the voice continues from LoopStart (crossfade baked into LoopSamples), through the release too.
                bool sustainLoop = instrument.LoopEnd > 0 && voice.SliceEnd == 0;
                if (sustainLoop && voice.SamplePosition >= instrument.LoopEnd)
                    voice.SamplePosition = instrument.LoopStart + (voice.SamplePosition - instrument.LoopEnd) % (instrument.LoopEnd - instrument.LoopStart);
                var samples = sustainLoop ? instrument.LoopSamples : instrument.Samples;
                int last = voice.SliceEnd > 0 ? voice.SliceEnd : instrument.Samples.Length - 1; // a slice ends where the next one starts
                int index = (int)voice.SamplePosition;
                if (index >= last) { voice.Active = false; return 0; }
                double fraction = voice.SamplePosition - index;
                tone = samples[index] * (1 - fraction) + samples[index + 1] * fraction;
                if (noiseLoop && cross > 0 && voice.SamplePosition >= instrument.Samples.Length - 1 - cross)
                {
                    double head = voice.SamplePosition - (instrument.Samples.Length - 1 - cross);
                    int h = (int)head; double blend = head / cross;
                    double first = instrument.Samples[h] * (1 - (head - h)) + instrument.Samples[h + 1] * (head - h);
                    tone = tone * (1 - blend) + first * blend;
                }
                else if (!noiseLoop && !sustainLoop) tone *= Math.Min(1, (last - voice.SamplePosition) / (instrument.SampleRate * .005));
                var glitch = instrument.Noise;
                double elapsed = Math.Max(0, _beat - voice.StartBeat);
                double duration = voice.EndBeat - voice.StartBeat;
                if (glitch != null) tone *= glitch.Gate(elapsed, duration);
                voice.SamplePosition += voice.SampleStep * bend * (glitch != null ? glitch.Speed(Math.Min(1, elapsed / duration)) : 1);
            }
            else
            {
                switch (instrument.Voice)
                {
                    case MusicVoice.Bell: tone = (Math.Sin(voice.Phase) + .25 * Math.Sin(voice.Phase * 2)) * Math.Exp(-voice.Age * 3); break;
                    case MusicVoice.Bass: tone = Math.Sin(voice.Phase) + .2 * Math.Sin(voice.Phase * 2); break;
                    case MusicVoice.Kick: tone = Math.Sin(2 * Math.PI * (45 * voice.Age + 8 * (1 - Math.Exp(-voice.Age * 20)))) * Math.Exp(-voice.Age * 16); break;
                    default: tone = .5 * (Math.Sin(voice.Phase) + Math.Sin(voice.Phase * 1.5)); break;
                }
                voice.Phase += 2 * Math.PI * _frequency[voice.Key] * bend / _sampleRate;
            }
            var part = voice.Part;
            if (part != null && part.DriveDb > 0) tone = MusicDrive.Shape(part.DriveMode, tone * part.DriveGain); // track drive, before the envelope
            voice.Age += 1.0 / _sampleRate;
            return tone * attack * release * voice.Gain;
        }

        // ---- V3 time stretch: two overlapping grains (sin² windows sum to 1) read the sample at the note's pitch while the source
        // position follows the song's beats, so the sample keeps the song tempo (tempo map included) without changing pitch.
        // WSOLA: a restarting grain shifts by up to StretchTolerance around the beat-locked position to where its waveform best
        // matches the grain it overlaps, so the crossfade does not comb-filter. Bounded work, no allocation. ----

        private const double StretchTolerance = .01, StretchMatch = .006; // seconds searched either side / compared

        private void StartStretchV3(ref Voice voice, MusicPart part, MusicNote note, bool sliced)
        {
            var instrument = voice.Instrument;
            voice.Stretch = true;
            voice.StretchStart = sliced ? instrument.SliceStarts[note.Key - instrument.RootKey] : 0;
            voice.StretchPitch = sliced ? 1 : _frequency[note.Key] / _frequency[instrument.RootKey];
            double period = part.StretchGrainSeconds * _sampleRate, step = instrument.SampleRate / (double)_sampleRate * voice.StretchPitch;
            // Grain A starts at its window peak reading the note's first source sample, grain B starts silent there: full level at once.
            voice.GrainAge = period * .5;
            voice.GrainStartA = voice.StretchStart - period * .5 * step;
            voice.GrainStartB = voice.StretchStart;
        }

        private double RenderStretchV3(ref Voice voice, double pitch)
        {
            var instrument = voice.Instrument; var part = voice.Part;
            if (_beat + 1e-9 >= voice.EndBeat && voice.ReleaseAge < 0) voice.ReleaseAge = voice.Age;
            double attack = Math.Min(1, voice.Age / instrument.Attack);
            double release = voice.ReleaseAge < 0 ? 1 : Math.Max(0, 1 - (voice.Age - voice.ReleaseAge) / instrument.Release);
            if (release <= 0) { voice.Active = false; return 0; }
            double bend = GlideMultiplier(ref voice);
            if (voice.GlideSeconds > 0) voice.GlideAge += 1.0 / _sampleRate;
            var samples = instrument.Samples;
            int last = voice.SliceEnd > 0 ? voice.SliceEnd : samples.Length - 1;
            double rate = instrument.SampleRate;
            double position = voice.RegionMap != null ? voice.RegionMap.SourceAt(_beat - voice.RegionStart) * rate // warp markers
                : voice.StretchSpb > 0 ? voice.StretchStart + Math.Max(0, _beat - voice.StartBeat) * voice.StretchSpb * rate
                : voice.StretchStart + Math.Max(0, _beat - voice.StartBeat) * 60 / part.StretchBpm * rate;
            if (position >= last) { voice.Active = false; return 0; }
            double period = part.StretchGrainSeconds * _sampleRate, half = period * .5;
            double step = rate / _sampleRate * voice.StretchPitch * bend * pitch;
            double ageA = voice.GrainAge, ageB = ageA >= half ? ageA - half : ageA + half;
            double tone = Grain(samples, voice.GrainStartA + ageA * step, voice.StretchStart, last) * Window(ageA / period)
                        + Grain(samples, voice.GrainStartB + ageB * step, voice.StretchStart, last) * Window(ageB / period);
            tone *= Math.Min(1, (last - position) / (rate * .005)); // short end taper, like V2
            // Advance the grain clock; a grain that wraps restarts at the current source position.
            double next = ageA + 1;
            // When one grain restarts the other is at its window peak half a period in: match against what it plays next.
            if (ageA < half && next >= half) voice.GrainStartB = Aligned(samples, position, voice.GrainStartA + next * step, step, rate, voice.StretchStart, last);
            if (next >= period) { next -= period; voice.GrainStartA = Aligned(samples, position, voice.GrainStartB + (next + half) * step, step, rate, voice.StretchStart, last); }
            voice.GrainAge = next;
            if (part.DriveDb > 0) tone = MusicDrive.Shape(part.DriveMode, tone * part.DriveGain);
            voice.Age += 1.0 / _sampleRate;
            return tone * attack * release * voice.Gain;
        }

        private static double Window(double phase) { double s = Math.Sin(Math.PI * phase); return s * s; }

        // Grain start near `target` whose next StretchMatch seconds best correlate with the overlapping grain's continuation at `other`.
        private static double Aligned(float[] samples, double target, double other, double step, double rate, double first, int last)
        {
            int tolerance = (int)(StretchTolerance * rate), length = Math.Max(8, (int)(StretchMatch * rate / Math.Max(step, 1e-9)));
            int stride = tolerance > 64 ? 2 : 1;
            double best = double.NegativeInfinity, chosen = target;
            for (int shift = -tolerance; shift <= tolerance; shift += stride)
            {
                double start = target + shift;
                if (start < first || start + length * step >= last || other < first || other + length * step >= last) continue;
                double dot = 0;
                for (int k = 0; k < length; k += 2) dot += Grain(samples, start + k * step, first, last) * Grain(samples, other + k * step, first, last);
                if (dot > best) { best = dot; chosen = start; }
            }
            return chosen;
        }

        private static double Grain(float[] samples, double read, double first, int last)
        {
            if (read < first || read >= last) return 0;
            int index = (int)read; double fraction = read - index;
            return samples[index] * (1 - fraction) + samples[index + 1] * fraction;
        }

        // The V2 master chain with automated mixer values; master volume automation is the last stage (a fader after the limiter and
        // glitches), so a fade-out really reaches silence. Without master lanes it computes exactly what MixMasterV2 does.
        private void MixMasterV3(double sum, double sumRight, double localBeat, out float left, out float right)
        {
            var lanes = _currentCue.MasterAutomation;
            int keyPart = _currentCue.MasterSidechain;
            var m = _currentCue.Mixer;
            if (lanes == null && keyPart < 0 && _decks[_current].Convolution == null && !m.Limiter && m.Color == null) { MixMasterV2(sum, sumRight, localBeat, out left, out right); return; }
            double local = LocalPosition(_currentCue, localBeat);
            if (lanes != null)
            {
                double drive = Lane(lanes, MusicAutomationTarget.DriveDb, local, -1);
                _masterMixer.GateDepthOverride = Lane(lanes, MusicAutomationTarget.GateDepth, local, -1); _masterMixer.CrushMixOverride = Lane(lanes, MusicAutomationTarget.CrushMix, local, -1);
                _masterDrive.GainOverride = drive < 0 ? -1 : Math.Pow(10, drive / 20); _masterDrive.MixOverride = Lane(lanes, MusicAutomationTarget.DriveMix, local, -1);
                _masterColor.RotaryOverride = Lane(lanes, MusicAutomationTarget.RotarySpeed, local, -1);
            }
            // A repeat shares deck 0's buses, so its key levels are deck 0's.
            int keyDeck = _current == 1 && ReferenceEquals(_decks[1].Effects, _decks[0].Effects) ? 0 : _current;
            double key = keyPart >= 0 ? _keyPeak[keyDeck * MusicCue.MaxParts + keyPart] : -1;
            _masterMixer.Process(ref sum, ref sumRight, m, localBeat, Lane(lanes, MusicAutomationTarget.LowPassHz, local, m.LowPass),
                Lane(lanes, MusicAutomationTarget.HighPassHz, local, m.HighPass), Lane(lanes, MusicAutomationTarget.EchoWet, local, m.Wet), key);
            if (m.Drive) _masterDrive.Process(ref sum, ref sumRight, m);
            if (m.AmpStage) _masterAmp.Process(ref sum, ref sumRight, m);
            _decks[_current].Convolution?.Process(ref sum,ref sumRight);
            if (m.Space) _masterSpace.Process(ref sum, ref sumRight, m, Lane(lanes, MusicAutomationTarget.ChorusMix, local, m.ChorusMix),
                Lane(lanes, MusicAutomationTarget.ReverbMix, local, m.ReverbMix));
            if (m.Color != null) _masterColor.Process(ref sum, ref sumRight, m.Color, local);
            if (lanes != null)
            {
                if (m.Stutter) _masterStutter.Process(ref sum, ref sumRight, Lane(lanes, MusicAutomationTarget.Stutter, local, 0) >= .5, Math.Min(MusicStutter.MaxSeconds, m.StutterBeats * 60 / _bpm), _sampleRate);
                double width = Lane(lanes, MusicAutomationTarget.Width, local, -1);
                if (width >= 0) { double mid = (sum + sumRight) * .5, side = (sum - sumRight) * .5 * width; sum = mid + side; sumRight = mid - side; }
            }
            double startup = Math.Min(1, _samples / (_sampleRate * .02));
            if (m.Limiter)
            {
                // V3 master limiter instead of the soft limiter: true-peak brick wall with 2 ms look-ahead.
                sum *= startup; sumRight *= startup;
                _masterLimiter.Process(ref sum, ref sumRight, m);
                left = _mixProcessor.Process((float)sum, _currentCue, localBeat, _bpm, _decks[_current].Repeated, _playOnce);
                right = _mixRight.Process((float)sumRight, _currentCue, localBeat, _bpm, _decks[_current].Repeated, _playOnce);
            }
            else
            {
                left = _mixProcessor.Process((float)Math.Tanh(sum * startup), _currentCue, localBeat, _bpm, _decks[_current].Repeated, _playOnce);
                right = _mixRight.Process((float)Math.Tanh(sumRight * startup), _currentCue, localBeat, _bpm, _decks[_current].Repeated, _playOnce);
            }
            var volume = lanes?[(int)MusicAutomationTarget.VolumeDb];
            double gain = volume == null ? 1 : MusicAutomation.Gain(volume.Evaluate(local));
            if (_speed < .15) gain *= Smooth(Math.Max(0, (_speed - MusicAutomation.MinSpeed) / (.15 - MusicAutomation.MinSpeed))); // tape stop: silent near the bottom and at 0
            if (gain == 1) return;
            left = (float)(left * gain); right = (float)(right * gain);
        }

        // Pre-fader send level factor: 1 / (track Gain × volume lane), so the send ignores the track's fader (exact for linear track chains).
        private double PreFader(MusicPart part, int deck, int partIndex)
        {
            double fader = part.Gain;
            if (part.VolumeAutomation != null && deck < 2) fader *= _auto[AutomationIndex(deck, partIndex) + (int)MusicAutomationTarget.VolumeDb];
            return fader > 1e-4 ? 1 / fader : 0;
        }

        private static double Lane(MusicAutomation[] lanes, MusicAutomationTarget target, double local, double authored)
        {
            var lane = lanes?[(int)target];
            return lane == null ? authored : lane.Evaluate(local);
        }

        private static int AutomationIndex(int slot, int part) => (slot * MusicCue.MaxParts + part) * MusicAutomation.TargetCount;

        /// <summary>Automation cache slot of a voice's deck (0/1 decks, 2 rhythm deck); harmonic-bridge notes (-2) are not automated.</summary>
        private static int AutomationSlot(int deck) => deck >= 0 ? deck : deck == -1 ? 2 : -1;

        // Once per frame, before voices and plugins mix: each deck's cue-local automation values for its V3 parts.
        private void UpdateAutomation()
        {
            for (int slot = 0; slot < 3; slot++)
            {
                var deck = slot < 2 ? _decks[slot] : _rhythm;
                var cue = deck.Cue;
                if (cue == null || !cue.HasPartAutomation) continue;
                double local = LocalPosition(cue, _beat - deck.Start);
                for (int i = 0; i < cue.PartCount; i++)
                {
                    var lanes = cue.Parts[i].Automation;
                    if (lanes == null) continue;
                    int index = AutomationIndex(slot, i);
                    for (int t = 0; t < lanes.Length; t++)
                        if (lanes[t] != null) _auto[index + t] = t == (int)MusicAutomationTarget.VolumeDb ? MusicAutomation.Gain(lanes[t].Evaluate(local))
                            : t == (int)MusicAutomationTarget.PitchSemitones ? Math.Pow(2, lanes[t].Evaluate(local) / 12) : lanes[t].Evaluate(local);
                    var followers = cue.Parts[i].Followers;
                    if (followers != null && slot < 2) ApplyFollowers(followers, slot, i, index);
                }
            }
        }

        // V3 performance lanes (GateDepth … Width) of a track bus (values from _auto at index) or a mix bus (evaluated here).
        private void SetPerformanceLanes(MusicTrackEffects bus, MusicAutomation[] lanes, int index, MusicAutomation[] evaluate, double local)
        {
            double V(MusicAutomationTarget t) => lanes[(int)t] == null ? -1 : evaluate != null ? evaluate[(int)t].Evaluate(local) : _auto[index + (int)t];
            double drive = V(MusicAutomationTarget.DriveDb);
            var s = bus.Settings;
            bus.SetLanes(V(MusicAutomationTarget.GateDepth), V(MusicAutomationTarget.CrushMix), drive < 0 ? -1 : Math.Pow(10, drive / 20), V(MusicAutomationTarget.DriveMix),
                V(MusicAutomationTarget.Stutter) >= .5, s == null ? .25 : Math.Min(MusicStutter.MaxSeconds, s.StutterBeats * 60 / _bpm), V(MusicAutomationTarget.Width), V(MusicAutomationTarget.RotarySpeed));
        }

        // V3 envelope followers: the source track's level (its sidechain-key peak, one frame behind), smoothed, moves the target value.
        private void ApplyFollowers(MusicFollower[] followers, int slot, int part, int index)
        {
            for (int k = 0; k < followers.Length && k < MusicModulators.MaxPerTrack; k++)
            {
                var f = followers[k]; if (f.Source < 0) continue;
                int e = (slot * MusicCue.MaxParts + part) * MusicModulators.MaxPerTrack + k;
                double peak = Math.Min(1, _keyPeak[slot * MusicCue.MaxParts + f.Source]), env = _followEnv[e];
                env += (peak - env) * (1 - Math.Exp(-1 / (_sampleRate * (peak > env ? f.AttackSeconds : f.ReleaseSeconds))));
                _followEnv[e] = env;
                double amount = f.Amount * env; int t = (int)f.Target;
                if (f.Target == MusicAutomationTarget.VolumeDb) _auto[index + t] *= MusicAutomation.Gain(amount);
                else if (f.Target == MusicAutomationTarget.PitchSemitones) _auto[index + t] *= Math.Pow(2, amount / 12);
                else if (f.Target == MusicAutomationTarget.LowPassHz || f.Target == MusicAutomationTarget.HighPassHz) _auto[index + t] *= Math.Pow(2, amount);
                else { MusicAutomation.Range(f.Target, out float low, out float high); _auto[index + t] = Math.Max(low, Math.Min(high, _auto[index + t] + amount)); }
            }
        }

        // ---- V2 track effects: voices of a part with Effects sum into its bus; every bus of both decks is processed each sample ----

        private MusicTrackEffects TrackBus(MusicPart part)
        {
            for (int d = 0; d < 2; d++)
            {
                var deck = _decks[d];
                if (deck.Effects == null) continue;
                for (int i = 0; i < deck.Cue.PartCount && i < deck.Effects.Length; i++)
                    if (ReferenceEquals(deck.Cue.Parts[i], part)) return deck.Effects[i];
            }
            return null;
        }

        // Buses keep running without voices so echo tails ring out; a repeat shares its deck's buses, so they run once.
        // V3 routing: a routed part's bus output goes to its mix bus (or the master) plus its sends; mix buses then run in their
        // compiled order (each feeds a later bus or the master), so one pass mixes the whole graph without allocation.
        private void MixTrackEffects(ref double sum, ref double sumRight)
        {
            for (int d = 0; d < 2; d++)
            {
                var buses = _decks[d].Effects;
                if (buses == null || (d == 1 && ReferenceEquals(buses, _decks[0].Effects))) continue;
                var cue = _decks[d].Cue;
                double local = _beat - _decks[d].Start;
                int parts = cue != null ? Math.Min(cue.PartCount, buses.Length) : buses.Length;
                bool routing = cue != null && buses.Length == cue.PartCount + cue.Buses.Length && cue.Buses.Length > 0;
                bool v3 = cue != null && cue.PlaybackVersion >= MusicPlaybackVersion.V3;
                // Sidechain keys first: every track's own signal is gathered before any chain runs, so keys never depend on order.
                if (v3) for (int i = 0; i < parts; i++)
                        if (cue.SidechainKey[i] && buses[i] != null) _keyPeak[d * MusicCue.MaxParts + i] = Math.Max(Math.Abs(buses[i].Left), Math.Abs(buses[i].Right));
                for (int i = 0; i < parts; i++)
                {
                    var bus = buses[i];
                    if (bus == null) continue;
                    if (bus.SyncedEcho) bus.SetEchoTempo(_bpm);
                    var part = cue?.Parts[i];
                    var lanes = bus.Automated && part != null ? part.Automation : null;
                    double key = v3 && part != null && cue.PartSidechain[i] >= 0 && cue.PlaybackVersionOf(part) >= MusicPlaybackVersion.V3 ? _keyPeak[d * MusicCue.MaxParts + cue.PartSidechain[i]] : -1;
                    double l, r;
                    if (lanes == null && key < 0) bus.Render(local, out l, out r);
                    else
                    {
                        // V3 effect automation (lanes evaluated in UpdateAutomation) and/or sidechain; absent lanes keep the chain's values.
                        var s = bus.Settings; int index = AutomationIndex(d, i);
                        if (lanes != null) SetPerformanceLanes(bus, lanes, index, null, 0);
                        bus.Render(local, out l, out r,
                            lanes?[(int)MusicAutomationTarget.LowPassHz] != null ? _auto[index + (int)MusicAutomationTarget.LowPassHz] : s.LowPass,
                            lanes?[(int)MusicAutomationTarget.HighPassHz] != null ? _auto[index + (int)MusicAutomationTarget.HighPassHz] : s.HighPass,
                            lanes?[(int)MusicAutomationTarget.EchoWet] != null ? _auto[index + (int)MusicAutomationTarget.EchoWet] : s.Wet,
                            lanes?[(int)MusicAutomationTarget.ChorusMix] != null ? _auto[index + (int)MusicAutomationTarget.ChorusMix] : s.ChorusMix,
                            lanes?[(int)MusicAutomationTarget.ReverbMix] != null ? _auto[index + (int)MusicAutomationTarget.ReverbMix] : s.ReverbMix, key);
                    }
                    if (!routing || part == null || !part.Routed || cue.PlaybackVersionOf(part) < MusicPlaybackVersion.V3) { sum += l; sumRight += r; continue; }
                    if (part.OutputBus >= 0) { var target = buses[parts + part.OutputBus]; target.Left += l; target.Right += r; }
                    else { sum += l; sumRight += r; }
                    if (part.Sends != null)
                        foreach (var send in part.Sends)
                        {
                            var target = buses[parts + send.Bus]; double gain = send.GainAt(local);
                            if (send.PreFader) gain *= PreFader(part, d, i); // the fader (Gain × volume lane) is applied before track effects here: divide it out
                            target.Left += l * gain; target.Right += r * gain;
                        }
                }
                if (!routing) continue;
                for (int b = 0; b < cue.Buses.Length; b++)
                {
                    var bus = buses[parts + b]; double l, r;
                    if (bus.SyncedEcho) bus.SetEchoTempo(_bpm);
                    int keyPart = cue.BusSidechain[b];
                    var definition = cue.Buses[b]; var lanes = definition.Automation;
                    if (keyPart < 0 && lanes == null) bus.Render(local, out l, out r);
                    else
                    {
                        var s = bus.Settings;
                        double Value(MusicAutomationTarget target, double authored) => lanes?[(int)target]?.Evaluate(local) ?? authored;
                        if (lanes != null) SetPerformanceLanes(bus, lanes, -1, lanes, local);
                        bus.Render(local, out l, out r, Value(MusicAutomationTarget.LowPassHz, s.LowPass), Value(MusicAutomationTarget.HighPassHz, s.HighPass),
                            Value(MusicAutomationTarget.EchoWet, s.Wet), Value(MusicAutomationTarget.ChorusMix, s.ChorusMix), Value(MusicAutomationTarget.ReverbMix, s.ReverbMix),
                            keyPart < 0 ? -1 : _keyPeak[d * MusicCue.MaxParts + keyPart]);
                    }
                    if (definition.VolumeAutomation != null) { double gain = MusicAutomation.Gain(definition.VolumeAutomation.Evaluate(local)); l *= gain; r *= gain; }
                    if (definition.Pan != 0) { l *= definition.Pan > 0 ? 1 - definition.Pan : 1; r *= definition.Pan < 0 ? 1 + definition.Pan : 1; }
                    int output = cue.Buses[b].Output;
                    if (output >= 0) { var target = buses[parts + output]; target.Left += l; target.Right += r; }
                    else { sum += l; sumRight += r; }
                }
            }
        }
    }
}
