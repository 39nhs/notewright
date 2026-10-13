using System;

namespace Graze.Presentation.Audio
{
    public sealed partial class AdaptiveMusicEngine
    {
        MusicPluginSession _pluginSession;
        MusicPluginBank _currentPlugins;
        void InitializePlugins(MusicPluginSession session, MusicCue initial)
        {
            if (session == null) return;
            if (session.SampleRate != _sampleRate) throw new ArgumentException("Engine and plugin session sample rates must match.");
            if (session.Looping == _playOnce) throw new ArgumentException("Use session loop:false with engine playOnce:true, or loop:true with normal playback.");
            _pluginSession = session; _currentPlugins = session.Get(initial);
            if (_currentPlugins == null) throw new InvalidOperationException("Call PrepareAsync(initial) before creating the engine.");
            _decks[0].Plugins = _currentPlugins.Decks[0]; _rhythm.Plugins = _decks[0].Plugins.Rhythm;
        }
        MusicPluginBank PreparePlugins(MusicCue cue)
        {
            if (_pluginSession == null) return null;
            // Called from TransitionTo, never from the audio callback. Worker loading is allowed to finish after the request.
            _pluginSession.PrepareAsync(cue); return _pluginSession.Get(cue);
        }
        MusicPluginDeck RhythmPlugins(MusicCue cue)
        {
            for (int i = 0; i < 2; i++) if (ReferenceEquals(_decks[i].Cue, cue)) return _decks[i].Plugins?.Rhythm;
            return null;
        }
        void BindPluginVoice(ref Voice voice, MusicPart part)
        {
            if (_pluginSession == null) return;
            voice.PluginDeck = voice.Deck >= 0 ? _decks[voice.Deck].Plugins : voice.Deck == -1 ? _rhythm.Plugins : null;
            voice.PluginSlot = voice.PluginDeck?.Slot(part) ?? -1;
        }
        void ReadPlugins(bool quiet)
        {
            // No dictionary access, native calls, memory-mapped I/O, allocation or locks here.
            for (int d = 0; d < 2; d++)
            {
                var deck = _decks[d];
                deck.Plugins?.Read(_beat - deck.Start, _bpm, _sampleRate, _pluginSession.Enabled && !quiet && (d == _current || _transitioning != 0));
            }
            _rhythm.Plugins?.Read(_beat - _rhythm.Start, _bpm, _sampleRate, _pluginSession.Enabled && !quiet, true);
        }
        void MixPlugins(ref double left, ref double right)
        {
            for (int d = 0; d < 2; d++) InjectPlugins(_decks[d].Plugins, d, false, ref left, ref right);
            InjectPlugins(_rhythm.Plugins, -1, true, ref left, ref right);
        }
        void InjectPlugins(MusicPluginDeck deck, int id, bool drums, ref double left, ref double right)
        {
            if (deck == null) return;
            for (int i = 0; i < deck.Tracks.Length; i++)
            {
                var part = deck.Cue.Parts[i]; var track = deck.Tracks[i];
                if (track == null || deck.Wet[i] == 0 || (part.Layer == MusicLayer.Drums) != drums) continue;
                double gain = part.Gain * track.Gain * deck.Wet[i] * DeckGain(id, part.Layer) * _layerGain[(int)part.Layer];
                double authoredPan = part.Pan;
                int slot = AutomationSlot(id);
                if (slot >= 0 && deck.Cue.HasPartAutomation && deck.Cue.PlaybackVersionOf(part) >= MusicPlaybackVersion.V3)
                {
                    int index = AutomationIndex(slot, i);
                    if (part.VolumeAutomation != null) gain *= _auto[index + (int)MusicAutomationTarget.VolumeDb];
                    if (part.PanAutomation != null) authoredPan = _auto[index + (int)MusicAutomationTarget.Pan];
                }
                double pan = Math.Max(-1, Math.Min(1, authoredPan + track.Pan));
                double l = deck.Left[i], r = deck.Right[i];
                if (deck.Cue.PlaybackVersionOf(part) >= MusicPlaybackVersion.V2 && part.DriveDb > 0)
                { l = MusicDrive.Shape(part.DriveMode, l * part.DriveGain); r = MusicDrive.Shape(part.DriveMode, r * part.DriveGain); }
                l *= gain * (pan > 0 ? 1 - pan : 1); r *= gain * (pan < 0 ? 1 + pan : 1);
                int partVersion = deck.Cue.PlaybackVersionOf(part);
                var bus = partVersion >= MusicPlaybackVersion.V2 ? TrackBus(part) : null; // exists only for V2 effects, V3 routing or a V3 sidechain key
                if (bus != null) { bus.Left += l; bus.Right += r; } else { left += l; right += r; }
            }
        }
    }
}
