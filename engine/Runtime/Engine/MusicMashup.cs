using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>One song in a mashup: which excerpt plays where, transposed and scaled, with which tracks.</summary>
    [Serializable]
    public sealed class MusicMashupLayer
    {
        public MusicCueDefinition Song;
        [Tooltip("Mashup beat where this layer starts (first beat = 0).")]
        public double AtBeat;
        [Tooltip("First beat of the song used.")]
        public double FromBeat;
        [Tooltip("Beats of the song used from FromBeat; 0 = to the end of the song's loop.")]
        public double LengthBeats;
        [Tooltip("Plays the excerpt this many times back to back.")]
        [Min(1)] public int Repeat = 1;
        [Tooltip("Semitones for every non-drum note. MusicMashup.SuggestTranspose matches this song's key to the base song's.")]
        [Range(-24, 24)] public int Transpose;
        [Tooltip("Multiplies every track gain of this song.")]
        [Range(0, 1)] public float Gain = 1;
        [Tooltip("Track names (Part.Name) of this song to leave out.")]
        public string[] MutedParts = Array.Empty<string>();
        [Tooltip("Also take this song's noise and glitch regions inside the excerpt. Glitch filters process the whole mashup mix.")]
        public bool IncludeNoiseRegions = true;
    }

    /// <summary>
    /// Mashup: several MusicCueDefinitions combined into one new song that the engine plays as usual. The first layer is the
    /// base: its tempo map, bars, master mixer, playback version and reference melody define the mashup; the other songs play
    /// at the base tempo (notes are in beats, so they stay on the beat) and keep their own sound — each track keeps its
    /// instrument, mixer settings, track effects and its song's playback version (pinned when it differs from the base).
    /// Call on the main thread (creates a ScriptableObject). Source songs are never modified.
    /// </summary>
    public static class MusicMashup
    {
        public sealed class Result
        {
            public MusicCueDefinition Song;
            public readonly List<string> Warnings = new List<string>();
        }

        public const string GeneratedTag = "[generated:MusicMashup]";

        public static Result Combine(IList<MusicMashupLayer> layers, string name = "Mashup", double loopBeats = 0)
        {
            if (layers == null || layers.Count == 0 || layers[0] == null || layers[0].Song == null)
                throw new ArgumentException("A mashup needs a base song (layer 1).");
            var result = new Result();
            var baseSong = layers[0].Song;
            int version = MusicPlaybackVersion.Require(baseSong.PlaybackVersion, baseSong.name);
            var spans = new (double from, double length)[layers.Count];
            double end = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                if (layer == null || layer.Song == null) throw new ArgumentException($"Mashup layer {i + 1} has no song.");
                var song = layer.Song;
                if (!MusicTrack.Finite(layer.AtBeat) || layer.AtBeat < 0 || !MusicTrack.Finite(layer.FromBeat) || layer.FromBeat < 0 ||
                    layer.FromBeat >= song.LoopBeats || !MusicTrack.Finite(layer.LengthBeats) || layer.LengthBeats < 0 || layer.Repeat < 1 ||
                    layer.Transpose < -24 || layer.Transpose > 24 || !MusicTrack.Finite(layer.Gain) || layer.Gain < 0 || layer.Gain > 1)
                    throw new ArgumentException($"Mashup layer {i + 1} ({song.name}): invalid range, repeat, transpose or gain.");
                MusicPlaybackVersion.Require(song.PlaybackVersion, song.name);
                double length = Math.Min(song.LoopBeats - layer.FromBeat, layer.LengthBeats > 0 ? layer.LengthBeats : double.MaxValue);
                spans[i] = (layer.FromBeat, length);
                end = Math.Max(end, layer.AtBeat + length * layer.Repeat);
                if (i > 0 && Math.Abs(SourceBpm(song, layer.FromBeat) - SourceBpm(baseSong, Math.Max(0, layer.AtBeat - layers[0].AtBeat + layers[0].FromBeat))) > .01)
                    result.Warnings.Add($"{song.name}: {SourceBpm(song, layer.FromBeat):0.##} BPM plays at the base tempo ({baseSong.name}).");
                if (i > 0 && song.TempoChanges != null && song.TempoChanges.Length > 0)
                    result.Warnings.Add($"{song.name}: its tempo changes are not used; the base song's tempo map applies.");
                if (song.MasterAutomation != null && Array.Exists(song.MasterAutomation, lane => lane != null && !lane.Bypass))
                    result.Warnings.Add($"{song.name}: its master automation is not used in the mashup (track automation moves with the tracks).");
                if (song.MeterChanges != null && song.MeterChanges.Length > 0)
                    result.Warnings.Add($"{song.name}: its meter changes are not used; the mashup keeps the base song's Beats Per Bar.");
                if (i > 0 && song.BeatsPerBar != baseSong.BeatsPerBar)
                    result.Warnings.Add($"{song.name}: {song.BeatsPerBar}/4 bars inside the base song's {baseSong.BeatsPerBar}/4 bars.");
            }
            int bar = Math.Max(1, baseSong.BeatsPerBar);
            double loop = loopBeats > 0 ? loopBeats : Math.Ceiling(end / bar - 1e-9) * bar;
            if (!MusicTrack.Finite(loop) || loop <= 0 || loop > MusicCue.MaxLoopBeats) throw new ArgumentException("Invalid mashup length.");

            var mashup = ScriptableObject.CreateInstance<MusicCueDefinition>();
            mashup.name = name;
            mashup.PlaybackVersion = version;
            mashup.BeatsPerBar = baseSong.BeatsPerBar;
            mashup.LoopBeats = loop;
            mashup.Mixer = Copy(baseSong.Mixer ?? new MusicMixerSettings());
            // Mix buses come from the base song; other songs' tracks keep routing only to buses of the same name.
            mashup.Buses = Array.ConvertAll(baseSong.Buses ?? Array.Empty<MusicBusDefinition>(), b => new MusicBusDefinition { Name = b.Name, Output = b.Output, Effects = Copy(b.Effects ?? new MusicMixerSettings()),
                Automation = MoveAutomation(b.Automation, spans[0].from, spans[0].length, layers[0].AtBeat, layers[0].Repeat, loop) });
            var busNames = new HashSet<string>(Array.ConvertAll(mashup.Buses, b => b.Name ?? ""));
            BuildTempo(mashup, baseSong, layers[0], spans[0], loop);

            var parts = new List<MusicCueDefinition.Part>();
            var regions = new List<MusicNoiseRegion>();
            var names = new HashSet<string>();
            var recipe = new List<string>();
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                var song = layer.Song;
                var (from, length) = spans[i];
                recipe.Add($"{i + 1}. {song.name}: beats {from:0.###}–{from + length:0.###} at beat {layer.AtBeat:0.###}" +
                    (layer.Repeat > 1 ? $" x{layer.Repeat}" : "") + (layer.Transpose != 0 ? $", transpose {layer.Transpose:+0;-0}" : "") +
                    (layer.Gain != 1 ? $", gain {layer.Gain:0.##}" : ""));
                int dropped = 0;
                for (int p = 0; p < song.Parts.Length; p++)
                {
                    var source = song.Parts[p];
                    if (source == null || Array.IndexOf(layer.MutedParts ?? Array.Empty<string>(), source.Name) >= 0) continue;
                    var notes = new List<MusicNote>();
                    bool drums = source.Layer == MusicLayer.Drums;
                    for (int r = 0; r < layer.Repeat; r++)
                        foreach (var note in source.Notes)
                        {
                            if (note.Beat < from - 1e-9 || note.Beat >= from + length - 1e-9) continue;
                            double beat = layer.AtBeat + r * length + (note.Beat - from);
                            if (beat >= loop - 1e-9) continue;
                            int key = drums ? note.Key : note.Key + layer.Transpose;
                            if (key < 0 || key > 127) { dropped++; continue; }
                            notes.Add(new MusicNote(beat, Math.Min(note.Duration, from + length - note.Beat), key, note.Velocity));
                        }
                    if (notes.Count == 0) continue;
                    notes.Sort((a, b) => a.Beat.CompareTo(b.Beat));
                    var part = Copy(source);
                    part.Notes = notes.ToArray();
                    part.Gain = source.Gain * layer.Gain;
                    part.Effects = Copy(source.Effects ?? new MusicMixerSettings());
                    part.Automation = MoveAutomation(source.Automation, from, length, layer.AtBeat, layer.Repeat, loop);
                    part.Sends = Array.FindAll(source.Sends ?? Array.Empty<MusicSendDefinition>(), s => busNames.Contains(s.Bus ?? ""));
                    for (int send = 0; send < part.Sends.Length; send++)
                    {
                        var value = part.Sends[send];
                        value.LevelAutomation = MoveAutomation(value.LevelAutomation, from, length, layer.AtBeat, layer.Repeat, loop);
                        part.Sends[send] = value;
                    }
                    if (!string.IsNullOrEmpty(source.Output) && !busNames.Contains(source.Output)) part.Output = "";
                    if (part.Output != (source.Output ?? "") || part.Sends.Length != (source.Sends?.Length ?? 0))
                        result.Warnings.Add($"{song.name}: {source.Name} routes to buses the base song does not have; those routes go to the master.");
                    int partVersion = song.PlaybackVersionOfPart(p);
                    part.PlaybackVersion = partVersion == version ? 0 : partVersion;
                    part.Solo = false; // a source song's audition solo would silence the other songs
                    part.Name = Unique(names, source.Name, song.name);
                    if (string.IsNullOrWhiteSpace(part.InstrumentId)) part.InstrumentId = source.Name;
                    parts.Add(part);
                }
                if (dropped > 0) result.Warnings.Add($"{song.name}: {dropped} notes left out (transposed outside MIDI keys 0–127).");
                if (layer.IncludeNoiseRegions)
                    foreach (var source in song.NoiseRegions)
                    {
                        if (source == null || !source.Enabled) continue;
                        for (int r = 0; r < layer.Repeat; r++)
                        {
                            if (source.StartBeat < from - 1e-9 || source.StartBeat >= from + length - 1e-9) continue;
                            var region = Copy(source);
                            region.StartBeat = layer.AtBeat + r * length + (source.StartBeat - from);
                            region.DurationBeats = Math.Min(source.DurationBeats, Math.Min(from + length - source.StartBeat, loop - region.StartBeat));
                            if (region.StartBeat >= loop - 1e-9 || region.DurationBeats <= 0) continue;
                            if (region.FadeInBeats + region.FadeOutBeats > region.DurationBeats)
                            {
                                double scale = region.DurationBeats / (region.FadeInBeats + region.FadeOutBeats);
                                region.FadeInBeats *= scale; region.FadeOutBeats *= scale;
                            }
                            regions.Add(region);
                        }
                    }
            }
            mashup.Parts = parts.ToArray();
            mashup.NoiseRegions = regions.ToArray();
            // A sidechain key must name exactly one mashup track; otherwise the compressor goes back to its own input.
            void CheckSidechain(MusicMixerSettings chain, string owner)
            {
                if (chain == null || string.IsNullOrEmpty(chain.SidechainTrack)) return;
                int matches = 0;
                foreach (var p in parts) if ((string.IsNullOrWhiteSpace(p.InstrumentId) ? p.Name : p.InstrumentId) == chain.SidechainTrack) matches++;
                if (matches == 1) return;
                result.Warnings.Add($"{owner}: sidechain key {chain.SidechainTrack} is {(matches == 0 ? "not in" : "ambiguous in")} the mashup; the compressor uses its own input.");
                chain.SidechainTrack = "";
            }
            CheckSidechain(mashup.Mixer, "Master");
            foreach (var bus in mashup.Buses) CheckSidechain(bus.Effects, "Bus " + bus.Name);
            foreach (var p in parts) CheckSidechain(p.Effects, p.Name);
            int additive = 0;
            foreach (var region in regions) if (!region.MasterFilter) additive++;
            if (parts.Count + additive > MusicCue.MaxParts)
                throw new InvalidOperationException($"The mashup needs {parts.Count} tracks + {additive} noise regions; the engine plays at most {MusicCue.MaxParts}. Leave out tracks (MutedParts) or noise regions.");
            if (parts.Count == 0) throw new InvalidOperationException("The mashup has no notes: check each layer's range and muted tracks.");
            // Reference/ghost melody stays silent authoring data: only the base song's, moved with its layer.
            var reference = new List<MusicNote>();
            foreach (var note in baseSong.ReferenceMelody ?? Array.Empty<MusicNote>())
                if (note.Beat >= spans[0].from - 1e-9 && note.Beat < spans[0].from + spans[0].length - 1e-9 && layers[0].AtBeat + note.Beat - spans[0].from < loop)
                    reference.Add(new MusicNote(layers[0].AtBeat + note.Beat - spans[0].from, note.Duration, note.Key, note.Velocity));
            mashup.ReferenceMelody = reference.ToArray();
            mashup.ReferenceSource = baseSong.ReferenceSource;
            mashup.Source = "Mashup of " + string.Join(" + ", Names(layers));
            mashup.ImportNotes = GeneratedTag + "\n" + string.Join("\n", recipe) + (result.Warnings.Count > 0 ? "\n" + string.Join("\n", result.Warnings) : "");
            result.Song = mashup;
            return result;
        }

        /// <summary>
        /// A track's automation over the excerpt [from, from + length), moved to the layer position and repeated with it. Each
        /// repeat starts and ends with the source value at that excerpt edge, so the track sounds as it did in its song.
        /// </summary>
        static MusicAutomationLane[] MoveAutomation(MusicAutomationLane[] lanes, double from, double length, double at, int repeat, double loop)
        {
            if (lanes == null || lanes.Length == 0) return Array.Empty<MusicAutomationLane>();
            var moved = new List<MusicAutomationLane>();
            foreach (var lane in lanes)
            {
                if (lane == null || lane.Points == null || lane.Points.Length == 0) continue;
                var source = new MusicAutomation(lane.Target, lane.Points, double.MaxValue);
                var sorted = (MusicAutomationPoint[])lane.Points.Clone();
                Array.Sort(sorted, (a, b) => a.Beat.CompareTo(b.Beat));
                MusicAutomationCurve CurveAt(double beat)
                {
                    var curve = MusicAutomationCurve.Linear;
                    foreach (var p in sorted) if (p.Beat <= beat + 1e-9) curve = p.Curve; else break;
                    return curve;
                }
                var points = new List<MusicAutomationPoint>();
                for (int r = 0; r < repeat; r++)
                {
                    double start = at + r * length;
                    if (start >= loop - 1e-9) break;
                    double end = Math.Min(start + length, loop);
                    points.Add(new MusicAutomationPoint(start, (float)source.Evaluate(from), CurveAt(from)));
                    foreach (var p in sorted)
                        if (p.Beat > from + 1e-9 && p.Beat < from + (end - start) - 1e-9) points.Add(new MusicAutomationPoint(start + p.Beat - from, p.Value, p.Curve));
                    points.Add(new MusicAutomationPoint(end, (float)source.Evaluate(from + (end - start)), MusicAutomationCurve.Step));
                }
                if (points.Count > 0) moved.Add(new MusicAutomationLane(lane.Target, points.ToArray()) { Bypass = lane.Bypass });
            }
            return moved.ToArray();
        }

        static IEnumerable<string> Names(IList<MusicMashupLayer> layers) { foreach (var l in layers) yield return l.Song.name; }

        static string Unique(HashSet<string> used, string name, string song)
        {
            string candidate = string.IsNullOrWhiteSpace(name) ? "Track" : name;
            if (used.Add(candidate)) return candidate;
            candidate = $"{candidate} ({song})";
            for (int n = 2; !used.Add(candidate); n++) candidate = $"{name} ({song} {n})";
            return candidate;
        }

        static double SourceBpm(MusicCueDefinition song, double beat) =>
            new MusicTempoMap(song.Bpm, song.LoopBeats, song.TempoChanges).At(Math.Min(beat, song.LoopBeats - 1e-6), false);

        /// <summary>The base excerpt's tempo map, moved to its layer position and repeated with it.</summary>
        static void BuildTempo(MusicCueDefinition mashup, MusicCueDefinition song, MusicMashupLayer layer, (double from, double length) span, double loop)
        {
            double startBpm = SourceBpm(song, span.from);
            mashup.Bpm = startBpm;
            var changes = new List<MusicTempoChange>();
            var source = song.TempoChanges ?? Array.Empty<MusicTempoChange>();
            if (source.Length == 0) { mashup.TempoChanges = Array.Empty<MusicTempoChange>(); return; }
            var sorted = (MusicTempoChange[])source.Clone();
            Array.Sort(sorted, (a, b) => a.Beat.CompareTo(b.Beat));
            double endBpm = SourceBpm(song, Math.Min(song.LoopBeats - 1e-6, span.from + span.length - 1e-6));
            for (int r = 0; r < layer.Repeat; r++)
            {
                double origin = layer.AtBeat + r * span.length;
                if (r > 0 && Math.Abs(endBpm - startBpm) > 1e-9 && origin < loop) changes.Add(new MusicTempoChange { Beat = origin, Bpm = startBpm });
                foreach (var change in sorted)
                {
                    if (change.Beat <= span.from + 1e-9 || change.Beat >= span.from + span.length - 1e-9) continue;
                    double at = origin + change.Beat - span.from;
                    double ramp = Math.Min(change.RampBeats, Math.Min(span.from + span.length - change.Beat, loop - at));
                    if (at < loop - 1e-9) changes.Add(new MusicTempoChange { Beat = at, Bpm = change.Bpm, RampBeats = Math.Max(0, ramp) });
                }
            }
            mashup.TempoChanges = changes.ToArray();
        }

        /// <summary>Estimated key of a song excerpt (Krumhansl–Kessler profiles over duration-weighted pitch classes, drums excluded).</summary>
        public static (int tonic, bool minor, double confidence) EstimateKey(MusicCueDefinition song, double fromBeat = 0, double lengthBeats = 0)
        {
            if (song == null) throw new ArgumentNullException(nameof(song));
            double to = lengthBeats > 0 ? fromBeat + lengthBeats : double.MaxValue;
            var weight = new double[12];
            foreach (var part in song.Parts)
            {
                if (part == null || part.Layer == MusicLayer.Drums || part.Mute) continue;
                foreach (var note in part.Notes)
                    if (note.Beat >= fromBeat && note.Beat < to) weight[note.Key % 12] += Math.Min(note.Duration, 4) * Math.Max(.1, note.Velocity);
            }
            double[] major = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
            double[] minor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
            int bestTonic = 0; bool bestMinor = false; double best = double.NegativeInfinity, second = double.NegativeInfinity;
            for (int tonic = 0; tonic < 12; tonic++)
                for (int mode = 0; mode < 2; mode++)
                {
                    double r = Correlation(weight, mode == 1 ? minor : major, tonic);
                    if (r > best) { second = best; best = r; bestTonic = tonic; bestMinor = mode == 1; }
                    else if (r > second) second = r;
                }
            return (bestTonic, bestMinor, double.IsInfinity(second) || double.IsNaN(best) ? 0 : Math.Max(0, best - second));
        }

        static double Correlation(double[] weight, double[] profile, int tonic)
        {
            double mw = 0, mp = 0;
            for (int i = 0; i < 12; i++) { mw += weight[i]; mp += profile[i]; }
            mw /= 12; mp /= 12;
            double num = 0, dw = 0, dp = 0;
            for (int i = 0; i < 12; i++)
            {
                double w = weight[(i + tonic) % 12] - mw, p = profile[i] - mp;
                num += w * p; dw += w * w; dp += p * p;
            }
            return dw <= 0 ? double.NaN : num / Math.Sqrt(dw * dp);
        }

        /// <summary>Smallest transpose (-6..+5 semitones) that puts the layer's key on the base key; relative major/minor count as the same key.</summary>
        public static int SuggestTranspose((int tonic, bool minor) baseKey, (int tonic, bool minor) layerKey)
        {
            int Major((int tonic, bool minor) k) => k.minor ? (k.tonic + 3) % 12 : k.tonic;
            int shift = ((Major(baseKey) - Major(layerKey)) % 12 + 12) % 12;
            return shift > 5 ? shift - 12 : shift;
        }

        public static string KeyName((int tonic, bool minor) key) =>
            new[] { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" }[((key.tonic % 12) + 12) % 12] + (key.minor ? " minor" : " major");

        // Field-wise copies of the serializable authoring classes (all public fields; arrays and asset references are shared and
        // replaced where the mashup changes them).
        static T Copy<T>(T source) where T : class, new()
        {
            var copy = new T();
            foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance)) field.SetValue(copy, field.GetValue(source));
            return copy;
        }
    }
}
