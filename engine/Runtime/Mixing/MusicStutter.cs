using System;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// Beat repeat (playback V3 chains with Stutter on, driven by a Stutter lane): while engaged, the first slice plays through and is
    /// recorded, then it repeats until released (2 ms edges, 2 ms crossfade back to the live signal). Buffers are allocated up front.
    /// </summary>
    internal sealed class MusicStutter
    {
        public const double MaxSeconds = 2;
        readonly float[] bufferLeft, bufferRight;
        readonly int fade;
        int write, since = -1, start, length, release, releasePosition;

        public MusicStutter(int sampleRate)
        {
            int n = (int)(sampleRate * MaxSeconds) + 1;
            bufferLeft = new float[n]; bufferRight = new float[n];
            fade = Math.Max(1, sampleRate / 500);
        }

        double Window(int p) => Math.Min(1, Math.Min((p + 1) / (double)fade, (length - p) / (double)fade));

        public void Process(ref double left, ref double right, bool engaged, double sliceSeconds, int sampleRate)
        {
            int n = bufferLeft.Length;
            if (engaged && since < 0) { since = 0; start = write; length = Math.Max(fade * 2 + 1, Math.Min(n - 1, (int)(sliceSeconds * sampleRate))); release = 0; }
            if (!engaged && since >= 0)
            {
                if (since >= length) { release = fade; releasePosition = (since - length) % length; }
                since = -1;
            }
            if (since >= length)
            {
                int p = (since - length) % length, i = (start + p) % n; double w = Window(p);
                left = bufferLeft[i] * w; right = bufferRight[i] * w;
            }
            else
            {
                bufferLeft[write] = (float)left; bufferRight[write] = (float)right; write = (write + 1) % n;
                if (release > 0)
                {
                    int i = (start + releasePosition) % n; double w = Window(releasePosition), k = release / (double)(fade + 1);
                    left = left * (1 - k) + bufferLeft[i] * w * k; right = right * (1 - k) + bufferRight[i] * w * k;
                    releasePosition = (releasePosition + 1) % length; release--;
                }
            }
            if (since >= 0) since++;
        }
    }
}
