using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VagabondK.nF.WebUi.Generator
{
    /// <summary>매니페스트 파싱/직렬화용 초소형 JSON (targets 인라인 태스크에서 이식).</summary>
    internal sealed class MiniJson
    {
        string json;
        int pos;

        public object Parse(string text)
        {
            json = text; pos = 0;
            object v = ParseValue();
            SkipWs();
            if (pos != json.Length) throw new FormatException("trailing characters at offset " + pos);
            return v;
        }

        void SkipWs() { while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++; }
        char Peek() { SkipWs(); return pos < json.Length ? json[pos] : '\0'; }
        char Take() { SkipWs(); if (pos >= json.Length) throw new FormatException("unexpected end of JSON"); return json[pos++]; }

        object ParseValue()
        {
            char c = Take();
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                SkipWs();
                if (pos < json.Length && json[pos] == '}') { pos++; return d; }
                while (true)
                {
                    if (Peek() != '"') throw new FormatException("property name expected at offset " + pos);
                    string k = ParseString();
                    if (Take() != ':') throw new FormatException("':' expected after \"" + k + "\"");
                    d[k] = ParseValue();
                    char n = Take();
                    if (n == ',') continue;
                    if (n == '}') return d;
                    throw new FormatException("',' or '}' expected at offset " + (pos - 1));
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                SkipWs();
                if (pos < json.Length && json[pos] == ']') { pos++; return l; }
                while (true)
                {
                    l.Add(ParseValue());
                    char n = Take();
                    if (n == ',') continue;
                    if (n == ']') return l;
                    throw new FormatException("',' or ']' expected at offset " + (pos - 1));
                }
            }
            if (c == '"') { pos--; return ParseString(); }
            if (c == 't') { Expect("true"); return true; }
            if (c == 'f') { Expect("false"); return false; }
            if (c == 'n') { Expect("null"); return null; }
            int start = pos - 1;
            while (pos < json.Length && "+-.eE0123456789".IndexOf(json[pos]) >= 0) pos++;
            return double.Parse(json.Substring(start, pos - start), CultureInfo.InvariantCulture);
        }

        void Expect(string word)
        {
            pos--;
            if (json.Length - pos < word.Length || json.Substring(pos, word.Length) != word) throw new FormatException(word + " expected at offset " + pos);
            pos += word.Length;
        }

        string ParseString()
        {
            var sb = new StringBuilder();
            SkipWs();
            if (pos >= json.Length || json[pos] != '"') throw new FormatException("string expected at offset " + pos);
            pos++;
            while (true)
            {
                if (pos >= json.Length) throw new FormatException("unterminated string");
                char c = json[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= json.Length) throw new FormatException("unterminated escape");
                char e = json[pos++];
                if (e == 'u')
                {
                    if (pos + 4 > json.Length) throw new FormatException("bad \\u escape");
                    sb.Append((char)int.Parse(json.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    pos += 4;
                }
                else if (e == 'n') sb.Append('\n');
                else if (e == 'r') sb.Append('\r');
                else if (e == 't') sb.Append('\t');
                else if (e == 'b') sb.Append('\b');
                else if (e == 'f') sb.Append('\f');
                else sb.Append(e);
            }
        }

        public static string JsonOut(object v)
        {
            if (v == null) return "null";
            var d = v as Dictionary<string, object>;
            if (d != null)
            {
                var sb = new StringBuilder("{");
                bool first = true;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Quote(kv.Key)).Append(':').Append(JsonOut(kv.Value));
                }
                return sb.Append('}').ToString();
            }
            var l = v as List<object>;
            if (l != null)
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(JsonOut(l[i])); }
                return sb.Append(']').ToString();
            }
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is double)
            {
                double x = (double)v;
                if (x == Math.Floor(x) && Math.Abs(x) < 1e15) return ((long)x).ToString(CultureInfo.InvariantCulture);
                return x.ToString("R", CultureInfo.InvariantCulture);
            }
            return Quote((string)v);
        }

        static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') { sb.Append('\\').Append(c); }
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
