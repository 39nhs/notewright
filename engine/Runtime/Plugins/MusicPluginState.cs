using System;
using System.Text;

namespace Graze.Presentation.Audio
{
    /// <summary>Component/controller state, including the JUCE VC2! format used by the earlier Python tool.</summary>
    public sealed class MusicPluginState
    {
        const string Alphabet = ".ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+";
        public readonly byte[] Component, Controller;
        public MusicPluginState(byte[] component, byte[] controller = null)
        {
            if ((component?.LongLength ?? 0) + (controller?.LongLength ?? 0) > 12 * 1024 * 1024) throw new ArgumentException("Plugin state exceeds 12 MiB.");
            Component = (byte[])(component ?? Array.Empty<byte>()).Clone(); Controller = (byte[])(controller ?? Array.Empty<byte>()).Clone();
        }
        public static MusicPluginState FromBase64(string rawState)
        {
            if (string.IsNullOrEmpty(rawState)) return new MusicPluginState(null);
            if (rawState.Length > 24 * 1024 * 1024) throw new ArgumentException("Plugin state exceeds 16 MiB.");
            byte[] data = Convert.FromBase64String(rawState);
            if (data.Length < 8 || Encoding.ASCII.GetString(data, 0, 4) != "VC2!")
                throw new ArgumentException("Expected JUCE VC2! raw_state, not a .vstpreset/.fxp file.");
            uint size = (uint)(data[4] | data[5] << 8 | data[6] << 16 | data[7] << 24);
            if (size > 16 * 1024 * 1024 || size > data.Length - 8) throw new ArgumentException("Truncated state XML.");
            string xml = new UTF8Encoding(false, true).GetString(data, 8, (int)size);
            if (xml.IndexOf("<!", StringComparison.Ordinal) >= 0 || xml.IndexOf("&", StringComparison.Ordinal) >= 0 ||
                xml.IndexOf("<VST3PluginState>", StringComparison.Ordinal) < 0)
                throw new ArgumentException("Unsupported plugin state XML.");
            return new MusicPluginState(Decode(Tag(xml, "IComponent")), Decode(Tag(xml, "IEditController")));
        }
        static string Tag(string xml, string name)
        {
            string open = "<" + name + ">", close = "</" + name + ">";
            int a = xml.IndexOf(open, StringComparison.Ordinal);
            if (a < 0) return "0.";
            int b = xml.IndexOf(close, a + open.Length, StringComparison.Ordinal);
            if (b < 0 || xml.IndexOf(open, a + open.Length, StringComparison.Ordinal) >= 0) throw new ArgumentException("Invalid state element.");
            return xml.Substring(a + open.Length, b - a - open.Length).Trim();
        }
        static byte[] Decode(string text)
        {
            int dot = text.IndexOf('.'); int size;
            if (dot < 1 || !int.TryParse(text.Substring(0, dot), out size) || size < 0 || size > 16 * 1024 * 1024)
                throw new ArgumentException("Invalid JUCE memory block.");
            string encoded = text.Substring(dot + 1);
            if (encoded.Length != (size * 8L + 5) / 6) throw new ArgumentException("Truncated JUCE memory block.");
            byte[] result = new byte[size];
            for (int i = 0; i < encoded.Length; i++)
            {
                int value = Alphabet.IndexOf(encoded[i]); if (value < 0) throw new ArgumentException("Invalid JUCE alphabet.");
                for (int bit = 0; bit < 6; bit++) { long position = i * 6L + bit; if (position < size * 8L && (value & (1 << bit)) != 0) result[position / 8] |= (byte)(1 << (int)(position % 8)); }
            }
            return result;
        }
        static string Encode(byte[] data)
        {
            var text = new StringBuilder(); text.Append(data.Length).Append('.');
            for (long i = 0; i < (data.Length * 8L + 5) / 6; i++)
            {
                int value = 0;
                for (int bit = 0; bit < 6; bit++) { long p = i * 6 + bit; if (p < data.Length * 8L && (data[p / 8] & (1 << (int)(p % 8))) != 0) value |= 1 << bit; }
                text.Append(Alphabet[value]);
            }
            return text.ToString();
        }
        public string ToBase64()
        {
            byte[] xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?> <VST3PluginState><IComponent>" + Encode(Component) +
                "</IComponent><IEditController>" + Encode(Controller) + "</IEditController></VST3PluginState>");
            byte[] result = new byte[xml.Length + 9]; Encoding.ASCII.GetBytes("VC2!").CopyTo(result, 0);
            for (int i = 0; i < 4; i++) result[4 + i] = (byte)(xml.Length >> (8 * i));
            xml.CopyTo(result, 8); return Convert.ToBase64String(result);
        }
    }
}
