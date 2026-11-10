using System;
using System.Net;
using System.Text;
using nanoFramework.WebServer;

namespace VagabondK.nF.WebAuth
{
    /// <summary>
    /// <see cref="HttpListener"/> 응답용 JSON/텍스트/스트리밍 헬퍼입니다.
    /// 모든 응답에 표준 보안 헤더(노캐시·nosniff·frame 거부·노레퍼러)를 붙입니다.
    /// </summary>
    /// <remarks>
    /// 작은 페이로드(<see cref="Json"/>·<see cref="Error"/>)는 문자열을 완성해
    /// Content-Length를 설정하고 단일 쓰기로 내보냅니다. 큰 페이로드는
    /// <see cref="BeginStream"/>으로 헤더만 먼저 설정하고 호출자가 OutputStream에
    /// 직접 스트리밍하세요(첫 쓰기 이후에는 상태·헤더 변경 불가).
    /// </remarks>
    public static class WebApi
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding();

        /// <summary>"application/json; charset=utf-8"입니다.</summary>
        public const string JsonType = "application/json; charset=utf-8";
        /// <summary>"text/plain; charset=utf-8"입니다.</summary>
        public const string TextType = "text/plain; charset=utf-8";
        /// <summary>성공 응답 본문 <c>{"ok":true}</c>입니다.</summary>
        public const string OkJson = "{\"ok\":true}";

        /// <summary>JSON 본문을 단일 쓰기로 기록합니다.</summary>
        public static void Json(HttpListenerResponse response, int status, string body)
        {
            Write(response, status, JsonType, body);
        }

        /// <summary>텍스트 본문을 단일 쓰기로 기록합니다.</summary>
        public static void Text(HttpListenerResponse response, int status, string contentType, string body)
        {
            Write(response, status, contentType, body);
        }

        /// <summary>{"ok":false,"error":"..."} 형식의 오류 응답을 기록합니다.</summary>
        public static void Error(HttpListenerResponse response, int status, string message)
        {
            Json(response, status, "{\"ok\":false,\"error\":\"" + JsonEscape(message) + "\"}");
        }

        /// <summary>스트리밍 응답의 헤더만 설정합니다. 호출 직후 OutputStream에 직접 쓰세요.</summary>
        public static void BeginStream(HttpListenerResponse response, int status, string contentType, long length)
        {
            AddApiHeaders(response);
            response.StatusCode = status;
            response.ContentType = contentType;
            response.ContentLength64 = length;
        }

        /// <summary>요청 본문을 최대 크기 안에서 1회 읽습니다. 없거나 초과하면 null입니다.</summary>
        public static byte[] ReadBody(HttpListenerRequest request, int maxBytes)
        {
            return request.ReadBody(maxBytes);
        }

        /// <summary>본문을 ASCII 문자열로 읽습니다(0x80 이상은 '?'). 본문이 없으면 null입니다.</summary>
        public static string ReadBodyAsAscii(HttpListenerRequest request, int maxBytes)
        {
            byte[] body = ReadBody(request, maxBytes);
            if (body == null || body.Length == 0)
                return null;

            char[] characters = new char[body.Length];
            for (int i = 0; i < body.Length; i++)
            {
                byte value = body[i];
                characters[i] = value <= 0x7F ? (char)value : '?';
            }

            return new string(characters);
        }

        /// <summary>문자열을 JSON 문자 값으로 안전하게 만듭니다(따옴표·역슬래시 이스케이프, 제어문자 공백 치환).</summary>
        public static string JsonEscape(string value)
        {
            if (value == null || value.Length == 0)
                return string.Empty;

            StringBuilder builder = null;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '"' && c != '\\' && c >= ' ')
                {
                    if (builder != null)
                        builder.Append(c);
                    continue;
                }

                if (builder == null)
                {
                    builder = new StringBuilder(value.Length + 8);
                    builder.Append(value.Substring(0, i));
                }

                if (c == '"' || c == '\\')
                {
                    builder.Append('\\');
                    builder.Append(c);
                }
                else
                {
                    builder.Append(' ');
                }
            }

            return builder == null ? value : builder.ToString();
        }

        private static void Write(HttpListenerResponse response, int status, string contentType, string body)
        {
            byte[] bytes = Utf8.GetBytes(body);
            AddApiHeaders(response);
            response.StatusCode = status;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            if (bytes.Length > 0)
                response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        private static void AddApiHeaders(HttpListenerResponse response)
        {
            response.Headers.Add("Cache-Control", "no-store");
            response.Headers.Add("X-Content-Type-Options", "nosniff");
            response.Headers.Add("X-Frame-Options", "DENY");
            response.Headers.Add("Referrer-Policy", "no-referrer");
        }
    }
}