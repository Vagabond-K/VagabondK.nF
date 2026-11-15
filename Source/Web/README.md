# Web

[전체 프로젝트](../../README.md) · [빌드·패키징·공통 검증](../../Build/README.md)

임베디드 웹 서버의 로그인·세션·접근 제어 계층을 제공합니다.
이 문서의 명령과 코드 경로는 저장소 루트 기준입니다.

## 라이브러리 선택

| 프로젝트 문서 | 역할 |
|---|---|
| [VagabondK.nF.Web](VagabondK.nF.Web/README.md) | 관리자 비밀번호 해시 저장·검증, 메모리 세션, 정적 API 키, 로그인 실패 잠금 |

런타임 폴더의 `.nfproj`는 nanoFramework DLL을, 같은 폴더의 `.csproj`는 PC 검증용 빌드를 만듭니다.

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

비밀번호 해시(`PasswordHash`)와 `ApiKey`의 파일 영속화는 사용하는 앱의 책임입니다.
