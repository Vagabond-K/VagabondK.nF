# Web

[전체 프로젝트](../../README.md) · [빌드·패키징·공통 검증](../../Build/README.md)

임베디드 웹 서버의 로그인·세션·접근 제어 계층과 빌드 타임 관리 화면 리소스 생성기를 제공합니다.
이 문서의 명령과 코드 경로는 저장소 루트 기준입니다.

## 라이브러리 선택

| 프로젝트 문서 | 종류 | 역할 |
|---|---|---|
| [VagabondK.nF.Web](VagabondK.nF.Web/README.md) | 런타임 | 관리자 비밀번호 해시 저장·검증, 메모리 세션·리프레시 토큰, 정적 API 키, 로그인 실패 잠금, `GuardedWebServer` fail-closed 인증, 표준 인증 API |
| [VagabondK.nF.WebUi](VagabondK.nF.WebUi/README.md) | 도구 | HTML·JS·CSS 조각과 다국어 resx를 빌드 타임에 조립·축소·gzip해 장치 이진 리소스로 임베드하는 MSBuild targets + 컴포넌트 라이브러리 |

브라우저 관리 화면을 만들려면 두 패키지를 함께 사용하고, 인증 API만 필요하면 `VagabondK.nF.Web` 단독으로도 충분합니다.
`VagabondK.nF.WebUi.Tasks`는 WebUi 생성기의 실제 동작(MSBuild 작업 어셈블리)으로, NuGet 패키지가 없어도 targets가 자동으로 빌드·실행합니다.

## 사용 예

```csharp
using VagabondK.nF.Web;

var guard = new LoginGuard();
guard.SetPassword("s3cret!");            // 저장되는 것은 해시 문자열뿐
string token;
if (guard.TryLogin(input, out token) == LoginResult.Success) { /* 쿠키에 token 설정 */ }

// 요청 처리 시: 쿠키/헤더에서 추출한 값으로 판정
var decision = guard.CheckAccess(sessionFromCookie, apiKeyFromHeader);
```

비밀번호 해시(`PasswordHash`)와 `ApiKey`의 파일 영속화는 사용하는 앱의 책임이며,
`CredentialFile.Load/Save`(`h=`/`k=` 두 줄 형식)로 한 줄에 동기화할 수 있습니다.
런타임 폴더의 `.nfproj`는 nanoFramework DLL을, 같은 폴더의 `.csproj`는 PC 검증용 빌드를 만듭니다.
