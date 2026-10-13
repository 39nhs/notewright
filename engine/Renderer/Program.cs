using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Graze.Presentation.Audio;
using UnityEngine;

namespace GrazePlugin
{
    /// <summary>
    /// Headless renderer: song.json (engine/song-json.md) → WAV with the AdaptiveMusicEngine. Prints a JSON report on stdout. Usage:
    ///   renderer render --song song.json --out mix.wav [--samples DIR] [--rate 48000] [--bits 16|24|32]
    ///                   [--start BEAT] [--length BEATS] [--tail SECONDS] [--normalize DBFS] [--stems DIR] [--jobs N] [--report FILE]
    /// A range (--start/--length) plays from the song's start silently up to --start and stops at its end (see EndAt). The mix and the
    /// stems render on up to --jobs threads (default min(4, processors)); each has its own engine, so the audio is the same as one by one.
    /// </summary>
    public static partial class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || (args[0] != "render" && args[0] != "plugins" && args[0] != "plugin-info" && args[0] != "tempo"))
                    throw new ArgumentException("usage: renderer render --song song.json --out mix.wav [options] | renderer plugins | renderer plugin-info --plugin NAME | renderer tempo --audio FILE.wav");
                var o = Options(args);
                var report = args[0] == "plugins" ? ListPlugins(o) : args[0] == "plugin-info" ? PluginInfo(o) : args[0] == "tempo" ? Tempo(o) : Render(o);
                string json = Json.Write(report);
                if (o.TryGetValue("report", out var path)) File.WriteAllText(path, json);
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception e)
            {
                var error = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                if (error is AggregateException a && a.InnerExceptions.Count > 0) error = a.InnerExceptions[0];
                Console.WriteLine(Json.Write(new Dictionary<string, object> { ["ok"] = false, ["error"] = error.Message, ["type"] = error.GetType().Name }));
                return 2;
            }
        }

        static Dictionary<string, string> Options(string[] args)
        {
            var o = new Dictionary<string, string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException("bad option " + args[i]);
                o[args[i].Substring(2)] = args[++i];
            }
            if (args[0] == "render" && (!o.ContainsKey("song") || !o.ContainsKey("out"))) throw new ArgumentException("--song and --out are required");
            return o;
        }

        static double D(Dictionary<string, string> o, string key, double fallback) =>
            o.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

        static Dictionary<string, object> Render(Dictionary<string, string> o)
        {
            string songPath = Path.GetFullPath(o["song"]);
            string samples = o.TryGetValue("samples", out var s) ? s : Path.Combine(Path.GetDirectoryName(songPath), "Samples");
            int rate = (int)D(o, "rate", 48000), bits = (int)D(o, "bits", 16);
            var song = SongLoader.Load(File.ReadAllText(songPath), samples, out var groups, out var pluginTargets);
            var vst = Vst3Song.For(song, pluginTargets, o); // null without VST3 plugins (Windows x64 only)
            var lufs = vst == null || song.TargetLufs == 0 ? MusicLoudness.MatchTarget(song, rate) // "targetLufs": the master limiter input for the whole song
                : MusicLoudness.Match(song, song.TargetLufs, x => vst.RenderSong(x, rate), rate);
            double start = D(o, "start", 0), length = D(o, "length", song.LoopBeats - start), tail = D(o, "tail", 0);
            if (start < 0 || start >= song.LoopBeats || length <= 0) throw new ArgumentException("--start/--length outside the song");
            double end = Math.Min(song.LoopBeats, start + length);
            if (end < song.LoopBeats) EndAt(song, end);
            var cue = song.Compile();
            double startSeconds = cue.Tempo.SecondsAt(start), seconds = cue.Tempo.SecondsAt(end) - startSeconds + tail;
            long frames = Math.Max(1, (long)Math.Round(seconds * rate));
            int jobs = Math.Max(1, Math.Min(64, (int)D(o, "jobs", Math.Min(4, Environment.ProcessorCount))));

            // Job 0 is the mix; the others are stems (see Stem). All render at once; the gain needs the mix, so the WAVs are written after.
            o.TryGetValue("stems", out var stems);
            var names = stems == null ? new List<string>() : groups.Distinct().ToList();
            var sounding = song.NoiseRegions.Where(r => r != null && r.Enabled && !r.MasterFilter).ToList();
            string regionStem = null;
            if (stems != null && sounding.Count > 0)
            {
                regionStem = "regions";
                while (names.Contains(regionStem)) regionStem = "_" + regionStem;
                names.Add(regionStem);
            }
            var temps = new string[names.Count + 1];
            var results = new Stats[temps.Length];
            var notes = new int[temps.Length];
            var mute = song.Parts.Select(p => p.Mute).ToArray();
            var gate = new object();
            try
            {
                for (int t = 0; t < temps.Length; t++) temps[t] = Path.GetTempFileName();
                System.Threading.Tasks.Parallel.For(0, temps.Length, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = jobs }, t =>
                {
                    var job = cue;
                    if (t > 0) lock (gate) job = Stem(song, groups, names[t - 1], names[t - 1] == regionStem, sounding, mute, start, end, out notes[t]);
                    results[t] = RenderToFloat(job, rate, startSeconds, frames, temps[t], vst);
                });
                var mix = results[0];
                double gain = 1;
                if (o.TryGetValue("normalize", out var target) && mix.Peak > 0)
                    gain = Math.Pow(10, double.Parse(target, CultureInfo.InvariantCulture) / 20) / mix.Peak;
                CopyToWav(temps[0], o["out"], rate, bits, gain);
                var report = new Dictionary<string, object>
                {
                    ["ok"] = true, ["out"] = Path.GetFullPath(o["out"]), ["seconds"] = frames / (double)rate, ["rate"] = rate, ["bits"] = bits,
                    ["playbackVersion"] = cue.PlaybackVersion, ["voiceLimit"] = MusicPlaybackVersion.VoiceLimit(cue.PlaybackVersion),
                    ["peakVoices"] = mix.PeakVoices, ["droppedNotes"] = mix.Dropped,
                    ["peakDb"] = Db(mix.Peak), ["rmsDb"] = Db(mix.Rms), ["nearFullScale"] = mix.NearFullScale,
                    ["gainDb"] = Db(gain), ["outputPeakDb"] = Db(mix.Peak * gain), ["outputRmsDb"] = Db(mix.Rms * gain),
                    ["targetLufs"] = song.TargetLufs, ["lufs"] = lufs == null ? (object)null : Math.Round(lufs.Value, 2), ["limiterInputDb"] = song.Mixer.LimiterInputDb,
                    ["jobs"] = jobs,
                    ["plugins"] = vst == null ? new List<object>() : vst.Used.ToList<object>(),
                    ["pluginWarnings"] = vst == null ? new List<object>() : vst.Diagnostics.ToList<object>(),
                    ["perSecond"] = mix.PerSecond.Select(x => (object)new List<object> { Math.Round(Db(x.Rms), 1), Math.Round(Db(x.Peak), 1) }).ToList(),
                    // Analysis (before --normalize): loudness, frequency bands and stereo width.
                    ["lufs"] = mix.Analysis.Lufs, ["truePeakDb"] = mix.Analysis.TruePeakDb, ["bandNames"] = Analysis.BandNames.ToList<object>(),
                    ["bands"] = mix.Analysis.Average(), ["bandsPerSecond"] = mix.Analysis.PerSecond(), ["widthPerSecond"] = mix.Analysis.Width.Select(x => (object)x).ToList(),
                };
                if (stems != null)
                {
                    var parts = new List<object>();
                    for (int t = 1; t < temps.Length; t++)
                    {
                        string file = Path.Combine(stems, Safe(names[t - 1]) + ".wav");
                        CopyToWav(temps[t], file, rate, bits, gain);
                        var stem = results[t];
                        parts.Add(new Dictionary<string, object> { ["name"] = names[t - 1], ["kind"] = names[t - 1] == regionStem ? "regions" : "track",
                            ["file"] = Path.GetFullPath(file), ["notes"] = notes[t], ["rmsDb"] = Db(stem.Rms), ["peakDb"] = Db(stem.Peak), ["peakVoices"] = stem.PeakVoices,
                            ["lufs"] = stem.Analysis.Lufs, ["bands"] = stem.Analysis.Average(), ["bandsPerSecond"] = stem.Analysis.PerSecond(),
                            ["widthPerSecond"] = stem.Analysis.Width.Select(x => (object)x).ToList() });
                    }
                    report["parts"] = parts;
                }
                return report;
            }
            finally { foreach (var temp in temps) if (temp != null) File.Delete(temp); }
        }

        /// <summary>
        /// A stem's cue (call under a lock: it sets the definition's flags, compiles and puts them back). A track stem solos its "stem" group
        /// (a part's optional "stem" key, else its name). Noise regions that play as sound go to their own "regions" stem instead of into every
        /// track's stem, so the stems add up to the mix; MasterFilter regions process every stem, as they process the mix.
        /// </summary>
        static MusicCue Stem(MusicCueDefinition song, string[] groups, string group, bool regions, List<MusicNoiseRegion> sounding, bool[] mute,
            double start, double end, out int notes)
        {
            notes = 0;
            for (int i = 0; i < song.Parts.Length; i++)
            {
                song.Parts[i].Solo = !regions && groups[i] == group;
                song.Parts[i].Mute = mute[i] || regions;
                if (song.Parts[i].Solo) notes += MusicPatterns.PlayedNotes(song, song.Parts[i]).Count(n => n.Beat < end && n.Beat + n.Duration > start);
            }
            foreach (var r in sounding) r.Enabled = regions;
            if (regions) notes = sounding.Count(r => r.StartBeat < end && r.StartBeat + r.DurationBeats > start);
            try { return song.Compile(); }
            finally
            {
                for (int i = 0; i < song.Parts.Length; i++) { song.Parts[i].Solo = false; song.Parts[i].Mute = mute[i]; }
                foreach (var r in sounding) r.Enabled = true;
            }
        }

        /// <summary>
        /// A range render (--length before the song's end) stops the music at <paramref name="end"/>, so the tail holds only releases and
        /// effect decay, never the next section. Notes (pattern placements included) and noise or audio regions that start there or later are
        /// dropped; notes still sounding end there (only the release moves: their sound and expression up to the end are unchanged); a noise
        /// or audio region that is cut ends there and fades out with its own fade-out. Changes this song definition only (not the runtime).
        /// </summary>
        static void EndAt(MusicCueDefinition song, double end)
        {
            foreach (var part in song.Parts)
            {
                part.Notes = MusicPatterns.PlayedNotes(song, part).Where(n => n.Beat + 1e-9 < end)
                    .Select(n => { if (n.Beat + n.Duration > end) n.Duration = end - n.Beat; return n; }).ToArray();
                part.Placements = Array.Empty<MusicPatternPlacement>();
                part.Regions = (part.Regions ?? Array.Empty<MusicAudioRegion>()).Where(r => r == null || r.StartBeat + 1e-9 < end).Select(r =>
                {
                    if (r == null || r.StartBeat + r.LengthBeats <= end) return r;
                    var cut = MusicAudioRegions.Piece(song, r, 0, end - r.StartBeat);
                    cut.FadeOutBeats = (float)Math.Min(r.FadeOutBeats, cut.LengthBeats - cut.FadeInBeats);
                    return cut;
                }).ToArray();
            }
            song.NoiseRegions = song.NoiseRegions.Where(r => r == null || r.StartBeat + 1e-9 < end).ToArray();
            foreach (var r in song.NoiseRegions)
            {
                if (r == null || r.StartBeat + r.DurationBeats <= end) continue;
                r.DurationBeats = end - r.StartBeat;
                r.FadeInBeats = Math.Min(r.FadeInBeats, r.DurationBeats);
                r.FadeOutBeats = Math.Min(r.FadeOutBeats, r.DurationBeats - r.FadeInBeats);
            }
        }

        static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        static double Db(double x) => x > 1e-9 ? Math.Round(20 * Math.Log10(x), 2) : -180;

        sealed class Stats { public double Peak, Rms; public long NearFullScale; public int Dropped, PeakVoices; public List<(double Rms, double Peak)> PerSecond = new List<(double, double)>(); public Analysis Analysis; }

        /// <summary>
        /// Renders one second at a time into a raw float32 stereo file (playOnce). With VST3 plugins the song is prepared offline first
        /// (<see cref="Vst3Song.Prepare"/>) and this reads the prepared audio; voice counts are then not reported (0).
        /// </summary>
        static Stats RenderToFloat(MusicCue cue, int rate, double startSeconds, long frames, string path, Vst3Song vst = null)
        {
            var engine = vst == null ? new AdaptiveMusicEngine(cue, rate, playOnce: true) : null;
            var block = new float[rate * 2];
            long first = (long)Math.Round(startSeconds * rate);
            var prepared = vst == null ? null : vst.Prepare(cue, rate, (first + frames) / (double)rate).CreateReader(checked((int)first), checked((int)frames));
            if (engine != null)
            for (long skip = first; skip > 0; skip -= rate)
            {
                int n = (int)Math.Min(rate, skip);
                engine.RenderStereo(n == rate ? block : new float[n * 2]);
            }
            var stats = new Stats { Analysis = new Analysis(rate, frames / (double)rate) };
            double energy = 0;
            var bytes = new byte[rate * 8];
            using (var w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)))
            {
                for (long done = 0; done < frames; done += rate)
                {
                    int n = (int)Math.Min(rate, frames - done);
                    var buffer = n == rate ? block : new float[n * 2];
                    if (engine != null) engine.RenderStereo(buffer); else prepared.ReadStereo(buffer);
                    double secondEnergy = 0, secondPeak = 0;
                    for (int i = 0; i < n * 2; i++)
                    {
                        float v = buffer[i];
                        double a = Math.Abs(v);
                        if (a > secondPeak) secondPeak = a;
                        if (a >= .98) stats.NearFullScale++;
                        secondEnergy += v * (double)v;
                    }
                    Buffer.BlockCopy(buffer, 0, bytes, 0, n * 8); // the same little-endian float32 values BinaryWriter.Write(float) wrote
                    w.Write(bytes, 0, n * 8);
                    energy += secondEnergy;
                    stats.Peak = Math.Max(stats.Peak, secondPeak);
                    stats.PerSecond.Add((Math.Sqrt(secondEnergy / (n * 2)), secondPeak));
                    stats.Analysis.Add(buffer, n);
                }
            }
            stats.Rms = Math.Sqrt(energy / (frames * 2));
            if (engine != null) { stats.Dropped = engine.DroppedNotes; stats.PeakVoices = engine.PeakVoices; }
            return stats;
        }

        static void CopyToWav(string floatPath, string wavPath, int rate, int bits, double gain)
        {
            using (var r = new FileStream(floatPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var w = new WavIO.Writer(wavPath, rate, bits))
            {
                var buffer = new float[rate * 2]; var bytes = new byte[buffer.Length * 4];
                int got;
                while ((got = ReadFull(r, bytes)) > 0)
                {
                    Buffer.BlockCopy(bytes, 0, buffer, 0, got - got % 4);
                    w.Write(buffer, got / 4, gain);
                }
            }
        }

        static int ReadFull(Stream s, byte[] bytes)
        {
            int total = 0, n;
            while (total < bytes.Length && (n = s.Read(bytes, total, bytes.Length - total)) > 0) total += n;
            return total;
        }
    }

    /// <summary>song.json → MusicCueDefinition (the format of engine/song-json.md; missing numbers are 0, unknown fields in settings objects are errors).</summary>
    public static class SongLoader
    {
        static MusicAutomationLane[] Lanes(Dictionary<string, object> owner, string key, string where)
        {
            if (!owner.TryGetValue(key, out var raw) || raw == null) return Array.Empty<MusicAutomationLane>();
            return Json.List(raw, where + "." + key).Select((item, i) =>
            {
                var lane = Json.Obj(item, where + "." + key + "[" + i + "]");
                string name = Json.Str(lane, "target") ?? "";
                if (!MusicAutomation.JsonNames.TryGetValue(name, out var target))
                    throw new FormatException(where + ": automation target must be one of " + string.Join(", ", MusicAutomation.JsonNames.Keys) + ".");
                var values = lane.TryGetValue("points", out var pointsRaw) && pointsRaw != null ? Json.List(pointsRaw, where + " points").Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray() : Array.Empty<double>();
                if (values.Length == 0 || values.Length % 3 != 0) throw new FormatException(where + ": automation points must be beat,value,curve triples.");
                var points = new MusicAutomationPoint[values.Length / 3];
                for (int p = 0; p < points.Length; p++) points[p] = new MusicAutomationPoint(values[p * 3], (float)values[p * 3 + 1], (MusicAutomationCurve)(int)values[p * 3 + 2]);
                return new MusicAutomationLane(target, points) { Bypass = Json.Bool(lane, "disabled") };
            }).ToArray();
        }

        /// <summary>Track modulators: {"kind": "lfo"|"follower", "target": automation target name, "amount", LFO "wave", "rateHz", "syncBeats",
        /// "phase"; follower "source" (track name), "attackMs", "releaseMs"}.</summary>
        static MusicModulator[] Modulators(Dictionary<string, object> part, string name)
        {
            if (!part.TryGetValue("modulators", out var raw) || raw == null) return Array.Empty<MusicModulator>();
            return Json.List(raw, name + ".modulators").Select((item, i) =>
            {
                string where = name + ".modulators[" + i + "]";
                var m = Json.Obj(item, where);
                var unknown = m.Keys.Except(new[] { "kind", "target", "amount", "wave", "rateHz", "syncBeats", "phase", "source", "attackMs", "releaseMs", "disabled" }).ToArray();
                if (unknown.Length > 0) throw new FormatException(where + ": unknown fields " + string.Join(", ", unknown) + ".");
                string target = Json.Str(m, "target") ?? "";
                if (!MusicAutomation.JsonNames.TryGetValue(target, out var t))
                    throw new FormatException(where + ": target must be one of " + string.Join(", ", MusicAutomation.JsonNames.Keys) + ".");
                string kind = Json.Str(m, "kind") ?? "lfo", wave = Json.Str(m, "wave");
                return new MusicModulator
                {
                    Enabled = !Json.Bool(m, "disabled"), Kind = (MusicModulatorKind)Enum.Parse(typeof(MusicModulatorKind), kind, true), Target = t,
                    Amount = (float)Json.Num(m, "amount"), Wave = string.IsNullOrEmpty(wave) ? MusicLfoWave.Sine : (MusicLfoWave)Enum.Parse(typeof(MusicLfoWave), wave, true),
                    RateHz = Json.Num(m, "rateHz") > 0 ? (float)Json.Num(m, "rateHz") : 1, SyncBeats = (float)Json.Num(m, "syncBeats"), Phase = (float)Json.Num(m, "phase"),
                    Source = Json.Str(m, "source") ?? "", AttackMs = Json.Num(m, "attackMs") > 0 ? (float)Json.Num(m, "attackMs") : 10,
                    ReleaseMs = Json.Num(m, "releaseMs") > 0 ? (float)Json.Num(m, "releaseMs") : 200,
                };
            }).ToArray();
        }

        public static MusicCueDefinition Load(string json, string samplesFolder) => Load(json, samplesFolder, out _, out _);

        public static MusicCueDefinition Load(string json, string samplesFolder, out string[] stemGroups) => Load(json, samplesFolder, out stemGroups, out _);

        /// <param name="stemGroups">Per part: its optional "stem" key (renderer only), else its name.</param>
        /// <param name="plugins">Per part with a "plugin" (a VST3 instrument, Windows x64 only): its name and the plugin object (see Vst3.cs).</param>
        public static MusicCueDefinition Load(string json, string samplesFolder, out string[] stemGroups, out List<KeyValuePair<string, Dictionary<string, object>>> plugins)
        {
            var targets = new List<KeyValuePair<string, Dictionary<string, object>>>();
            var root = Json.Obj(Json.Parse(json), "song");
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = Json.Str(root, "name", "Song");
            song.Bpm = Json.Num(root, "bpm");
            song.BeatsPerBar = (int)Json.Num(root, "beatsPerBar");
            song.MeterChanges = root.TryGetValue("meter", out var meterRaw) && meterRaw != null
                ? Json.List(meterRaw, "meter").Select((m, i) => { var o = Json.Obj(m, "meter[" + i + "]"); return new MusicMeterChange(Json.Num(o, "beat"), (int)Json.Num(o, "numerator"), (int)Json.Num(o, "denominator"), Json.Str(o, "grouping")); }).ToArray()
                : Array.Empty<MusicMeterChange>();
            song.LoopBeats = Json.Num(root, "loopBeats");
            // Section markers (same filter as the Unity builder: ones at or past the loop end stay out).
            song.Sections = root.TryGetValue("sections", out var sectionsRaw) && sectionsRaw != null
                ? Json.List(sectionsRaw, "sections").Select((x, i) => { var o = Json.Obj(x, "sections[" + i + "]"); return new MusicSectionMarker((Json.Str(o, "name") ?? "").Trim(), Json.Num(o, "beat")); })
                    .Where(m => m.Name.Length > 0 && m.Beat >= 0 && m.Beat < song.LoopBeats - 1e-9).GroupBy(m => Math.Round(m.Beat, 6)).Select(g => g.Last()).OrderBy(m => m.Beat).ToArray()
                : Array.Empty<MusicSectionMarker>();
            song.Key = Json.Str(root, "key") ?? "";
            song.KeyChanges = root.TryGetValue("keys", out var keysRaw) && keysRaw != null
                ? Json.List(keysRaw, "keys").Select((x, i) => { var o = Json.Obj(x, "keys[" + i + "]"); return new MusicKeyChange(Json.Num(o, "beat"), Json.Str(o, "key")); }).ToArray()
                : Array.Empty<MusicKeyChange>();
            song.Chords = root.TryGetValue("chords", out var chordsRaw) && chordsRaw != null
                ? Json.List(chordsRaw, "chords").Select((x, i) => { var o = Json.Obj(x, "chords[" + i + "]"); return new MusicChordMarker(Json.Num(o, "beat"), Json.Str(o, "chord")); }).ToArray()
                : Array.Empty<MusicChordMarker>();
            song.Swing = (float)Json.Num(root, "swing"); song.SwingGrid = Json.Num(root, "swingGrid") > 0 ? (float)Json.Num(root, "swingGrid") : .5f;
            song.TargetLufs = (float)Json.Num(root, "targetLufs"); song.VariationSeed = (int)Json.Num(root, "variationSeed");
            song.PlaybackVersion = MusicPlaybackVersion.Resolve((int)Json.Num(root, "playbackVersion"));
            song.TempoChanges = root.TryGetValue("tempo", out var tempo) && tempo != null
                ? Json.List(tempo, "tempo").Select((t, i) => (MusicTempoChange)Json.Fill(new MusicTempoChange(), Json.Obj(t, "tempo[" + i + "]"), "tempo[" + i + "]")).ToArray()
                : Array.Empty<MusicTempoChange>();
            song.Mixer = root.TryGetValue("master", out var master) && master != null
                ? (MusicMixerSettings)Json.Fill(new MusicMixerSettings(), Json.Obj(master, "master"), "master") : new MusicMixerSettings();
            song.NoiseRegions = root.TryGetValue("regions", out var regions) && regions != null
                ? Json.List(regions, "regions").Select((r, i) => (MusicNoiseRegion)Json.Fill(new MusicNoiseRegion(), Json.Obj(r, "regions[" + i + "]"), "regions[" + i + "]")).ToArray()
                : Array.Empty<MusicNoiseRegion>();
            if (!root.TryGetValue("parts", out var partList) || partList == null || Json.List(partList, "parts").Count == 0)
                throw new FormatException("Song data has no parts.");
            var clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
            AudioClip Clip(string sample, string owner)
            {
                string path = Path.IsPathRooted(sample) ? sample : Path.Combine(samplesFolder, sample);
                if (!clips.TryGetValue(path, out var clip))
                {
                    if (!File.Exists(path)) throw new FileNotFoundException(owner + ": missing sample " + path);
                    var audio = WavIO.Read(path);
                    clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), audio.Frames, audio.Channels, audio.Rate, false);
                    clip.SetData(audio.Interleaved, 0);
                    clips[path] = clip;
                }
                return clip;
            }
            // V3 mix buses, as SongJsonBuilder: name, output (empty = master), effects, automation, impulse.
            song.Buses = root.TryGetValue("buses", out var busList) && busList != null
                ? Json.List(busList, "buses").Select((item, i) =>
                {
                    var b = Json.Obj(item, "buses[" + i + "]");
                    string busName = Json.Str(b, "name") ?? "";
                    var effects = b.TryGetValue("effects", out var fx) && fx != null
                        ? (MusicMixerSettings)Json.Fill(new MusicMixerSettings(), Json.Obj(fx, "Bus " + busName + ".effects"), "Bus " + busName + ".effects") : new MusicMixerSettings();
                    string busImpulse = Json.Str(b, "impulse");
                    if (!string.IsNullOrEmpty(busImpulse)) effects.CabinetImpulse = Clip(busImpulse, "Bus " + busName + " cabinet IR");
                    return new MusicBusDefinition { Name = busName, Output = Json.Str(b, "output") ?? "", Effects = effects, Automation = Lanes(b, "automation", "Bus " + busName), Pan = (float)Json.Num(b, "pan") };
                }).ToArray()
                : Array.Empty<MusicBusDefinition>();
            string masterImpulse=Json.Str(root,"masterImpulse");
            if(!string.IsNullOrEmpty(masterImpulse))song.Mixer.CabinetImpulse=Clip(masterImpulse,"Master cabinet IR");
            song.MasterAutomation = Lanes(root, "masterAutomation", "master");
            stemGroups = Json.List(partList, "parts").Select((item, index) =>
            {
                var p = Json.Obj(item, "parts[" + index + "]");
                return Json.Str(p, "stem") ?? Json.Str(p, "name", "Part" + index);
            }).ToArray();
            song.Patterns = root.TryGetValue("patterns", out var patternsRaw) && patternsRaw != null
                ? Json.List(patternsRaw, "patterns").Select((x, i) =>
                {
                    var o = Json.Obj(x, "patterns[" + i + "]"); string name = Json.Str(o, "name") ?? "Pattern " + (i + 1);
                    var q = Json.List(o.TryGetValue("notes", out var qn) ? qn : new List<object>(), name + ".notes");
                    if (q.Count % 4 != 0) throw new FormatException(name + ": notes must be beat,duration,key,velocity quads.");
                    return new MusicPattern { Name = name, LengthBeats = Json.Num(o, "length", 4), Notes = Enumerable.Range(0, q.Count / 4).Select(k => new MusicNote(Convert.ToDouble(q[k * 4], CultureInfo.InvariantCulture),
                        Convert.ToDouble(q[k * 4 + 1], CultureInfo.InvariantCulture), Convert.ToInt32(q[k * 4 + 2], CultureInfo.InvariantCulture), Convert.ToSingle(q[k * 4 + 3], CultureInfo.InvariantCulture))).ToArray() };
                }).ToArray()
                : Array.Empty<MusicPattern>();
            song.Parts = Json.List(partList, "parts").Select((item, index) =>
            {
                var p = Json.Obj(item, "parts[" + index + "]");
                string name = Json.Str(p, "name", "Part" + index);
                var notes = Json.List(p.TryGetValue("notes", out var n) ? n : new List<object>(), name + ".notes");
                if (notes.Count % 4 != 0) throw new FormatException(name + ": notes must be beat,duration,key,velocity quads.");
                var quads = new MusicNote[notes.Count / 4];
                for (int i = 0; i < quads.Length; i++)
                    quads[i] = new MusicNote(Convert.ToDouble(notes[i * 4], CultureInfo.InvariantCulture), Convert.ToDouble(notes[i * 4 + 1], CultureInfo.InvariantCulture),
                        Convert.ToInt32(notes[i * 4 + 2], CultureInfo.InvariantCulture), Convert.ToSingle(notes[i * 4 + 3], CultureInfo.InvariantCulture));
                // Per-note expression, matched to notes by beat and key (as the Unity builder).
                if (p.TryGetValue("expression", out var exprRaw) && exprRaw != null)
                    foreach (var (x, k) in Json.List(exprRaw, name + ".expression").Select((x, k) => (Json.Obj(x, name + ".expression[" + k + "]"), k)))
                    {
                        double beat = Json.Num(x, "beat"); int key = (int)Json.Num(x, "key");
                        int at = Array.FindIndex(quads, q => Math.Abs(q.Beat - beat) < 1e-6 && q.Key == key);
                        if (at < 0) throw new FormatException($"{name}: expression at beat {beat} key {key} matches no note.");
                        quads[at].BendFrom = (float)Json.Num(x, "bendFrom"); quads[at].BendTo = (float)Json.Num(x, "bendTo");
                        quads[at].BendStart = (float)Json.Num(x, "bendStart"); quads[at].BendEnd = (float)Json.Num(x, "bendEnd");
                        quads[at].Vibrato = (float)Json.Num(x, "vibrato"); quads[at].Articulation = (int)Json.Num(x, "articulation");
                        quads[at].SkipChance = (float)Json.Num(x, "skip"); quads[at].RepeatEvery = (int)Json.Num(x, "every"); quads[at].RepeatOffset = (int)Json.Num(x, "offset");
                    }
                MusicSynthSettings synth = null;
                string preset = Json.Str(p, "synthPreset");
                if (p.TryGetValue("synth", out var synthRaw) && synthRaw != null)
                {
                    var fields = Json.Obj(synthRaw, name + ".synth");
                    synth = (MusicSynthSettings)Json.Fill(new MusicSynthSettings(), fields, name + ".synth");
                    if (!synth.Enabled) synth = null;
                }
                if (!string.IsNullOrEmpty(preset))
                {
                    if (synth != null) throw new FormatException(name + ": use either \"synth\" or \"synthPreset\", not both.");
                    synth = MusicSynthPresets.Create(preset);
                }
                var plugin = p.TryGetValue("plugin", out var pluginRaw) && pluginRaw != null ? Json.Obj(pluginRaw, name + ".plugin") : null;
                if (plugin != null)
                {
                    targets.Add(new KeyValuePair<string, Dictionary<string, object>>(name, plugin));
                    // The engine's own voice stands in only if the plugin fails (the report lists that); without one the Init synth does.
                    if (synth == null && string.IsNullOrEmpty(Json.Str(p, "sample")) && !p.ContainsKey("zones")) synth = MusicSynthPresets.Create("Init");
                }
                string wavetable = Json.Str(p, "wavetable"); // custom table for the Wavetable waveform (frames of 2048 samples)
                if (!string.IsNullOrEmpty(wavetable) && synth != null) synth.WavetableClip = Clip(wavetable, name + " wavetable");
                string sampleName = Json.Str(p, "sample");
                var zones = p.TryGetValue("zones", out var zl) && zl != null
                    ? Json.List(zl, name + ".zones").Select((z, i) =>
                    {
                        var zone = Json.Obj(z, name + ".zones[" + i + "]");
                        return new MusicCueDefinition.SampleZone
                        {
                            Sample = Clip(Json.Str(zone, "sample") ?? throw new FormatException(name + ": zone " + (i + 1) + " missing sample"), name),
                            RootKey = (int)Json.Num(zone, "root", 60), LowKey = (int)Json.Num(zone, "low"),
                            HighKey = Json.Num(zone, "high") > 0 ? (int)Json.Num(zone, "high") : 127,
                            MinVelocity = (float)Json.Num(zone, "minVelocity"), GainDb = (float)Json.Num(zone, "gainDb"),
                            LoopStartSeconds = (float)Json.Num(zone, "loopStart"), LoopEndSeconds = (float)Json.Num(zone, "loopEnd"),
                            Articulation = (int)Json.Num(zone, "articulation"), StartSeconds = (float)Json.Num(zone, "start"),
                        };
                    }).ToArray()
                    : Array.Empty<MusicCueDefinition.SampleZone>();
                var clip = (synth != null || zones.Length > 0) && string.IsNullOrEmpty(sampleName) ? null : Clip(sampleName ?? throw new FormatException(name + ": missing sample"), name);
                var variants = p.TryGetValue("variants", out var vl) && vl != null
                    ? Json.List(vl, name + ".variants").Select((v, i) =>
                    {
                        var variant = Json.Obj(v, name + ".variants[" + i + "]");
                        return new MusicCueDefinition.SampleVariant
                        {
                            Sample = Clip(Json.Str(variant, "sample") ?? throw new FormatException(name + ": variant " + (i + 1) + " missing sample"), name),
                            MinVelocity = (float)Json.Num(variant, "minVelocity"), GainDb = (float)Json.Num(variant, "gainDb"),
                        };
                    }).ToArray()
                    : Array.Empty<MusicCueDefinition.SampleVariant>();
                var settings=p.TryGetValue("effects",out var settingsRaw)&&settingsRaw!=null?(MusicMixerSettings)Json.Fill(new MusicMixerSettings(),Json.Obj(settingsRaw,name+".effects"),name+".effects"):new MusicMixerSettings();
                string impulse=Json.Str(p,"impulse");if(!string.IsNullOrEmpty(impulse))settings.CabinetImpulse=Clip(impulse,name+" cabinet IR");
                return new MusicCueDefinition.Part
                {
                    Name = name, InstrumentId = name,
                    Layer = (MusicLayer)Enum.Parse(typeof(MusicLayer), Json.Str(p, "layer", "Melody"), true),
                    Gain = (float)Json.Num(p, "gain"), Pan = (float)Json.Num(p, "pan"), Sample = clip, SampleRootKey = (int)Json.Num(p, "root"),
                    AttackSeconds = Json.Num(p, "attack") > 0 ? (float)Json.Num(p, "attack") : .008f, ReleaseSeconds = Json.Num(p, "release") > 0 ? (float)Json.Num(p, "release") : .12f, Notes = quads,
                    Synth = synth ?? new MusicSynthSettings(),
                    PlaybackVersion = (int)Json.Num(p, "playbackVersion"), GlideSeconds = (float)Json.Num(p, "glide"),
                    DriveDb = (float)Json.Num(p, "drive"), DriveMode = p.TryGetValue("driveMode", out var mode) && mode is string modeName
                        ? (MusicDriveMode)Enum.Parse(typeof(MusicDriveMode), modeName, true) : (MusicDriveMode)(int)Json.Num(p, "driveMode"),
                    TrackEffects = Json.Bool(p, "trackEffects"),
                    Effects = settings,
                    SliceSeconds = p.TryGetValue("slices", out var sl) && sl != null ? Json.List(sl, name + ".slices").Select(x => Convert.ToSingle(x, CultureInfo.InvariantCulture)).ToArray() : Array.Empty<float>(),
                    Variants = variants,
                    Placements = p.TryGetValue("placements", out var pl) && pl != null ? Json.List(pl, name + ".placements").Select((x, i) =>
                    {
                        var o = Json.Obj(x, name + ".placements[" + i + "]");
                        return new MusicPatternPlacement(Json.Str(o, "pattern"), Json.Num(o, "start"), (int)Json.Num(o, "transpose"), Math.Max(1, (int)Json.Num(o, "repeats", 1)));
                    }).ToArray() : Array.Empty<MusicPatternPlacement>(),
                    Zones = zones,
                    LoopStartSeconds = (float)Json.Num(p, "loopStart"), LoopEndSeconds = (float)Json.Num(p, "loopEnd"), LoopCrossfadeSeconds = (float)Json.Num(p, "loopCrossfade", .01),
                    StretchBpm = (float)Json.Num(p, "stretchBpm"), StretchGrainMs = Json.Num(p, "stretchGrainMs") > 0 ? (float)Json.Num(p, "stretchGrainMs") : 60,
                    FallbackVoice = p.TryGetValue("fallbackVoice", out var voice) && voice is string voiceName ? (MusicVoice)Enum.Parse(typeof(MusicVoice), voiceName, true) : MusicVoice.Bell,
                    Modulators = Modulators(p, name),
                    Automation = Lanes(p, "automation", name),
                    Output = Json.Str(p, "output") ?? "", IgnoreSwing = Json.Bool(p, "ignoreSwing"),
                    Articulations = p.TryGetValue("articulations", out var artRaw) && artRaw != null
                        ? Json.List(artRaw, name + ".articulations").Select((x, k) => (MusicArticulation)Json.Fill(new MusicArticulation(), Json.Obj(x, name + ".articulations[" + k + "]"), name + ".articulations[" + k + "]")).ToArray()
                        : Array.Empty<MusicArticulation>(),
                    KeySwitches = p.TryGetValue("keySwitches", out var ksRaw) && ksRaw != null
                        ? Json.List(ksRaw, name + ".keySwitches").Select((x, k) => (MusicKeySwitch)Json.Fill(new MusicKeySwitch(), Json.Obj(x, name + ".keySwitches[" + k + "]"), name + ".keySwitches[" + k + "]")).ToArray()
                        : Array.Empty<MusicKeySwitch>(),
                    VibratoRateHz = Json.Num(p, "vibratoRate") > 0 ? (float)Json.Num(p, "vibratoRate") : (float)MusicPart.DefaultVibratoRateHz,
                    Regions = p.TryGetValue("regions", out var regionsRaw) && regionsRaw != null
                        ? Json.List(regionsRaw, name + ".regions").Select((x, k) =>
                        {
                            var o = Json.Obj(x, name + ".regions[" + k + "]"); string sample = Json.Str(o, "sample"); string warp = Json.Str(o, "warp");
                            var marks = o.TryGetValue("markers", out var mr) && mr != null ? Json.List(mr, name + " markers").Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray() : new double[0];
                            return new MusicAudioRegion
                            {
                                Sample = string.IsNullOrEmpty(sample) ? null : Clip(sample, name), StartBeat = Json.Num(o, "start"), LengthBeats = Json.Num(o, "length"),
                                SourceStartSeconds = (float)Json.Num(o, "sourceStart"), GainDb = (float)Json.Num(o, "gainDb"), FadeInBeats = (float)Json.Num(o, "fadeIn"), FadeOutBeats = (float)Json.Num(o, "fadeOut"),
                                Semitones = (float)Json.Num(o, "semitones"), SourceBpm = Json.Num(o, "sourceBpm") > 0 ? (float)Json.Num(o, "sourceBpm") : 120, Mute = Json.Bool(o, "mute"),
                                Warp = string.IsNullOrEmpty(warp) ? MusicRegionWarp.Off : (MusicRegionWarp)Enum.Parse(typeof(MusicRegionWarp), warp, true),
                                Markers = Enumerable.Range(0, marks.Length / 2).Select(i => new MusicWarpMarker(marks[i * 2], (float)marks[i * 2 + 1])).ToArray(),
                            };
                        }).ToArray()
                        : Array.Empty<MusicAudioRegion>(),
                    SampleShape = p.TryGetValue("sampleShape", out var shapeRaw) && shapeRaw != null
                        ? (MusicSampleShapeSettings)Json.Fill(new MusicSampleShapeSettings(), Json.Obj(shapeRaw, name + ".sampleShape"), name + ".sampleShape") : new MusicSampleShapeSettings(),
                    Sends = p.TryGetValue("sends", out var sendList) && sendList != null
                        ? Json.List(sendList, name + ".sends").Select((sendItem, i) =>
                        {
                            var send = Json.Obj(sendItem, name + ".sends[" + i + "]");
                            string bus = Json.Str(send, "bus") ?? "";
                            var lane = send.TryGetValue("automation", out var laneRaw) && laneRaw != null
                                ? Lanes(new Dictionary<string, object> { ["automation"] = new List<object> { laneRaw } }, "automation", name + " send " + bus)[0] : null;
                            return new MusicSendDefinition(bus, (float)Json.Num(send, "db"), lane, Json.Bool(send, "preFader"));
                        }).ToArray()
                        : Array.Empty<MusicSendDefinition>(),
                };
            }).ToArray();
            plugins = targets;
            return song;
        }
    }
}
