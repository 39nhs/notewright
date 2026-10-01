using System;
using System.IO;
using System.Text;

namespace GrazePlugin
{
    /// <summary>RIFF/WAVE reader (PCM 8/16/24/32-bit, IEEE float 32/64, WAVE_FORMAT_EXTENSIBLE) and streaming writer.</summary>
    public static class WavIO
    {
        public sealed class Audio { public float[] Interleaved; public int Channels, Rate; public int Frames => Interleaved.Length / Channels; }

        public static Audio Read(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException(path + ": not a RIFF/WAVE file");
                r.ReadUInt32();
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException(path + ": not a WAVE file");
                int format = 0, channels = 0, rate = 0, bits = 0;
                byte[] data = null;
                while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
                {
                    string id = Encoding.ASCII.GetString(r.ReadBytes(4));
                    long size = r.ReadUInt32();
                    long next = r.BaseStream.Position + size + (size & 1);
                    if (id == "fmt ")
                    {
                        format = r.ReadUInt16(); channels = r.ReadUInt16(); rate = r.ReadInt32();
                        r.ReadInt32(); r.ReadUInt16(); bits = r.ReadUInt16();
                        if (format == 0xFFFE && size >= 40) { r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32(); format = r.ReadUInt16(); }
                    }
                    else if (id == "data")
                    {
                        long available = Math.Min(size, r.BaseStream.Length - r.BaseStream.Position);
                        data = r.ReadBytes((int)available);
                    }
                    if (next > r.BaseStream.Length) break;
                    r.BaseStream.Position = next;
                }
                if (data == null || channels < 1 || rate < 1) throw new InvalidDataException(path + ": missing fmt or data chunk");
                int bytes = bits / 8;
                if (bytes < 1) throw new InvalidDataException(path + ": unsupported bit depth " + bits);
                int count = data.Length / bytes / channels * channels;
                var pcm = new float[count];
                for (int i = 0, o = 0; i < count; i++, o += bytes)
                {
                    double v;
                    if (format == 3 && bits == 32) v = BitConverter.ToSingle(data, o);
                    else if (format == 3 && bits == 64) v = BitConverter.ToDouble(data, o);
                    else if (format == 1 && bits == 8) v = (data[o] - 128) / 128.0;
                    else if (format == 1 && bits == 16) v = BitConverter.ToInt16(data, o) / 32768.0;
                    else if (format == 1 && bits == 24) v = ((data[o] | data[o + 1] << 8 | data[o + 2] << 16) << 8 >> 8) / 8388608.0;
                    else if (format == 1 && bits == 32) v = BitConverter.ToInt32(data, o) / 2147483648.0;
                    else throw new InvalidDataException($"{path}: unsupported WAV format {format} / {bits}-bit (use PCM or float WAV)");
                    pcm[i] = (float)Math.Max(-1, Math.Min(1, double.IsNaN(v) ? 0 : v));
                }
                return new Audio { Interleaved = pcm, Channels = channels, Rate = rate };
            }
        }

        /// <summary>Streaming stereo writer: 16/24-bit PCM or 32-bit float. Sizes are patched on Dispose.</summary>
        public sealed class Writer : IDisposable
        {
            readonly BinaryWriter w; readonly int bits; long frames;
            public Writer(string path, int rate, int bits)
            {
                if (bits != 16 && bits != 24 && bits != 32) throw new ArgumentException("bits must be 16, 24 or 32 (float)");
                this.bits = bits;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20));
                int bytes = bits / 8;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(0u); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)(bits == 32 ? 3 : 1)); w.Write((short)2); w.Write(rate); w.Write(rate * 2 * bytes);
                w.Write((short)(2 * bytes)); w.Write((short)bits);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(0u);
            }
            public void Write(float[] interleaved, int count, double gain)
            {
                for (int i = 0; i < count; i++)
                {
                    double v = interleaved[i] * gain;
                    if (bits == 32) { w.Write((float)v); continue; }
                    v = Math.Max(-1, Math.Min(1, v));
                    if (bits == 16) w.Write((short)Math.Round(v * 32767));
                    else { int q = (int)Math.Round(v * 8388607); w.Write((byte)q); w.Write((byte)(q >> 8)); w.Write((byte)(q >> 16)); }
                }
                frames += count / 2;
            }
            public void Dispose()
            {
                long data = frames * 2 * (bits / 8);
                if (data > uint.MaxValue - 36) throw new IOException("WAV larger than 4 GB");
                w.Seek(4, SeekOrigin.Begin); w.Write((uint)(36 + data));
                w.Seek(40, SeekOrigin.Begin); w.Write((uint)data);
                w.Dispose();
            }
        }
    }
}
