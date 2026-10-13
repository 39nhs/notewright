using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Graze.Presentation.Audio;

namespace GrazePlugin
{
    public static partial class Program
    {
        /// <summary>
        /// renderer tempo --audio FILE.wav [--min 60] [--max 200] [--beats-per-bar 4]: tempo, beats and bar start of a recording
        /// (MusicBeatDetector), plus a tempo map for a drifting take.
        /// </summary>
        static Dictionary<string, object> Tempo(Dictionary<string, string> o)
        {
            if (!o.TryGetValue("audio", out var path)) throw new ArgumentException("--audio FILE.wav is required");
            var audio = WavIO.Read(Path.GetFullPath(path));
            int perBar = (int)D(o, "beats-per-bar", 4);
            var analysis = MusicBeatDetector.Detect(MusicBeatDetector.Mono(audio.Interleaved, audio.Channels), audio.Rate, D(o, "min", 60), D(o, "max", 200), perBar);
            var changes = analysis.BeatSeconds.Length > perBar + 1 ? analysis.ToTempoChanges(out double first) : Array.Empty<MusicTempoChange>();
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["file"] = Path.GetFullPath(path), ["seconds"] = Math.Round(audio.Frames / (double)audio.Rate, 3),
                ["bpm"] = Math.Round(analysis.Bpm, 2), ["confidence"] = Math.Round(analysis.Confidence, 2), ["drift"] = Math.Round(analysis.Drift, 3),
                ["beatsPerBar"] = perBar, ["downbeatSeconds"] = Math.Round(analysis.DownbeatSeconds, 4), ["beats"] = analysis.BeatSeconds.Length,
                ["beatSeconds"] = analysis.BeatSeconds.Select(x => (object)Math.Round(x, 4)).ToList(),
                ["tempoChanges"] = changes.Select(c => (object)new Dictionary<string, object> { ["beat"] = c.Beat, ["bpm"] = Math.Round(c.Bpm, 2) }).ToList(),
                ["trace"] = analysis.Trace,
            };
        }
    }
}
