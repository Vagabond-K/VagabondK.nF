using System;

namespace VagabondK.nF.Web
{
    /// <summary>
    /// 인증이 활성화된 상태에서도 인증 없이 접근할 수 있는 처리 메서드를 표시합니다
    /// (ASP.NET Core의 AllowAnonymous에 해당합니다).
    /// </summary>
    /// <remarks>
    /// 이 특성은 라이브러리 자체가 아니라 라우터가 해석하는 마커입니다
    /// (해석 구현체: 이 어셈블리의 <see cref="GuardedWebServer"/>). 기본 정책은 fail-closed로,
    /// 인증이 켜진 상태에서 특성이 없는 처리 메서드는 모두 인증을 요구합니다.
    /// 로그인·상태 조회·정적 페이지처럼 인증 화면 자체에 필요한 엔드포인트에만 붙입니다.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class PublicEndpointAttribute : Attribute
    {
    }
}
