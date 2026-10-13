using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Graze.Presentation.Audio
{
    public sealed class MusicPluginIdentity
    {
        public string ClassId { get; }
        public string Name { get; }
        public string Vendor { get; }
        public string Version { get; }
        public MusicPluginIdentity(string classId, string name = "", string vendor = "", string version = "")
        {
            if (classId == null || classId.Length != 32) throw new ArgumentException("VST3 classId must contain 32 hexadecimal characters.");
            foreach (char c in classId) if (!Uri.IsHexDigit(c)) throw new ArgumentException("Invalid VST3 classId.");
            ClassId = classId.ToUpperInvariant(); Name = name ?? ""; Vendor = vendor ?? ""; Version = version ?? "";
        }
    }
    public sealed class MusicPluginTarget
    {
        public string Song { get; }
        public string Instrument { get; }
        public MusicPluginIdentity Plugin { get; }
        public MusicPluginState State { get; }
        public IReadOnlyDictionary<string, double> Parameters { get; }
        public double Gain { get; }
        public double Pan { get; }
        public int Transpose { get; }
        public double VelocityCurve { get; }
        public bool Mono { get; }
        /// <summary>V3 prepared playback: selected native output bus → existing cue track. Empty keeps released single-output behavior.</summary>
        public IReadOnlyList<MusicPluginOutputRoute> Outputs { get; }
        /// <summary>
        /// Playback V3+ parameter automation: parameter key (numeric VST3 ID or name, like <see cref="Parameters"/>) → points
        /// (beat inside the cue, normalized value 0..1, curve). The lane overrides the static value while the cue plays.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MusicAutomationPoint>> Automation { get; }
        // Retain the existing compiled-client constructor signature; source calls may use the new optional outputs argument.
        public MusicPluginTarget(string song,string instrument,MusicPluginIdentity plugin,MusicPluginState state,
            IDictionary<string,double> parameters,double gain,double pan,int transpose,double velocityCurve,bool mono,
            IDictionary<string,MusicAutomationPoint[]> automation)
            :this(song,instrument,plugin,state,parameters,gain,pan,transpose,velocityCurve,mono,automation,null) { }
        public MusicPluginTarget(string song, string instrument, MusicPluginIdentity plugin, MusicPluginState state = null,
            IDictionary<string, double> parameters = null, double gain = 1, double pan = 0, int transpose = 0, double velocityCurve = 1, bool mono = false,
            IDictionary<string, MusicAutomationPoint[]> automation = null, MusicPluginOutputRoute[] outputs = null)
        {
            if (string.IsNullOrWhiteSpace(song) || string.IsNullOrWhiteSpace(instrument) || plugin == null ||
                !MusicTrack.Finite(gain) || gain < 0 || gain > 4 || !MusicTrack.Finite(pan) || pan < -1 || pan > 1 ||
                transpose < -127 || transpose > 127 || !MusicTrack.Finite(velocityCurve) || velocityCurve <= 0 || velocityCurve > 8)
                throw new ArgumentException("Invalid plugin target.");
            Song = song; Instrument = instrument; Plugin = plugin; State = state ?? new MusicPluginState(null);
            var copy = new Dictionary<string, double>(StringComparer.Ordinal);
            if (parameters != null) foreach (var p in parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Key) || !MusicTrack.Finite(p.Value) || p.Value < 0 || p.Value > 1) throw new ArgumentException("Parameters must be normalized to 0..1.");
                copy.Add(p.Key, p.Value);
            }
            Parameters = new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(copy);
            Gain = gain; Pan = pan; Transpose = transpose; VelocityCurve = velocityCurve; Mono = mono;
            var routes = outputs == null ? Array.Empty<MusicPluginOutputRoute>() : (MusicPluginOutputRoute[])outputs.Clone();
            if (routes.Length > MusicPluginOutputRoute.MaxOutputs) throw new ArgumentException("At most 16 output routes.");
            var buses = new HashSet<int>(); var destinations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in routes) if (route == null || !buses.Add(route.Bus) || !destinations.Add(route.Instrument))
                throw new ArgumentException("Each output bus and destination track must occur only once.");
            Outputs = Array.AsReadOnly(routes);
            var lanes = new Dictionary<string, IReadOnlyList<MusicAutomationPoint>>(StringComparer.Ordinal);
            if (automation != null) foreach (var lane in automation)
            {
                if (string.IsNullOrWhiteSpace(lane.Key)) throw new ArgumentException("Automation needs a parameter key.");
                // Beats are checked against the cue's loop when a session prepares it.
                new MusicAutomation(MusicAutomationTarget.PluginParameter, lane.Value, double.MaxValue);
                lanes.Add(lane.Key, Array.AsReadOnly((MusicAutomationPoint[])lane.Value.Clone()));
            }
            Automation = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<MusicAutomationPoint>>(lanes);
        }
    }
    /// <summary>Code-first sidecar. Does not modify the cue schema or any released playback version.</summary>
    public sealed class MusicPluginMod
    {
        public const string Format = "graze-plugin-mod/1";
        public const string MultiOutputFormat = "graze-plugin-mod/2";
        public string Name { get; }
        readonly MusicPluginTarget[] targets;
        public IReadOnlyList<MusicPluginTarget> Targets => Array.AsReadOnly(targets);
        public MusicPluginMod(string name, params MusicPluginTarget[] targets)
        {
            if (string.IsNullOrWhiteSpace(name) || targets == null || targets.Length > MusicCue.MaxParts) throw new ArgumentException("Invalid plugin mod.");
            Name = name; this.targets = (MusicPluginTarget[])targets.Clone();
            var keys = new HashSet<string>();
            foreach (var target in targets)
                if (target == null || !keys.Add(target.Song + "\0" + target.Instrument)) throw new ArgumentException("Duplicate/null plugin target.");
        }
        internal MusicPluginTarget Find(MusicCue cue, MusicPart part)
        {
            MusicPluginTarget wildcard = null;
            foreach (var target in targets) if (target.Instrument == part.InstrumentId)
            { if (target.Song == cue.Name) return target; if (target.Song == "*") wildcard = target; }
            return wildcard;
        }
        public static MusicPluginMod FromJson(string json)
        {
            var root = MusicPluginJson.Object(MusicPluginJson.Parse(json));
            string format=MusicPluginJson.String(root,"format");
            if (format != Format && format != MultiOutputFormat) throw new ArgumentException("Unsupported plugin mod format.");
            var list = MusicPluginJson.Array(root, "targets"); var result = new MusicPluginTarget[list.Count];
            for (int i = 0; i < result.Length; i++)
            {
                var t = MusicPluginJson.Object(list[i]); var p = MusicPluginJson.Object(MusicPluginJson.Required(t, "plugin"));
                if (p.ContainsKey("path") || t.ContainsKey("path")) throw new ArgumentException("Mod files must not specify executable paths.");
                var parameters = new Dictionary<string, double>(); object raw;
                if (t.TryGetValue("parameters", out raw)) foreach (var entry in MusicPluginJson.Object(raw)) parameters.Add(entry.Key, MusicPluginJson.Number(entry.Value));
                double transpose = MusicPluginJson.Number(t, "transpose", 0);
                if (transpose != Math.Truncate(transpose)) throw new ArgumentException("Transpose must be an integer.");
                string voices = MusicPluginJson.String(t, "voices", "poly"); if (voices != "mono" && voices != "poly") throw new ArgumentException("voices must be mono or poly.");
                // "automation": { "parameter key": [beat, value, curve, ...] } — flattened triples like song.json (curve 0 linear, 1 step, 2 smooth).
                var automation = new Dictionary<string, MusicAutomationPoint[]>();
                if (t.TryGetValue("automation", out raw)) foreach (var entry in MusicPluginJson.Object(raw))
                {
                    var flat = entry.Value as List<object> ?? throw new ArgumentException("Automation lanes must be arrays.");
                    if (flat.Count == 0 || flat.Count % 3 != 0) throw new ArgumentException("Automation points must be beat,value,curve triples.");
                    var points = new MusicAutomationPoint[flat.Count / 3];
                    for (int k = 0; k < points.Length; k++)
                    {
                        double curve = MusicPluginJson.Number(flat[k * 3 + 2]);
                        if (curve != Math.Truncate(curve)) throw new ArgumentException("Automation curve must be 0, 1 or 2.");
                        points[k] = new MusicAutomationPoint(MusicPluginJson.Number(flat[k * 3]), (float)MusicPluginJson.Number(flat[k * 3 + 1]), (MusicAutomationCurve)(int)curve);
                    }
                    automation.Add(entry.Key, points);
                }
                var outputs = new List<MusicPluginOutputRoute>();
                if (t.TryGetValue("outputs", out raw)) foreach (var item in MusicPluginJson.Array(t,"outputs"))
                {
                    var route=MusicPluginJson.Object(item); double bus=MusicPluginJson.Number(MusicPluginJson.Required(route,"bus"));
                    if(bus!=Math.Truncate(bus)||bus<0||bus>=64)throw new ArgumentException("Output bus must be an integer from 0 to 63.");
                    outputs.Add(new MusicPluginOutputRoute((int)bus,MusicPluginJson.String(route,"instrument")));
                }
                if(outputs.Count>0&&format!=MultiOutputFormat)throw new ArgumentException("Output routing requires graze-plugin-mod/2 so old clients cannot silently drop routes.");
                result[i] = new MusicPluginTarget(MusicPluginJson.String(t, "song"), MusicPluginJson.String(t, "instrument"),
                    new MusicPluginIdentity(MusicPluginJson.String(p, "classId"), MusicPluginJson.String(p, "name", ""), MusicPluginJson.String(p, "vendor", ""), MusicPluginJson.String(p, "version", "")),
                    MusicPluginState.FromBase64(MusicPluginJson.String(t, "state", "")), parameters,
                    MusicPluginJson.Number(t, "gain", 1), MusicPluginJson.Number(t, "pan", 0), checked((int)transpose), MusicPluginJson.Number(t, "velocityCurve", 1), voices == "mono", automation, outputs.ToArray());
            }
            return new MusicPluginMod(MusicPluginJson.String(root, "name"), result);
        }
        public string ToJson()
        {
            bool multi=false;foreach(var target in targets)multi|=target.Outputs.Count>0;
            var result = new StringBuilder("{\"format\":" + MusicPluginJson.Quote(multi?MultiOutputFormat:Format) + ",\"name\":" + MusicPluginJson.Quote(Name) + ",\"targets\":[");
            for (int i = 0; i < targets.Length; i++)
            {
                if (i != 0) result.Append(','); var t = targets[i];
                result.Append("{\"song\":").Append(MusicPluginJson.Quote(t.Song)).Append(",\"instrument\":").Append(MusicPluginJson.Quote(t.Instrument));
                result.Append(",\"plugin\":{\"classId\":").Append(MusicPluginJson.Quote(t.Plugin.ClassId)).Append(",\"name\":").Append(MusicPluginJson.Quote(t.Plugin.Name));
                result.Append(",\"vendor\":").Append(MusicPluginJson.Quote(t.Plugin.Vendor)).Append(",\"version\":").Append(MusicPluginJson.Quote(t.Plugin.Version)).Append('}');
                result.Append(",\"state\":").Append(MusicPluginJson.Quote(t.State.ToBase64())).Append(",\"parameters\":{");
                bool first = true; foreach (var p in t.Parameters) { if (!first) result.Append(','); first = false; result.Append(MusicPluginJson.Quote(p.Key)).Append(':').Append(p.Value.ToString("R", CultureInfo.InvariantCulture)); }
                result.Append("},\"gain\":").Append(t.Gain.ToString("R", CultureInfo.InvariantCulture)).Append(",\"pan\":").Append(t.Pan.ToString("R", CultureInfo.InvariantCulture));
                result.Append(",\"transpose\":").Append(t.Transpose).Append(",\"velocityCurve\":").Append(t.VelocityCurve.ToString("R", CultureInfo.InvariantCulture));
                result.Append(",\"voices\":").Append(MusicPluginJson.Quote(t.Mono ? "mono" : "poly"));
                if(t.Outputs.Count>0)
                {
                    result.Append(",\"outputs\":[");
                    for(int k=0;k<t.Outputs.Count;k++){if(k!=0)result.Append(',');var route=t.Outputs[k];result.Append("{\"bus\":").Append(route.Bus).Append(",\"instrument\":").Append(MusicPluginJson.Quote(route.Instrument)).Append('}');}
                    result.Append(']');
                }
                if (t.Automation.Count > 0)
                {
                    result.Append(",\"automation\":{"); bool firstLane = true;
                    foreach (var lane in t.Automation)
                    {
                        if (!firstLane) result.Append(','); firstLane = false; result.Append(MusicPluginJson.Quote(lane.Key)).Append(":[");
                        for (int k = 0; k < lane.Value.Count; k++)
                        {
                            var p = lane.Value[k]; if (k != 0) result.Append(',');
                            result.Append(p.Beat.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(p.Value.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append((int)p.Curve);
                        }
                        result.Append(']');
                    }
                    result.Append('}');
                }
                result.Append('}');
            }
            return result.Append("]}").ToString();
        }
    }
    // Small bounded JSON parser avoids third-party runtime dependencies.
    internal sealed class MusicPluginJson
    {
        readonly string text; int position;
        MusicPluginJson(string text) { this.text = text; }
        internal static object Parse(string text)
        {
            if (text == null || text.Length > 24 * 1024 * 1024) throw new ArgumentException("Invalid/oversize manifest.");
            var reader = new MusicPluginJson(text); object result = reader.Value(0); reader.White();
            if (reader.position != text.Length) throw new ArgumentException("Trailing JSON data."); return result;
        }
        void White() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        bool Take(char c) { White(); if (position >= text.Length || text[position] != c) return false; position++; return true; }
        object Value(int depth)
        {
            if (depth > 16) throw new ArgumentException("JSON nesting exceeds 16."); White();
            if (position >= text.Length) throw new ArgumentException("Truncated JSON.");
            if (text[position] == '"') return ReadString();
            if (Take('{')) { var map = new Dictionary<string, object>(); if (Take('}')) return map; do { White(); string key = ReadString(); if (!Take(':')) throw new ArgumentException("Expected colon."); if (map.ContainsKey(key)) throw new ArgumentException("Duplicate JSON key."); map.Add(key, Value(depth + 1)); } while (Take(',')); if (!Take('}')) throw new ArgumentException("Expected object end."); return map; }
            if (Take('[')) { var array = new List<object>(); if (Take(']')) return array; do { if (array.Count >= 65536) throw new ArgumentException("JSON array too large."); array.Add(Value(depth + 1)); } while (Take(',')); if (!Take(']')) throw new ArgumentException("Expected array end."); return array; }
            foreach (string literal in new[] { "true", "false", "null" }) if (position + literal.Length <= text.Length && text.Substring(position, literal.Length) == literal) { position += literal.Length; return literal == "null" ? null : (object)(literal == "true"); }
            int start = position; while (position < text.Length && "-+0123456789.eE".IndexOf(text[position]) >= 0) position++;
            double number; if (position == start || !double.TryParse(text.Substring(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !MusicTrack.Finite(number)) throw new ArgumentException("Invalid JSON number."); return number;
        }
        string ReadString()
        {
            if (!Take('"')) throw new ArgumentException("Expected JSON string."); var result = new StringBuilder();
            while (position < text.Length)
            {
                char c = text[position++]; if (c == '"') return result.ToString(); if (c < 32) throw new ArgumentException("Control character in JSON string.");
                if (c == '\\')
                {
                    if (position >= text.Length) break; c = text[position++];
                    switch (c) { case '"': case '\\': case '/': break; case 'b': c = '\b'; break; case 'f': c = '\f'; break; case 'n': c = '\n'; break; case 'r': c = '\r'; break; case 't': c = '\t'; break;
                        case 'u': ushort n; if (position + 4 > text.Length || !ushort.TryParse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) throw new ArgumentException("Invalid Unicode escape."); position += 4; c = (char)n; break;
                        default: throw new ArgumentException("Invalid JSON escape."); }
                }
                result.Append(c);
            }
            throw new ArgumentException("Unterminated JSON string.");
        }
        internal static string Quote(string s) { var b = new StringBuilder("\""); foreach (char c in s) { if (c == '"' || c == '\\') b.Append('\\').Append(c); else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); } return b.Append('"').ToString(); }
        internal static Dictionary<string, object> Object(object o) => o as Dictionary<string, object> ?? throw new ArgumentException("Expected JSON object.");
        internal static object Required(Dictionary<string, object> o, string key) { object result; if (!o.TryGetValue(key, out result)) throw new ArgumentException("Missing " + key); return result; }
        internal static string String(Dictionary<string, object> o, string key) => Required(o, key) as string ?? throw new ArgumentException("Expected string: " + key);
        internal static string String(Dictionary<string, object> o, string key, string fallback) => o.ContainsKey(key) ? String(o, key) : fallback;
        internal static double Number(object o) => o is double ? (double)o : throw new ArgumentException("Expected number.");
        internal static double Number(Dictionary<string, object> o, string key, double fallback) => o.ContainsKey(key) ? Number(o[key]) : fallback;
        internal static List<object> Array(Dictionary<string, object> o, string key) => Required(o, key) as List<object> ?? throw new ArgumentException("Expected JSON array.");
    }
}
