using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graze.Presentation.Audio
{
    /// <summary>Control-thread effect processor. Implementations must return complete finite stereo blocks.</summary>
    public interface IMusicPluginInsertProcessor : IDisposable
    {
        int LatencySamples { get; }
        uint ParameterId(string key);
        void Process(float[] input, float[] output, int frames, long firstFrame, double bpm, double beat, double loopBeats,
            int beatsPerBar, IReadOnlyList<MusicPluginParameterPoint> automation);
    }

    public sealed class MusicPluginInsertContext
    {
        public readonly string HostExecutable;
        public readonly MusicPluginCatalog Catalog;
        readonly string[] approved;
        public MusicPluginInsertContext(string hostExecutable, MusicPluginCatalog catalog, IEnumerable<string> approvedClassIds)
        {
            if (string.IsNullOrWhiteSpace(hostExecutable) || catalog == null || approvedClassIds == null) throw new ArgumentException("Insert preparation needs an application-owned host, catalog and approval list.");
            HostExecutable = hostExecutable; Catalog = catalog; approved = new List<string>(approvedClassIds).ToArray();
        }
        internal IMusicPluginInsertProcessor Create(MusicPluginInsert insert, int rate, int block)
            => new NativeInsertProcessor(HostExecutable, Catalog.Resolve(insert.Plugin, approved), insert, rate, block);
        /// <summary>Resolve a selected plugin using this application's approval list (presets cannot approve themselves).</summary>
        public MusicPluginRegistration Resolve(MusicPluginIdentity identity)=>Catalog.Resolve(identity,approved);
        internal MusicPluginSession Instruments(MusicPluginMod mod, int rate, int block)
            => new MusicPluginSession(HostExecutable, Catalog, approved, mod, rate, blockFrames: block, loop: false);
    }

    sealed class NativeInsertProcessor : IMusicPluginInsertProcessor
    {
        readonly MusicPluginHost host;
        public int LatencySamples => host.LatencySamples;
        internal NativeInsertProcessor(string executable, MusicPluginRegistration registration, MusicPluginInsert insert, int rate, int block)
        {
            host = new MusicPluginHost(executable);
            try
            {
                host.LoadEffect(registration, rate, block);
                var ids = new HashSet<uint>();
                foreach (string key in insert.Parameters.Keys) if (!ids.Add(host.ParameterId(key))) throw new ArgumentException("Two insert keys refer to the same parameter.");
                host.Configure(insert.State, insert.Parameters);
            }
            catch { host.Dispose(); throw; }
        }
        public uint ParameterId(string key) => host.ParameterId(key);
        public void Process(float[] input, float[] output, int frames, long firstFrame, double bpm, double beat, double loopBeats,
            int beatsPerBar, IReadOnlyList<MusicPluginParameterPoint> automation)
            => host.ProcessEffect(input, output, frames, firstFrame, bpm, beat, loopBeats, beatsPerBar, automation);
        public void Dispose() => host.Dispose();
    }

    /// <summary>Immutable prepared PCM. Native code has finished before any playback reader is created.</summary>
    public sealed class MusicPreparedInsertSong
    {
        readonly float[] stereo;
        public readonly int SampleRate;
        public readonly MusicCue Cue;
        public readonly IReadOnlyList<string> Diagnostics;
        public int Frames => stereo.Length / 2;
        internal MusicPreparedInsertSong(MusicCue cue, int rate, float[] pcm, List<string> diagnostics)
        { Cue = cue; SampleRate = rate; stereo = pcm; Diagnostics = diagnostics.AsReadOnly(); }
        public float[] CopyStereo() => (float[])stereo.Clone();
        public MusicPreparedInsertReader CreateReader(int startFrame = 0, int frames = 0, bool loop = false)
        {
            int length = frames == 0 ? Frames - startFrame : frames;
            if (startFrame < 0 || length < 1 || startFrame > Frames - length) throw new ArgumentException("Invalid prepared playback range.");
            return new MusicPreparedInsertReader(stereo, startFrame, length, loop);
        }
    }

    /// <summary>One audio consumer per reader. ReadStereo/Seek allocate nothing and perform no native I/O.</summary>
    public sealed class MusicPreparedInsertReader
    {
        readonly float[] pcm; readonly int start, length; readonly bool loop; int position;
        public int PositionFrames => Volatile.Read(ref position);
        internal MusicPreparedInsertReader(float[] pcm, int start, int length, bool loop) { this.pcm = pcm; this.start = start; this.length = length; this.loop = loop; }
        public void Seek(int frame) { if (frame < 0 || frame > length) throw new ArgumentOutOfRangeException(nameof(frame)); Volatile.Write(ref position, frame); }
        public void ReadStereo(float[] output)
        {
            if (output == null || output.Length % 2 != 0) throw new ArgumentException("Complete stereo frames required.");
            int at = position, written = 0;
            while (written < output.Length / 2)
            {
                if (at == length) { if (loop) at = 0; else { Array.Clear(output, written * 2, output.Length - written * 2); break; } }
                int count = Math.Min(length - at, output.Length / 2 - written);
                Array.Copy(pcm, (start + at) * 2, output, written * 2, count * 2); written += count; at += count;
            }
            Volatile.Write(ref position, at);
        }
    }

    // Hooks run only on preparation engines. Each pass captures one node; previous nodes supply latency-aligned PCM.
    internal sealed class MusicInsertRenderGraph
    {
        internal const int BusOffset = MusicCue.MaxParts, Master = MusicCue.MaxParts + MusicRouting.MaxBuses, Slots = Master + 1;
        internal readonly float[][] Wet, Raw;
        internal readonly int Capture;
        internal readonly float[] Captured;
        internal readonly bool CaptureRaw, CaptureAfterWet;
        internal MusicInsertRenderGraph(float[][] wet, float[][] raw, int capture = -1, float[] captured = null, bool captureRaw = false, bool captureAfterWet = false)
        { Wet = wet; Raw = raw; Capture = capture; Captured = captured; CaptureRaw = captureRaw; CaptureAfterWet = captureAfterWet; }
        internal void Inputs(MusicTrackEffects[] buses, int parts, long frame)
        {
            if (Raw == null) return;
            for (int i = 0; i < parts; i++)
            {
                var pcm = Raw[i]; var bus = buses[i]; if (pcm == null || bus == null) continue;
                int offset = checked((int)frame * 2);
                if (CaptureRaw) { pcm[offset] = (float)bus.Left; pcm[offset + 1] = (float)bus.Right; }
                else { bus.Left = pcm[offset]; bus.Right = pcm[offset + 1]; }
            }
        }
        internal void Apply(int node, long frame, ref double left, ref double right)
        {
            int offset = checked((int)frame * 2);
            if (Capture == node && !CaptureAfterWet) { Captured[offset] = (float)left; Captured[offset + 1] = (float)right; }
            var pcm = Wet[node];
            if (pcm != null) { left = pcm[offset]; right = pcm[offset + 1]; }
            if (Capture == node && CaptureAfterWet) { Captured[offset] = (float)left; Captured[offset + 1] = (float)right; }
        }
    }

    public static class MusicPluginInsertRenderer
    {
        public const int DefaultMemoryMiB = 512;
        public static Task<MusicPreparedInsertSong> PrepareAsync(MusicCue cue, MusicPluginInsertContext context, int sampleRate = 48000,
            double seconds = 0, int memoryMiB = DefaultMemoryMiB, CancellationToken cancellation = default, MusicPluginMod instruments = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return Task.Run(() => Prepare(cue, sampleRate, context.Create, seconds, memoryMiB, cancellation, context, instruments,
                (target,rate,block)=>new NativeOutputProcessor(context,target,rate,block)), cancellation);
        }
        /// <summary>Code-first backend injection for tests/other application-owned hosts. No engine-external objects are accessed.</summary>
        public static MusicPreparedInsertSong Prepare(MusicCue cue, int sampleRate,
            Func<MusicPluginInsert, int, int, IMusicPluginInsertProcessor> factory, double seconds = 0,
            int memoryMiB = DefaultMemoryMiB, CancellationToken cancellation = default)
            => Prepare(cue, sampleRate, factory, seconds, memoryMiB, cancellation, null, null, null);

        /// <summary>
        /// One track's own output as stereo PCM (playback V3 cue): its voices or native instrument, level, pan and automation, its effect chain
        /// and VST3 inserts — before its output bus, sends and the master (CopyStereo; Diagnostics lists plugin fallbacks).
        /// Native instruments and inserts are prepared through the application's context like <see cref="PrepareAsync"/>.
        /// </summary>
        public static Task<MusicPreparedInsertSong> RenderTrackAsync(MusicCue cue, int part, MusicPluginInsertContext context, int sampleRate = 48000, double seconds = 0,
            int memoryMiB = DefaultMemoryMiB, CancellationToken cancellation = default, MusicPluginMod instruments = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return Task.Run(() => Prepare(cue, sampleRate, context.Create, seconds, memoryMiB, cancellation, context, instruments,
                (target, rate, block) => new NativeOutputProcessor(context, target, rate, block), part), cancellation);
        }

        /// <summary>Same as <see cref="RenderTrackAsync"/> for a track without native instruments or inserts (no context needed).</summary>
        public static MusicPreparedInsertSong RenderTrack(MusicCue cue, int part, int sampleRate = 48000, double seconds = 0, int memoryMiB = DefaultMemoryMiB, CancellationToken cancellation = default)
            => Prepare(cue, sampleRate, (insert, rate, block) => throw new InvalidOperationException("This track has VST3 inserts: render it with RenderTrackAsync and an insert context."),
                seconds, memoryMiB, cancellation, null, null, null, part);

        public static MusicPreparedInsertSong PrepareOutputs(MusicCue cue,MusicPluginMod instruments,int sampleRate,
            Func<MusicPluginTarget,int,int,IMusicPluginOutputProcessor> outputFactory,
            Func<MusicPluginInsert,int,int,IMusicPluginInsertProcessor> insertFactory,double seconds=0,
            int memoryMiB=DefaultMemoryMiB,CancellationToken cancellation=default)
        {
            if(outputFactory==null)throw new ArgumentNullException(nameof(outputFactory));
            return Prepare(cue,sampleRate,insertFactory,seconds,memoryMiB,cancellation,null,instruments,outputFactory);
        }

        static MusicPreparedInsertSong Prepare(MusicCue cue, int rate, Func<MusicPluginInsert, int, int, IMusicPluginInsertProcessor> factory,
            double seconds, int memoryMiB, CancellationToken token, MusicPluginInsertContext context, MusicPluginMod instruments,
            Func<MusicPluginTarget,int,int,IMusicPluginOutputProcessor> outputFactory, int capturePart = -1)
        {
            if (cue == null || factory == null || rate < 8000 || rate > 192000 || memoryMiB < 32 || memoryMiB > 2048) throw new ArgumentException("Invalid insert preparation configuration.");
            if (capturePart >= 0 && (capturePart >= cue.PartCount || cue.PlaybackVersion < MusicPlaybackVersion.V3 || cue.PlaybackVersionOf(cue.Parts[capturePart]) < MusicPlaybackVersion.V2))
                throw new ArgumentException("Rendering one track needs a playback V3 cue and a V2+ track inside it.");
            if (seconds == 0) seconds = cue.Tempo.SecondsAt(cue.LoopBeats);
            if (!MusicTrack.Finite(seconds) || seconds <= 0 || seconds > 1800 || seconds > cue.Tempo.SecondsAt(cue.LoopBeats) + 30)
                throw new ArgumentException("Insert preparation supports at most 30 minutes and up to 30 seconds beyond the cue.");
            int frames = checked((int)Math.Ceiling(seconds * rate));
            var chains = new MusicPluginInsert[MusicInsertRenderGraph.Slots][]; int nodes = 0, rawCount = 0;
            var rawParts = new bool[cue.PartCount];
            var outputPlans=MusicPluginMultiOutput.Plans(cue,instruments);
            var ordinaryTargets=new List<MusicPluginTarget>();
            if(instruments!=null)for(int i=0;i<cue.PartCount;i++)
            {
                var target=instruments.Find(cue,cue.Parts[i]);
                if(target!=null&&target.Outputs.Count==0&&!ordinaryTargets.Contains(target))ordinaryTargets.Add(target);
            }
            MusicPluginMod ordinary=instruments==null||ordinaryTargets.Count==0?null:new MusicPluginMod(instruments.Name,ordinaryTargets.ToArray());
            foreach(var plan in outputPlans){rawParts[plan.Source]=true;foreach(int slot in plan.Destinations)rawParts[slot]=true;}
            if (cue.PlaybackVersion >= MusicPlaybackVersion.V3)
            {
                for (int i = 0; i < cue.PartCount; i++) if (cue.PlaybackVersionOf(cue.Parts[i]) >= MusicPlaybackVersion.V3) chains[i] = cue.Parts[i].Effects?.Inserts;
                for (int b = 0; b < cue.BusCount; b++) chains[MusicInsertRenderGraph.BusOffset + b] = cue.Buses[b].Effects.Inserts;
                chains[MusicInsertRenderGraph.Master] = cue.Mixer.Inserts;
            }
            // One track's output depends only on its own chain (sidechain keys are taken before any chain): skip every other insert chain.
            if (capturePart >= 0) for (int node = 0; node < chains.Length; node++) if (node != capturePart) chains[node] = null;
            foreach (var chain in chains) if ((chain?.Length ?? 0) > 0) nodes++;
            if (instruments != null)
            {
                if (cue.PlaybackVersion < MusicPlaybackVersion.V3) throw new ArgumentException("Combined instrument/insert preparation requires a V3 cue.");
                for (int i = 0; i < cue.PartCount; i++) if (instruments.Find(cue, cue.Parts[i]) != null)
                {
                    if (cue.PlaybackVersionOf(cue.Parts[i]) < MusicPlaybackVersion.V2) throw new ArgumentException("A native instrument in insert preparation needs a V2+ track.");
                    rawParts[i] = true;
                }
            }
            foreach(bool used in rawParts)if(used)rawCount++;
            if(ordinary!=null&&context==null)throw new ArgumentException("Ordinary instrument targets need an application-owned native context.");
            if ((long)frames * 8 * (nodes + rawCount + 4) > (long)memoryMiB * 1024 * 1024) throw new ArgumentException("Prepared insert PCM exceeds the memory budget; shorten the range or explicitly increase memoryMiB.");
            const int block = 512;
            if (capturePart >= 0 && nodes == 0 && rawCount == 0)
            {
                var track = new float[frames * 2];
                Pass(cue, rate, frames, new MusicInsertRenderGraph(new float[MusicInsertRenderGraph.Slots][], new float[cue.PartCount][], capturePart, track, captureAfterWet: true), null, null, token, null);
                return new MusicPreparedInsertSong(cue, rate, track, new List<string>());
            }
            if (nodes == 0 && rawCount == 0)
            {
                var plain = new float[frames * 2]; Pass(cue, rate, frames, null, plain, null, token, null);
                return new MusicPreparedInsertSong(cue, rate, plain, new List<string>());
            }
            var wet = new float[MusicInsertRenderGraph.Slots][]; var raw = new float[cue.PartCount][]; var clock = new double[frames]; var diagnostics = new List<string>();
            for (int i = 0; i < raw.Length; i++) if (rawParts[i]) raw[i] = new float[frames * 2];
            using (var plugins = ordinary == null ? null : context.Instruments(ordinary, rate, block))
            {
                plugins?.PrepareAsync(cue).GetAwaiter().GetResult();
                Pass(cue, rate, frames, new MusicInsertRenderGraph(wet, raw, captureRaw: true), null, clock, token, plugins);
                if (plugins != null)
                {
                    diagnostics.AddRange(plugins.Diagnostics);
                    for (int i = 0; i < rawParts.Length; i++) if (rawParts[i])
                    {
                        string failure = plugins.Failure(cue, i); long underruns = plugins.Underruns(cue, i);
                        if (!string.IsNullOrEmpty(failure) || underruns > 0)
                            diagnostics.Add(cue.Parts[i].InstrumentId + ": native instrument source fallback, underruns=" + underruns + ", failure=" + failure);
                    }
                }
            }
            MusicPluginMultiOutput.Render(cue,rate,frames,raw,clock,outputPlans,outputFactory,token);
            for (int node = 0; node < chains.Length; node++)
            {
                var chain = chains[node]; if (chain == null || chain.Length == 0) continue;
                token.ThrowIfCancellationRequested();
                var source = new float[frames * 2];
                Pass(cue, rate, frames, new MusicInsertRenderGraph(wet, raw, node, source), null, null, token, null);
                foreach (var insert in chain) source = Effect(source, clock, cue, rate, block, insert, factory, diagnostics, token);
                wet[node] = source;
            }
            if (capturePart >= 0)
            {
                var track = new float[frames * 2];
                Pass(cue, rate, frames, new MusicInsertRenderGraph(wet, raw, capturePart, track, captureAfterWet: true), null, null, token, null);
                return new MusicPreparedInsertSong(cue, rate, track, diagnostics);
            }
            var output = new float[frames * 2];
            Pass(cue, rate, frames, new MusicInsertRenderGraph(wet, raw), output, null, token, null);
            return new MusicPreparedInsertSong(cue, rate, output, diagnostics);
        }

        static void Pass(MusicCue cue, int rate, int frames, MusicInsertRenderGraph graph, float[] result, double[] clock,
            CancellationToken token, MusicPluginSession plugins)
        {
            var engine = new AdaptiveMusicEngine(cue, rate, plugins, graph, 0, 0, 0, true);
            var pcm = new float[1024];
            if (clock != null) { engine.TraceBpm = new double[512]; engine.TraceStep = new double[512]; engine.TraceLocal = new double[512]; }
            for (int at = 0; at < frames; at += 512)
            {
                token.ThrowIfCancellationRequested(); int count = Math.Min(512, frames - at);
                plugins?.WaitForBuffered(cue, at, count);
                engine.RenderStereo(pcm, count);
                if (result != null) Array.Copy(pcm, 0, result, at * 2, count * 2);
                if (clock != null) Array.Copy(engine.TraceLocal, 0, clock, at, count);
            }
            if (engine.DroppedNotes != 0) throw new InvalidOperationException("Insert preparation dropped notes; reduce simultaneous voices.");
        }

        static float[] Effect(float[] source, double[] clock, MusicCue cue, int rate, int block, MusicPluginInsert insert,
            Func<MusicPluginInsert, int, int, IMusicPluginInsertProcessor> factory, List<string> diagnostics, CancellationToken token)
        {
            try
            {
                using (var processor = factory(insert, rate, block))
                {
                    if (processor == null || processor.LatencySamples < 0 || processor.LatencySamples > rate * 10) throw new InvalidOperationException("Invalid effect latency/processor.");
                    int latency = processor.LatencySamples, frames = source.Length / 2;
                    var output = new float[source.Length]; var inputBlock = new float[block * 2]; var outputBlock = new float[block * 2];
                    var resolved = new SortedDictionary<uint, MusicAutomation>();
                    foreach (var lane in insert.Automation) { uint id = processor.ParameterId(lane.Key); if (resolved.ContainsKey(id)) throw new ArgumentException("Two automation keys refer to one parameter."); resolved.Add(id, lane.Value); }
                    var points = new List<MusicPluginParameterPoint>();
                    double Beat(long frame) => frame < frames ? clock[frame] : clock[frames - 1] + (frame - frames + 1) * cue.Tempo.At(clock[frames - 1], false) / (60 * rate);
                    for (int at = 0; at < frames + latency; at += block)
                    {
                        token.ThrowIfCancellationRequested(); int count = Math.Min(block, frames + latency - at);
                        Array.Clear(inputBlock, 0, inputBlock.Length);
                        if (at < frames) Array.Copy(source, at * 2, inputBlock, 0, Math.Min(count, frames - at) * 2);
                        points.Clear();
                        foreach (var lane in resolved)
                            for (int offset = 0; ; offset = Math.Min(offset + 64, count - 1))
                            { points.Add(new MusicPluginParameterPoint(lane.Key, offset, lane.Value.Evaluate(Math.Min(cue.LoopBeats, Beat(at + offset))))); if (offset == count - 1) break; }
                        processor.Process(inputBlock, outputBlock, count, at, cue.Tempo.At(Beat(at), false), Beat(at), cue.LoopBeats, cue.BeatsPerBar, points);
                        if (processor.LatencySamples != latency) throw new InvalidOperationException("Effect latency changed during preparation.");
                        for (int f = 0; f < count; f++)
                        {
                            int target = at + f - latency; if (target < 0 || target >= frames) continue;
                            for (int channel = 0; channel < 2; channel++)
                            {
                                float value = outputBlock[f * 2 + channel]; if (!MusicTrack.Finite(value)) throw new InvalidOperationException("Non-finite effect PCM.");
                                int index = target * 2 + channel; output[index] = (float)(source[index] * (1 - insert.Mix) + value * insert.Mix);
                            }
                        }
                    }
                    return output;
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException) && !(e is OutOfMemoryException))
            {
                token.ThrowIfCancellationRequested(); diagnostics.Add(insert.Plugin.Name + " (" + insert.Plugin.ClassId + "): bypassed — " + e.Message); return source;
            }
        }
    }
}
