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

        /// <summary>
        /// 거부 응답(401/403/429)을 쓰기 전에 요청 본문을 소비하고 연결을 닫습니다.
        /// 본문을 스트림에 남기면 keep-alive 연결의 다음 요청이 어긋나서(phantom request)
        /// 전혀 다른 핸들러가 그 본문을 받아들이는 사고가 생길 수 있습니다.
        /// 본문이 상한을 넘으면 읽지 않고 연결만 닫습니다.
        /// </summary>
        public static void Reject(HttpListenerRequest request, HttpListenerResponse response, int status, string message, int maxBodyBytes)
        {
            // ReadBody는 ContentLength만큼 정확히 읽는다. 고정 버퍼로 InputStream.Read를
            // 호출하면 nanoFramework 스트림이 버퍼 채울 데이터를 기다리며 막힐 수 있다.
            try
            {
                request.ReadBody(maxBodyBytes);
            }
            catch
            {
            }

            try
            {
                response.KeepAlive = false;
            }
            catch
            {
            }

            // 죽은 연결에 쓰면 예외가 서버 루프를 종료시킬 수 있어 삼킨다.
            try
            {
                Error(response, status, message);
            }
            catch
            {
            }
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