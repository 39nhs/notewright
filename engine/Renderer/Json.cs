using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace GrazePlugin
{
    /// <summary>Minimal JSON reader/writer (objects, arrays, strings, numbers, true/false/null). No Unity, no dependencies.</summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            var value = Value(text, ref i);
            Space(text, ref i);
            if (i != text.Length) throw Error(text, i, "unexpected trailing text");
            return value;
        }

        static object Value(string s, ref int i)
        {
            Space(s, ref i);
            if (i >= s.Length) throw Error(s, i, "unexpected end");
            char c = s[i];
            if (c == '{')
            {
                var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                i++; Space(s, ref i);
                if (s[i] == '}') { i++; return obj; }
                while (true)
                {
                    Space(s, ref i);
                    string key = String(s, ref i);
                    Space(s, ref i);
                    if (s[i] != ':') throw Error(s, i, "expected ':'");
                    i++;
                    obj[key] = Value(s, ref i);
                    Space(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return obj; }
                    throw Error(s, i, "expected ',' or '}'");
                }
            }
            if (c == '[')
            {
                var list = new List<object>();
                i++; Space(s, ref i);
                if (s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i));
                    Space(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw Error(s, i, "expected ',' or ']'");
                }
            }
            if (c == '"') return String(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw Error(s, i, "unexpected character '" + c + "'");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string String(string s, ref int i)
        {
            if (s[i] != '"') throw Error(s, i, "expected string");
            var sb = new StringBuilder();
            for (i++; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c != '\\') { sb.Append(c); continue; }
                c = s[++i];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)System.Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                    default: sb.Append(c); break;
                }
            }
            throw Error(s, i, "unterminated string");
        }

        static void Space(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static FormatException Error(string s, int i, string what)
        {
            int line = 1; for (int k = 0; k < i && k < s.Length; k++) if (s[k] == '\n') line++;
            return new FormatException($"JSON line {line}: {what}");
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string str:
                    sb.Append('"');
                    foreach (char c in str)
                    {
                        if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                        else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                    }
                    sb.Append('"');
                    break;
                case IDictionary<string, object> obj:
                    sb.Append('{'); bool first = true;
                    foreach (var kv in obj) { if (!first) sb.Append(','); first = false; Write(sb, kv.Key); sb.Append(':'); Write(sb, kv.Value); }
                    sb.Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    sb.Append('['); bool head = true;
                    foreach (var item in list) { if (!head) sb.Append(','); head = false; Write(sb, item); }
                    sb.Append(']');
                    break;
                case double d: sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture)); break;
                case float f: Write(sb, (double)f); break;
                default: sb.Append(System.Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            }
        }

        // ---- helpers for typed reads ----
        public static Dictionary<string, object> Obj(object o, string where) =>
            o as Dictionary<string, object> ?? throw new FormatException(where + ": expected an object");
        public static List<object> List(object o, string where) =>
            o as List<object> ?? throw new FormatException(where + ": expected an array");
        public static double Num(Dictionary<string, object> o, string key, double fallback = 0) =>
            o.TryGetValue(key, out var v) && v != null ? System.Convert.ToDouble(v, CultureInfo.InvariantCulture) : fallback;
        public static string Str(Dictionary<string, object> o, string key, string fallback = null) =>
            o.TryGetValue(key, out var v) && v != null ? System.Convert.ToString(v, CultureInfo.InvariantCulture) : fallback;
        public static bool Bool(Dictionary<string, object> o, string key) => o.TryGetValue(key, out var v) && v is bool b && b;

        /// <summary>Sets public fields of a [Serializable] engine object from JSON keys with the C# field names (as Unity's JsonUtility does). Unknown keys are errors.</summary>
        public static object Fill(object target, Dictionary<string, object> source, string where, params string[] ignore)
        {
            var type = target.GetType();
            foreach (var kv in source)
            {
                if (Array.IndexOf(ignore, kv.Key) >= 0) continue;
                var field = type.GetField(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                if (field == null) throw new FormatException($"{where}: unknown field '{kv.Key}' (fields are the C# names of {type.Name})");
                field.SetValue(target, Convert(kv.Value, field.FieldType, where + "." + kv.Key));
            }
            return target;
        }

        static object Convert(object value, Type type, string where)
        {
            if (type.IsEnum)
            {
                if (value is string name) return Enum.Parse(type, name, true);
                return Enum.ToObject(type, System.Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }
            if (type == typeof(string)) return value == null ? "" : System.Convert.ToString(value, CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return value is bool b ? b : throw new FormatException(where + ": expected true/false");
            if (type == typeof(float)) return System.Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (type == typeof(int)) return System.Convert.ToInt32(value, CultureInfo.InvariantCulture);
            throw new FormatException(where + ": unsupported field type " + type.Name);
        }
    }
}
