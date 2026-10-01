using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Graze.Presentation.Audio
{
    /// <summary>Bounded SMF 0/1 PPQN parser. Note timing stays in beats for live tempo control.</summary>
    public static class MidiMusicReader
    {
        public sealed class Part
        {
            public string Name;
            public int Channel, Program;
            public readonly List<MusicNote> Notes = new List<MusicNote>();
        }
        public struct Tempo { public double Beat, Bpm; }
        public sealed class Result
        {
            public readonly List<Part> Parts = new List<Part>();
            public readonly List<Tempo> Tempos = new List<Tempo>();
            public readonly List<string> Warnings = new List<string>();
            public int BeatsPerBar = 4;
            public double Bpm = 120, EndBeat;
        }
        private sealed class Held
        {
            public long Start;
            public int Key, Velocity;
            public Part Part;
        }
        private sealed class Cursor
        {
            public readonly byte[] Data;
            public int Position, End;
            public Cursor(byte[] data) { Data = data; End = data.Length; }
            public int Byte() { if (Position >= End) throw new InvalidDataException("Truncated MIDI."); return Data[Position++]; }
            public int Word() => Byte() << 8 | Byte();
            public int Length()
            {
                long n = (long)Byte() << 24 | (long)Byte() << 16 | (long)Byte() << 8 | (uint)Byte();
                if (n > End - Position) throw new InvalidDataException("Invalid MIDI chunk length.");
                return (int)n;
            }
            public int Variable()
            {
                int value = 0;
                for (int i = 0; i < 4; i++) { int b = Byte(); value = value << 7 | b & 127; if (b < 128) return value; }
                throw new InvalidDataException("Invalid MIDI variable length.");
            }
            public string Text(int size)
            {
                if (size < 0 || size > End - Position) throw new InvalidDataException("Truncated MIDI event.");
                string text = Encoding.UTF8.GetString(Data, Position, size); Position += size; return text;
            }
            public void Skip(int size)
            { if (size < 0 || size > End - Position) throw new InvalidDataException("Truncated MIDI event."); Position += size; }
        }

        public static Result Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("MIDI exceeds 16 MB limit.");
            var c = new Cursor(bytes);
            if (c.Text(4) != "MThd") throw new InvalidDataException("Not a Standard MIDI File.");
            int headerLength = c.Length();
            if (headerLength < 6) throw new InvalidDataException("Invalid MIDI header.");
            int format = c.Word(), tracks = c.Word(), ppqn = c.Word();
            if (format > 1 || tracks < 1 || tracks > 256 || (format == 0 && tracks != 1) || ppqn == 0 || (ppqn & 0x8000) != 0)
                throw new InvalidDataException("Only SMF 0/1 with PPQN timing is supported (no format 2/SMPTE).");
            c.Skip(headerLength - 6);
            var result = new Result();
            bool ignoredController = false, dangling = false;
            int totalNotes = 0;
            for (int track = 0; track < tracks; track++)
            {
                if (c.Text(4) != "MTrk") throw new InvalidDataException("Missing MIDI track.");
                int length = c.Length(), end = c.Position + length;
                c.End = end;
                long tick = 0;
                int running = 0;
                var programs = new int[16];
                var sustain = new bool[16];
                var active = new Dictionary<int, Queue<Held>>();
                var released = new List<Held>[16];
                var parts = new Dictionary<int, Part>();
                string name = "Track " + (track + 1);
                for (int i = 0; i < 16; i++) released[i] = new List<Held>();
                while (c.Position < end)
                {
                    tick += c.Variable();
                    if (tick / (double)ppqn > MusicCue.MaxLoopBeats) throw new InvalidDataException("MIDI exceeds beat limit.");
                    int status = c.Byte();
                    if (status < 128)
                    { if (running == 0) throw new InvalidDataException("Invalid running status."); c.Position--; status = running; }
                    if (status == 0xff)
                    {
                        running = 0;
                        int type = c.Byte(), size = c.Variable();
                        if (size > end - c.Position) throw new InvalidDataException("Truncated metadata.");
                        if (type == 3) name = c.Text(size);
                        else if (type == 0x51 && size == 3)
                        {
                            int micros = c.Byte() << 16 | c.Byte() << 8 | c.Byte();
                            if (micros == 0) throw new InvalidDataException("Zero MIDI tempo.");
                            result.Tempos.Add(new Tempo { Beat = tick / (double)ppqn, Bpm = 60000000.0 / micros });
                        }
                        else if (type == 0x58 && size == 4)
                        {
                            int numerator = c.Byte(), power = c.Byte(); c.Skip(2);
                            double quarterBeats = numerator * 4.0 / Math.Pow(2, power);
                            if (tick == 0 && quarterBeats >= 1 && quarterBeats <= 12 && quarterBeats == Math.Floor(quarterBeats)) result.BeatsPerBar = (int)quarterBeats;
                            else result.Warnings.Add("Meter change or fractional quarter-note bar: review the bar grid manually.");
                        }
                        else c.Skip(size);
                        if (type == 0x2f) { c.Position = end; break; }
                        continue;
                    }
                    if (status == 0xf0 || status == 0xf7) { running = 0; c.Skip(c.Variable()); ignoredController = true; continue; }
                    if (status >= 0xf0) throw new InvalidDataException("Unsupported system event.");
                    running = status;
                    int channel = status & 15, kind = status & 0xf0;
                    int a = c.Byte(), b = kind == 0xc0 || kind == 0xd0 ? 0 : c.Byte();
                    if (a > 127 || b > 127) throw new InvalidDataException("Invalid MIDI data byte.");
                    int key = channel * 128 + a;
                    if (kind == 0x90 && b > 0)
                    {
                        if (++totalNotes > MusicPart.MaxNotes) throw new InvalidDataException("Too many MIDI notes.");
                        int partKey = channel * 128 + programs[channel];
                        if (!parts.TryGetValue(partKey, out var part))
                        {
                            if (result.Parts.Count >= 32) throw new InvalidDataException("More than 32 channel/program parts; split the file first.");
                            part = new Part { Name = name, Channel = channel, Program = programs[channel] };
                            parts.Add(partKey, part); result.Parts.Add(part);
                        }
                        if (!active.TryGetValue(key, out var queue)) active.Add(key, queue = new Queue<Held>());
                        queue.Enqueue(new Held { Start = tick, Key = a, Velocity = b, Part = part });
                    }
                    else if (kind == 0x80 || kind == 0x90)
                    {
                        if (active.TryGetValue(key, out var queue) && queue.Count > 0)
                        {
                            var held = queue.Dequeue();
                            if (sustain[channel]) released[channel].Add(held); else EndNote(held, tick, ppqn);
                        }
                    }
                    else if (kind == 0xc0) programs[channel] = a;
                    else if (kind == 0xb0 && a == 64)
                    {
                        sustain[channel] = b >= 64;
                        if (!sustain[channel]) { foreach (var held in released[channel]) EndNote(held, tick, ppqn); released[channel].Clear(); }
                    }
                    else ignoredController = true;
                }
                foreach (var queue in active.Values)
                    foreach (var held in queue) { EndNote(held, tick, ppqn); dangling = true; }
                foreach (var list in released) foreach (var held in list) EndNote(held, tick, ppqn);
                result.EndBeat = Math.Max(result.EndBeat, tick / (double)ppqn);
                c.End = bytes.Length;
            }
            result.Tempos.Sort((a, b) => a.Beat.CompareTo(b.Beat));
            foreach (var tempo in result.Tempos) if (tempo.Beat == 0) result.Bpm = tempo.Bpm;
            if (result.Tempos.Count > 1) result.Warnings.Add("Original tempo map retained in import notes; runtime uses one editable base BPM plus live transitions.");
            if (ignoredController) result.Warnings.Add("Pitch bend, expression/volume controllers, aftertouch and SysEx are not rendered. CC64 sustain is supported.");
            if (dangling) result.Warnings.Add("Unclosed notes ended at their track end.");
            foreach (var part in result.Parts) part.Notes.Sort((a, b) => a.Beat.CompareTo(b.Beat));
            return result;
        }

        private static void EndNote(Held held, long end, int ppqn)
        {
            held.Part.Notes.Add(new MusicNote(held.Start / (double)ppqn, Math.Max(1, end - held.Start) / (double)ppqn, held.Key, held.Velocity / 127f));
        }
    }
}
