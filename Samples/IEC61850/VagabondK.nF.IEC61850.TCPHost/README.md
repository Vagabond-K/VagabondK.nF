# IEC 61850 MMS TCP Server 예제

저장된 네트워크 설정으로 접속한 nanoFramework 보드에서 TCP 18102를 사용합니다.
IP 주소 준비는 애플리케이션/펌웨어 설정에서 완료해야 합니다. 한 번에 연결 하나만 처리합니다.
실제 IED·출력 장치·표준 적합성 시험을 대체하지 않는 로컬 평가 예제입니다.

- domain: `NFTESTLD0`
- 값: `GGIO1$ST$Count$stVal`(INT32), `$q`(Quality), `$t`(UTC)
- DataSet: `GGIO1$Values`
- URCB: `GGIO1$RP$Events`; 비활성 상태로 시작하며 Client가 예약·활성화합니다.

500ms마다 Count가 증가하고 dchg/GI/1초 integrity를 제공합니다.
UTC의 기본 시간 품질은 **동기화되지 않음**입니다. 이 예제는 시계 동기화를 수행하지 않습니다.
쓰기·설비 제어는 등록하지 않으며 네트워크/프로토콜 오류는 디버그 출력 후 연결을 폐기합니다.
연결 종료 시 URCB 소유권을 해제합니다. 보고 큐/버퍼와 자동 재전송은 없습니다.

```powershell
MSBuild Samples\IEC61850\VagabondK.nF.IEC61850.TCPHost\VagabondK.nF.IEC61850.TCPHost.nfproj /t:Rebuild /p:Configuration=Release
```

로컬 NuGet 사용 시 MMS 코어·Server·TCP와 IEC 코어·Server 패키지 `0.1.0-preview.1`로 ProjectReference를 대체하십시오.
MMS.Client·IEC61850.Client DLL은 필요하지 않습니다.
`Build\Test-Packages.ps1 -LibrarySet IEC61850`은 저장소 소스 참조 없이
각 역할의 패키지 소비자 이미지를 생성합니다. 원본 libiec61850 검증 피어는 제품/패키지에 포함하지 않습니다.

## 실물 손상 입력·복구 시험

샘플 앱과 별도로 메모리 모델 전용 시험기를 격리된 COM3 평가 보드에 배포합니다.
시험기는 TCP 18103, 진단 UDP 20004를 사용합니다. 실행 종료 시 성공·실패 모두 원래
Modbus RTU 이미지를 복원하고 COM6 읽기까지 확인합니다.

```powershell
.\Build\Test-Iec61850ServerHardware.ps1 -AllowDeviceChanges -MalformedRecovery
```

손상 입력 13종은 TPKT 버전·예약 필드 오류, 짧거나 초과한 길이, 부분 헤더 뒤 EOF,
잘못된 COTP CR, 잘린 Association, 길이가 확정되지 않은 BER, 연결 수립 뒤 잘못된 TPKT/COTP,
부분 프레임 뒤 EOF, 스칼라·Operate 쓰기의 중복 값 목록입니다.
각 입력에 대해 Faulted 상태 진입·Associated 해제·후속 호출 거절·제어 처리기 미호출을 요구합니다.
손상 전에 설정한 스칼라 값은 복구 직전에 덮어쓰지 않고 원본 libiec61850 Client로 확인합니다.
각 경우 뒤 새 Association에서 정상 탐색·읽기·쓰기·제어를 수행합니다.

추가 1종은 PC Client가 MCU의 Operate 응답을 완전히 수신한 뒤 로컬 채널에서
폐기하여 시간 초과를 발생시킵니다. 서버 실행 정확히 1회, PC Operate 송신 1회,
Faulted 뒤 재호출 송신 0회를 확인합니다. 물리 네트워크 유실 시험이나 MCU Client의
미재실행 검증, 분산 exactly-once 보장은 아닙니다.
손상 입력에는 저장소 코덱을 사용하고 정상 복구에는 독립 원본 피어를 사용합니다.

2026-10-28 결과: `bin\iec61850-server-malformed-20261028-0110`에서
1,493개 검증, 손상 13종 + 응답 폐기 1종, 독립 정상 Association 14회/요청 1,344개,
제어 검증·실행 각각 29회를 통과했습니다. 첫 정상 순회 이후 GC 후 가용 힙은
730,140 B로 유지됐고 원래 Modbus 복원·COM6 읽기도 성공했습니다.
`-MalformedRecovery`를 생략하면 기존 전체 Server 시험을 실행합니다.
역할별 남은 범위와 선행 실패 기록은 `Tests\FaultRecovery.md`를 참조하십시오.
