// 极简 JSON 工具：足以序列化/反序列化本桥要用的受限结构（对象/数组/字符串/数字/布尔/null）。
// 不引第三方库、不引 System.Web.Extensions，保持 add-in 零额外依赖、离线可编。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace caxa3dmcp
{
    [ComVisible(false)]
    internal enum JKind { Object, Array, String, Number, Bool, Null }

    [ComVisible(false)]
    internal sealed class JValue
    {
        public JKind Kind = JKind.Null;
        public Dictionary<string, JValue> Obj;
        public List<JValue> Arr;
        public string Str;
        public double Num;
        public bool Bool;

        public static readonly JValue Null = new JValue();

        public string AsString() { return Kind == JKind.String ? Str : null; }
        public int AsInt() { return Kind == JKind.Number ? (int)Math.Round(Num) : 0; }
        public double AsDouble() { return Kind == JKind.Number ? Num : 0.0; }
        public bool AsBool() { return Kind == JKind.Bool && Bool; }
        public bool IsNull { get { return Kind == JKind.Null; } }
        public int Count { get { return Kind == JKind.Array && Arr != null ? Arr.Count : 0; } }

        public JValue this[string key]
        {
            get
            {
                if (Kind == JKind.Object && Obj != null && Obj.TryGetValue(key, out var v)) return v;
                return Null;
            }
        }

        public string ParamString(string key, string dflt = "")
        {
            JValue v = this[key];
            if (v.Kind == JKind.String) return v.Str;
            if (v.Kind == JKind.Number) return v.Num.ToString(CultureInfo.InvariantCulture);
            return dflt;
        }

        public double ParamDouble(string key, double dflt = 0.0)
        {
            JValue v = this[key];
            if (v.Kind == JKind.Number) return v.Num;
            if (v.Kind == JKind.String)
            {
                double d;
                if (double.TryParse(v.Str, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            return dflt;
        }

        public int ParamInt(string key, int dflt = 0)
        {
            JValue v = this[key];
            if (v.Kind == JKind.Number) return (int)Math.Round(v.Num);
            return dflt;
        }

        public bool ParamBool(string key, bool dflt = false)
        {
            JValue v = this[key];
            if (v.Kind == JKind.Bool) return v.Bool;
            return dflt;
        }

        public bool Has(string key) { return !this[key].IsNull; }
    }

    [ComVisible(false)]
    internal static class JsonRpc
    {
        // ---------------- 解析 ----------------
        public static JValue Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return JValue.Null;
            int i = 0;
            return ParseValue(s, ref i);
        }

        private static void SkipWs(string s, ref int i)
        {
            // ★ 坑（2026-11-12 实机取证）：绝不能把 ',' 当空白跳过！
            // 旧版这里跳过了 ','，导致 ParseObject 在第一个键值对后读不到分隔符而 break，
            // 结果 {"id":1,"tool":"ping"} 只解析出 id，tool 恒为空 → 所有工具报 "unknown tool: "。
            // 对象/数组循环里已有显式的逗号消费逻辑，这里只跳真正的空白。
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        private static JValue ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return JValue.Null;
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return new JValue { Kind = JKind.String, Str = ParseString(s, ref i) };
                case 't': i += 4; return new JValue { Kind = JKind.Bool, Bool = true };
                case 'f': i += 5; return new JValue { Kind = JKind.Bool, Bool = false };
                case 'n': i += 4; return JValue.Null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static JValue ParseObject(string s, ref int i)
        {
            var obj = new Dictionary<string, JValue>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return new JValue { Kind = JKind.Object, Obj = obj }; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (s[i] != '"') break;
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') break;
                i++; // :
                JValue val = ParseValue(s, ref i);
                obj[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return new JValue { Kind = JKind.Object, Obj = obj };
        }

        private static JValue ParseArray(string s, ref int i)
        {
            var arr = new List<JValue>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return new JValue { Kind = JKind.Array, Arr = arr }; }
            while (i < s.Length)
            {
                JValue val = ParseValue(s, ref i);
                arr.Add(val);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return new JValue { Kind = JKind.Array, Arr = arr };
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                string hex = s.Substring(i, 4);
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                                    sb.Append((char)cp);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static JValue ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsDigit(c) || c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+') i++;
                else break;
            }
            string num = s.Substring(start, i - start);
            double d;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return new JValue { Kind = JKind.Number, Num = d };
            return JValue.Null;
        }

        // ---------------- 序列化 ----------------
        public static string Serialize(object o)
        {
            var sb = new StringBuilder();
            WriteValue(sb, o);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string s) { WriteString(sb, s); return; }
            if (o is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (o is int || o is long || o is short || o is byte) { sb.Append(Convert.ToInt64(o).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is double || o is float || o is decimal)
            {
                double dv = Convert.ToDouble(o);
                sb.Append(dv.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (o is Dictionary<string, object> d) { WriteObject(sb, d); return; }
            if (o is System.Collections.IDictionary id)
            {
                sb.Append('{');
                bool first = true;
                foreach (System.Collections.DictionaryEntry e in id)
                {
                    if (!first) sb.Append(',');
                    WriteString(sb, Convert.ToString(e.Key));
                    sb.Append(':');
                    WriteValue(sb, e.Value);
                    first = false;
                }
                sb.Append('}');
                return;
            }
            if (o is System.Collections.IList l)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in l)
                {
                    if (!first) sb.Append(',');
                    WriteValue(sb, item);
                    first = false;
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, o.ToString());
        }

        private static void WriteObject(StringBuilder sb, Dictionary<string, object> d)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(',');
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value);
                first = false;
            }
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u" + ((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }
    }
}
