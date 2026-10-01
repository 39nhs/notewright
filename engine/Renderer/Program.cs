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
    /// Headless renderer: song.json (the format Unity's SongJsonBuilder reads) → WAV with the same AdaptiveMusicEngine.
    /// Prints a JSON report on stdout. Usage:
    ///   renderer render --song song.json --out mix.wav [--samples DIR] [--rate 48000] [--bits 16|24|32]
    ///                   [--start BEAT] [--length BEATS] [--tail SECONDS] [--normalize DBFS] [--stems DIR] [--report FILE]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || args[0] != "render") throw new ArgumentException("usage: renderer render --song song.json --out mix.wav [options]");
                var o = Options(args);
                var report = Render(o);
                string json = Json.Write(report);
                if (o.TryGetValue("report", out var path)) File.WriteAllText(path, json);
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception e)
            {
                var error = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
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
            if (!o.ContainsKey("song") || !o.ContainsKey("out")) throw new ArgumentException("--song and --out are required");
            return o;
        }

        static double D(Dictionary<string, string> o, string key, double fallback) =>
            o.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

        static Dictionary<string, object> Render(Dictionary<string, string> o)
        {
            string songPath = Path.GetFullPath(o["song"]);
            string samples = o.TryGetValue("samples", out var s) ? s : Path.Combine(Path.GetDirectoryName(songPath), "Samples");
            int rate = (int)D(o, "rate", 48000), bits = (int)D(o, "bits", 16);
            var song = SongLoader.Load(File.ReadAllText(songPath), samples, out var groups);
            var cue = song.Compile();
            double start = D(o, "start", 0), length = D(o, "length", song.LoopBeats - start), tail = D(o, "tail", 0);
            if (start < 0 || start >= song.LoopBeats || length <= 0) throw new ArgumentException("--start/--length outside the song");
            double end = Math.Min(song.LoopBeats, start + length);
            double startSeconds = cue.Tempo.SecondsAt(start), seconds = cue.Tempo.SecondsAt(end) - startSeconds + tail;
            long frames = Math.Max(1, (long)Math.Round(seconds * rate));

            string temp = Path.GetTempFileName();
            try
            {
                var mix = RenderToFloat(cue, rate, startSeconds, frames, temp);
                double gain = 1;
                if (o.TryGetValue("normalize", out var target) && mix.Peak > 0)
                    gain = Math.Pow(10, double.Parse(target, CultureInfo.InvariantCulture) / 20) / mix.Peak;
                CopyToWav(temp, o["out"], rate, bits, gain);
                var report = new Dictionary<string, object>
                {
                    ["ok"] = true, ["out"] = Path.GetFullPath(o["out"]), ["seconds"] = frames / (double)rate, ["rate"] = rate, ["bits"] = bits,
                    ["playbackVersion"] = cue.PlaybackVersion, ["voiceLimit"] = MusicPlaybackVersion.VoiceLimit(cue.PlaybackVersion),
                    ["peakVoices"] = mix.PeakVoices, ["droppedNotes"] = mix.Dropped,
                    ["peakDb"] = Db(mix.Peak), ["rmsDb"] = Db(mix.Rms), ["nearFullScale"] = mix.NearFullScale,
                    ["gainDb"] = Db(gain), ["outputPeakDb"] = Db(mix.Peak * gain),
                    ["perSecond"] = mix.PerSecond.Select(x => (object)new List<object> { Math.Round(Db(x.Rms), 1), Math.Round(Db(x.Peak), 1) }).ToList(),
                };
                if (o.TryGetValue("stems", out var stems))
                {
                    // One stem per "stem" group (a part's optional "stem" key, else its name): the group's parts are soloed together.
                    var parts = new List<object>();
                    foreach (var group in groups.Distinct())
                    {
                        int notes = 0;
                        for (int i = 0; i < song.Parts.Length; i++)
                        {
                            song.Parts[i].Solo = groups[i] == group;
                            if (song.Parts[i].Solo) notes += song.Parts[i].Notes.Length;
                        }
                        var stem = RenderToFloat(song.Compile(), rate, startSeconds, frames, temp);
                        string file = Path.Combine(stems, Safe(group) + ".wav");
                        CopyToWav(temp, file, rate, bits, gain);
                        parts.Add(new Dictionary<string, object> { ["name"] = group, ["file"] = Path.GetFullPath(file),
                            ["notes"] = notes, ["rmsDb"] = Db(stem.Rms), ["peakDb"] = Db(stem.Peak), ["peakVoices"] = stem.PeakVoices });
                    }
                    foreach (var p in song.Parts) p.Solo = false;
                    report["parts"] = parts;
                }
                return report;
            }
            finally { File.Delete(temp); }
        }

        static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        static double Db(double x) => x > 1e-9 ? Math.Round(20 * Math.Log10(x), 2) : -180;

        sealed class Stats { public double Peak, Rms; public long NearFullScale; public int Dropped, PeakVoices; public List<(double Rms, double Peak)> PerSecond = new List<(double, double)>(); }

        /// <summary>Renders one second at a time into a raw float32 stereo file (playOnce, like the Arranger's export).</summary>
        static Stats RenderToFloat(MusicCue cue, int rate, double startSeconds, long frames, string path)
        {
            var engine = new AdaptiveMusicEngine(cue, rate, playOnce: true);
            var block = new float[rate * 2];
            for (long skip = (long)Math.Round(startSeconds * rate); skip > 0; skip -= rate)
            {
                int n = (int)Math.Min(rate, skip);
                engine.RenderStereo(n == rate ? block : new float[n * 2]);
            }
            var stats = new Stats();
            double energy = 0;
            using (var w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)))
            {
                for (long done = 0; done < frames; done += rate)
                {
                    int n = (int)Math.Min(rate, frames - done);
                    var buffer = n == rate ? block : new float[n * 2];
                    engine.RenderStereo(buffer);
                    double secondEnergy = 0, secondPeak = 0;
                    for (int i = 0; i < n * 2; i++)
                    {
                        float v = buffer[i];
                        double a = Math.Abs(v);
                        if (a > secondPeak) secondPeak = a;
                        if (a >= .98) stats.NearFullScale++;
                        secondEnergy += v * (double)v;
                        w.Write(v);
                    }
                    energy += secondEnergy;
                    stats.Peak = Math.Max(stats.Peak, secondPeak);
                    stats.PerSecond.Add((Math.Sqrt(secondEnergy / (n * 2)), secondPeak));
                }
            }
            stats.Rms = Math.Sqrt(energy / (frames * 2));
            stats.Dropped = engine.DroppedNotes; stats.PeakVoices = engine.PeakVoices;
            return stats;
        }

        static void CopyToWav(string floatPath, string wavPath, int rate, int bits, double gain)
        {
            using (var r = new BinaryReader(File.OpenRead(floatPath)))
            using (var w = new WavIO.Writer(wavPath, rate, bits))
            {
                var buffer = new float[rate * 2];
                while (r.BaseStream.Position < r.BaseStream.Length)
                {
                    int n = 0;
                    while (n < buffer.Length && r.BaseStream.Position < r.BaseStream.Length) buffer[n++] = r.ReadSingle();
                    w.Write(buffer, n, gain);
                }
            }
        }
    }

    /// <summary>song.json → MusicCueDefinition, mirroring Editor/SongJsonBuilder.cs (missing numbers are 0, as with JsonUtility).</summary>
    public static class SongLoader
    {
        public static MusicCueDefinition Load(string json, string samplesFolder) => Load(json, samplesFolder, out _);

        /// <param name="stemGroups">Per part: its optional "stem" key (renderer only; Unity ignores it), else its name.</param>
        public static MusicCueDefinition Load(string json, string samplesFolder, out string[] stemGroups)
        {
            var root = Json.Obj(Json.Parse(json), "song");
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = Json.Str(root, "name", "Song");
            song.Bpm = Json.Num(root, "bpm");
            song.BeatsPerBar = (int)Json.Num(root, "beatsPerBar");
            song.LoopBeats = Json.Num(root, "loopBeats");
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
            stemGroups = Json.List(partList, "parts").Select((item, index) =>
            {
                var p = Json.Obj(item, "parts[" + index + "]");
                return Json.Str(p, "stem") ?? Json.Str(p, "name", "Part" + index);
            }).ToArray();
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
                string sample = Json.Str(p, "sample") ?? throw new FormatException(name + ": missing sample");
                string path = Path.IsPathRooted(sample) ? sample : Path.Combine(samplesFolder, sample);
                if (!clips.TryGetValue(path, out var clip))
                {
                    if (!File.Exists(path)) throw new FileNotFoundException(name + ": missing sample " + path);
                    var audio = WavIO.Read(path);
                    clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), audio.Frames, audio.Channels, audio.Rate, false);
                    clip.SetData(audio.Interleaved, 0);
                    clips[path] = clip;
                }
                return new MusicCueDefinition.Part
                {
                    Name = name, InstrumentId = name,
                    Layer = (MusicLayer)Enum.Parse(typeof(MusicLayer), Json.Str(p, "layer", "Melody"), true),
                    Gain = (float)Json.Num(p, "gain"), Pan = (float)Json.Num(p, "pan"), Sample = clip, SampleRootKey = (int)Json.Num(p, "root"),
                    AttackSeconds = (float)Json.Num(p, "attack"), ReleaseSeconds = (float)Json.Num(p, "release"), Notes = quads,
                    PlaybackVersion = (int)Json.Num(p, "playbackVersion"), GlideSeconds = (float)Json.Num(p, "glide"),
                    DriveDb = (float)Json.Num(p, "drive"), DriveMode = p.TryGetValue("driveMode", out var mode) && mode is string modeName
                        ? (MusicDriveMode)Enum.Parse(typeof(MusicDriveMode), modeName, true) : (MusicDriveMode)(int)Json.Num(p, "driveMode"),
                    TrackEffects = Json.Bool(p, "trackEffects"),
                    Effects = p.TryGetValue("effects", out var fx) && fx != null ? (MusicMixerSettings)Json.Fill(new MusicMixerSettings(), Json.Obj(fx, name + ".effects"), name + ".effects") : new MusicMixerSettings(),
                };
            }).ToArray();
            return song;
        }
    }
}
