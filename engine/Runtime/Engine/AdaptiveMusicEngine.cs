using System;
using System.Threading;

namespace Graze.Presentation.Audio
{
    /// <summary>Two cue decks, 1024 preallocated voices (per-version limit, V1 = 128), beat-quantized transitions and pitch-stable tempo ramps.</summary>
    /// <remarks>Sound production is per playback version in AdaptiveMusicEngine.Playback.cs; this file is scheduling and transitions.</remarks>
    public sealed partial class AdaptiveMusicEngine
    {
        /// <summary>Preallocated voice slots; a cue's playback version limits how many it may use (MusicPlaybackVersion.VoiceLimit).</summary>
        public const int VoiceCapacity = 1024;
        /// <summary>Transition harmony checks per candidate boundary; bounds audio-thread work at extreme tempos (unchanged up to 2048-beat windows).</summary>
        private const int MaxBridgeChecks = 4096;
        private sealed class Request
        {
            public readonly MusicCue Cue;
            public readonly double Beats;
            public readonly bool Positioned;
            public readonly MusicTransitionMode Mode;
            public readonly double MaximumSeconds;
            public readonly double FixedSeconds;
            public readonly bool AlignBars;
            public readonly double IncomingSeconds, GapSeconds;
            public double OutgoingSeconds;
            public MusicTrackEffects[] Effects; // built on the caller thread
            public MusicConvolution Convolution;
            public MusicPluginBank Plugins; // optional prewarmed banks, resolved on the caller thread
            public Request(MusicCue cue, double beats) { Cue = cue; Beats = beats; }
            public Request(MusicCue cue, double beats, MusicTransitionMode mode, double maximumSeconds, double fixedSeconds, bool alignBars, double incomingSeconds, double gapSeconds)
            { Cue = cue; Beats = beats; Positioned = true; Mode = mode; MaximumSeconds = maximumSeconds; FixedSeconds = fixedSeconds; AlignBars = alignBars; IncomingSeconds = incomingSeconds; GapSeconds = gapSeconds; }
        }
        private sealed class Deck
        {
            public bool Repeated;
            public MusicCue Cue;
            public MusicTrackEffects[] Effects; // per part index, null = no track effects
            public MusicConvolution Convolution;
            public MusicPluginDeck Plugins;
            public double Start;
            public readonly int[] Index = new int[MusicCue.MaxParts];
            public readonly long[] Loop = new long[MusicCue.MaxParts];
            /// <summary>Passes played before this deck started (a repeat-fade restart continues the count), for note variation.</summary>
            public long Pass;
            public void Reset(MusicCue cue, double start)
            { Cue = cue; Start = start; Repeated = false; Pass = 0; Array.Clear(Index, 0, Index.Length); Array.Clear(Loop, 0, Loop.Length); }
            public void Seek(MusicCue cue, double start, double local)
            {
                Reset(cue, start);
                long loop = (long)Math.Floor(local / cue.LoopBeats);
                double phase = local - loop * cue.LoopBeats;
                for (int i = 0; i < cue.PartCount; i++)
                {
                    var notes = cue.Parts[i].Notes;
                    Loop[i] = loop;
                    int lo = 0, hi = notes.Length;
                    while (lo < hi) { int mid = (lo + hi) / 2; if (notes[mid].Beat < phase - 1e-8) lo = mid + 1; else hi = mid; }
                    if (lo == notes.Length) { Index[i] = 0; Loop[i]++; } else Index[i] = lo;
                }
            }
        }
        private struct Voice
        {
            public bool Active;
            public int Deck, Key;
            public MusicLayer Layer;
            public MusicInstrument Instrument;
            public double Phase, Age, SamplePosition, SampleStep, Gain, StartBeat, EndBeat, ReleaseAge;
            public double Pan;
            public int Version;
            public MusicPart Part;
            public int PartIndex; // index in the cue's parts when the cue has V3 track automation, else -1
            public MusicTrackEffects Bus; // track effect bus this voice sums into (V2+), or null = master
            public MusicPluginDeck PluginDeck;
            public int PluginSlot;
            // V2 glide: pitch multiplier GlideRatio^(1 - GlideAge/GlideSeconds) bends the voice onto its (new) Key.
            public double GlideRatio, GlideAge, GlideSeconds;
            public int SliceEnd; // V2 slice sampler: last frame of the slice (0 = the whole sample)
            public double TakeGain; // V2 velocity layer / round-robin take balance (1 = the instrument's own sample)
            // V3 time stretch: source start (slice or 0), pitch ratio, grain clock (output samples) and the two grains' source starts.
            public bool Stretch;
            public double StretchStart, StretchPitch, GrainAge, GrainStartA, GrainStartB;
            // V3 built-in synth: this voice renders its instrument's MusicSynthPatch with state at _synth[Slot * SynthStride].
            public bool Synth;
            public int Slot;
            // V3 per-note expression (MusicNote.BendFrom… / Vibrato): pitch offset in semitones over the note, vibrato at ExprRate Hz.
            public bool Expr;
            public float ExprFrom, ExprTo, ExprStart, ExprEnd, ExprVibrato;
            public double ExprRate;
            // V3 audio region voice: fades over the whole region (beats) and the stretch's recording seconds per beat (0 = the track's Stretch Bpm).
            public bool Region;
            public double RegionStart, RegionLength, RegionFadeIn, RegionFadeOut, StretchSpb;
            public MusicRegionSegment RegionMap; // warp markers of the region, or null
            // V3 sample shaping (MusicSampleShape): decay/sustain and a TPT state-variable filter with control-rate coefficients.
            public MusicSampleShape Shape;
            public double ShIc1, ShIc2, ShA1, ShA2, ShA3, ShK, ShEnvAtRelease;
            public int ShCountdown;
        }

        private readonly int _sampleRate;
        private readonly MusicMixProcessor _mixProcessor;
        private readonly MusicMixProcessor _mixRight;
        private readonly MusicMasterMixer _masterMixer;
        private readonly MusicMasterDrive _masterDrive;
        private readonly MusicAmp _masterAmp;
        private readonly MusicSpace _masterSpace;
        private readonly MusicColor _masterColor; // V3 color stage of the master chain
        private readonly MusicStutter _masterStutter; // V3 master beat repeat (Stutter lane)
        // V3 master Speed lane (tape speed): scales the tempo clock and every V3 voice's pitch; 1 = off.
        private double _speed = 1;
        private readonly MusicLimiter _masterLimiter;
        private float _peakLeft, _peakRight;
        public float PeakLeft => Volatile.Read(ref _peakLeft);
        public float PeakRight => Volatile.Read(ref _peakRight);
        private MusicLevelMeter _levelMeter;
        /// <summary>Attach an application-owned stereo meter before rendering. One engine/audio writer per meter. Null disables it.</summary>
        public void AttachLevelMeter(MusicLevelMeter meter)
        {
            if(meter!=null&&(meter.SampleRate!=_sampleRate||meter.Channels!=2)) throw new ArgumentException("Engine meter must be stereo at the engine's sample rate.");
            Volatile.Write(ref _levelMeter,meter);
        }
        private readonly double _repeatFadeSeconds;
        private readonly bool _playOnce;
        private readonly double _repeatPauseSeconds, _slowFadeInSeconds;
        private double _fadeOutSeconds, _fadeInSeconds, _silenceSeconds;
        private bool _wholeMixFade, _outgoingCleared;
        private bool _overlapFade, _customFade;
        public double LastFadeOutSeconds => _fadeOutSeconds;
        public double LastSilenceSeconds => _silenceSeconds;
        public double LastFadeInSeconds => _fadeInSeconds;
        public double LastTransitionDurationSeconds => _fixedSeconds;
        public int RepeatTransitions { get; private set; }
        private readonly Deck[] _decks = { new Deck(), new Deck() };
        private readonly Deck _rhythm = new Deck();
        private readonly int[] _matches = new int[MusicCue.MaxParts];
        private readonly int[] _bridgeKeys = new int[MusicCue.MaxParts];
        private readonly Voice[] _voices = new Voice[VoiceCapacity];
        // V2 round robin: next take per (instrument, velocity layer). Slots are reused oldest-first; preallocated for the audio thread.
        private const int RobinSlots = 128;
        private readonly MusicInstrument[] _robinOwner = new MusicInstrument[RobinSlots];
        private readonly int[] _robinNext = new int[RobinSlots * MusicInstrument.MaxVelocityLayers];
        private int _robinCursor;
        private readonly double[] _frequency = new double[128];
        private readonly double[] _layerGain = { 1, 1, 1, 1 };
        // V3 track automation evaluated once per frame: [(slot * MaxParts + part) * TargetCount + target], slot 0/1 = decks, 2 = rhythm deck.
        // Volume is stored as linear gain, every other target as its value.
        private readonly double[] _auto = new double[3 * MusicCue.MaxParts * MusicAutomation.TargetCount];
        // V3 sidechain: each deck's key track levels this frame (peak of the track's own signal before its effects), [deck * MaxParts + part].
        private readonly double[] _keyPeak = new double[2 * MusicCue.MaxParts];
        private readonly double[] _followEnv = new double[2 * MusicCue.MaxParts * MusicModulators.MaxPerTrack]; // V3 envelope follower state
        private readonly float[] _layerTarget = { 1, 1, 1, 1 };
        private Request _mailbox, _waiting;
        private MusicCue _currentCue;
        private int _current, _transitioning, _droppedNotes;
        private int _voiceEnd, _activeVoices, _peakVoices; // _voiceEnd: one past the highest active slot
        private long _samples;
        private double _beat, _bpm, _publishedBeat, _publishedBpm, _nextBar;
        private double _transitionStart, _transitionLength, _fromBpm, _progress;
        private double _plannedBar = -1, _bridgeNext;
        private bool _harmonicBridge;
        private bool _positioned, _waitForEnd, _endHandoff, _incomingPrimed;
        private double _entryBeat, _incomingActivation, _positionedLength;
        private double _fixedSeconds;
        private long _transitionSampleStart;
        private bool _alignBars, _rhythmHandedOff;
        public double LastHandoffBeat { get; private set; }
        public double LastTransitionStartSeconds { get; private set; }
        private MusicCue _pendingRhythm;
        private double _rhythmSwitchBeat;
        private double _publishedPosition, _lastEntry, _lastSourceProgress, _lastStart;
        public double PositionBeats => Volatile.Read(ref _publishedPosition);
        /// <summary>Section of the current cue at <see cref="PositionBeats"/> (index into <c>CurrentCue.Sections</c>), or -1 before its first marker.</summary>
        public int CurrentSection { get { var cue = CurrentCue; return cue == null ? -1 : cue.Sections.IndexAt(PositionBeats); } }
        /// <summary>Name of the current section, or "".</summary>
        public string CurrentSectionName { get { var cue = CurrentCue; return cue == null ? "" : cue.Sections.NameAt(PositionBeats); } }
        /// <summary>Key in force at <see cref="PositionBeats"/> (song Key and key changes), or null.</summary>
        public MusicKey? CurrentKey { get { var cue = CurrentCue; return cue?.HarmonyTrack.KeyAt(PositionBeats); } }
        /// <summary>Chord-track chord at <see cref="PositionBeats"/>, or null.</summary>
        public MusicChord? CurrentChord { get { var cue = CurrentCue; return cue?.HarmonyTrack.ChordAt(PositionBeats); } }
        public string CurrentChordName { get { var cue = CurrentCue; return cue == null ? "" : cue.HarmonyTrack.ChordNameAt(PositionBeats); } }
        public double LastEntryBeat => Volatile.Read(ref _lastEntry);
        public double LastSourceProgress => Volatile.Read(ref _lastSourceProgress);
        public double LastTransitionStartBeat => Volatile.Read(ref _lastStart);
        private int _bridgeNotes, _drumNotes;
        public bool UsesHarmonicBridge => _harmonicBridge;
        public int BridgeNotesScheduled => Volatile.Read(ref _bridgeNotes);
        public int DrumNotesScheduled => Volatile.Read(ref _drumNotes);

        public MusicCue CurrentCue => Volatile.Read(ref _currentCue);
        public double Beat => Volatile.Read(ref _publishedBeat);
        public double Bpm => Volatile.Read(ref _publishedBpm);
        public bool IsTransitioning => Volatile.Read(ref _transitioning) != 0;
        public double TransitionProgress => Volatile.Read(ref _progress);
        public int DroppedNotes => Volatile.Read(ref _droppedNotes);
        /// <summary>Voices sounding in the last rendered sample, and the most at once since this engine started.</summary>
        public int ActiveVoices => Volatile.Read(ref _activeVoices);
        public int PeakVoices => Volatile.Read(ref _peakVoices);

        public AdaptiveMusicEngine(MusicCue initial, int sampleRate, double repeatFadeSeconds = 0,
            double repeatPauseSeconds = 0, double slowFadeInSeconds = 0, bool playOnce = false)
            : this(initial, sampleRate, null, repeatFadeSeconds, repeatPauseSeconds, slowFadeInSeconds, playOnce) { }

        /// <summary>Opt-in VST3 instruments (Windows x64 only). The original constructor remains available.</summary>
        public AdaptiveMusicEngine(MusicCue initial, int sampleRate, MusicPluginSession plugins, double repeatFadeSeconds = 0,
            double repeatPauseSeconds = 0, double slowFadeInSeconds = 0, bool playOnce = false)
            : this(initial, sampleRate, plugins, null, repeatFadeSeconds, repeatPauseSeconds, slowFadeInSeconds, playOnce) { }

        internal readonly MusicInsertRenderGraph InsertGraph;
        internal AdaptiveMusicEngine(MusicCue initial, int sampleRate, MusicPluginSession plugins, MusicInsertRenderGraph inserts,
            double repeatFadeSeconds, double repeatPauseSeconds, double slowFadeInSeconds, bool playOnce)
        {
            if (initial == null) throw new ArgumentNullException(nameof(initial));
            if (sampleRate < 8000 || sampleRate > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (!MusicTrack.Finite(repeatFadeSeconds) || repeatFadeSeconds < 0 || repeatFadeSeconds > 120)
                throw new ArgumentOutOfRangeException(nameof(repeatFadeSeconds));
            _repeatFadeSeconds = repeatFadeSeconds;
            _playOnce = playOnce;
            if (!MusicTrack.Finite(repeatPauseSeconds) || repeatPauseSeconds < 0 || repeatPauseSeconds > 120 ||
                !MusicTrack.Finite(slowFadeInSeconds) || slowFadeInSeconds < 0 || slowFadeInSeconds > 120)
                throw new ArgumentOutOfRangeException(nameof(slowFadeInSeconds));
            _repeatPauseSeconds = repeatPauseSeconds; _slowFadeInSeconds = slowFadeInSeconds;
            _sampleRate = sampleRate; _currentCue = initial; _bpm = initial.Tempo.At(0); _publishedBpm = _bpm;
            _mixProcessor = new MusicMixProcessor(sampleRate);
            _mixRight = new MusicMixProcessor(sampleRate);
            _masterMixer = new MusicMasterMixer(sampleRate);
            _masterDrive = new MusicMasterDrive(sampleRate);
            _masterAmp = new MusicAmp(sampleRate);
            _masterSpace = new MusicSpace(sampleRate);
            _masterColor = new MusicColor(sampleRate);
            _masterStutter = new MusicStutter(sampleRate);
            _masterLimiter = new MusicLimiter(sampleRate);
            _decks[0].Reset(initial, 0); _nextBar = initial.Meter.NextBarLine(0);
            InsertGraph = inserts;
            _decks[0].Effects = MusicTrackEffects.For(initial, sampleRate, inserts != null);
            _decks[0].Convolution = MusicConvolution.For(initial.Mixer,initial.PlaybackVersion,sampleRate);
            _rhythm.Reset(initial, 0);
            InitializePlugins(plugins, initial);
            EnsureSynth(initial);
            for (int key = 0; key < 128; key++) _frequency[key] = 440 * Math.Pow(2, (key - 69) / 12.0);
        }

        // Commands allocate on the caller thread only. During a transition, the latest request waits.
        public void TransitionTo(MusicCue cue, double transitionBeats = 4)
        {
            if (cue == null || !MusicTrack.Finite(transitionBeats) || transitionBeats < .25 || transitionBeats > 32)
                throw new ArgumentException("Invalid transition.");
            EnsureSynth(cue);
            Interlocked.Exchange(ref _mailbox, new Request(cue, transitionBeats) { Effects = MusicTrackEffects.For(cue, _sampleRate), Convolution=MusicConvolution.For(cue.Mixer,cue.PlaybackVersion,_sampleRate), Plugins = PreparePlugins(cue) });
        }

        /// <summary>MaximumSeconds bounds wait plus blend; short remaining ToStart cues finish first.</summary>
        public void TransitionTo(MusicCue cue, MusicTransitionMode mode, double maximumSeconds = 8, double transitionBeats = 4, double fixedTransitionSeconds = 0, bool alignBars = false, double incomingFadeSeconds = 0, double gapSeconds = 0, double outgoingFadeSeconds = 0)
        {
            if (cue == null || !Enum.IsDefined(typeof(MusicTransitionMode), mode) || !MusicTrack.Finite(maximumSeconds) ||
                maximumSeconds < .05 || maximumSeconds > 120 || !MusicTrack.Finite(transitionBeats) || transitionBeats < .25 || transitionBeats > 32 ||
                !MusicTrack.Finite(fixedTransitionSeconds) || fixedTransitionSeconds < 0 || fixedTransitionSeconds > maximumSeconds)
                throw new ArgumentException("Invalid positioned transition.");
            if (!MusicTrack.Finite(incomingFadeSeconds) || incomingFadeSeconds < 0 || incomingFadeSeconds > 120 ||
                !MusicTrack.Finite(gapSeconds) || gapSeconds < 0 || gapSeconds > 120 ||
                (gapSeconds > 0 && incomingFadeSeconds <= 0)) throw new ArgumentException("Invalid fade timing.");
            if (!MusicTrack.Finite(outgoingFadeSeconds) || outgoingFadeSeconds < 0 || outgoingFadeSeconds > 120)
                throw new ArgumentException("Invalid outgoing fade.");
            EnsureSynth(cue);
            Interlocked.Exchange(ref _mailbox, new Request(cue, transitionBeats, mode, maximumSeconds, fixedTransitionSeconds, alignBars, incomingFadeSeconds, gapSeconds) { OutgoingSeconds = outgoingFadeSeconds, Effects = MusicTrackEffects.For(cue, _sampleRate), Convolution=MusicConvolution.For(cue.Mixer,cue.PlaybackVersion,_sampleRate), Plugins = PreparePlugins(cue) });
        }

        public void SetLayerGain(MusicLayer layer, float gain)
        {
            if ((int)layer < 0 || (int)layer > 3 || !MusicTrack.Finite(gain) || gain < 0 || gain > 1)
                throw new ArgumentException("Invalid layer gain.");
            Volatile.Write(ref _layerTarget[(int)layer], gain);
        }

        public void Render(float[] output)
            => RenderInternal(output, false, output?.Length ?? 0);

        public void RenderStereo(float[] interleaved)
            => RenderInternal(interleaved, true, interleaved?.Length ?? 0);

        /// <summary>Renders the first <paramref name="frames"/> stereo frames of a larger buffer (VST3 insert preparation blocks).</summary>
        internal void RenderStereo(float[] interleaved, int frames)
        {
            if (interleaved == null || frames < 0 || frames * 2 > interleaved.Length) throw new ArgumentException("Invalid frame count.");
            RenderInternal(interleaved, true, frames * 2);
        }

        // VST3 preparation: trace buffers record, per rendered frame, the tempo, the beat step (0 while a pause holds the note clock)
        // and the position in the current cue. Null (default) = normal playback, unchanged.
        internal double[] TraceBpm, TraceStep, TraceLocal;

        private void RenderInternal(float[] output, bool stereo, int count)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (stereo && count % 2 != 0) throw new ArgumentException("Stereo buffer must contain complete frames.");
            float peakLeft = 0, peakRight = 0;
            var levelMeter = Volatile.Read(ref _levelMeter);
            int activeVoices = _activeVoices, peakVoices = _peakVoices;
            // No locks, Unity API calls or allocations in this method and its audio-thread callees.
            for (int sample = 0; sample < count; sample += stereo ? 2 : 1)
            {
                int frame = stereo ? sample >> 1 : sample;
                // Cue automation owns the clock outside crossfades; transition tempo owns it during a crossfade.
                if (_transitioning == 0) _bpm = _currentCue.Tempo.At(_beat - _decks[_current].Start, !_playOnce);
                _speed = 1;
                if (_transitioning == 0 && _currentCue.PlaybackVersion >= MusicPlaybackVersion.V3)
                {
                    var speedLane = _currentCue.MasterAutomation?[(int)MusicAutomationTarget.Speed];
                    if (speedLane != null) _speed = speedLane.Evaluate(LocalPosition(_currentCue, _beat - _decks[_current].Start)); // the cue's tempo map already includes it
                }
                var request = Interlocked.Exchange(ref _mailbox, null);
                if (request != null) { _waiting = request; _plannedBar = -1; }
                if (_transitioning != 0)
                {
                    double p = _fixedSeconds > 0 ? Math.Min(1, (_samples - _transitionSampleStart) / (_sampleRate * _fixedSeconds)) :
                        _beat + 1e-9 >= _transitionStart + _transitionLength ? 1 : Math.Min(1, (_beat - _transitionStart) / _transitionLength);
                    _progress = p;
                    _bpm = _fromBpm + (_decks[1 - _current].Cue.Bpm - _fromBpm) * Smooth(p);
                    if (p >= 1) FinishTransition();
                }
                if (_transitioning == 0 && _waiting != null && _waiting.Positioned)
                {
                    if (ReferenceEquals(_waiting.Cue, _currentCue)) _waiting = null;
                    else
                    {
                        if (_plannedBar < 0) PlanPositioned(_waiting);
                        if (_beat + 1e-9 >= _plannedBar)
                        { BeginTransition(_waiting); _waiting = null; _plannedBar = -1; }
                    }
                }
                if (_beat + 1e-9 >= _nextBar)
                {
                    if (_transitioning == 0 && _waiting != null && !_waiting.Positioned)
                    {
                        if (ReferenceEquals(_waiting.Cue, _currentCue)) _waiting = null;
                        else
                        {
                            if (_plannedBar < 0) _plannedBar = ChooseBoundary(_waiting);
                            if (_beat + 1e-9 >= _plannedBar) { BeginTransition(_waiting); _waiting = null; _plannedBar = -1; }
                        }
                    }
                    _nextBar += _currentCue.Meter.BarLengthAt(_nextBar - _decks[_current].Start);
                }
                if (!_playOnce && _repeatFadeSeconds > 0 && _transitioning == 0 && _waiting == null)
                {
                    var meter = _currentCue.Meter;
                    // Use the last complete bar of excerpts ending mid-bar. Never wrap raw notes.
                    double cycle = Math.Max(meter.BarLengthAt(0), meter.BarFloor(_currentCue.LoopBeats));
                    double half = _repeatFadeSeconds * _bpm / 120;
                    double minimumCycle = (_repeatFadeSeconds / 2 + (_slowFadeInSeconds > 0 ? _slowFadeInSeconds : _repeatFadeSeconds / 2)) * _bpm / 60;
                    cycle = Math.Max(cycle, meter.BarCeil(minimumCycle));
                    double midpoint = _decks[_current].Start + cycle;
                    if (_beat + 1e-9 >= midpoint - half) BeginRepeat(midpoint, half);
                }
                double elapsed = (_samples - _transitionSampleStart) / (double)_sampleRate;
                bool wholeFade = _transitioning != 0 && _wholeMixFade;
                bool quiet = wholeFade && elapsed >= _fadeOutSeconds && elapsed < _fadeOutSeconds + _silenceSeconds;
                if (wholeFade && elapsed >= _fadeOutSeconds && !_outgoingCleared)
                {
                    for (int i = 0; i < _voices.Length; i++)
                        if (_voices[i].Deck == _current || _voices[i].Deck < 0) _voices[i].Active = false;
                    _outgoingCleared = true;
                }
                if ((!_endHandoff || _transitioning == 0) && (!wholeFade || elapsed < _fadeOutSeconds)) Schedule(_current);
                if (_transitioning != 0 && !quiet && (!wholeFade || elapsed >= _fadeOutSeconds + _silenceSeconds) &&
                    (_customFade ? elapsed >= (_overlapFade ? 0 : _fadeOutSeconds + _silenceSeconds) : _beat + 1e-9 >= _incomingActivation))
                {
                    if (_customFade && !_rhythmHandedOff)
                    {
                        _incomingActivation = _beat;
                        _decks[1 - _current].Seek(_decks[1 - _current].Cue, _beat - _entryBeat, _entryBeat);
                    }
                    if ((_alignBars || _customFade) && !_rhythmHandedOff)
                    {
                        var incoming = _decks[1 - _current];
                        if (incoming.Cue.HasDrums) { _rhythm.Seek(incoming.Cue, incoming.Start, _entryBeat); _rhythm.Plugins = incoming.Plugins?.Rhythm; }
                        _rhythmHandedOff = true;
                    }
                    if (!_incomingPrimed) { PrimeIncoming(); _incomingPrimed = true; }
                    Schedule(1 - _current);
                }
                if (_pendingRhythm != null && _beat + 1e-9 >= _rhythmSwitchBeat)
                {
                    double local = _rhythmSwitchBeat - _decks[_current].Start;
                    double phase = Math.Ceiling(local - 1e-8);
                    _rhythm.Seek(_pendingRhythm, _rhythmSwitchBeat - phase, phase);
                    _rhythm.Plugins = RhythmPlugins(_pendingRhythm);
                    _pendingRhythm = null;
                }
                if (!quiet) ScheduleDeck(_rhythm, -1, true);
                if (_transitioning != 0 && _harmonicBridge) ScheduleBridge();
                for (int layer = 0; layer < 4; layer++)
                    _layerGain[layer] += (Volatile.Read(ref _layerTarget[layer]) - _layerGain[layer]) * (1 - Math.Exp(-1.0 / (_sampleRate * .05)));
                double sum = 0, sumRight = 0;
                UpdateAutomation();
                if (_pluginSession != null) ReadPlugins(quiet);
                int active = 0;
                // Slot order is the mixing order; only slots below _voiceEnd can be active.
                for (int i = 0; i < _voiceEnd; i++)
                {
                    ref var voice = ref _voices[i];
                    if (!voice.Active) continue;
                    active++;
                    double gain = DeckGain(voice.Deck, voice.Layer) * _layerGain[(int)voice.Layer];
                    if (voice.Bus != null) MixVoice(ref voice, gain, ref voice.Bus.Left, ref voice.Bus.Right);
                    else MixVoice(ref voice, gain, ref sum, ref sumRight);
                }
                if (_pluginSession != null) MixPlugins(ref sum, ref sumRight);
                MixTrackEffects(ref sum, ref sumRight);
                while (_voiceEnd > 0 && !_voices[_voiceEnd - 1].Active) _voiceEnd--;
                activeVoices = active;
                if (active > peakVoices) peakVoices = active;
                MixMaster(sum, sumRight, _beat - _decks[_current].Start, out float left, out float right);
                if (wholeFade)
                {
                    double gain = elapsed < _fadeOutSeconds ? 1 - Smooth(Math.Min(1, elapsed / _fadeOutSeconds)) :
                        elapsed < _fadeOutSeconds + _silenceSeconds ? 0 :
                        Smooth(Math.Min(1, (elapsed - _fadeOutSeconds - _silenceSeconds) / _fadeInSeconds));
                    left *= (float)gain; right *= (float)gain;
                }
                peakLeft = Math.Max(peakLeft, Math.Abs(left)); peakRight = Math.Max(peakRight, Math.Abs(right));
                levelMeter?.AddFrame(left,right);
                if (stereo) { output[sample] = left; output[sample + 1] = right; }
                else output[sample] = (left + right) * .5f;
                if (TraceBpm != null)
                { TraceBpm[frame] = _bpm; TraceStep[frame] = quiet ? 0 : _bpm / (60 * _sampleRate); TraceLocal[frame] = _beat - _decks[_current].Start; }
                // Keep the first bar intact: the note clock rests together with the audio.
                if (!quiet) _beat += _bpm / (60 * _sampleRate);
                _samples++;
            }
            Volatile.Write(ref _publishedBeat, _beat);
            levelMeter?.Publish();
            Volatile.Write(ref _peakLeft, peakLeft); Volatile.Write(ref _peakRight, peakRight);
            Volatile.Write(ref _publishedBpm, _bpm);
            Volatile.Write(ref _activeVoices, activeVoices); Volatile.Write(ref _peakVoices, peakVoices);
            Volatile.Write(ref _publishedPosition, LocalPosition(_currentCue, _beat - _decks[_current].Start));
        }

        private static double Smooth(double x) => x * x * (3 - 2 * x);

        private void BeginRepeat(double midpoint, double halfBeats)
        {
            _overlapFade = false; _customFade = false;
            // Same two-deck sequential fade, prepared without audio-thread allocations.
            _positioned = true; _endHandoff = false; _alignBars = true;
            _rhythmHandedOff = false; _harmonicBridge = false;
            _fadeOutSeconds = _repeatFadeSeconds / 2;
            _fadeInSeconds = _slowFadeInSeconds > 0 ? _slowFadeInSeconds : _fadeOutSeconds;
            _silenceSeconds = _repeatPauseSeconds;
            _wholeMixFade = _slowFadeInSeconds > 0 || _silenceSeconds > 0; _outgoingCleared = false;
            _fixedSeconds = _fadeOutSeconds + _silenceSeconds + _fadeInSeconds; _transitionSampleStart = _samples;
            _transitionStart = midpoint - halfBeats; _transitionLength = (_fadeOutSeconds + _fadeInSeconds) * _bpm / 60;
            _fromBpm = _bpm; _progress = 0; _entryBeat = 0;
            _incomingActivation = midpoint; _incomingPrimed = true;
            _decks[1 - _current].Reset(_currentCue, midpoint);
            _decks[1 - _current].Effects = _decks[_current].Effects; // same parts: the repeat shares the buses (tails continue)
            _decks[1 - _current].Convolution = _decks[_current].Convolution;
            _decks[1 - _current].Plugins = _decks[_current].Plugins?.Bank.Decks[1 - _current];
            _decks[1 - _current].Repeated = true;
            _decks[1 - _current].Pass = _decks[_current].Pass + 1; // the restart is the next pass
            _pendingRhythm = null;
            _lastEntry = 0; _lastSourceProgress = 1; _lastStart = _transitionStart;
            LastHandoffBeat = midpoint; LastTransitionStartSeconds = _samples / (double)_sampleRate;
            RepeatTransitions++;
            Volatile.Write(ref _transitioning, 1);
        }

        private void BeginTransition(Request request)
        {
            // Preserve the exact musical grid, not the first audio sample just after it.
            _positioned = request.Positioned;
            _customFade = request.IncomingSeconds > 0;
            _overlapFade = _customFade && request.GapSeconds == 0;
            _fixedSeconds = FadeDuration(request); _transitionSampleStart = _samples;
            _fadeOutSeconds = OutgoingDuration(request);
            _fadeInSeconds = _customFade ? request.IncomingSeconds : _slowFadeInSeconds > 0 ? _slowFadeInSeconds : _fadeOutSeconds;
            _silenceSeconds = request.GapSeconds; _outgoingCleared = false;
            _wholeMixFade = !_overlapFade && (_customFade || (_slowFadeInSeconds > 0 && request.FixedSeconds > 0));
            LastTransitionStartSeconds = _samples / (double)_sampleRate;
            _endHandoff = _positioned && _waitForEnd;
            _alignBars = request.AlignBars && _fixedSeconds > 0 && !_endHandoff && request.Mode != MusicTransitionMode.ForceFadeToStart;
            _rhythmHandedOff = false;
            _transitionStart = _positioned ? _plannedBar : _nextBar;
            _transitionLength = _positioned ? _positionedLength : TransitionLength(request); _fromBpm = _bpm; _progress = 0;
            double midpoint = _transitionStart + FadeOutBeats(request, _bpm);
            double fraction = LocalPosition(_currentCue, (_alignBars ? midpoint : _transitionStart) - _decks[_current].Start) / _currentCue.LoopBeats;
            _entryBeat = _positioned && request.Mode == MusicTransitionMode.MatchProgress ? fraction * request.Cue.LoopBeats : 0;
            if (_alignBars)
                _entryBeat = Math.Min(request.Cue.Meter.BarFloor(request.Cue.LoopBeats - 1e-8), request.Cue.Meter.Nearest(_entryBeat));
            _lastEntry = _entryBeat; _lastSourceProgress = fraction; _lastStart = _transitionStart;
            // Fixed-time handoff reserves the first half for fade-out and the second for fade-in.
            _harmonicBridge = !_endHandoff && (!_positioned || request.Mode != MusicTransitionMode.ForceFadeToStart) &&
                (_overlapFade || (_fixedSeconds == 0 && CanBridge(request.Cue, _transitionStart, _transitionLength, _entryBeat)));
            for (int i = 0; i < _matches.Length; i++) _matches[i] = -1;
            for (int i = 0; i < _currentCue.PartCount; i++)
            {
                var from = _currentCue.Parts[i];
                if (from.Layer == MusicLayer.Drums || from.Instrument.Noise != null || string.IsNullOrEmpty(from.InstrumentId) || from.NoteCount == 0 || from.Gain == 0) continue;
                for (int j = 0; j < request.Cue.PartCount; j++)
                {
                    var to = request.Cue.Parts[j];
                    bool used = false;
                    for (int k = 0; k < i; k++) if (_matches[k] == j) { used = true; break; }
                    if (!used && to.Gain > 0 && to.NoteCount > 0 && to.Layer == from.Layer && to.InstrumentId == from.InstrumentId)
                    {
                        _matches[i] = j;
                        _bridgeKeys[i] = MusicHarmony.KeyBefore(from, (_beat - _decks[_current].Start) % _currentCue.LoopBeats);
                        break;
                    }
                }
            }
            bool anyMatch = false;
            foreach (int match in _matches) if (match >= 0) { anyMatch = true; break; }
            _harmonicBridge &= anyMatch;
            _bridgeNext = _transitionStart + _transitionLength * .25;
            double activation = _positioned && !_endHandoff ? (_harmonicBridge ? .75 : .5) : 0;
            if (_wholeMixFade && !_endHandoff) activation = _fadeOutSeconds / _fixedSeconds;
            if (_overlapFade) activation = 0;
            // Integral of smoothstep tempo over real seconds; keep the note clock in sync with fade timing.
            _incomingActivation = _transitionStart + (_fixedSeconds > 0 ? _fixedSeconds / 60 *
                (_fromBpm * activation + (request.Cue.Bpm - _fromBpm) * (activation * activation * activation - .5 * Math.Pow(activation, 4))) :
                _transitionLength * activation);
            _decks[1 - _current].Seek(request.Cue, _incomingActivation - _entryBeat, _entryBeat);
            _decks[1 - _current].Effects = request.Effects;
            _decks[1 - _current].Convolution = request.Convolution;
            _decks[1 - _current].Plugins = request.Plugins?.Decks[1 - _current];
            LastHandoffBeat = _incomingActivation;
            _incomingPrimed = _entryBeat == 0;
            if (_endHandoff)
                for (int i = 0; i < _voices.Length; i++)
                    if (_voices[i].Active && _voices[i].Deck == _current)
                    { _voices[i].Deck = -3; if (_voices[i].ReleaseAge < 0) _voices[i].ReleaseAge = _voices[i].Age; }
            Volatile.Write(ref _transitioning, 1);
        }

        private static double LocalPosition(MusicCue cue, double beat) =>
            (((beat + 1e-9) % cue.LoopBeats) + cue.LoopBeats) % cue.LoopBeats;

        private static double HalfFadeBeats(double seconds, double from, double to) =>
            seconds / 60 * (from * .5 + (to - from) * .09375);

        private double FadeDuration(Request request) => request.IncomingSeconds > 0 ?
            (request.GapSeconds == 0 ? Math.Max(OutgoingDuration(request), request.IncomingSeconds) :
                OutgoingDuration(request) + request.GapSeconds + request.IncomingSeconds) :
            request.FixedSeconds > 0 && _slowFadeInSeconds > 0 ? request.FixedSeconds / 2 + _slowFadeInSeconds : request.FixedSeconds;

        private static double OutgoingDuration(Request request) => request.OutgoingSeconds > 0 ? request.OutgoingSeconds : request.FixedSeconds > 0 ? request.FixedSeconds / 2 : 1;

        private double FadeOutBeats(Request request, double from)
        {
            if (request.IncomingSeconds > 0 && request.GapSeconds == 0) return 0;
            double total = FadeDuration(request);
            if (total <= 0) return 0;
            double p = OutgoingDuration(request) / total;
            return total / 60 * (from * p + (request.Cue.Bpm - from) * (p * p * p - .5 * p * p * p * p));
        }

        private void PlanPositioned(Request request)
        {
            // Use the slower BPM for a conservative wall-clock upper bound on the blend.
            double secondsPerBeat = 60 / Math.Min(_bpm, request.Cue.Bpm);
            _positionedLength = Math.Min(TransitionLength(request), request.MaximumSeconds / secondsPerBeat);
            if (request.FixedSeconds > 0 || request.IncomingSeconds > 0) _positionedLength = FadeDuration(request) * (_bpm + request.Cue.Bpm) / 120;
            _waitForEnd = false;
            double local = LocalPosition(_currentCue, _beat - _decks[_current].Start);
            double remaining = _currentCue.LoopBeats - local;
            if (request.Mode == MusicTransitionMode.ToStart && remaining * 60 / _bpm < request.MaximumSeconds)
            {
                // Exact end can be between bar lines. Do not loop the old tune again.
                _plannedBar = _decks[_current].Start + (Math.Floor((_beat - _decks[_current].Start + 1e-8) / _currentCue.LoopBeats) + 1) * _currentCue.LoopBeats;
                _waitForEnd = true;
                return;
            }
            if (request.Mode == MusicTransitionMode.ForceFadeToStart) { _plannedBar = _beat; return; }
            if (request.AlignBars && request.FixedSeconds > 0)
            {
                // Wait as needed: the fade midpoint, not its start, is the source downbeat.
                double half = FadeOutBeats(request, _bpm);
                double origin = _decks[_current].Start;
                _plannedBar = Math.Max(_beat, origin + _currentCue.Meter.BarCeil(_beat + half - origin - 1e-9) - half);
                return;
            }
            double blendSeconds = request.FixedSeconds > 0 ? request.FixedSeconds : _positionedLength * secondsPerBeat;
            double latest = _beat + Math.Max(0, request.MaximumSeconds - blendSeconds) * _bpm / 60;
            _plannedBar = Math.Min(_nextBar, latest);
            for (int bars = 0; bars <= MusicHarmony.MaxWaitBars; bars++)
            {
                double boundary = _decks[_current].Start + _currentCue.Meter.Advance(_nextBar - _decks[_current].Start, bars);
                if (boundary > latest + 1e-9) break;
                double entry = request.Mode == MusicTransitionMode.MatchProgress ?
                    LocalPosition(_currentCue, boundary - _decks[_current].Start) / _currentCue.LoopBeats * request.Cue.LoopBeats : 0;
                if (CanBridge(request.Cue, boundary, _positionedLength, entry)) { _plannedBar = boundary; break; }
            }
        }

        private void PrimeIncoming()
        {
            // Restore held notes at a middle entry with a new attack, bounded to 128 preceding notes per part.
            var deck = _decks[1 - _current];
            foreach (var part in deck.Cue.Parts)
            {
                if (part.Layer == MusicLayer.Drums || part.NoteCount == 0) continue;
                int lo = 0, hi = part.NoteCount;
                while (lo < hi) { int mid = (lo + hi) / 2; if (part.Notes[mid].Beat < _entryBeat - 1e-8) lo = mid + 1; else hi = mid; }
                int limit = MusicPlaybackVersion.VoiceLimit(deck.Cue.PlaybackVersion);
                for (int n = lo - 1, read = 0; n >= 0 && read < limit; n--, read++)
                {
                    var note = part.Notes[n];
                    double remaining = note.Beat + note.Duration - _entryBeat;
                    if (remaining > 1e-8) StartVoice(1 - _current, deck.Cue, part, new MusicNote(0, remaining, note.Key, note.Velocity), _incomingActivation,
                        part.Instrument.Noise != null ? _entryBeat - note.Beat : 0);
                }
            }
        }

        private static double TransitionLength(Request request) =>
            request.Cue.Meter.BarCeil(request.Beats);

        private bool CanBridge(MusicCue target, double boundary, double length, double entry = 0)
        {
            double step = Math.Max(MusicHarmony.Resolution, length / MaxBridgeChecks);
            for (double offset = 0; offset < length; offset += step)
                if (!MusicHarmony.Compatible(MusicHarmony.MaskAt(_currentCue, boundary - _decks[_current].Start + offset),
                    MusicHarmony.MaskAt(target, entry + offset))) return false;
            return true;
        }

        private double ChooseBoundary(Request request)
        {
            double length = TransitionLength(request);
            for (int bars = 0; bars <= MusicHarmony.MaxWaitBars; bars++)
            {
                double boundary = _decks[_current].Start + _currentCue.Meter.Advance(_nextBar - _decks[_current].Start, bars);
                if (CanBridge(request.Cue, boundary, length)) return boundary;
            }
            return _nextBar; // No suitable chord window: fade at the next bar rather than waiting indefinitely.
        }

        private void ScheduleBridge()
        {
            if (_beat + 1e-9 < _bridgeNext || _bridgeNext >= _transitionStart + _transitionLength * .75) return;
            double offset = _bridgeNext - _transitionStart;
            var target = _decks[1 - _current].Cue;
            double targetBeat = _entryBeat + (_positioned ? Math.Max(0, _bridgeNext - _incomingActivation) : offset);
            int fromMask = MusicHarmony.MaskAt(_currentCue, _bridgeNext - _decks[_current].Start);
            int toMask = MusicHarmony.MaskAt(target, targetBeat);
            int common = fromMask & toMask;
            // Overlaps need only a compatible local window, not five seconds of identical harmony.
            if (_overlapFade && common == 0)
            { _bridgeNext += MusicHarmony.Resolution; return; }
            for (int i = 0; i < _currentCue.PartCount; i++)
            {
                if (_matches[i] < 0) continue;
                int destination = MusicHarmony.KeyBefore(target.Parts[_matches[i]], targetBeat % target.LoopBeats);
                int previous = _bridgeKeys[i];
                int desired = previous + Math.Sign(destination - previous) * Math.Min(2, Math.Abs(destination - previous));
                int key = MusicHarmony.NearestCommonKey(common, desired);
                if (key < 0 || Math.Abs(key - previous) > MusicHarmony.MaximumBridgeLeap) continue;
                StartVoice(-2, _currentCue, _currentCue.Parts[i], new MusicNote(0, MusicHarmony.Resolution * .85, key, .65f), _bridgeNext);
                _bridgeKeys[i] = key;
                Interlocked.Increment(ref _bridgeNotes);
            }
            _bridgeNext += MusicHarmony.Resolution;
        }

        private void FinishTransition()
        {
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].Deck == _current) _voices[i].Active = false; // outgoing gain is already zero
            _current = 1 - _current;
            Volatile.Write(ref _currentCue, _decks[_current].Cue);
            // One rhythm scheduler owns attacks. Old hits keep their natural release tails.
            // A cue without percussion inherits the current groove rather than creating a silent gap.
            if (_currentCue.HasDrums && !_alignBars && !_customFade)
            {
                if (_positioned)
                { _pendingRhythm = _currentCue; _rhythmSwitchBeat = Math.Ceiling(_transitionStart + _transitionLength - 1e-8); }
                else { _rhythm.Seek(_currentCue, _transitionStart, _transitionLength); _rhythm.Plugins = _decks[_current].Plugins?.Rhythm; }
            }
            _bpm = _currentCue.Tempo.At(_beat - _decks[_current].Start, !_playOnce);
            // Align future requests to the incoming cue's own bar grid.
            double local = _beat - _decks[_current].Start;
            _nextBar = _decks[_current].Start + _currentCue.Meter.NextBarLine(local);
            Volatile.Write(ref _transitioning, 0);
        }

        private double DeckGain(int deck, MusicLayer layer)
        {
            if (_transitioning != 0 && _overlapFade)
            {
                if (deck == -2) return .65 * Math.Sin(Math.PI * _progress);
                if (deck < 0) return 1;
                double elapsed = (_samples - _transitionSampleStart) / (double)_sampleRate;
                return deck == _current ? 1 - Smooth(Math.Min(1, elapsed / _fadeOutSeconds)) :
                    Smooth(Math.Min(1, elapsed / _fadeInSeconds));
            }
            if (_transitioning != 0 && _wholeMixFade)
            {
                if (deck < 0) return 1;
                double elapsed = (_samples - _transitionSampleStart) / (double)_sampleRate;
                return deck == _current ? (elapsed < _fadeOutSeconds ? 1 : 0) :
                    (elapsed >= _fadeOutSeconds + _silenceSeconds ? 1 : 0);
            }
            if (deck == -1 || deck == -3) return 1;
            if (deck == -2)
                return _transitioning == 0 ? 0 : Smooth(Math.Min(1, Math.Max(0, (_progress - .25) * 8))) *
                    (1 - Smooth(Math.Min(1, Math.Max(0, (_progress - .625) * 8))));
            if (_transitioning == 0) return deck == _current ? 1 : 0;
            if (_endHandoff) return deck == _current ? 0 : 1;
            double p = _progress;
            if (_harmonicBridge)
                return deck == _current ? 1 - Smooth(Math.Min(1, p * 4)) : Smooth(Math.Max(0, (p - .75) * 4));
            return deck == _current ? 1 - Smooth(Math.Min(1, p * 2)) : Smooth(Math.Max(0, (p - .5) * 2));
        }

        private void Schedule(int deckId)
            => ScheduleDeck(_decks[deckId], deckId, false);

        private void ScheduleDeck(Deck deck, int deckId, bool drumsOnly)
        {
            for (int partId = 0; partId < deck.Cue.Parts.Length; partId++)
            {
                var part = deck.Cue.Parts[partId];
                if ((part.Layer == MusicLayer.Drums) != drumsOnly) continue;
                if (part.Notes.Length == 0) continue;
                while (true)
                {
                    if (part.Instrument.Noise != null && !part.Instrument.Noise.RepeatWithCue && (deck.Repeated || deck.Loop[partId] > 0)) break;
                    if (_playOnce && deck.Loop[partId] > 0) break;
                    if (!drumsOnly && _repeatFadeSeconds > 0 && deck.Loop[partId] > 0) break;
                    var note = part.Notes[deck.Index[partId]];
                    double start = deck.Start + deck.Loop[partId] * deck.Cue.LoopBeats + note.Beat;
                    if (start > _beat + 1e-9) break;
                    // V3 note variation: left out on this pass (skip chance, every-n-th pass).
                    if (!note.HasVariation || deck.Cue.PlaybackVersionOf(part) < MusicPlaybackVersion.V3 || MusicVariation.Plays(note, deck.Pass + deck.Loop[partId], partId, deck.Cue.VariationSeed))
                    {
                        StartVoice(deckId, deck.Cue, part, note, start);
                        if (drumsOnly && part.Gain > 0 && note.Velocity > 0) Interlocked.Increment(ref _drumNotes);
                    }
                    if (++deck.Index[partId] == part.Notes.Length)
                    { deck.Index[partId] = 0; deck.Loop[partId]++; }
                }
            }
        }
    }
}
