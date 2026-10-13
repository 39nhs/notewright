using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Graze.Presentation.Audio;

namespace GrazePlugin
{
    /// <summary>
    /// VST3 plugins in song.json: a part's "plugin" (an instrument) and "Inserts" in effects settings (track, bus, master). Windows x64 only:
    /// each plugin runs in its own worker process (engine/Native/PluginHost/graze-plugin-host.exe) and the song is prepared offline before
    /// mixing (MusicPluginInsertRenderer). Only plugins installed in the trusted VST3 folders load: a song names a plugin (class ID or
    /// name), never a file. Usage:
    ///   renderer plugins [--plugin-host EXE] [--vst3-roots "DIR;DIR"] [--scan-cache FILE] [--rescan true]
    ///   renderer plugin-info --plugin NAME|CLASSID [--preset FILE.vstpreset] [same options]
    /// </summary>
    public static partial class Program
    {
        /// <summary>The worker is a Windows x64 program; every other system gets this error instead of a silent stand-in sound.</summary>
        internal static void RequireWindows(string what, bool song = true)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && IntPtr.Size == 8) return;
            throw new PlatformNotSupportedException(what + " needs Windows x64: VST3 plugins run in a Windows worker process. This machine is " +
                Environment.OSVersion.VersionString + (IntPtr.Size == 8 ? "" : " (32-bit process)") + (song
                ? ". Remove the VST3 plugins from the song (use the built-in synth, samples or the built-in effects) or render it on Windows."
                : ". Here, use the built-in synth, samples and the built-in effects instead."));
        }

        static string PluginHost(Dictionary<string, string> o)
        {
            string path = o.TryGetValue("plugin-host", out var h) ? h : Environment.GetEnvironmentVariable("NOTEWRIGHT_PLUGIN_HOST");
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("--plugin-host: give the VST3 worker (engine/Native/PluginHost/graze-plugin-host.exe).");
            if (!File.Exists(path)) throw new FileNotFoundException("The VST3 worker is missing: " + path);
            return Path.GetFullPath(path);
        }

        /// <summary>--vst3-roots, else NOTEWRIGHT_VST3_PATH (both ';'-separated), else the standard system and per-user VST3 folders.</summary>
        static string[] PluginRoots(Dictionary<string, string> o)
        {
            string list = o.TryGetValue("vst3-roots", out var r) ? r : Environment.GetEnvironmentVariable("NOTEWRIGHT_VST3_PATH");
            if (!string.IsNullOrWhiteSpace(list)) return list.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            return new[] { MusicPluginCatalog.DefaultWindowsRoot,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3") };
        }

        /// <summary>
        /// Scans the trusted roots (one worker per file, so a broken plugin only fails itself). With a cache file, a file whose
        /// modification stamp is unchanged is not loaded again.
        /// </summary>
        static List<MusicPluginRegistration> ScanPlugins(Dictionary<string, string> o, out string[] roots, List<string> errors)
        {
            string host = PluginHost(o);
            roots = PluginRoots(o);
            o.TryGetValue("scan-cache", out var cachePath);
            bool rescan = o.TryGetValue("rescan", out var re) && re == "true";
            var cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (!rescan && !string.IsNullOrEmpty(cachePath) && File.Exists(cachePath))
                try { foreach (var kv in Json.Obj(Json.Obj(Json.Parse(File.ReadAllText(cachePath)), "cache").TryGetValue("files", out var f) ? f : null, "cache")) cache[kv.Key] = kv.Value; }
                catch (Exception e) when (e is FormatException || e is IOException) { cache.Clear(); }
            var fresh = new Dictionary<string, object>();
            var result = new List<MusicPluginRegistration>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
                foreach (string path in MusicPluginCatalog.Files(root))
                {
                    if (!seen.Add(path)) continue;
                    string stamp = Stamp(path);
                    Dictionary<string, object> entry = null;
                    if (cache.TryGetValue(path, out var cached) && cached is Dictionary<string, object> c && Json.Str(c, "stamp") == stamp) entry = c;
                    if (entry == null)
                    {
                        var classes = new List<object>(); string error = null;
                        try
                        {
                            using (var worker = new MusicPluginHost(host))
                                foreach (var p in worker.Scan(path, true))
                                    classes.Add(new Dictionary<string, object> { ["classId"] = p.Identity.ClassId, ["name"] = p.Identity.Name,
                                        ["vendor"] = p.Identity.Vendor, ["version"] = p.Identity.Version, ["instrument"] = p.IsInstrument });
                        }
                        catch (Exception e) when (e is IOException || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException) { error = e.Message; }
                        entry = new Dictionary<string, object> { ["stamp"] = stamp, ["classes"] = classes };
                        if (error != null) entry["error"] = error;
                    }
                    fresh[path] = entry;
                    if (Json.Str(entry, "error") is string failed) errors.Add(path + ": " + failed);
                    foreach (var item in Json.List(entry.TryGetValue("classes", out var list) ? list : new List<object>(), path))
                    {
                        var k = Json.Obj(item, path);
                        result.Add(new MusicPluginRegistration(path, new MusicPluginIdentity(Json.Str(k, "classId"), Json.Str(k, "name", ""),
                            Json.Str(k, "vendor", ""), Json.Str(k, "version", "")), Json.Bool(k, "instrument")));
                    }
                }
            if (!string.IsNullOrEmpty(cachePath))
                try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))); File.WriteAllText(cachePath, Json.Write(new Dictionary<string, object> { ["files"] = fresh })); }
                catch (IOException) { } // a cache that cannot be written only costs the next scan time
            return result;
        }

        static string Stamp(string path)
        {
            if (File.Exists(path)) { var f = new FileInfo(path); return f.LastWriteTimeUtc.Ticks + ":" + f.Length; }
            long ticks = 0, bytes = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)) { ticks = Math.Max(ticks, f.LastWriteTimeUtc.Ticks); bytes += f.Length; }
            return ticks + ":" + bytes;
        }

        /// <summary>A class ID (32 hex digits) or a plugin name (case-insensitive), optionally narrowed by vendor and version.</summary>
        static MusicPluginRegistration FindPlugin(List<MusicPluginRegistration> all, string[] roots, string classId, string name, string vendor,
            string version, bool? instrument, string where)
        {
            string wanted = !string.IsNullOrEmpty(classId) ? classId : name;
            if (string.IsNullOrWhiteSpace(wanted)) throw new FormatException(where + ": give the plugin's classId or name.");
            var matches = all.Where(p => (!string.IsNullOrEmpty(classId) ? string.Equals(p.Identity.ClassId, classId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(p.Identity.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(vendor) || string.Equals(p.Identity.Vendor, vendor, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(version) || p.Identity.Version == version)).ToList();
            if (instrument != null && matches.Count > 1) matches = matches.Where(p => p.IsInstrument == instrument).DefaultIfEmpty(matches[0]).ToList();
            var distinct = matches.GroupBy(p => p.Identity.ClassId).Select(g => g.First()).ToList();
            if (distinct.Count == 0)
            {
                var kind = all.Where(p => instrument == null || p.IsInstrument == instrument).Select(p => p.Identity.Name).Distinct().OrderBy(n => n).ToList();
                throw new FileNotFoundException($"{where}: the VST3 {(instrument == true ? "instrument" : instrument == false ? "effect" : "plugin")} '{wanted}' is not installed in " +
                    string.Join("; ", roots) + ". Installed: " + (kind.Count == 0 ? "none" : string.Join(", ", kind)) + " (`notewright plugins` lists them).");
            }
            if (distinct.Count > 1)
                throw new FormatException($"{where}: '{wanted}' matches several plugins ({string.Join(", ", distinct.Select(p => p.Identity.Name + " by " + p.Identity.Vendor + " " + p.Identity.ClassId))}); give its classId.");
            if (instrument != null && distinct[0].IsInstrument != instrument)
                throw new FormatException($"{where}: '{distinct[0].Identity.Name}' is {(distinct[0].IsInstrument ? "an instrument" : "an effect")}; " +
                    (instrument == true ? "a part's \"plugin\" needs an instrument." : "\"Inserts\" need effects."));
            return distinct[0];
        }

        /// <summary>The VST3 plugins one song uses: resolved against the installed ones, approved for this render only.</summary>
        internal sealed class Vst3Song
        {
            readonly MusicPluginInsertContext context;
            readonly MusicPluginMod mod;
            public readonly List<string> Diagnostics = new List<string>();
            public readonly List<string> Used = new List<string>();
            Vst3Song(MusicPluginInsertContext context, MusicPluginMod mod) { this.context = context; this.mod = mod; }

            static readonly string[] TargetKeys = { "classId", "name", "vendor", "version", "state", "parameters", "automation", "gain", "pan",
                "transpose", "velocityCurve", "voices", "outputs" };

            /// <summary>Null when the song uses no VST3 plugin (then nothing here runs and the render is the plain engine's).</summary>
            public static Vst3Song For(MusicCueDefinition song, List<KeyValuePair<string, Dictionary<string, object>>> targets, Dictionary<string, string> o)
            {
                var inserts = new List<KeyValuePair<string, MusicPluginInsertDefinition>>();
                void Collect(string where, MusicMixerSettings settings)
                { foreach (var d in settings?.Inserts ?? Array.Empty<MusicPluginInsertDefinition>()) if (d != null) inserts.Add(new KeyValuePair<string, MusicPluginInsertDefinition>(where, d)); }
                Collect("master", song.Mixer);
                foreach (var b in song.Buses ?? Array.Empty<MusicBusDefinition>()) Collect("bus " + b.Name, b.Effects);
                foreach (var p in song.Parts)
                {
                    if ((p.Effects?.Inserts?.Length ?? 0) > 0 && !p.TrackEffects) throw new FormatException(p.Name + ": effects \"Inserts\" need \"trackEffects\": true.");
                    Collect(p.Name, p.Effects);
                }
                if (targets.Count == 0 && inserts.Count == 0) return null;
                RequireWindows("This song uses VST3 plugins (" + string.Join(", ", targets.Select(t => t.Key).Concat(inserts.Select(i => i.Key + " Inserts")).Distinct()) + "); it");
                if (song.PlaybackVersion < MusicPlaybackVersion.V3) throw new FormatException("VST3 plugins need \"playbackVersion\": 3.");
                var errors = new List<string>();
                var all = ScanPlugins(o, out var roots, errors);
                var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var used = new List<string>();
                foreach (var entry in inserts)
                {
                    var d = entry.Value;
                    var found = FindPlugin(all, roots, d.ClassId, d.Name, d.Vendor, d.Version, false, entry.Key + " Inserts");
                    d.ClassId = found.Identity.ClassId; d.Name = found.Identity.Name; d.Vendor = found.Identity.Vendor;
                    approved.Add(d.ClassId); used.Add(found.Identity.Name);
                }
                var list = new List<object>();
                foreach (var target in targets)
                {
                    var t = target.Value; string where = target.Key + ".plugin";
                    var unknown = t.Keys.Except(TargetKeys).ToArray();
                    if (unknown.Length > 0) throw new FormatException(where + ": unknown fields " + string.Join(", ", unknown) + " (valid: " + string.Join(", ", TargetKeys) + ").");
                    var found = FindPlugin(all, roots, Json.Str(t, "classId"), Json.Str(t, "name"), Json.Str(t, "vendor"), Json.Str(t, "version"), true, where);
                    approved.Add(found.Identity.ClassId); used.Add(found.Identity.Name);
                    var item = new Dictionary<string, object> { ["song"] = song.name, ["instrument"] = target.Key, ["plugin"] = new Dictionary<string, object>
                        { ["classId"] = found.Identity.ClassId, ["name"] = found.Identity.Name, ["vendor"] = found.Identity.Vendor, ["version"] = Json.Str(t, "version", "") } };
                    foreach (var kv in t) if (Array.IndexOf(TargetKeys, kv.Key) >= 4) item[kv.Key] = kv.Value;
                    list.Add(item);
                }
                MusicPluginMod mod = null;
                if (list.Count > 0)
                    try { mod = MusicPluginMod.FromJson(Json.Write(new Dictionary<string, object> { ["format"] = MusicPluginMod.MultiOutputFormat, ["name"] = song.name, ["targets"] = list })); }
                    catch (ArgumentException e) { throw new FormatException("parts' \"plugin\": " + e.Message); }
                var result = new Vst3Song(new MusicPluginInsertContext(PluginHost(o), MusicPluginCatalog.From(all), approved), mod);
                result.Used.AddRange(used.Distinct());
                return result;
            }

            /// <summary>The instrument targets that sound in this cue (a stem leaves the other parts at gain 0: their plugins need not run).</summary>
            MusicPluginMod For(MusicCue cue)
            {
                if (mod == null) return null;
                var keep = mod.Targets.Where(t => t.Outputs.Count > 0 || cue.Parts.Any(p => p.InstrumentId == t.Instrument && p.Gain > 0)).ToArray();
                return keep.Length == 0 ? null : new MusicPluginMod(mod.Name, keep);
            }

            /// <summary>Plugins run offline over the whole range (from the song's start, as the engine plays it) before the mix.</summary>
            public MusicPreparedInsertSong Prepare(MusicCue cue, int rate, double seconds)
            {
                if (seconds > cue.Tempo.SecondsAt(cue.LoopBeats) + 30) throw new ArgumentException("A song with VST3 plugins renders at most 30 seconds past its end (--tail).");
                var prepared = MusicPluginInsertRenderer.PrepareAsync(cue, context, rate, seconds, 2048, default, For(cue)).GetAwaiter().GetResult();
                lock (Diagnostics) foreach (var d in prepared.Diagnostics) if (!Diagnostics.Contains(d)) Diagnostics.Add(d);
                return prepared;
            }

            public float[] RenderSong(MusicCueDefinition song, int rate)
            {
                var cue = song.Compile();
                return Prepare(cue, rate, cue.Tempo.SecondsAt(cue.LoopBeats)).CopyStereo();
            }
        }

        static Dictionary<string, object> ListPlugins(Dictionary<string, string> o)
        {
            RequireWindows("Listing VST3 plugins", false);
            var errors = new List<string>();
            int protocol;
            using (var worker = new MusicPluginHost(PluginHost(o))) protocol = worker.ProtocolVersion; // the worker starts and answers
            var all = ScanPlugins(o, out var roots, errors);
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["worker"] = protocol, ["roots"] = roots.ToList<object>(),
                ["plugins"] = all.OrderBy(p => p.Identity.Name, StringComparer.OrdinalIgnoreCase).Select(p => (object)new Dictionary<string, object>
                {
                    ["name"] = p.Identity.Name, ["vendor"] = p.Identity.Vendor, ["version"] = p.Identity.Version, ["classId"] = p.Identity.ClassId,
                    ["kind"] = p.IsInstrument ? "instrument" : "effect", ["path"] = p.Path,
                }).ToList(),
                ["errors"] = errors.ToList<object>(),
            };
        }

        /// <summary>Loads one plugin and lists its parameters (keys for "parameters"/"automation"), its latency and its state (or a preset's).</summary>
        static Dictionary<string, object> PluginInfo(Dictionary<string, string> o)
        {
            RequireWindows("Inspecting a VST3 plugin", false);
            if (!o.TryGetValue("plugin", out var wanted)) throw new ArgumentException("--plugin NAME|CLASSID is required");
            var errors = new List<string>();
            var all = ScanPlugins(o, out var roots, errors);
            bool id = wanted.Length == 32 && wanted.All(Uri.IsHexDigit);
            var plugin = FindPlugin(all, roots, id ? wanted : null, id ? null : wanted, null, null, null, "--plugin");
            using (var host = new MusicPluginHost(PluginHost(o)))
            {
                if (plugin.IsInstrument) host.Load(plugin, 48000, 512); else host.LoadEffect(plugin, 48000, 512);
                var state = o.TryGetValue("preset", out var preset) ? host.LoadPresetFile(preset) : host.CaptureState();
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["name"] = plugin.Identity.Name, ["vendor"] = plugin.Identity.Vendor, ["version"] = plugin.Identity.Version,
                    ["classId"] = plugin.Identity.ClassId, ["kind"] = plugin.IsInstrument ? "instrument" : "effect", ["latencySamples"] = host.LatencySamples,
                    ["parameters"] = host.Parameters.Select(p => (object)new Dictionary<string, object>
                    {
                        ["id"] = (long)p.Id, ["key"] = p.Key, ["name"] = p.Name, ["unit"] = p.Unit, ["value"] = p.Value, ["steps"] = p.StepCount,
                    }).ToList(),
                    ["state"] = state.ToBase64(), ["stateFrom"] = preset == null ? "default" : Path.GetFileName(preset),
                };
            }
        }
    }
}
