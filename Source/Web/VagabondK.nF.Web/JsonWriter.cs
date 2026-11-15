using System;
using System.Text;

namespace VagabondK.nF.Web
{
    /// <summary>
    /// 리플렉션 없는 수동 JSON 생성기입니다. 키와 값을 순서대로 적어 넣으면
    /// 따옴표·이스케이프·쉼표·중첩을 대신 관리합니다(자동 직렬화기는 nanoFramework
    /// 힙/반사 제약 때문에 의도적으로 제공하지 않습니다).
    /// </summary>
    /// <remarks>
    /// 사용: new JsonWriter().BeginObject().Prop("ok", true).Prop("name", s).EndObject().ToString().
    /// Prop/Begin은 체이닝, 값은 문자열·bool·정수 오버로드와 Raw(이미 JSON인 조각)를 지원합니다.
    /// </remarks>
    public sealed class JsonWriter
    {
        private const int MaxDepth = 8;

        private readonly StringBuilder _sb;
        private int _depth;
        // 0=컨테이너 막 열림, 1=요소 하나 완료(다음은 콤마), 2=키 적고 값 대기(콤마 금지)
        private int _last;

        /// <summary>기본 용량(96자)으로 생성합니다.</summary>
        public JsonWriter()
        {
            _sb = new StringBuilder(96);
        }

        /// <summary>지정 용량으로 생성합니다(큰 응답은 미리 잡아 재할당을 줄임).</summary>
        public JsonWriter(int capacity)
        {
            _sb = new StringBuilder(capacity);
        }

        /// <summary>현재까지 완성된 JSON 문자열입니다.</summary>
        public override string ToString()
        {
            return _sb.ToString();
        }

        /// <summary>"이름": 을 씁니다. 값 메서드(Str/Num/Bool/Raw/Begin*)가 뒤따라야 합니다.</summary>
        public JsonWriter Prop(string name)
        {
            Sep();
            Quote(name);
            _sb.Append(':');
            _last = 2;
            return this;
        }

        /// <summary>"key":"escaped-value" 한 쌍을 한 번에.</summary>
        public JsonWriter Prop(string name, string value)
        {
            return Prop(name).Str(value);
        }

        /// <summary>"key":true|false 한 쌍을 한 번에.</summary>
        public JsonWriter Prop(string name, bool value)
        {
            return Prop(name).Bool(value);
        }

        /// <summary>"key":숫자 한 쌍을 한 번에.</summary>
        public JsonWriter Prop(string name, long value)
        {
            return Prop(name).Num(value);
        }

        /// <summary>"key":숫자 한 쌍을 한 번에.</summary>
        public JsonWriter Prop(string name, int value)
        {
            return Prop(name).Num(value);
        }

        /// <summary>"key":생 JSON — 이미 JSON인 조각(배열 본문 등)을 키와 함께 병합합니다.</summary>
        public JsonWriter PropRaw(string name, string json)
        {
            return Prop(name).Raw(json);
        }

        /// <summary>{ 를 열고 객체 컨텍스트로 진입합니다.</summary>
        public JsonWriter BeginObject()
        {
            Sep();
            _sb.Append('{');
            Push();
            return this;
        }

        /// <summary>} 로 객체를 닫습니다.</summary>
        public JsonWriter EndObject()
        {
            Pop();
            _sb.Append('}');
            _last = 1;
            return this;
        }

        /// <summary>[ 를 열고 배열 컨텍스트로 진입합니다.</summary>
        public JsonWriter BeginArray()
        {
            Sep();
            _sb.Append('[');
            Push();
            return this;
        }

        /// <summary>] 로 배열을 닫습니다.</summary>
        public JsonWriter EndArray()
        {
            Pop();
            _sb.Append(']');
            _last = 1;
            return this;
        }

        /// <summary>문자열 값("..."). null/빈 문자열은 빈 문자열로 나갑니다.</summary>
        public JsonWriter Str(string value)
        {
            Sep();
            Quote(value);
            _last = 1;
            return this;
        }

        /// <summary>true/false 값.</summary>
        public JsonWriter Bool(bool value)
        {
            Sep();
            _sb.Append(value ? "true" : "false");
            _last = 1;
            return this;
        }

        /// <summary>숫자 값. 문자열이 아니라 JSON 숫자로 나갑니다.</summary>
        public JsonWriter Num(long value)
        {
            Sep();
            _sb.Append(value.ToString());
            _last = 1;
            return this;
        }

        /// <summary>null 값.</summary>
        public JsonWriter Null()
        {
            Sep();
            _sb.Append("null");
            _last = 1;
            return this;
        }

        /// <summary>JSON으로 이미 유효한 조각을 그대로 삽입(호출자가 책임).</summary>
        public JsonWriter Raw(string json)
        {
            Sep();
            _sb.Append(json);
            _last = 1;
            return this;
        }

        /// <summary>"key":["a","b"] 문자열 배열을 한 번에.</summary>
        public JsonWriter PropArr(string name, string[] items)
        {
            Prop(name);
            BeginArray();
            if (items != null)
                for (int i = 0; i < items.Length; i++)
                    Str(items[i]);
            EndArray();
            return this;
        }

        private void Sep()
        {
            if (_last == 1)
                _sb.Append(',');
        }

        private void Quote(string value)
        {
            _sb.Append('"');
            if (value != null && value.Length > 0)
                _sb.Append(WebApi.JsonEscape(value));
            _sb.Append('"');
        }

        private void Push()
        {
            if (_depth >= MaxDepth)
                throw new InvalidOperationException("JsonWriter depth");
            _depth++;
            _last = 0;
        }

        private void Pop()
        {
            _depth--;
        }
    }
}
