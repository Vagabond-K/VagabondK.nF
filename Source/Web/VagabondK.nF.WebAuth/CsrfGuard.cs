using System;
using System.Net;

namespace VagabondK.nF.WebAuth
{
    /// <summary>
    /// 사이트 간 요청 위조(CSRF) 차단 검사입니다.
    /// </summary>
    /// <remarks>
    /// 두 겹의 방어: ① 지정 커스텀 헤더(값 "1")를 요구 — 교차 사이트 폼은 CORS
    /// 사전검사를 통과해야 헤더를 붙일 수 있지만 본 서버가 사전검사를 승인하지 않으면
    /// 차단됩니다. ② Host가 장치 자기 주소와 일치해야 합니다(DNS rebinding 방지).
    /// Origin 헤더가 있으면 Host와 동일해야 합니다.
    /// </remarks>
    public static class CsrfGuard
    {
        /// <summary>차단 시 클라이언트에 알릴 메시지입니다(현지화 용도, 기본 영어).</summary>
        public static string BlockedMessage = "cross-site request blocked.";

        /// <summary>
        /// 요청을 검사하고 차단 대상이면 403 응답을 기록합니다.
        /// </summary>
        /// <param name="context">검사할 요청 컨텍스트입니다.</param>
        /// <param name="markerHeader">요구할 커스텀 헤더 이름(예: "x-nanogate")입니다.</param>
        /// <param name="localAddress">현재 장치 IP 문자열(예: "192.168.0.187")입니다.</param>
        /// <returns>차단되었으면 <see langword="true"/> — 핸들러는 즉시 반환해야 합니다.</returns>
        public static bool Check(HttpListenerContext context, string markerHeader, string localAddress)
        {
            string host = context.Request.Headers["host"];
            string origin = context.Request.Headers["origin"];

            if (context.Request.Headers[markerHeader] != "1" ||
                host == null ||
                StripPort(host) != localAddress ||
                (origin != null && origin != "http://" + host))
            {
                // 본문을 소비하지 않고 남기면 keep-alive 연결이 어긋나 다음 요청이
                // 다른 핸들러로 새는 사고가 생길 수 있어 Reject로 함께 닫는다.
                WebApi.Reject(context.Request, context.Response, 403, BlockedMessage, 2048);
                return true;
            }

            return false;
        }

        private static string StripPort(string host)
        {
            int colon = host.IndexOf(':');
            return colon < 0 ? host : host.Substring(0, colon);
        }
    }
}