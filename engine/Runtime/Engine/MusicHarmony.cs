using System;

namespace Graze.Presentation.Audio
{
    /// <summary>Conservative half-beat pitch-class analysis, built before audio rendering.</summary>
    public static class MusicHarmony
    {
        public const double Resolution = .5;
        public const int MaxWaitBars = 2;
        public const double MinimumSharedRatio = .5;
        public const int MaximumBridgeLeap = 7;

        /// <summary>Pitch-class mask per half-beat, stored as runs so memory follows the note count, not the song length.</summary>
        internal sealed class Map
        {
            internal readonly int Count;      // half-beat slots in the loop
            internal readonly int[] Starts;   // first slot of each run, ascending, Starts[0] == 0
            internal readonly ushort[] Masks;
            internal Map(int count, int[] starts, ushort[] masks) { Count = count; Starts = starts; Masks = masks; }
            internal int At(int slot)
            {
                int lo = 0, hi = Starts.Length - 1;
                while (lo < hi) { int mid = (lo + hi + 1) / 2; if (Starts[mid] <= slot) lo = mid; else hi = mid - 1; }
                return Masks[lo];
            }
        }

        internal static Map Analyze(MusicCue cue)
        {
            int count = (int)Math.Ceiling(cue.LoopBeats / Resolution);
            // Sweep over note start/end events (slot, pitch class, melodic?, +1/-1); a note longer than the loop wraps once.
            var events = new System.Collections.Generic.List<long>();
            void Add(int slot, int pc, bool melodic, int delta)
            {
                if (slot >= count) return; // ends at the loop end never apply inside it
                events.Add(((long)slot << 8) | ((long)(delta > 0 ? 1 : 0) << 5) | ((long)(melodic ? 1 : 0) << 4) | (long)pc);
            }
            foreach (var part in cue.Parts)
            {
                if (part.Layer == MusicLayer.Drums || part.Gain == 0 || part.Instrument.Noise != null) continue;
                bool melodic = part.Layer == MusicLayer.Melody;
                foreach (var note in part.Notes)
                {
                    if (note.Velocity == 0) continue;
                    int pc = note.Key % 12;
                    int first = (int)Math.Floor(note.Beat / Resolution);
                    double endBeat = note.Beat + Math.Min(note.Duration, cue.LoopBeats);
                    int end = Math.Min(count, (int)Math.Ceiling(endBeat / Resolution));
                    Add(first, pc, melodic, 1); Add(end, pc, melodic, -1);
                    if (endBeat > cue.LoopBeats)
                    { Add(0, pc, melodic, 1); Add(Math.Min(count, (int)Math.Ceiling((endBeat - cue.LoopBeats) / Resolution)), pc, melodic, -1); }
                }
            }
            events.Sort();
            var harmonic = new int[12];
            var melody = new int[12];
            var starts = new System.Collections.Generic.List<int> { 0 };
            var masks = new System.Collections.Generic.List<ushort> { 0 };
            for (int i = 0; i < events.Count;)
            {
                int slot = (int)(events[i] >> 8);
                for (; i < events.Count && (int)(events[i] >> 8) == slot; i++)
                {
                    int pc = (int)(events[i] & 15), delta = (events[i] & 32) != 0 ? 1 : -1;
                    if ((events[i] & 16) != 0) melody[pc] += delta; else harmonic[pc] += delta;
                }
                ushort h = 0, m = 0;
                for (int pc = 0; pc < 12; pc++)
                {
                    if (harmonic[pc] > 0) h |= (ushort)(1 << pc);
                    if (melody[pc] > 0) m |= (ushort)(1 << pc);
                }
                ushort mask = h != 0 ? h : m;
                if (starts[starts.Count - 1] == slot) masks[masks.Count - 1] = mask;
                else if (masks[masks.Count - 1] != mask) { starts.Add(slot); masks.Add(mask); }
            }
            return new Map(count, starts.ToArray(), masks.ToArray());
        }

        public static int MaskAt(MusicCue cue, double beat)
        {
            double local = (((beat + 1e-8) % cue.LoopBeats) + cue.LoopBeats) % cue.LoopBeats;
            return cue.Harmony.At(Math.Min(cue.Harmony.Count - 1, (int)(local / Resolution)));
        }

        public static int Count(int mask)
        { int result = 0; for (; mask != 0; mask &= mask - 1) result++; return result; }

        public static bool Compatible(int a, int b)
        {
            int common = Count(a & b), largest = Math.Max(Count(a), Count(b));
            // Single-note coincidences do not establish a usable chord progression.
            return common >= 2 && largest > 0 && common / (double)largest >= MinimumSharedRatio;
        }

        public static int NearestCommonKey(int mask, int desired)
        {
            for (int distance = 0; distance <= MaximumBridgeLeap; distance++)
            {
                int low = desired - distance, high = desired + distance;
                if (low >= 0 && low <= 127 && (mask & (1 << (low % 12))) != 0) return low;
                if (high >= 0 && high <= 127 && (mask & (1 << (high % 12))) != 0) return high;
            }
            return -1;
        }

        internal static int KeyBefore(MusicPart part, double local)
        {
            if (part.Notes.Length == 0) return 60;
            int lo = 0, hi = part.Notes.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (part.Notes[mid].Beat <= local) lo = mid + 1; else hi = mid; }
            return part.Notes[lo == 0 ? part.Notes.Length - 1 : lo - 1].Key;
        }
    }
}
