using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// Standard MIDI File (type 1) writer for a song asset, the inverse of <see cref="MidiMusicReader"/>: a conductor track (name, tempo map with
    /// ramps as steps, time signatures, key signature for major/minor keys, section markers) and one track per part (name, program, notes,
    /// pitch bend with an RPN bend range, CC1 modulation and CC74 brightness from the V3 expression lanes). Parts get distinct channels
    /// (drum layer = channel 10) so a re-import splits them the same way.
    /// </summary>
    public static class MidiMusicWriter
    {
        public sealed class Options
        {
            public int Ppqn = 480;
            /// <summary>Write pitch bend / CC1 / CC74 from the expression lanes.</summary>
            public bool Expression = true;
            /// <summary>Tempo ramps and curved lanes are written as steps this far apart (beats).</summary>
            public double StepBeats = 1 / 32.0;
        }

        public sealed class Result
        {
            public byte[] Bytes;
            public readonly List<string> Warnings = new List<string>();
        }

        struct Event
        {
            public long Tick; public int Order; public byte[] Data;
            public Event(long tick, int order, params byte[] data) { Tick = tick; Order = order; Data = data; }
        }
        // At the same tick: meta, then controllers/programs, then note-offs, then note-ons (a repeated key ends before it restarts).
        const int Meta = 0, Control = 1, Off = 2, On = 3;

        public static Result Write(MusicCueDefinition song, Options options = null)
        {
            if (song == null) throw new ArgumentNullException(nameof(song));
            options = options ?? new Options();
            if (options.Ppqn < 24 || options.Ppqn > 32767 || !(options.StepBeats > 0)) throw new ArgumentException("PPQN 24..32767 and a positive step.");
            var result = new Result();
            long Tick(double beat) => (long)Math.Round(beat * options.Ppqn);
            var parts = song.Parts ?? Array.Empty<MusicCueDefinition.Part>();
            if (parts.Length > 64) throw new ArgumentException("At most 64 parts.");

            // ---------------- conductor ----------------
            var conductor = new List<Event> { new Event(0, Meta, MetaText(3, string.IsNullOrEmpty(song.name) ? "Song" : song.name)) };
            var map = new MusicTempoMap(song.Bpm, Math.Max(1e-9, song.LoopBeats), song.TempoChanges);
            void TempoAt(double beat, double bpm)
            {
                int micros = (int)Math.Round(60000000.0 / Math.Max(4, bpm)); micros = Math.Max(1, Math.Min(0xFFFFFF, micros));
                conductor.Add(new Event(Tick(beat), Meta, 0xFF, 0x51, 3, (byte)(micros >> 16), (byte)(micros >> 8), (byte)micros));
            }
            TempoAt(0, map.At(0, false));
            foreach (var t in (song.TempoChanges ?? Array.Empty<MusicTempoChange>()).OrderBy(t => t.Beat))
            {
                if (t.Beat <= 0 && t.RampBeats <= 0) continue;
                if (t.RampBeats > 0) for (double b = t.Beat; b < t.Beat + t.RampBeats - 1e-9; b += Math.Max(options.StepBeats, 1 / 16.0)) TempoAt(b, map.At(b + Math.Max(options.StepBeats, 1 / 16.0) / 2, false));
                TempoAt(t.Beat + Math.Max(0, t.RampBeats), t.Bpm);
            }
            var meters = (song.MeterChanges ?? Array.Empty<MusicMeterChange>()).OrderBy(m => m.Beat).ToList();
            if (meters.Count == 0 || meters[0].Beat > 1e-9) meters.Insert(0, new MusicMeterChange(0, Math.Max(1, song.BeatsPerBar), 4));
            foreach (var m in meters)
            {
                int power = (int)Math.Round(Math.Log(m.Denominator, 2));
                conductor.Add(new Event(Tick(m.Beat), Meta, 0xFF, 0x58, 4, (byte)m.Numerator, (byte)power, 24, 8));
            }
            if (MusicKey.TryParse(song.Key ?? "", out var key))
            {
                if (key.Mode == MusicMode.Major || key.Mode == MusicMode.Minor)
                {
                    int sf = KeySharps((key.Tonic + (key.Mode == MusicMode.Minor ? 3 : 0)) % 12);
                    conductor.Add(new Event(0, Meta, 0xFF, 0x59, 2, unchecked((byte)(sbyte)sf), (byte)(key.Mode == MusicMode.Minor ? 1 : 0)));
                }
                else result.Warnings.Add($"Key {key} has no MIDI key signature (major/minor only); not written.");
            }
            foreach (var s in song.Sections ?? Array.Empty<MusicSectionMarker>()) conductor.Add(new Event(Tick(s.Beat), Meta, MetaText(6, s.Name ?? "")));
            foreach (var k in song.KeyChanges ?? Array.Empty<MusicKeyChange>())
                if (MusicKey.TryParse(k.Key ?? "", out var changed) && (changed.Mode == MusicMode.Major || changed.Mode == MusicMode.Minor))
                    conductor.Add(new Event(Tick(k.Beat), Meta, 0xFF, 0x59, 2, unchecked((byte)(sbyte)KeySharps((changed.Tonic + (changed.Mode == MusicMode.Minor ? 3 : 0)) % 12)), (byte)(changed.Mode == MusicMode.Minor ? 1 : 0)));

            // ---------------- channels ----------------
            var channels = new int[parts.Length]; var programs = new int[parts.Length];
            var used = new HashSet<int>(); var usedPairs = new HashSet<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i]; if (p == null) continue;
                bool drums = p.Layer == MusicLayer.Drums;
                int program = Math.Max(0, Math.Min(127, p.MidiProgram));
                int channel = drums ? 9 : (p.MidiChannel >= 0 && p.MidiChannel <= 15 && p.MidiChannel != 9 && !used.Contains(p.MidiChannel) ? p.MidiChannel : -1);
                if (channel < 0) foreach (int c in Enumerable.Range(0, 16)) if (c != 9 && !used.Contains(c)) { channel = c; break; }
                if (channel < 0)
                {
                    channel = Enumerable.Range(0, 16).Where(c => c != 9).OrderBy(c => c).First(c => Enumerable.Range(0, 128).Any(pr => !usedPairs.Contains(c * 128 + pr)));
                    result.Warnings.Add($"{p.Name}: more than 15 melodic parts; shares channel {channel + 1} (expression lanes on a shared channel mix).");
                }
                while (usedPairs.Contains(channel * 128 + program)) program = (program + 1) % 128; // re-import splits parts by channel + program
                used.Add(channel); usedPairs.Add(channel * 128 + program);
                channels[i] = channel; programs[i] = program;
            }

            // ---------------- part tracks ----------------
            var tracks = new List<List<Event>> { conductor };
            long end = Tick(Math.Max(0, song.LoopBeats));
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i]; if (p == null) continue;
                int ch = channels[i];
                var events = new List<Event> { new Event(0, Meta, MetaText(3, p.Name ?? "Part " + (i + 1))), new Event(0, Control, (byte)(0xC0 | ch), (byte)programs[i]) };
                var performed = song.PerformedNotes(p);
                foreach (var n in performed) // the track's notes, pattern placements and swing
                {
                    if (n.Key < 0 || n.Key > 127 || n.Velocity <= 0) continue;
                    long on = Tick(n.Beat), off = Math.Max(on + 1, Tick(n.Beat + n.Duration));
                    byte velocity = (byte)Math.Max(1, Math.Min(127, (int)Math.Round(n.Velocity * 127)));
                    events.Add(new Event(on, On, (byte)(0x90 | ch), (byte)n.Key, velocity));
                    events.Add(new Event(off, Off, (byte)(0x80 | ch), (byte)n.Key, 0));
                }
                bool noteBends = options.Expression && performed.Any(n => n.HasExpression);
                if (options.Expression) WriteExpression(p, ch, events, Tick, options.StepBeats, song.LoopBeats, result, skipPitch: noteBends);
                if (noteBends) WriteNoteBends(song, p, performed, ch, events, Tick, result);
                tracks.Add(events);
                if (events.Count > 0) end = Math.Max(end, events.Max(e => e.Tick));
            }

            using (var stream = new MemoryStream())
            {
                Ascii(stream, "MThd"); Int32(stream, 6); Int16(stream, 1); Int16(stream, tracks.Count); Int16(stream, options.Ppqn);
                foreach (var track in tracks)
                {
                    var body = new MemoryStream(); long last = 0;
                    foreach (var e in track.Select((e, index) => (e, index)).OrderBy(x => x.e.Tick).ThenBy(x => x.e.Order).ThenBy(x => x.index).Select(x => x.e))
                    { Variable(body, e.Tick - last); body.Write(e.Data, 0, e.Data.Length); last = e.Tick; }
                    Variable(body, Math.Max(0, end - last)); body.Write(new byte[] { 0xFF, 0x2F, 0 }, 0, 3);
                    Ascii(stream, "MTrk"); Int32(stream, (int)body.Length); body.WriteTo(stream);
                }
                result.Bytes = stream.ToArray();
            }
            return result;
        }

        /// <summary>
        /// Per-note bend and vibrato (playback V3 note expression) as pitch bend on the track's channel, on top of its pitch lane; MIDI pitch
        /// bend is per channel, so overlapping notes with expression bend together (a warning says so). Bend range = the largest bend needed.
        /// </summary>
        static void WriteNoteBends(MusicCueDefinition song, MusicCueDefinition.Part p, MusicNote[] notes, int ch, List<Event> events, Func<double, long> tick, Result result)
        {
            const double Step = 1 / 64.0, VibratoFade = .15;
            var laneDef = (p.Automation ?? Array.Empty<MusicAutomationLane>()).FirstOrDefault(l => l != null && !l.Bypass && l.Target == MusicAutomationTarget.PitchSemitones && (l.Points?.Length ?? 0) > 0);
            var lane = laneDef == null ? null : new MusicAutomation(MusicAutomationTarget.PitchSemitones, laneDef.Points, song.LoopBeats);
            var tempo = new MusicTempoMap(song.Bpm, song.LoopBeats, song.TempoChanges);
            double rate = p.VibratoRateHz > 0 ? p.VibratoRateHz : MusicPart.DefaultVibratoRateHz;
            var expressive = notes.Where(n => n.HasExpression).OrderBy(n => n.Beat).ToArray();
            for (int i = 1; i < expressive.Length; i++)
                if (expressive[i].Beat < expressive[i - 1].Beat + expressive[i - 1].Duration - 1e-9) { result.Warnings.Add($"{p.Name}: overlapping notes with bends or vibrato bend the whole MIDI channel together."); break; }
            // The note in charge of the bend at a beat: the latest-started sounding note with expression.
            double Semitones(double beat)
            {
                double value = lane?.Evaluate(beat) ?? 0;
                for (int i = expressive.Length - 1; i >= 0; i--)
                {
                    var n = expressive[i]; if (n.Beat > beat + 1e-9 || beat >= n.Beat + n.Duration - 1e-9) continue;
                    double t = beat - n.Beat, s = n.BendFrom;
                    if (n.BendTo != n.BendFrom)
                    {
                        if (n.BendEnd > n.BendStart) { double x = Math.Max(0, Math.Min(1, (t - n.BendStart) / (n.BendEnd - n.BendStart))); s += (n.BendTo - n.BendFrom) * x * x * (3 - 2 * x); }
                        else if (t >= n.BendStart) s = n.BendTo;
                    }
                    if (n.Vibrato > 0)
                    {
                        double age = tempo.SecondsAt(Math.Min(song.LoopBeats, beat)) - tempo.SecondsAt(n.Beat);
                        s += n.Vibrato * Math.Sin(2 * Math.PI * rate * age) * Math.Min(1, age / VibratoFade);
                    }
                    return value + s;
                }
                return value;
            }
            var beats = new SortedSet<double> { 0 };
            if (laneDef != null) foreach (var pt in laneDef.Points) if (pt.Beat <= song.LoopBeats) beats.Add(pt.Beat);
            if (laneDef != null) for (double b = 0; b < song.LoopBeats; b += Step) beats.Add(b); // the lane's ramps between its points
            foreach (var n in expressive) { for (double b = n.Beat; b < n.Beat + n.Duration && b < song.LoopBeats; b += Step) beats.Add(b); beats.Add(Math.Min(song.LoopBeats, n.Beat + n.Duration)); }
            var values = beats.Select(b => (b, v: Semitones(b))).ToArray();
            double range = Math.Max(MusicMidiExpression.DefaultBendRange, Math.Min(24, Math.Ceiling(values.Max(x => Math.Abs(x.v)))));
            if (values.Any(x => Math.Abs(x.v) > 24)) result.Warnings.Add($"{p.Name}: bends beyond ±24 semitones are clipped.");
            foreach (var (cc, v) in new[] { (101, 0), (100, 0), (6, (int)range), (38, 0), (101, 127), (100, 127) })
                events.Add(new Event(0, Control, (byte)(0xB0 | ch), (byte)cc, (byte)v));
            int last = -1;
            foreach (var (b, v) in values)
            {
                int raw = Math.Max(0, Math.Min(16383, (int)Math.Round(8192 + v / range * 8192)));
                if (raw == last) continue; last = raw;
                events.Add(new Event(tick(b), Control, (byte)(0xE0 | ch), (byte)(raw & 127), (byte)(raw >> 7)));
            }
        }

        static void WriteExpression(MusicCueDefinition.Part p, int ch, List<Event> events, Func<double, long> tick, double step, double loopBeats, Result result, bool skipPitch = false)
        {
            foreach (var lane in p.Automation ?? Array.Empty<MusicAutomationLane>())
            {
                if (lane == null || lane.Bypass || lane.Points == null || lane.Points.Length == 0) continue;
                int target = Array.IndexOf(MusicMidiExpression.Targets, lane.Target);
                if (target < 0 || (target == 0 && skipPitch)) continue; // the pitch lane goes out with the note bends
                var points = lane.Points.OrderBy(x => x.Beat).ToArray();
                double range = 2;
                if (target == 0)
                {
                    range = Math.Max(MusicMidiExpression.DefaultBendRange, Math.Min(24, Math.Ceiling(points.Max(x => Math.Abs(x.Value)))));
                    foreach (var (cc, v) in new[] { (101, 0), (100, 0), (6, (int)range), (38, 0), (101, 127), (100, 127) })
                        events.Add(new Event(0, Control, (byte)(0xB0 | ch), (byte)cc, (byte)v));
                }
                int last = -1;
                void Emit(double beat, double value)
                {
                    int raw = target == 0 ? (int)Math.Round(8192 + value / range * 8192) : target == 1 ? (int)Math.Round(value * 127) : (int)Math.Round(64 + value / MusicMidiExpression.BrightnessOctaves * 64);
                    raw = Math.Max(0, Math.Min(target == 0 ? 16383 : 127, raw));
                    if (raw == last) return; last = raw;
                    events.Add(target == 0 ? new Event(tick(beat), Control, (byte)(0xE0 | ch), (byte)(raw & 127), (byte)(raw >> 7))
                                           : new Event(tick(beat), Control, (byte)(0xB0 | ch), (byte)(target == 1 ? 1 : 74), (byte)raw));
                }
                for (int k = 0; k < points.Length; k++)
                {
                    var a = points[k]; if (a.Beat > loopBeats + 1e-9) break;
                    Emit(a.Beat, a.Value);
                    if (k + 1 >= points.Length || a.Curve == MusicAutomationCurve.Step) continue;
                    var b = points[k + 1]; double span = b.Beat - a.Beat;
                    for (double x = a.Beat + step; x < b.Beat - 1e-9; x += step)
                    {
                        double t = (x - a.Beat) / span; if (a.Curve == MusicAutomationCurve.Smooth) t = t * t * (3 - 2 * t);
                        Emit(x, a.Value + (b.Value - a.Value) * t);
                    }
                }
                if (points.Length > 0 && (target == 2 && Math.Abs(points.Max(x => x.Value)) > MusicMidiExpression.BrightnessOctaves || target == 2 && points.Min(x => x.Value) < -MusicMidiExpression.BrightnessOctaves))
                    result.Warnings.Add($"{p.Name}: brightness beyond ±{MusicMidiExpression.BrightnessOctaves} octaves is clipped by CC74.");
            }
        }

        // Major key with this tonic pitch class → sharps (positive) or flats (negative), preferring the usual spelling.
        static int KeySharps(int tonic) => new[] { 0, -5, 2, -3, 4, -1, 6, 1, -4, 3, -2, 5 }[tonic];

        /// <summary>The key a MIDI key signature names ("E minor", "Bb major").</summary>
        public static string KeyName(int sharps, bool minor)
        {
            if (sharps < -7 || sharps > 7) throw new ArgumentOutOfRangeException(nameof(sharps));
            int major = ((sharps * 7) % 12 + 12) % 12;
            string[] names = sharps < 0 ? new[] { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" } : new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            return minor ? names[(major + 9) % 12] + " minor" : names[major] + " major";
        }

        static byte[] MetaText(int type, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); var s = new MemoryStream();
            s.WriteByte(0xFF); s.WriteByte((byte)type); Variable(s, bytes.Length); s.Write(bytes, 0, bytes.Length);
            return s.ToArray();
        }
        static void Variable(Stream s, long value)
        {
            if (value < 0 || value > 0x0FFFFFFF) throw new InvalidDataException("MIDI delta out of range.");
            var stack = new Stack<byte>(); stack.Push((byte)(value & 127));
            while ((value >>= 7) > 0) stack.Push((byte)(value & 127 | 128));
            foreach (var b in stack) s.WriteByte(b);
        }
        static void Ascii(Stream s, string text) { var b = Encoding.ASCII.GetBytes(text); s.Write(b, 0, b.Length); }
        static void Int32(Stream s, int v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        static void Int16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    }
}
