using System;
using System.Threading;

namespace Graze.Presentation.Audio
{
    // Playback-version dispatch and the per-version sound code (voice start, voice render, pan, master chain).
    // A released version's methods are frozen: to change the sound, add a version and new methods beside them.
    // See MusicPlaybackVersion and engine/playback-versions.md. Audio-thread rules apply to everything here.
    public sealed partial class AdaptiveMusicEngine
    {
        private void StartVoice(int deckId, MusicCue cue, MusicPart part, MusicNote note, double start, double elapsedBeats = 0)
        {
            if (note.Velocity == 0 || part.Gain == 0) return;
            int version = cue.PlaybackVersionOf(part);
            if (version >= MusicPlaybackVersion.V2 && part.GlideSeconds > 0 && deckId >= 0 && GlideV2(deckId, part, note, start)) return;
            // Polyphony follows the cue (a track pin changes only the voice's sound): a V1 cue searches only the first 128 slots, as before.
            int limit = MusicPlaybackVersion.VoiceLimit(cue.PlaybackVersion);
            for (int i = 0; i < limit; i++)
            {
                if (_voices[i].Active) continue;
                if (i >= _voiceEnd) _voiceEnd = i + 1;
                _voices[i] = new Voice { Active = true, Deck = deckId, Layer = part.Layer, Key = note.Key, Instrument = part.Instrument,
                    StartBeat = start - elapsedBeats, EndBeat = start + note.Duration, ReleaseAge = -1, Version = version, Part = part,
                    Bus = version >= MusicPlaybackVersion.V2 && part.Effects != null ? TrackBus(part) : null };
                switch (version)
                {
                    case MusicPlaybackVersion.V1:
                    case MusicPlaybackVersion.V2: // a V2 voice starts like V1 (V2 = more voices + glide)
                    default: // MusicCue and MusicPart reject versions this engine does not know.
                        StartVoiceV1(ref _voices[i], part, note, elapsedBeats); break;
                }
                return;
            }
            Interlocked.Increment(ref _droppedNotes); // bounded work; never steal an audible voice abruptly
        }

        private void MixVoice(ref Voice voice, double gain, ref double left, ref double right)
        {
            switch (voice.Version)
            {
                case MusicPlaybackVersion.V2: MixVoiceV2(ref voice, gain, ref left, ref right); break;
                case MusicPlaybackVersion.V1:
                default: MixVoiceV1(ref voice, gain, ref left, ref right); break;
            }
        }

        // The master chain follows the audible (current) cue, like its mixer settings and glitch regions.
        private void MixMaster(double left, double right, double localBeat, out float outLeft, out float outRight)
        {
            switch (_currentCue.PlaybackVersion)
            {
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
            voice.Gain = part.Gain * note.Velocity;
            if (part.Instrument.Samples != null)
                voice.SampleStep = part.Instrument.SampleRate / (double)_sampleRate * (_frequency[note.Key] / _frequency[part.Instrument.RootKey]);
            return true;
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
                int index = (int)voice.SamplePosition;
                if (index >= instrument.Samples.Length - 1) { voice.Active = false; return 0; }
                double fraction = voice.SamplePosition - index;
                tone = instrument.Samples[index] * (1 - fraction) + instrument.Samples[index + 1] * fraction;
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

        // V2 master: V1 chain with the drive stage between the mixer's output gain and the soft limiter (identical to V1 when Drive is off).
        private void MixMasterV2(double sum, double sumRight, double localBeat, out float left, out float right)
        {
            _masterMixer.Process(ref sum, ref sumRight, _currentCue.Mixer, localBeat);
            if (_currentCue.Mixer.Drive) _masterDrive.Process(ref sum, ref sumRight, _currentCue.Mixer);
            double startup = Math.Min(1, _samples / (_sampleRate * .02));
            left = _mixProcessor.Process((float)Math.Tanh(sum * startup), _currentCue, localBeat, _bpm, _decks[_current].Repeated, _playOnce);
            right = _mixRight.Process((float)Math.Tanh(sumRight * startup), _currentCue, localBeat, _bpm, _decks[_current].Repeated, _playOnce);
        }

        // ---- V2 track effects: voices of a part with Effects sum into its bus; every bus of both decks is processed each sample ----

        private MusicTrackEffects TrackBus(MusicPart part)
        {
            for (int d = 0; d < 2; d++)
            {
                var deck = _decks[d];
                if (deck.Effects == null) continue;
                for (int i = 0; i < deck.Cue.PartCount; i++)
                    if (ReferenceEquals(deck.Cue.Parts[i], part)) return deck.Effects[i];
            }
            return null;
        }

        // Buses keep running without voices so echo tails ring out; a repeat shares its deck's buses, so they run once.
        private void MixTrackEffects(ref double sum, ref double sumRight)
        {
            for (int d = 0; d < 2; d++)
            {
                var buses = _decks[d].Effects;
                if (buses == null || (d == 1 && ReferenceEquals(buses, _decks[0].Effects))) continue;
                double local = _beat - _decks[d].Start;
                for (int i = 0; i < buses.Length; i++) buses[i]?.Process(local, ref sum, ref sumRight);
            }
        }
    }
}
