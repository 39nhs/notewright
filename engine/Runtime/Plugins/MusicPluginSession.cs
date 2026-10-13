using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graze.Presentation.Audio
{
    /// <summary>Owns prewarmed native instruments and two banks for the engine's cue decks. Dispose after stopping audio.</summary>
    public sealed class MusicPluginSession : IDisposable
    {
        readonly string executable; readonly MusicPluginCatalog catalog; readonly string[] approved;
        readonly MusicPluginMod mod; readonly int sampleRate, blockFrames; readonly double aheadSeconds;
        readonly bool loop;
        readonly object gate = new object(); readonly Dictionary<MusicCue, MusicPluginBank> banks = new Dictionary<MusicCue, MusicPluginBank>();
        readonly List<Task> preparation = new List<Task>(); readonly List<string> diagnostics = new List<string>(); bool disposed;
        int enabled = 1;
        public bool Enabled { get => Volatile.Read(ref enabled) != 0; set => Volatile.Write(ref enabled, value ? 1 : 0); }
        public int SampleRate => sampleRate;
        public bool Looping => loop;
        public string[] Diagnostics { get { lock (gate) return diagnostics.ToArray(); } }
        public MusicPluginSession(string hostExecutable, MusicPluginCatalog catalog, IEnumerable<string> approvedClassIds,
            MusicPluginMod mod, int sampleRate, double aheadSeconds = 3, int blockFrames = 512, bool loop = true)
        {
            if (catalog == null || approvedClassIds == null || mod == null || sampleRate < 8000 || sampleRate > 192000 ||
                !MusicTrack.Finite(aheadSeconds) || aheadSeconds < 2 || aheadSeconds > 4 || blockFrames < 16 || blockFrames > 4096)
                throw new ArgumentException("Invalid plugin session configuration.");
            foreach(var target in mod.Targets)if(target.Outputs.Count>0)throw new ArgumentException("Multi-output mods require MusicPluginInsertRenderer prepared playback, not MusicPluginSession.");
            executable = hostExecutable; this.catalog = catalog; approved = new List<string>(approvedClassIds).ToArray(); this.mod = mod;
            this.sampleRate = sampleRate; this.aheadSeconds = aheadSeconds; this.blockFrames = blockFrames;
            this.loop = loop;
        }
        /// <summary>Load/state restore/warm-up and initial PCM ahead, without blocking the caller's audio thread.</summary>
        public Task PrepareAsync(MusicCue cue)
        {
            if (cue == null) throw new ArgumentNullException(nameof(cue));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(MusicPluginSession));
                MusicPluginBank existing; if (banks.TryGetValue(cue, out existing)) return existing.Preparation;
                var bank = new MusicPluginBank(cue, loop); banks.Add(cue, bank);
                var jobs = new List<Task>();
                for (int d = 0; d < 2; d++) for (int i = 0; i < cue.PartCount; i++)
                {
                    var part = cue.Parts[i]; var target = mod.Find(cue, part); if (target == null) continue;
                    int deck = d, index = i;
                    jobs.Add(Task.Run(() =>
                    {
                        MusicPluginTrack track = null;
                        try
                        {
                            var plugin = catalog.Resolve(target.Plugin, approved);
                            track = new MusicPluginTrack(executable, plugin, cue, part, target, sampleRate, blockFrames, aheadSeconds, loop);
                            // Publish before waiting: disposal must be able to abort even a stuck native load.
                            lock (gate) { if (disposed) { track.Dispose(); return; } bank.Decks[deck].Tracks[index] = track; }
                            track.Start(); track.WaitReady(60000);
                        }
                        catch (Exception e)
                        {
                            track?.Dispose(); lock (gate) { diagnostics.Add(cue.Name + "/" + part.InstrumentId + ": " + e.Message); }
                        }
                    }));
                }
                bank.Preparation = Task.WhenAll(jobs); preparation.Add(bank.Preparation); return bank.Preparation;
            }
        }
        internal MusicPluginBank Get(MusicCue cue)
        {
            lock (gate) { if (disposed) return null; MusicPluginBank result; return banks.TryGetValue(cue, out result) ? result : null; }
        }
        public MusicPluginState CaptureState(MusicCue cue, int partIndex)
        {
            var bank = Get(cue); if (bank == null || partIndex < 0 || partIndex >= cue.PartCount || bank.Decks[0].Tracks[partIndex] == null)
                throw new InvalidOperationException("Prepare this cue's plugin first.");
            return bank.Decks[0].Tracks[partIndex].CaptureState();
        }
        public void SetParameter(MusicCue cue, int partIndex, string key, double normalizedValue)
        {
            if (!MusicTrack.Finite(normalizedValue) || normalizedValue < 0 || normalizedValue > 1) throw new ArgumentException("Parameter must be 0..1.");
            var bank = Get(cue); if (bank == null || partIndex < 0 || partIndex >= cue.PartCount) throw new ArgumentException("Unprepared cue/part.");
            for (int d = 0; d < 2; d++) bank.Decks[d].Tracks[partIndex]?.SetParameter(key, normalizedValue);
        }
        public string Failure(MusicCue cue, int partIndex)
        { var bank = Get(cue); if (bank == null || partIndex < 0 || partIndex >= cue.PartCount) return "Cue is not prepared."; return bank.Decks[0].Tracks[partIndex]?.Failure; }
        public long Underruns(MusicCue cue, int partIndex)
        { var bank = Get(cue); if (bank == null || partIndex < 0 || partIndex >= cue.PartCount) return 0; return (bank.Decks[0].Tracks[partIndex]?.Underruns ?? 0) + (bank.Decks[1].Tracks[partIndex]?.Underruns ?? 0); }
        public int WorkerProcessId(MusicCue cue, int partIndex, int deck = 0)
        { var bank = Get(cue); if (bank == null || partIndex < 0 || partIndex >= cue.PartCount || deck < 0 || deck > 1) throw new ArgumentException("Unprepared cue/part/deck."); return bank.Decks[deck].Tracks[partIndex]?.ProcessId ?? 0; }
        /// <summary>Offline rendering barrier, not an audio callback API. Keeps a faster-than-real-time exporter ahead of native workers.</summary>
        public void WaitForBuffered(MusicCue cue, long firstFrame, int frames, int timeoutMilliseconds = 60000)
        {
            if (firstFrame < 0 || frames < 1 || frames > sampleRate || timeoutMilliseconds < 1 || timeoutMilliseconds > 300000) throw new ArgumentException("Invalid offline buffer range.");
            var bank = Get(cue); if (bank == null) throw new InvalidOperationException("Prepare the cue first.");
            bank.Preparation.GetAwaiter().GetResult();
            foreach (var deck in bank.Decks) foreach (var track in deck.Tracks) track?.WaitForBuffered(firstFrame, frames, timeoutMilliseconds);
        }
        public void Dispose()
        {
            MusicPluginBank[] copy;
            lock (gate) { if (disposed) return; disposed = true; copy = new List<MusicPluginBank>(banks.Values).ToArray(); }
            foreach (var bank in copy) foreach (var deck in bank.Decks) foreach (var track in deck.Tracks) track?.Dispose();
        }
    }
    internal sealed class MusicPluginBank
    {
        internal readonly MusicPluginDeck[] Decks;
        internal Task Preparation;
        internal MusicPluginBank(MusicCue cue, bool loop) { Decks = new[] { new MusicPluginDeck(cue, loop), new MusicPluginDeck(cue, loop) }; foreach (var deck in Decks) deck.Bank = this; }
    }
    internal sealed class MusicPluginDeck
    {
        internal readonly MusicPluginTrack[] Tracks; internal readonly double[] Wet, Left, Right;
        internal readonly MusicPluginDeck Rhythm;
        internal MusicPluginBank Bank;
        bool previousClock;
        double previousBpm;
        internal readonly MusicCue Cue; internal double PreviousLocal = -1, Frame;
        readonly bool loop;
        internal MusicPluginDeck(MusicCue cue, bool loop, MusicPluginTrack[] shared = null) { Cue = cue; this.loop = loop; Tracks = shared ?? new MusicPluginTrack[cue.PartCount]; Wet = new double[cue.PartCount]; Left = new double[cue.PartCount]; Right = new double[cue.PartCount]; if (shared == null) Rhythm = new MusicPluginDeck(cue, loop, Tracks); }
        internal int Slot(MusicPart part) { for (int i = 0; i < Tracks.Length; i++) if (ReferenceEquals(Cue.Parts[i], part)) return i; return -1; }
        internal void Read(double local, double bpm, int sampleRate, bool audible, bool drums = false)
        {
            // A nominal-tempo PCM stream stays pitch-stable. Different transition/follower tempo uses stock synthesis.
            bool clock = audible && local >= 0 && Math.Abs(bpm - Cue.Tempo.At(local, loop)) < .0001;
            if (local < 0) Frame = 0;
            else if (!previousClock || PreviousLocal < 0 || Math.Abs(local - PreviousLocal - previousBpm / (60 * sampleRate)) > .00001)
            {
                double period = Cue.Tempo.SecondsAt(Cue.LoopBeats);
                if (!loop && local >= Cue.LoopBeats) Frame = (period + (local - Cue.LoopBeats) * 60 / Cue.Tempo.At(Cue.LoopBeats, false)) * sampleRate;
                else { double cycle = Math.Floor(local / Cue.LoopBeats); Frame = (cycle * period + Cue.Tempo.SecondsAt(local - cycle * Cue.LoopBeats)) * sampleRate; }
            }
            else Frame += 1;
            PreviousLocal = local;
            previousClock = clock;
            previousBpm = bpm;
            long at = Math.Max(0, (long)Math.Round(Frame)); double step = 1.0 / (sampleRate * .005);
            for (int i = 0; i < Tracks.Length; i++)
            {
                if ((Cue.Parts[i].Layer == MusicLayer.Drums) != drums) continue;
                var track = Volatile.Read(ref Tracks[i]); if (track == null) continue;
                float left = 0, right = 0; bool ready = clock && track.Read(at, out left, out right);
                if (ready) { Left[i] = left; Right[i] = right; }
                Wet[i] = Math.Max(0, Math.Min(1, Wet[i] + (ready ? step : -step)));
            }
        }
    }
    // Single producer/single audio consumer. Tags publish complete stereo frames; no I/O, locks or allocation on reads.
    internal sealed class MusicPluginRing
    {
        readonly float[] samples; readonly long[] tags;
        internal int Capacity => tags.Length;
        internal MusicPluginRing(int frames) { samples = new float[frames * 2]; tags = new long[frames]; for (int i = 0; i < tags.Length; i++) tags[i] = -1; }
        internal void Write(long first, float[] source, int frames, int sourceFrame = 0)
        {
            for (int i = 0; i < frames; i++)
            { long frame = first + i; int slot = (int)(frame % tags.Length); Volatile.Write(ref tags[slot], -1); samples[slot * 2] = source[(sourceFrame + i) * 2]; samples[slot * 2 + 1] = source[(sourceFrame + i) * 2 + 1]; Volatile.Write(ref tags[slot], frame); }
        }
        internal bool Read(long frame, out float left, out float right)
        {
            left = right = 0;
            int slot = (int)(frame % tags.Length); if (Volatile.Read(ref tags[slot]) != frame) { left = right = 0; return false; }
            left = samples[slot * 2]; right = samples[slot * 2 + 1]; Thread.MemoryBarrier();
            return Volatile.Read(ref tags[slot]) == frame;
        }
    }
    internal sealed class MusicPluginTrack : IDisposable
    {
        readonly string executable; readonly MusicPluginRegistration plugin; readonly MusicCue cue; readonly MusicPluginTarget target;
        readonly int rate, block, ahead; readonly MusicPluginRing ring; readonly MusicPluginSchedule schedule;
        readonly bool loop;
        readonly ManualResetEventSlim ready = new ManualResetEventSlim(); readonly object controls = new object();
        readonly Dictionary<string, double> values = new Dictionary<string, double>(); readonly float[] pcm;
        MusicPluginHost host; Thread worker; MusicPluginState restore; int stopped, changed, parameterVersion, appliedVersion;
        // Playback V3 parameter automation, resolved to VST3 IDs once the plugin is loaded (sorted by ID); null = none.
        uint[] automatedIds; MusicAutomation[] automatedLanes; double[] lastSent; readonly List<MusicPluginParameterPoint> points = new List<MusicPluginParameterPoint>();
        readonly bool automate;
        /// <summary>Automation points per lane and block: one every AutomationStep frames (plus the block's last frame), sent only when the value changes.</summary>
        internal const int AutomationStep = 64;
        long desired, produced, underruns; string failure; int wasMissing;
        internal string Failure => Volatile.Read(ref failure);
        internal long Underruns => Interlocked.Read(ref underruns);
        internal double Gain => target.Gain;
        internal double Pan => target.Pan;
        internal int ProcessId { get { lock (controls) return host?.ProcessId ?? 0; } }
        internal MusicPluginTrack(string executable, MusicPluginRegistration plugin, MusicCue cue, MusicPart part, MusicPluginTarget target, int rate, int block, double aheadSeconds, bool loop)
        {
            this.executable = executable; this.plugin = plugin; this.cue = cue; this.target = target; this.rate = rate; this.block = block;
            this.loop = loop;
            ahead = (int)(rate * aheadSeconds); ring = new MusicPluginRing(ahead + block * 4); pcm = new float[block * 2];
            schedule = new MusicPluginSchedule(cue, part, target, rate, loop); restore = target.State;
            automate = target.Automation.Count > 0 && cue.PlaybackVersionOf(part) >= MusicPlaybackVersion.V3;
            foreach (var p in target.Parameters) values.Add(p.Key, p.Value);
        }
        internal void Start() { worker = new Thread(Run) { IsBackground = true, Name = "Graze VST3 PCM" }; worker.Start(); }
        internal void WaitReady(int milliseconds)
        { if (!ready.Wait(milliseconds)) throw new TimeoutException("Plugin prewarm timed out."); if (Failure != null) throw new InvalidOperationException(Failure); }
        internal void WaitForBuffered(long firstFrame, int frames, int timeoutMilliseconds)
        {
            Interlocked.Exchange(ref desired, firstFrame); var deadline = System.Diagnostics.Stopwatch.StartNew(); float l, r;
            while (!ring.Read(firstFrame, out l, out r) || !ring.Read(firstFrame + frames - 1, out l, out r))
            {
                if (Volatile.Read(ref stopped) != 0) return; // stock fallback also works for offline export
                if (deadline.ElapsedMilliseconds >= timeoutMilliseconds) throw new TimeoutException("Native PCM render timed out."); Thread.Sleep(1);
            }
        }
        internal bool Read(long frame, out float left, out float right)
        {
            Interlocked.Exchange(ref desired, frame);
            left = right = 0;
            bool result = Volatile.Read(ref stopped) == 0 && ring.Read(frame, out left, out right);
            if (!result) { left = right = 0; if (wasMissing == 0) { wasMissing = 1; Interlocked.Increment(ref underruns); } } else wasMissing = 0;
            return result;
        }
        internal void SetParameter(string key, double value)
        { lock (controls) { if (host == null) throw new InvalidOperationException("Plugin is not ready."); host.ParameterId(key); values[key] = value; parameterVersion++; changed = 1; } }
        internal MusicPluginState CaptureState()
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            for (;;)
            {
                lock (controls)
                {
                    if (host == null || Volatile.Read(ref stopped) != 0) throw new InvalidOperationException(Failure ?? "Plugin is not ready.");
                    if (Volatile.Read(ref appliedVersion) == parameterVersion) return host.CaptureState();
                }
                if (deadline.ElapsedMilliseconds > 60000) throw new TimeoutException("Parameter/state update timed out."); Thread.Sleep(1);
            }
        }
        void Run()
        {
            try
            {
                var created = new MusicPluginHost(executable);
                created.Exited += () => { Interlocked.CompareExchange(ref failure, "Plugin worker exited.", null); Interlocked.Exchange(ref stopped, 1); ready.Set(); };
                lock (controls) { if (Volatile.Read(ref stopped) != 0) { created.Dispose(); return; } host = created; }
                host.Load(plugin, rate, block); host.Configure(restore, values); restore = host.CaptureState();
                if (automate) ResolveAutomation();
                while (Volatile.Read(ref stopped) == 0)
                {
                    if (host.HasExited) throw new System.IO.IOException("Plugin worker exited.");
                    long want = Interlocked.Read(ref desired);
                    if (want < produced - host.LatencySamples - ring.Capacity + block)
                    {
                        host.Process(pcm, block, produced, cue.Tempo.At(0), 0, cue.LoopBeats, schedule.ReleaseAll(), cue.BeatsPerBar);
                        lock (controls) host.Configure(restore, new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(values));
                        schedule.Reset(); produced = 0; ForgetSentAutomation();
                    }
                    int version = -1;
                    lock (controls) if (changed != 0) { host.Configure(new MusicPluginState(null), new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(values), false); version = parameterVersion; changed = 0; ForgetSentAutomation(); }
                    if (produced >= want + ahead + host.LatencySamples && version < 0) { ready.Set(); Thread.Sleep(2); continue; }
                    long first = produced;
                    var midi = schedule.Block(first, block);
                    double ppq = schedule.BeatAt(first / (double)rate);
                    host.Process(pcm, block, first, cue.Tempo.At(ppq, loop), ppq, cue.LoopBeats, midi, cue.BeatsPerBar, AutomationBlock(first));
                    if (version >= 0) Volatile.Write(ref appliedVersion, version);
                    // Seeking reconstructs held notes by processing from zero; only the final ahead window is retained.
                    long outputFirst = first - host.LatencySamples; int skip = (int)Math.Min(block, Math.Max(0, -outputFirst));
                    if (skip < block) ring.Write(Math.Max(0, outputFirst), pcm, block - skip, skip);
                    produced += block;
                    if (produced >= ahead + host.LatencySamples) ready.Set();
                }
            }
            catch (Exception e) { Volatile.Write(ref failure, e.Message); Interlocked.Exchange(ref stopped, 1); }
            finally { ready.Set(); lock (controls) { host?.Dispose(); host = null; } }
        }
        void ResolveAutomation()
        {
            var resolved = new SortedDictionary<uint, MusicAutomation>();
            foreach (var lane in target.Automation)
            {
                uint id = host.ParameterId(lane.Key);
                if (resolved.ContainsKey(id)) throw new ArgumentException("Two automation lanes address the same plugin parameter: " + lane.Key);
                var points = new MusicAutomationPoint[lane.Value.Count]; for (int i = 0; i < points.Length; i++) points[i] = lane.Value[i];
                resolved.Add(id, new MusicAutomation(MusicAutomationTarget.PluginParameter, points, cue.LoopBeats));
            }
            automatedIds = new uint[resolved.Count]; automatedLanes = new MusicAutomation[resolved.Count]; lastSent = new double[resolved.Count];
            int n = 0; foreach (var entry in resolved) { automatedIds[n] = entry.Key; automatedLanes[n] = entry.Value; n++; }
            ForgetSentAutomation();
        }

        // After a state restore or a static parameter update the plugin holds the configured values: resend every lane.
        void ForgetSentAutomation() { if (lastSent != null) for (int i = 0; i < lastSent.Length; i++) lastSent[i] = double.NaN; }

        /// <summary>Parameter points for the block starting at frame <paramref name="first"/> (cue-local beats, repeating with the loop).</summary>
        IReadOnlyList<MusicPluginParameterPoint> AutomationBlock(long first)
        {
            if (automatedLanes == null) return null;
            points.Clear();
            for (int lane = 0; lane < automatedLanes.Length; lane++)
                for (int offset = 0; ; offset = Math.Min(offset + AutomationStep, block - 1))
                {
                    double beat = schedule.BeatAt((first + offset) / (double)rate);
                    double local = loop ? beat - Math.Floor(beat / cue.LoopBeats) * cue.LoopBeats : Math.Min(beat, cue.LoopBeats);
                    double value = automatedLanes[lane].Evaluate(local);
                    if (value != lastSent[lane]) { points.Add(new MusicPluginParameterPoint(automatedIds[lane], offset, value)); lastSent[lane] = value; }
                    if (offset == block - 1) break;
                }
            return points;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref stopped, 1); Volatile.Read(ref host)?.Abort();
            if (worker != null && worker != Thread.CurrentThread) worker.Join(5000);
            // WaitReady may still be awaiting this signal. Keep the small managed signal alive until GC.
        }
    }
    // Heap of recurring note on/off events: correct note IDs and long notes across loop boundaries, no accumulated rounding drift.
    internal sealed class MusicPluginSchedule
    {
        struct Event { internal double Seconds; internal int Key, Id; internal float Velocity; internal bool On; internal long Cycle; }
        readonly MusicCue cue; readonly int rate; readonly double period; readonly Event[] original, heap; readonly bool loop;
        readonly List<MusicPluginMidiEvent> events = new List<MusicPluginMidiEvent>(); readonly bool mono; int monoId = -1, monoKey;
        readonly Dictionary<int, int> held = new Dictionary<int, int>();
        internal MusicPluginSchedule(MusicCue cue, MusicPart part, MusicPluginTarget target, int sampleRate, bool loop)
        {
            this.cue = cue; rate = sampleRate; period = cue.Tempo.SecondsAt(cue.LoopBeats); mono = target.Mono;
            this.loop = loop;
            var list = new List<Event>();
            for (int i = 0; i < part.NoteCount; i++)
            {
                var n = part.Notes[i]; int key = n.Key + target.Transpose; if (n.Velocity <= 0 || key < 0 || key > 127) continue;
                list.Add(new Event { Seconds = SecondsAt(n.Beat), Key = key, Id = i, Velocity = (float)Math.Pow(n.Velocity, target.VelocityCurve), On = true });
                list.Add(new Event { Seconds = SecondsAt(n.Beat + n.Duration), Key = key, Id = i, Velocity = 0, On = false });
            }
            original = list.ToArray(); heap = new Event[original.Length]; Reset();
        }
        double SecondsAt(double beat) { if (!loop && beat >= cue.LoopBeats) return period + (beat - cue.LoopBeats) * 60 / cue.Tempo.At(cue.LoopBeats, false); double cycle = Math.Floor(beat / cue.LoopBeats); return cycle * period + cue.Tempo.SecondsAt(beat - cycle * cue.LoopBeats); }
        internal double BeatAt(double seconds)
        {
            if (!loop && seconds >= period) return cue.LoopBeats + (seconds - period) * cue.Tempo.At(cue.LoopBeats, false) / 60;
            double cycle = Math.Floor(seconds / period), local = seconds - cycle * period, lo = 0, hi = cue.LoopBeats;
            for (int i = 0; i < 48; i++) { double mid = (lo + hi) * .5; if (cue.Tempo.SecondsAt(mid) < local) lo = mid; else hi = mid; }
            return cycle * cue.LoopBeats + (lo + hi) * .5;
        }
        internal void Reset() { Array.Copy(original, heap, original.Length); Array.Sort(heap, Compare); monoId = -1; held.Clear(); }
        internal IReadOnlyList<MusicPluginMidiEvent> ReleaseAll()
        { events.Clear(); foreach (var note in held) events.Add(new MusicPluginMidiEvent(0, note.Value, 0, note.Key, false)); held.Clear(); return events; }
        static int Compare(Event a, Event b) { int c = a.Seconds.CompareTo(b.Seconds); if (c != 0) return c; c = a.On.CompareTo(b.On); return c != 0 ? c : a.Id.CompareTo(b.Id); }
        internal IReadOnlyList<MusicPluginMidiEvent> Block(long start, int frames)
        {
            events.Clear();
            while (heap.Length > 0)
            {
                Event e = heap[0]; long at = (long)Math.Round((e.Seconds + e.Cycle * period) * rate);
                if (double.IsPositiveInfinity(e.Seconds)) break;
                if (at >= start + frames) break;
                int id = (int)((e.Id + e.Cycle * (original.Length + 1L)) % int.MaxValue);
                if (at >= start)
                {
                    int offset = (int)(at - start);
                    if (mono && e.On && monoId >= 0) { events.Add(new MusicPluginMidiEvent(offset, monoKey, 0, monoId, false)); held.Remove(monoId); }
                    if (!mono || e.On || id == monoId) { events.Add(new MusicPluginMidiEvent(offset, e.Key, e.Velocity, id, e.On)); if (e.On) { if (held.Count >= 65536) throw new InvalidOperationException("Plugin held-note limit exceeded."); held[id] = e.Key; } else held.Remove(id); }
                    if (mono) { if (e.On) { monoId = id; monoKey = e.Key; } else if (id == monoId) monoId = -1; }
                }
                if (events.Count > 65536) throw new InvalidOperationException("MIDI density exceeds 65536 events per block; reduce block size.");
                if (loop) e.Cycle++; else e.Seconds = double.PositiveInfinity; heap[0] = e; int root = 0;
                for (;;)
                {
                    int child = root * 2 + 1; if (child >= heap.Length) break;
                    if (child + 1 < heap.Length && Earlier(heap[child + 1], heap[child])) child++;
                    if (!Earlier(heap[child], heap[root])) break; Event temp = heap[root]; heap[root] = heap[child]; heap[child] = temp; root = child;
                }
            }
            return events;
        }
        bool Earlier(Event a, Event b)
        { double ta = a.Seconds + a.Cycle * period, tb = b.Seconds + b.Cycle * period; if (ta != tb) return ta < tb; if (a.On != b.On) return !a.On; return a.Id < b.Id; }
    }
}
