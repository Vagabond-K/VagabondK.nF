using System;
using System.Text;

namespace VagabondK.nF.Web
{
    /// <summary>JSON 값의 종류입니다.</summary>
    public enum JsonKind
    {
        /// <summary>null 리터럴.</summary>
        Null = 0,
        /// <summary>true/false.</summary>
        Bool = 1,
        /// <summary>숫자(원본 텍스트를 보존).</summary>
        Number = 2,
        /// <summary>문자열(역 이스케이프 완료).</summary>
        String = 3,
        /// <summary>배열.</summary>
        Array = 4,
        /// <summary>키-값 객체.</summary>
        Object = 5
    }

    /// <summary>
    /// <see cref="JsonReader"/>가 만드는 불변 JSON 노드입니다.
    /// 리플렉션·제네릭 컨테이너 없이 배열로 멤버를 보존하므로 nanoFramework 힙 제약에 안전합니다.
    /// </summary>
    public sealed class JsonValue
    {
        internal JsonValue(JsonKind kind, bool flag, string text, string[] keys, JsonValue[] vals)
        {
            Kind = kind;
            _flag = flag;
            _text = text;
            _keys = keys;
            _vals = vals;
        }

        private readonly bool _flag;
        private readonly string _text;
        private readonly string[] _keys;
        private readonly JsonValue[] _vals;

        /// <summary>노드 종류.</summary>
        public JsonKind Kind { get; private set; }

        /// <summary>Bool 노드의 값(그 외에는 false).</summary>
        public bool AsBool { get { return _flag; } }

        /// <summary>String/Number 노드의 원본 텍스트(그 외에는 null).</summary>
        public string AsString { get { return Kind == JsonKind.String || Kind == JsonKind.Number ? _text : null; } }

        /// <summary>Array/Object 노드의 멤버 수(그 외에는 0).</summary>
        public int Count { get { return _vals == null ? 0 : _vals.Length; } }

        /// <summary>Object 노드의 키 목록(그 외에는 null).</summary>
        public string[] Keys { get { return _keys; } }

        /// <summary>Object: 키 조회, Array: 미사용. 없으면 null.</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (_keys == null || key == null)
                    return null;
                for (int i = 0; i < _keys.Length; i++)
                    if (_keys[i] == key)
                        return _vals[i];
                return null;
            }
        }

        /// <summary>Array/Object의 i번 멤버. 범위 밖이면 null.</summary>
        public JsonValue this[int index]
        {
            get
            {
                if (_vals == null || index < 0 || index >= _vals.Length)
                    return null;
                return _vals[index];
            }
        }

        /// <summary>Number 텍스트를 정수로 변환 시도(문자 기반, 로캘 무관).</summary>
        public bool TryAsLong(out long value)
        {
            value = 0;
            if (Kind != JsonKind.Number || _text == null)
                return false;
            return NumberTools.TryParseInteger(_text, out value);
        }

        /// <summary>Number 텍스트를 배정밀도로 변환 시도(수동 파싱, '.' 구분자·지수 지원).</summary>
        public bool TryAsDouble(out double value)
        {
            value = 0.0;
            if (Kind != JsonKind.Number || _text == null)
                return false;
            return NumberTools.TryParseDouble(_text, out value);
        }

        /// <summary>Object에서 문자열 값(String 또는 Number의 텍스트)을 꺼냅니다.</summary>
        public bool TryGetString(string key, out string value)
        {
            value = null;
            JsonValue v = this[key];
            if (v == null)
                return false;
            value = v.AsString;
            return value != null || v.Kind == JsonKind.Null;
        }

        /// <summary>Object에서 불 값을 꺼냅니다.</summary>
        public bool TryGetBool(string key, out bool value)
        {
            value = false;
            JsonValue v = this[key];
            if (v == null || v.Kind != JsonKind.Bool)
                return false;
            value = v._flag;
            return true;
        }

        /// <summary>Object에서 정수 값을 꺼냅니다(Number만).</summary>
        public bool TryGetLong(string key, out long value)
        {
            value = 0;
            JsonValue v = this[key];
            if (v == null)
                return false;
            return v.TryAsLong(out value);
        }
    }

    /// <summary>
    /// 리플렉션 없는 최소 JSON 판독기입니다(JsonWriter의 대비 방향).
    /// DOM을 만들며 깊이는 <see cref="MaxDepth"/>까지. 문자열 하나를 입력으로 받습니다.
    /// </summary>
    /// <remarks>
    /// 사용: JsonValue root; string err;
    /// if (JsonReader.TryParse(body, out root, out err)) { long n; root.TryGetLong("count", out n); }
    /// </remarks>
    public static class JsonReader
    {
        /// <summary>중첩 상한(초과 시 오류). JsonWriter와 같은 값입니다.</summary>
        public const int MaxDepth = 8;

        /// <summary>解析 성공 시 루트 노드를 돌려줍니다. 실패 시 FormatException.</summary>
        public static JsonValue Parse(string json)
        {
            JsonValue value;
            string error;
            if (!TryParse(json, out value, out error))
                throw new FormatException(error);
            return value;
        }

        /// <summary>실패 원인을 문자열로 알리는 비예외 파싱 진입점입니다.</summary>
        public static bool TryParse(string json, out JsonValue value, out string error)
        {
            value = null;
            error = null;
            if (json == null)
            {
                error = "json is null";
                return false;
            }

            Parser p = new Parser(json);
            p.SkipWhitespace();
            string err = null;
            if (!p.ParseValue(0, out value, ref err))
            {
                error = err;
                return false;
            }
            p.SkipWhitespace();
            if (p.Position != json.Length)
            {
                error = "trailing text at " + p.Position.ToString();
                return false;
            }
            return true;
        }

        /// <summary>유효성만 판정합니다(DOM 보존 불필요한 검사용).</summary>
        public static bool IsValid(string json)
        {
            JsonValue value;
            string error;
            return TryParse(json, out value, out error);
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            internal Parser(string s)
            {
                _s = s;
                _i = 0;
            }

            internal int Position { get { return _i; } }

            internal void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                        return;
                    _i++;
                }
            }

            // value : object | array | string | number | true | false | null
            internal bool ParseValue(int depth, out JsonValue value, ref string error)
            {
                value = null;
                if (depth > MaxDepth)
                {
                    error = "depth";
                    return false;
                }

                if (_i >= _s.Length)
                {
                    error = "unexpected end";
                    return false;
                }

                char c = _s[_i];
                if (c == '{')
                    return ParseObject(depth, out value, ref error);
                if (c == '[')
                    return ParseArray(depth, out value, ref error);
                if (c == '"')
                {
                    string text;
                    if (!ParseString(out text, ref error))
                        return false;
                    value = new JsonValue(JsonKind.String, false, text, null, null);
                    return true;
                }
                if (c == 't')
                    return ParseLiteral("true", true, out value, ref error);
                if (c == 'f')
                    return ParseLiteral("false", false, out value, ref error);
                if (c == 'n')
                {
                    if (!MatchWord("null", ref error))
                        return false;
                    value = new JsonValue(JsonKind.Null, false, null, null, null);
                    return true;
                }
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    int start = _i;
                    while (_i < _s.Length)
                    {
                        char d = _s[_i];
                        if ((d >= '0' && d <= '9') || d == '-' || d == '+' || d == '.' || d == 'e' || d == 'E')
                        {
                            _i++;
                            continue;
                        }
                        break;
                    }
                    if (_i == start)
                    {
                        error = "bad value at " + start.ToString();
                        return false;
                    }
                    value = new JsonValue(JsonKind.Number, false, _s.Substring(start, _i - start), null, null);
                    return true;
                }

                error = "unexpected character at " + _i.ToString();
                return false;
            }

            private bool ParseObject(int depth, out JsonValue value, ref string error)
            {
                value = null;
                _i++; // {
                int memberCount = 0;
                string[] keys = new string[4];
                JsonValue[] vals = new JsonValue[4];

                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == '}')
                {
                    _i++;
                    value = new JsonValue(JsonKind.Object, false, null, Empty.Strings, Empty.Values);
                    return true;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != '"')
                    {
                        error = "expected key at " + _i.ToString();
                        return false;
                    }
                    string keyText;
                    if (!ParseString(out keyText, ref error))
                        return false;
                    if (memberCount == keys.Length)
                    {
                        keys = Grow(keys);
                        vals = Grow(vals);
                    }
                    keys[memberCount] = keyText;
                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != ':')
                    {
                        error = "expected ':' at " + _i.ToString();
                        return false;
                    }
                    _i++;
                    SkipWhitespace();
                    JsonValue v;
                    if (!ParseValue(depth + 1, out v, ref error))
                        return false;
                    vals[memberCount] = v;
                    memberCount++;

                    SkipWhitespace();
                    if (_i >= _s.Length)
                    {
                        error = "unterminated object";
                        return false;
                    }
                    if (_s[_i] == '}')
                    {
                        _i++;
                        string[] finalKeys = new string[memberCount];
                        JsonValue[] finalVals = new JsonValue[memberCount];
                        for (int i = 0; i < memberCount; i++)
                        {
                            finalKeys[i] = keys[i];
                            finalVals[i] = vals[i];
                        }
                        value = new JsonValue(JsonKind.Object, false, null, finalKeys, finalVals);
                        return true;
                    }
                    if (_s[_i] != ',')
                    {
                        error = "expected ',' or '}' at " + _i.ToString();
                        return false;
                    }
                    _i++;
                }
            }

            private bool ParseArray(int depth, out JsonValue value, ref string error)
            {
                value = null;
                _i++; // [
                JsonValue[] items = new JsonValue[4];
                int count = 0;

                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == ']')
                {
                    _i++;
                    value = new JsonValue(JsonKind.Array, false, null, null, Empty.Values);
                    return true;
                }

                while (true)
                {
                    SkipWhitespace();
                    JsonValue v;
                    if (!ParseValue(depth + 1, out v, ref error))
                        return false;
                    if (count == items.Length)
                        items = Grow(items);
                    items[count++] = v;

                    SkipWhitespace();
                    if (_i >= _s.Length)
                    {
                        error = "unterminated array";
                        return false;
                    }
                    if (_s[_i] == ']')
                    {
                        _i++;
                        JsonValue[] finalItems = new JsonValue[count];
                        for (int i = 0; i < count; i++)
                            finalItems[i] = items[i];
                        value = new JsonValue(JsonKind.Array, false, null, null, finalItems);
                        return true;
                    }
                    if (_s[_i] != ',')
                    {
                        error = "expected ',' or ']' at " + _i.ToString();
                        return false;
                    }
                    _i++;
                }
            }

            // "..." 이스케이프 해석. 조각 접합 비용 때문에 지연 StringBuilder를 씁니다.
            private bool ParseString(out string value, ref string error)
            {
                value = null;
                _i++; // 여는 따옴표
                int start = _i;
                StringBuilder builder = null;
                while (true)
                {
                    if (_i >= _s.Length)
                    {
                        error = "unterminated string";
                        return false;
                    }
                    char c = _s[_i];
                    if (c == '"')
                    {
                        _i++;
                        break;
                    }
                    if (c != '\\')
                    {
                        if (builder != null)
                            builder.Append(c);
                        _i++;
                        continue;
                    }

                    if (builder == null)
                    {
                        builder = new StringBuilder(_s.Length - _i + 8);
                        builder.Append(_s.Substring(start, _i - start));
                    }
                    _i++;
                    if (_i >= _s.Length)
                    {
                        error = "bad escape";
                        return false;
                    }
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            int code = 0;
                            for (int k = 0; k < 4; k++)
                            {
                                if (_i >= _s.Length)
                                {
                                    error = "bad \\u escape";
                                    return false;
                                }
                                int digit = HexValue(_s[_i++]);
                                if (digit < 0)
                                {
                                    error = "bad \\u escape";
                                    return false;
                                }
                                code = code * 16 + digit;
                            }
                            builder.Append((char)code);
                            break;
                        default:
                            error = "bad escape at " + (_i - 2).ToString();
                            return false;
                    }
                }

                value = builder == null ? _s.Substring(start, _i - 1 - start) : builder.ToString();
                return true;
            }

            private bool ParseLiteral(string word, bool flag, out JsonValue value, ref string error)
            {
                value = null;
                if (!MatchWord(word, ref error))
                    return false;
                value = new JsonValue(JsonKind.Bool, flag, null, null, null);
                return true;
            }

            private bool MatchWord(string word, ref string error)
            {
                if (_i + word.Length > _s.Length)
                {
                    error = "unexpected end";
                    return false;
                }
                for (int k = 0; k < word.Length; k++)
                {
                    if (_s[_i + k] != word[k])
                    {
                        error = "bad literal at " + _i.ToString();
                        return false;
                    }
                }
                _i += word.Length;
                return true;
            }

            private static int HexValue(char c)
            {
                if (c >= '0' && c <= '9')
                    return c - '0';
                if (c >= 'a' && c <= 'f')
                    return c - 'a' + 10;
                if (c >= 'A' && c <= 'F')
                    return c - 'A' + 10;
                return -1;
            }

            private static string[] Grow(string[] a)
            {
                string[] b = new string[a.Length * 2];
                for (int i = 0; i < a.Length; i++)
                    b[i] = a[i];
                return b;
            }

            private static JsonValue[] Grow(JsonValue[] a)
            {
                JsonValue[] b = new JsonValue[a.Length * 2];
                for (int i = 0; i < a.Length; i++)
                    b[i] = a[i];
                return b;
            }

            private static class Empty
            {
                internal static readonly string[] Strings = new string[0];
                internal static readonly JsonValue[] Values = new JsonValue[0];
            }
        }
    }

    /// <summary>
    /// double/int Parse 없이 숫자 텍스트를 수로 바꾸는 수동 변환기입니다
    /// (nanoFramework 파싱 편차 회피. 지수 표기 지원).
    /// </summary>
    internal static class NumberTools
    {
        internal static bool TryParseInteger(string text, out long value)
        {
            value = 0;
            if (text == null || text.Length == 0)
                return false;
            int i = 0;
            bool negative = false;
            if (text[0] == '-')
            {
                negative = true;
                i = 1;
            }
            if (i >= text.Length)
                return false;
            long acc = 0;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                    return false;
                acc = acc * 10 + (c - '0');
            }
            value = negative ? -acc : acc;
            return true;
        }

        internal static bool TryParseDouble(string text, out double value)
        {
            value = 0.0;
            if (text == null || text.Length == 0)
                return false;
            int i = 0;
            bool negative = false;
            if (text[i] == '-' || text[i] == '+')
            {
                negative = text[i] == '-';
                i++;
            }

            double acc = 0.0;
            bool any = false;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                    break;
                acc = acc * 10.0 + (c - '0');
                any = true;
            }
            if (!any)
                return false;

            int scale = 0;
            if (i < text.Length && text[i] == '.')
            {
                i++;
                bool fracAny = false;
                for (; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c < '0' || c > '9')
                        break;
                    acc = acc * 10.0 + (c - '0');
                    scale++;
                    fracAny = true;
                }
                if (!fracAny)
                    return false;
            }

            if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
            {
                i++;
                bool expNegative = false;
                if (i < text.Length && (text[i] == '-' || text[i] == '+'))
                {
                    expNegative = text[i] == '-';
                    i++;
                }
                int exp = 0;
                bool expAny = false;
                for (; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c < '0' || c > '9')
                        break;
                    exp = exp * 10 + (c - '0');
                    expAny = true;
                }
                if (!expAny || i != text.Length)
                    return false;
                for (int k = 0; k < exp; k++)
                {
                    if (expNegative)
                        acc /= 10.0;
                    else
                        acc *= 10.0;
                }
            }
            else if (i != text.Length)
            {
                return false;
            }

            for (int k = 0; k < scale; k++)
                acc /= 10.0;

            value = negative ? -acc : acc;
            return true;
        }
    }
}
