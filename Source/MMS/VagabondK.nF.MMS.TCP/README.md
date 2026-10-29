# MMS TCP 채널

`VagabondK.nF.MMS`와 nanoFramework System.Net을 참조하는 `MmsSocketChannel` 어댑터입니다.
`VagabondK.nF.MMS.TCP` 네임스페이스를 사용합니다.
일반 MMS와 IEC 61850 양쪽에서 같은 DLL을 사용하며 IEC DLL에 의존하지 않습니다.

호출자가 연결된 소켓을 전달하고 finally에서 해제합니다.
채널은 소켓 연결·수락·재접속을 하지 않으며 Poll·Read·Write에 남은 시간을 적용합니다.
모든 접근은 호출자가 직렬화해야 합니다. 채널은 소켓의 SendTimeout/ReceiveTimeout을 변경합니다.
`IMmsTimeoutChannel`도 구현하며 `TryRead`/`TryWrite`는 준비 대기 시간 초과만
false로 반환합니다. `TryRead`의 true·transferred=0은 EOF이며 시간 초과가 아닙니다.
소켓 예외는 여전히 전파하고 하드 실시간 반환 시각을 보장하지 않습니다.
기존 `Read`/`Write`는 준비 대기 만료 시 `TimeoutException`을 발생시킵니다.
TPKT/COTP/Session/ACSE 코덱은 MMS core, 세션 상태와 처리는 각 Client/Server DLL이 담당합니다.
이 채널은 Client/Server DLL을 참조하지 않습니다.
