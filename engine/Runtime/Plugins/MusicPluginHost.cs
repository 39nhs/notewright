using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace Graze.Presentation.Audio
{
    public sealed class MusicPluginParameter
    {
        public uint Id { get; }
        public string Name { get; }
        public string Unit { get; }
        public string Key { get; }
        public double Value { get; internal set; }
        public int StepCount { get; }
        internal MusicPluginParameter(uint id, string name, string unit, double value, int stepCount = 0)
        {
            Id = id; Name = name; Unit = unit; Value = value; StepCount = stepCount;
            var key = new StringBuilder(); bool separator = false;
            foreach (char c in name.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) { if (separator && key.Length > 0) key.Append('_'); key.Append(c); separator = false; } else separator = true;
            Key = key.ToString();
        }
    }
    public struct MusicPluginMidiEvent
    {
        public int Offset, Key, NoteId;
        public float Velocity;
        public bool On;
        public MusicPluginMidiEvent(int offset, int key, float velocity, int noteId, bool on)
        { Offset = offset; Key = key; Velocity = velocity; NoteId = noteId; On = on; }
    }
    /// <summary>A sample-accurate normalized parameter value inside one process block (playback V3 plugin automation).</summary>
    public struct MusicPluginParameterPoint
    {
        public uint ParameterId;
        public int Offset;
        public double Value;
        public MusicPluginParameterPoint(uint parameterId, int offset, double value) { ParameterId = parameterId; Offset = offset; Value = value; }
    }
    /// <summary>Blocking native worker client. Use only on a background/control thread, never from an audio callback.</summary>
    public sealed class MusicPluginHost : IDisposable
    {
        const uint Magic = 0x31485047; const int MaxMessage = 16 * 1024 * 1024;
        static readonly object StartupGate = new object(); // Windows/Mono redirected pipe handles must not race between launches.
        readonly object gate = new object(); readonly Process process; readonly BinaryReader input; readonly BinaryWriter output;
        readonly Timer watchdog; readonly int timeout; readonly StringBuilder errors = new StringBuilder();
        MemoryMappedFile memory; MemoryMappedViewAccessor view; int maxFrames, hostRate; bool disposed;
        bool loadedEffect;
        bool loadedMulti;
        MusicPluginOutputBus[] outputBuses = Array.Empty<MusicPluginOutputBus>();
        public IReadOnlyList<MusicPluginOutputBus> OutputBuses => Array.AsReadOnly(outputBuses);
        int[] selectedOutputs = Array.Empty<int>();
        MusicPluginRegistration loadedPlugin;
        public int ProtocolVersion { get; private set; }
        MusicPluginParameter[] parameters = Array.Empty<MusicPluginParameter>();
        public IReadOnlyList<MusicPluginParameter> Parameters => Array.AsReadOnly(parameters);
        public int ProcessId => process.Id;
        public bool HasExited { get { try { return process.WaitForExit(0); } catch (InvalidOperationException) { return true; } } }
        public event Action Exited;
        public int LatencySamples { get; private set; }
        public string Diagnostic { get { lock (errors) return errors.ToString(); } }
        public MusicPluginHost(string executable, int timeoutMilliseconds = 60000)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8) throw new PlatformNotSupportedException("The VST3 worker requires Windows x64.");
            if (timeoutMilliseconds < 1000 || timeoutMilliseconds > 300000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            timeout = timeoutMilliseconds;
            process = new Process { StartInfo = new ProcessStartInfo(Path.GetFullPath(executable), System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) { if (errors.Length > 12000) errors.Remove(0, errors.Length - 8000); errors.AppendLine(e.Data); } };
            process.EnableRaisingEvents = true; process.Exited += (s, e) => Exited?.Invoke();
            lock (StartupGate) if (!process.Start()) throw new IOException("Cannot start the VST3 worker.");
            process.BeginErrorReadLine(); input = new BinaryReader(process.StandardOutput.BaseStream); output = new BinaryWriter(process.StandardInput.BaseStream);
            watchdog = new Timer(s => Abort(), null, Timeout.Infinite, Timeout.Infinite);
            try { using (var reply = Call(0, null)) { ProtocolVersion = reply.ReadInt32(); if (ProtocolVersion < 2 || ProtocolVersion > 4) throw new IOException("Incompatible VST3 worker (needs protocol 2, 3 or 4)."); } }
            catch { Dispose(); throw; }
        }
        internal static void WriteText(BinaryWriter writer, string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
        internal static string ReadText(BinaryReader reader) => new UTF8Encoding(false, true).GetString(ReadBlob(reader));
        static byte[] ReadBlob(BinaryReader reader)
        {
            int size = reader.ReadInt32(); if (size < 0 || size > MaxMessage) throw new IOException("Invalid worker blob size.");
            byte[] bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException(); return bytes;
        }
        static void WriteBlob(BinaryWriter writer, byte[] bytes) { if (bytes.Length > MaxMessage) throw new ArgumentException("Plugin state too large."); writer.Write(bytes.Length); writer.Write(bytes); }
        BinaryReader Call(ushort operation, Action<BinaryWriter> write)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(MusicPluginHost));
                using (var bytes = new MemoryStream()) using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    write?.Invoke(writer); writer.Flush(); if (bytes.Length > MaxMessage) throw new ArgumentException("Worker request too large.");
                    watchdog.Change(timeout, Timeout.Infinite);
                    try
                    {
                        output.Write(Magic); output.Write((ushort)1); output.Write(operation); output.Write((uint)bytes.Length); output.Write(bytes.ToArray()); output.Flush();
                        if (input.ReadUInt32() != Magic || input.ReadUInt16() != 1 || input.ReadUInt16() != operation) throw new IOException("Invalid worker reply.");
                        uint length = input.ReadUInt32(); if (length > MaxMessage) throw new IOException("Oversize worker reply.");
                        byte[] response = input.ReadBytes((int)length); if (response.Length != length) throw new EndOfStreamException("Plugin worker exited.");
                        var reader = new BinaryReader(new MemoryStream(response)); int status = reader.ReadInt32();
                        if (status != 0) { string message = ReadText(reader); reader.Dispose(); throw new IOException(message); }
                        return reader;
                    }
                    finally { watchdog.Change(Timeout.Infinite, Timeout.Infinite); }
                }
            }
        }
        internal MusicPluginRegistration[] Scan(string path, bool includeEffects = false)
        {
            using (var reply = Call(1, w => WriteText(w, path)))
            {
                int count = reply.ReadInt32(); if (count < 0 || count > 4096) throw new IOException("Invalid plugin class count.");
                var result = new List<MusicPluginRegistration>();
                for (int i = 0; i < count; i++) { var identity = new MusicPluginIdentity(ReadText(reply), ReadText(reply), ReadText(reply), ReadText(reply)); bool instrument = reply.ReadInt32() != 0; if (instrument || includeEffects) result.Add(new MusicPluginRegistration(path, identity, instrument)); }
                return result.ToArray();
            }
        }
        public void Load(MusicPluginRegistration plugin, int sampleRate = 48000, int blockFrames = 1024)
            => LoadCore(plugin, sampleRate, blockFrames, false);
        public void LoadEffect(MusicPluginRegistration plugin, int sampleRate = 48000, int blockFrames = 1024)
            => LoadCore(plugin, sampleRate, blockFrames, true);
        /// <summary>Control thread only. Activates selected mono/stereo instrument outputs on one instance; bus order defines ProcessOutputs buffer order.</summary>
        public void LoadOutputs(MusicPluginRegistration plugin, IEnumerable<int> buses, int sampleRate=48000, int blockFrames=512)
        {
            if(buses==null)throw new ArgumentNullException(nameof(buses));
            var list=new List<int>();var unique=new HashSet<int>();
            foreach(int bus in buses){if(bus<0||bus>=64||!unique.Add(bus)||list.Count>=MusicPluginOutputRoute.MaxOutputs)throw new ArgumentException("Invalid/duplicate output bus.");list.Add(bus);}
            if(list.Count==0)throw new ArgumentException("Select at least one output bus.");
            if(ProtocolVersion<4)throw new IOException("Multiple outputs require worker protocol 4.");
            LoadCore(plugin,sampleRate,blockFrames,false,list.ToArray());
        }
        void LoadCore(MusicPluginRegistration plugin, int sampleRate, int blockFrames, bool effect, int[] buses=null)
        {
            if (plugin == null || sampleRate < 8000 || sampleRate > 192000 || blockFrames < 16 || blockFrames > 4096) throw new ArgumentException("Invalid plugin audio setup.");
            if (plugin.IsInstrument == effect) throw new ArgumentException("Use Load for instruments or LoadEffect for effects.");
            if (effect && ProtocolVersion < 3) throw new IOException("VST3 effects require worker protocol 3.");
            lock (gate)
            {
                loadedPlugin=null; loadedMulti=false;outputBuses=Array.Empty<MusicPluginOutputBus>();selectedOutputs=Array.Empty<int>(); ReleaseMemory(); string name = "Local\\GrazePlugin_" + Guid.NewGuid().ToString("N"); maxFrames = blockFrames; hostRate = sampleRate;
                memory = MemoryMappedFile.CreateNew(name, 64 + blockFrames * (buses!=null ? buses.Length*8L : effect ? 16L : 8L)); view = memory.CreateViewAccessor(); view.Write(0, Magic);
                using (var reply = Call(buses!=null ? (ushort)11 : effect ? (ushort)9 : (ushort)2, w => { WriteText(w, plugin.Path); WriteText(w, plugin.Identity.ClassId); WriteText(w, name); w.Write(sampleRate); w.Write(blockFrames); if(buses!=null){w.Write(buses.Length);foreach(int bus in buses)w.Write(bus);} }))
                {
                    ReadText(reply); uint latency = reply.ReadUInt32(); if (latency > sampleRate * 10) throw new IOException("Plugin latency exceeds ten seconds."); LatencySamples = (int)latency;
                    int count = reply.ReadInt32(); if (count < 0 || count > 4096) throw new IOException("Invalid parameter count."); parameters = new MusicPluginParameter[count];
                    for (int i = 0; i < count; i++)
                    {
                        uint id = reply.ReadUInt32(); string title = ReadText(reply), unit = ReadText(reply); double value = reply.ReadDouble();
                        parameters[i] = new MusicPluginParameter(id, title, unit, value, effect ? reply.ReadInt32() : 0);
                    }
                    if(buses!=null)
                    {
                        int n=reply.ReadInt32();if(n<1||n>64)throw new IOException("Invalid output bus count.");outputBuses=new MusicPluginOutputBus[n];
                        for(int i=0;i<n;i++){string title=ReadText(reply);int channels=reply.ReadInt32();bool active=reply.ReadInt32()!=0;if(channels<0||channels>64)throw new IOException("Invalid channel count.");outputBuses[i]=new MusicPluginOutputBus(i,title,channels,active);}
                        foreach(int bus in buses)if(bus>=n||!outputBuses[bus].Active||outputBuses[bus].Channels<1||outputBuses[bus].Channels>2)throw new IOException("Selected output unavailable.");
                    }
                }
                loadedEffect = effect;
                loadedMulti=buses!=null;selectedOutputs=buses??Array.Empty<int>();
                loadedPlugin=plugin;
            }
        }
        public uint ParameterId(string key)
        {
            uint id; if (uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out id)) foreach (var p in parameters) if (p.Id == id) return id;
            MusicPluginParameter found = null;
            foreach (var p in parameters) if (p.Key == key || p.Name == key) { if (found != null) throw new ArgumentException("Ambiguous parameter; use its numeric ID: " + key); found = p; }
            if (found == null) throw new ArgumentException("Unknown plugin parameter: " + key); return found.Id;
        }
        public void Configure(MusicPluginState state, IReadOnlyDictionary<string, double> values = null, bool warm = true)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            lock (gate) using (var reply = Call(3, w =>
            {
                WriteBlob(w, state.Component); WriteBlob(w, state.Controller); w.Write(values?.Count ?? 0);
                if (values != null) foreach (var p in values)
                { if (!MusicTrack.Finite(p.Value) || p.Value < 0 || p.Value > 1) throw new ArgumentException("Parameter must be normalized."); w.Write(ParameterId(p.Key)); w.Write(p.Value); }
                w.Write(warm ? 1 : 0);
            })) { uint latency = reply.ReadUInt32(); if (latency > hostRate * 10) throw new IOException("Plugin latency exceeds ten seconds."); LatencySamples = (int)latency; }
        }
        public MusicPluginState CaptureState()
        { using (var reply = Call(5, null)) return new MusicPluginState(ReadBlob(reply), ReadBlob(reply)); }
        /// <summary>Control-thread preset import; class match, restore, warm and capture a portable VC2 state. No file use in playback.</summary>
        public MusicPluginState LoadPreset(MusicPluginPreset preset)
        {
            if(preset==null)throw new ArgumentNullException(nameof(preset));
            lock(gate)
            {
                if(loadedPlugin==null)throw new InvalidOperationException("Load an approved instrument/effect before its preset.");
                if(loadedPlugin.Identity.ClassId!=preset.Plugin.ClassId)throw new ArgumentException("Preset class does not match the loaded plugin.");
                // Drain earlier Configure(warm:false) parameter queues before the new state so they cannot override it.
                Configure(new MusicPluginState(null),warm:true);
                var previous=CaptureState();
                try{Configure(preset.State,warm:true);return CaptureState();}
                catch(Exception e)
                {
                    try{Configure(previous,warm:true);}catch{Abort();}
                    throw new IOException("Preset restore/capture failed; previous state was restored when possible, otherwise the worker was stopped.",e);
                }
            }
        }
        public MusicPluginState LoadPresetFile(string path,uint? expectedLegacyFxId=null)
        {
            lock(gate)
            {
                if(loadedPlugin==null)throw new InvalidOperationException("Load an approved plugin before reading its preset.");
                return LoadPreset(MusicPluginPreset.Load(path,loadedPlugin.Identity,expectedLegacyFxId));
            }
        }
        public void Process(float[] stereo, int frames, long sampleTime, double bpm, double ppq, double loopBeats,
            IReadOnlyList<MusicPluginMidiEvent> midi = null, int beatsPerBar = 4, IReadOnlyList<MusicPluginParameterPoint> automation = null)
            => ProcessCore(stereo,frames,sampleTime,bpm,ppq,loopBeats,midi,beatsPerBar,automation,null);
        public void ProcessOutputs(float[][] stereo, int frames, long sampleTime, double bpm, double ppq, double loopBeats,
            IReadOnlyList<MusicPluginMidiEvent> midi=null,int beatsPerBar=4,IReadOnlyList<MusicPluginParameterPoint> automation=null)
        {
            if(!loadedMulti||stereo==null||stereo.Length!=selectedOutputs.Length)throw new ArgumentException("Provide one stereo buffer per selected output bus.");
            var seen=new HashSet<float[]>();foreach(var buffer in stereo)if(buffer==null||buffer.Length<frames*2||!seen.Add(buffer))throw new ArgumentException("Output buffers must be distinct and large enough.");
            ProcessCore(stereo[0],frames,sampleTime,bpm,ppq,loopBeats,midi,beatsPerBar,automation,stereo);
        }
        void ProcessCore(float[] stereo,int frames,long sampleTime,double bpm,double ppq,double loopBeats,
            IReadOnlyList<MusicPluginMidiEvent> midi,int beatsPerBar,IReadOnlyList<MusicPluginParameterPoint> automation,float[][] outputs)
        {
            if (loadedEffect) throw new InvalidOperationException("Use ProcessEffect to supply effect audio input.");
            if(loadedMulti!=(outputs!=null))throw new InvalidOperationException("Use ProcessOutputs for a multi-output instrument.");
            if (stereo == null || frames < 1 || frames > maxFrames || stereo.Length < frames * 2 || sampleTime < 0 ||
                !MusicTrack.Finite(bpm) || bpm < MusicTempoMap.MinBpm || bpm > MusicTempoMap.MaxBpm || !MusicTrack.Finite(ppq) ||
                !MusicTrack.Finite(loopBeats) || loopBeats <= 0 || beatsPerBar < 1 || beatsPerBar > 32 || (midi?.Count ?? 0) > 65536 || (automation?.Count ?? 0) > 65536)
                throw new ArgumentException("Invalid plugin process block.");
            bool automated = outputs!=null || (automation != null && automation.Count > 0);
            lock (gate)
            {
                // Op 8 = op 4 plus parameter points (protocol 2), sorted by parameter and offset.
                using (var reply = Call(outputs!=null ? (ushort)12 : automated ? (ushort)8 : (ushort)4, w =>
                {
                    w.Write(frames); w.Write(sampleTime); w.Write(bpm); w.Write(ppq); w.Write(0.0); w.Write(loopBeats); w.Write(1); w.Write(beatsPerBar); w.Write(midi?.Count ?? 0);
                    if (midi != null) foreach (var e in midi)
                    { if (e.Offset < 0 || e.Offset >= frames || e.Key < 0 || e.Key > 127 || !MusicTrack.Finite(e.Velocity) || e.Velocity < 0 || e.Velocity > 1) throw new ArgumentException("Invalid MIDI event."); w.Write(e.Offset); w.Write(e.Key); w.Write(e.Velocity); w.Write(e.NoteId); w.Write(e.On ? 1 : 0); }
                    if (automated)
                    {
                        w.Write(automation?.Count??0); uint lastId = 0; int lastOffset = -1;
                        foreach (var p in automation??Array.Empty<MusicPluginParameterPoint>())
                        {
                            if (p.Offset < 0 || p.Offset >= frames || !MusicTrack.Finite(p.Value) || p.Value < 0 || p.Value > 1 ||
                                p.ParameterId < lastId || (p.ParameterId == lastId && p.Offset < lastOffset)) throw new ArgumentException("Invalid or unsorted automation point.");
                            w.Write(p.ParameterId); w.Write(p.Offset); w.Write(p.Value); lastId = p.ParameterId; lastOffset = p.Offset;
                        }
                    }
                })) { if (reply.ReadInt32() != frames) throw new IOException("Invalid PCM frame count.");if(outputs!=null){uint latency=reply.ReadUInt32();if(latency>hostRate*10)throw new IOException("Invalid plugin latency.");LatencySamples=(int)latency;} }
                if (view.ReadInt32(16) != frames) throw new IOException("PCM publication failed.");
                if(outputs==null)view.ReadArray(64, stereo, 0, frames * 2);
                else for(int b=0;b<outputs.Length;b++)view.ReadArray(64+b*maxFrames*8L,outputs[b],0,frames*2);
            }
        }
        /// <summary>Blocking effect processing with stereo input/output (may be the same array). Control/background threads only.</summary>
        public void ProcessEffect(float[] source, float[] destination, int frames, long sampleTime, double bpm, double ppq, double loopBeats,
            int beatsPerBar = 4, IReadOnlyList<MusicPluginParameterPoint> automation = null)
        {
            if (!loadedEffect || source == null || destination == null || frames < 1 || frames > maxFrames || source.Length < frames * 2 || destination.Length < frames * 2 || sampleTime < 0 ||
                !MusicTrack.Finite(bpm) || bpm < MusicTempoMap.MinBpm || bpm > MusicTempoMap.MaxBpm || !MusicTrack.Finite(ppq) || !MusicTrack.Finite(loopBeats) || loopBeats <= 0 ||
                beatsPerBar < 1 || beatsPerBar > 32 || (automation?.Count ?? 0) > 65536) throw new ArgumentException("Invalid effect process block.");
            for (int i = 0; i < frames * 2; i++) if (!MusicTrack.Finite(source[i])) throw new ArgumentException("Non-finite effect input.");
            lock (gate)
            {
                view.WriteArray(64 + maxFrames * 8L, source, 0, frames * 2);
                using (var reply = Call(10, w =>
                {
                    w.Write(frames); w.Write(sampleTime); w.Write(bpm); w.Write(ppq); w.Write(0.0); w.Write(loopBeats); w.Write(1); w.Write(beatsPerBar); w.Write(0);
                    w.Write(automation?.Count ?? 0); uint lastId = 0; int lastOffset = -1;
                    if (automation != null) foreach (var p in automation)
                    {
                        if (p.Offset < 0 || p.Offset >= frames || !MusicTrack.Finite(p.Value) || p.Value < 0 || p.Value > 1 || p.ParameterId < lastId ||
                            (p.ParameterId == lastId && p.Offset < lastOffset)) throw new ArgumentException("Invalid or unsorted effect automation point.");
                        w.Write(p.ParameterId); w.Write(p.Offset); w.Write(p.Value); lastId = p.ParameterId; lastOffset = p.Offset;
                    }
                }))
                {
                    if (reply.ReadInt32() != frames || view.ReadInt32(16) != frames) throw new IOException("Effect PCM publication failed.");
                    uint latency = reply.ReadUInt32();
                    if (latency != LatencySamples) throw new IOException("Effect latency changed during rendering; prepare its new settings again.");
                }
                view.ReadArray(64, destination, 0, frames * 2);
                for (int i = 0; i < frames * 2; i++) if (!MusicTrack.Finite(destination[i])) throw new IOException("Non-finite effect output.");
            }
        }
        public void Abort() { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        void ReleaseMemory() { view?.Dispose(); memory?.Dispose(); view = null; memory = null; }
        public void Dispose()
        {
            // Abort first: this also interrupts a plugin stuck inside process(), before acquiring the call lock.
            Abort(); lock (gate) { if (disposed) return; disposed = true; watchdog?.Dispose(); input?.Dispose(); output?.Dispose(); ReleaseMemory(); process.Dispose(); }
        }
    }
    public sealed class MusicPluginRegistration
    {
        public string Path { get; }
        public MusicPluginIdentity Identity { get; }
        public bool IsInstrument { get; }
        internal MusicPluginRegistration(string path, MusicPluginIdentity identity, bool isInstrument = true) { Path = path; Identity = identity; IsInstrument = isInstrument; }
    }
    /// <summary>Scan only explicitly trusted installation roots. A mod cannot provide a DLL path or approve itself.</summary>
    public sealed class MusicPluginCatalog
    {
        readonly List<MusicPluginRegistration> plugins = new List<MusicPluginRegistration>();
        public IReadOnlyList<MusicPluginRegistration> Plugins => plugins.AsReadOnly();
        public static string DefaultWindowsRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3");
        // Preserve the released four-argument signature for already compiled instrument clients.
        public static MusicPluginCatalog Scan(string hostExecutable, IEnumerable<string> trustedRoots, IList<string> diagnostics = null, int timeoutMilliseconds = 60000)
            => Scan(hostExecutable, trustedRoots, false, diagnostics, timeoutMilliseconds);
        public static MusicPluginCatalog Scan(string hostExecutable, IEnumerable<string> trustedRoots, bool includeEffects, IList<string> diagnostics = null, int timeoutMilliseconds = 60000)
        {
            if (trustedRoots == null) throw new ArgumentNullException(nameof(trustedRoots));
            var result = new MusicPluginCatalog(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in trustedRoots)
            {
                string full = System.IO.Path.GetFullPath(root); if (!Directory.Exists(full)) continue;
                foreach (string path in Candidates(full)) if (seen.Add(path))
                {
                    try { using (var host = new MusicPluginHost(hostExecutable, timeoutMilliseconds)) result.plugins.AddRange(host.Scan(path, includeEffects)); }
                    catch (Exception e) when (e is IOException || e is System.ComponentModel.Win32Exception) { diagnostics?.Add(path + ": " + e.Message); }
                }
            }
            return result;
        }
        /// <summary>A catalog of already scanned plugins (the renderer's scan cache).</summary>
        internal static MusicPluginCatalog From(IEnumerable<MusicPluginRegistration> scanned)
        { var result = new MusicPluginCatalog(); result.plugins.AddRange(scanned); return result; }
        /// <summary>The .vst3 files and bundles under a trusted root, as <see cref="Scan"/> visits them.</summary>
        internal static IEnumerable<string> Files(string root)
        { string full = System.IO.Path.GetFullPath(root); return Directory.Exists(full) ? Candidates(full) : Array.Empty<string>(); }
        static IEnumerable<string> Candidates(string directory)
        {
            // Never follow junctions/reparse points out of the approved installation tree.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) yield break;
            if (directory.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) { yield return directory; yield break; }
            foreach (string file in Directory.GetFiles(directory, "*.vst3")) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (string child in Directory.GetDirectories(directory)) foreach (string path in Candidates(child)) yield return path;
        }
        public MusicPluginRegistration Resolve(MusicPluginIdentity identity, IEnumerable<string> approvedClassIds)
        {
            if (identity == null || approvedClassIds == null) throw new ArgumentNullException(); bool approved = false;
            foreach (var id in approvedClassIds) if (string.Equals(id, identity.ClassId, StringComparison.OrdinalIgnoreCase)) approved = true;
            if (!approved) throw new UnauthorizedAccessException("Plugin class has not been approved by the application/user: " + identity.ClassId);
            MusicPluginRegistration match = null;
            foreach (var p in plugins) if (p.Identity.ClassId == identity.ClassId &&
                (identity.Name.Length == 0 || p.Identity.Name == identity.Name) && (identity.Vendor.Length == 0 || p.Identity.Vendor == identity.Vendor) &&
                (identity.Version.Length == 0 || p.Identity.Version == identity.Version))
            { if (match != null) throw new InvalidOperationException("Plugin identity occurs more than once in trusted roots."); match = p; }
            return match ?? throw new FileNotFoundException("Approved VST3 instrument identity/version is not installed: " + identity.ClassId);
        }
    }
}
