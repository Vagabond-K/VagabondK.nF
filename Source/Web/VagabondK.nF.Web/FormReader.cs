using System;
using System.Net;
using System.Text;
using nanoFramework.WebServer;

namespace VagabondK.nF.Web
{
    /// <summary>
    /// urlencoded 요청 본문을 <b>1회만</b> 읽고 여러 필드를 파싱하는 리더입니다.
    /// </summary>
    /// <remarks>
    /// 요청 본문 스트림은 소진되면 다시 읽을 수 없고, 두 번째 읽기는 타임아웃 없이
    /// 무한정 블로킹될 수 있습니다(장치가 응답 불가 상태로 멈춤). 따라서 필드가 여럿
    /// 필요한 핸들러는 반드시 이 클래스로 본문을 한 번만 읽어 사용하세요.
    /// percent 인코딩(%XX, +)과 UTF-8 유효성 검사를 수행하며 잘못된 인코딩은
    /// <see langword="null"/>을 반환합니다.
    /// </remarks>
    /// <example>
    /// <code>
    /// FormReader form = new FormReader(request, 768);
    /// string grant = form.Get("grant_type");
    /// string password = form.Get("password"); // 본문을 다시 읽지 않음
    /// </code>
    /// </example>
    public sealed class FormReader
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding();

        private readonly byte[] body;
        private readonly byte[] scratch;

        /// <summary>요청 본문을 즉시 1회 읽어 생성합니다.</summary>
        /// <param name="request">본문을 읽을 요청입니다.</param>
        /// <param name="maxBytes">허용하는 최대 본문 크기입니다. 초과하면 본문 없음으로 취급됩니다.</param>
        public FormReader(HttpListenerRequest request, int maxBytes)
        {
            body = request == null ? null : request.ReadBody(maxBytes);
            if (body != null && body.Length > 0)
                scratch = new byte[body.Length];
        }

        /// <summary>본문이 존재하고 비어 있지 않은지 나타냅니다.</summary>
        public bool HasBody
        {
            get { return body != null && body.Length > 0; }
        }

        /// <summary>지정 이름의 필드 값을 디코딩해 반환합니다. 없으면 <see langword="null"/>입니다.</summary>
        public string Get(string name)
        {
            if (body == null || name == null)
                return null;

            int start = 0;
            while (start < body.Length)
            {
                int end = start;
                while (end < body.Length && body[end] != (byte)'&')
                    end++;

                int equals = start;
                while (equals < end && body[equals] != (byte)'=')
                    equals++;

                if (equals > start)
                {
                    string key = Decode(start, equals);
                    if (key == name)
                        return Decode(equals + 1, end);
                }

                start = end + 1;
            }

            return null;
        }

        /// <summary>본문을 한 번 읽어 단일 필드를 반환합니다(필드가 하나일 때 편리 메서드).</summary>
        public static string Read(HttpListenerRequest request, int maxBytes, string name)
        {
            return new FormReader(request, maxBytes).Get(name);
        }

        private string Decode(int start, int end)
        {
            int count = 0;
            for (int i = start; i < end; i++)
            {
                int value = body[i];
                if (value == (byte)'+')
                {
                    value = ' ';
                }
                else if (value == (byte)'%')
                {
                    if (i + 2 >= end)
                        return null;
                    int high = HexValue(body[i + 1]);
                    int low = HexValue(body[i + 2]);
                    if (high < 0 || low < 0)
                        return null;
                    value = (high << 4) | low;
                    i += 2;
                }

                if (count == scratch.Length)
                    return null;
                scratch[count++] = (byte)value;
            }

            if (count == 0)
                return string.Empty;
            if (!IsValidUtf8(scratch, count))
                return null;
            return Utf8.GetString(scratch, 0, count);
        }

        private static int HexValue(byte value)
        {
            if (value >= (byte)'0' && value <= (byte)'9')
                return value - '0';
            if (value >= (byte)'a' && value <= (byte)'f')
                return value - 'a' + 10;
            if (value >= (byte)'A' && value <= (byte)'F')
                return value - 'A' + 10;
            return -1;
        }

        private static bool IsValidUtf8(byte[] data, int length)
        {
            int i = 0;
            while (i < length)
            {
                byte b = data[i];
                int extra;
                int code;

                if (b < 0x80)
                {
                    i++;
                    continue;
                }
                else if (b >= 0xC2 && b <= 0xDF)
                {
                    extra = 1;
                    code = b & 0x1F;
                }
                else if (b >= 0xE0 && b <= 0xEF)
                {
                    extra = 2;
                    code = b & 0x0F;
                }
                else if (b >= 0xF0 && b <= 0xF4)
                {
                    extra = 3;
                    code = b & 0x07;
                }
                else
                {
                    return false;
                }

                if (i + extra >= length)
                    return false;

                for (int n = 1; n <= extra; n++)
                {
                    byte c = data[i + n];
                    if (c < 0x80 || c > 0xBF)
                        return false;
                    code = (code << 6) | (c & 0x3F);
                }

                // 과잉 인코딩·서로게이트·범위 초과 거부
                if (extra == 1 && code < 0x80) return false;
                if (extra == 2 && code < 0x800) return false;
                if (extra == 3 && (code < 0x10000 || code > 0x10FFFF)) return false;
                if (code >= 0xD800 && code <= 0xDFFF) return false;

                i += extra + 1;
            }

            return true;
        }
    }
}