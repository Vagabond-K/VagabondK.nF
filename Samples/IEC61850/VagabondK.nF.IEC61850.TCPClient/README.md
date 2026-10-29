# MMS TCP Client와 안전한 재접속 예제

IEC 코어·Client와 MMS 코어·Client·TCP를 참조하는 nanoFramework 예제입니다. 두 Server DLL은 필요하지 않습니다.
Client API의 네임스페이스는 `VagabondK.nF.IEC61850.Client`입니다.
저장된 네트워크 설정이 준비된 보드에서 `Program.cs`의 PC IPv4/포트를 수정해 사용합니다.
기본 피어는 원본 libiec61850 `dataserver` 가상 IED입니다.

```powershell
.\bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe dataserver 18102 192.168.0.210
MSBuild Samples\IEC61850\VagabondK.nF.IEC61850.TCPClient\VagabondK.nF.IEC61850.TCPClient.nfproj /t:Rebuild /p:Configuration=Release
```

가상 IED는 `READY` 후 대기하고 표준 입력에 `quit`를 입력하면 종료합니다.
동일 포트를 사용하는 다른 시험과 동시에 실행하지 마십시오.
원본 피어의 GPL/상용 라이선스 경계는 [Client README](../../../Source/IEC61850/VagabondK.nF.IEC61850.Client/README.md)를 따르며 예제/런타임 패키지에 포함하지 않습니다.

`MonitorSession`은 도메인·DataSet 페이지 탐색(16개 이름×최대 16페이지),
정적 DataSet 디렉터리/값 조회, 비활성 URCB 예약·활성화,
GI 세 번과 보고 콜백, 비활성화·예약 해제·정상 Release를 수행합니다.
서버 구성/접근 오류는 성공처럼 숨기지 않습니다.
기본 이름은 `NFTESTLD0`, `GGIO1$Measurements`, `GGIO1$RP$Events`이며 피어 모델과 일치해야 합니다.
연속 구독 서비스가 아닌 **한 번의 유한한 구독 흐름**입니다.

**제어는 기본 비활성**(`controlItem=null`)입니다.
직접 구성한 격리된 Boolean 메모리 제어에 대해서만 CO 경로를 지정하십시오.
원본 `dataserver`에는 제어 객체가 없으므로 제어 경로를 그대로 추가하면 안 됩니다.
제어를 지정해도 기본은 `Test=true`이며 실제 출력 요청을 하지 않습니다.
`MonitorSession.Run(..., controlItem, test: false)`는 별도로 명시해야 합니다.
Direct/SBO normal/enhanced 모델을 조회해 적합한 선택·실행 API를 사용합니다.
실제 설비용 인터록·권한·UTC 동기화·피드백 정책은 제공하지 않습니다.

통신 실패 시 Socket/Client/보고 객체를 버리고 최대 5회 연결합니다.
재시도 간격은 1/2/4/8초이며 설정/서비스 오류는 자동 재시도하지 않습니다.
Socket.Connect의 소요 시간 자체는 운영체제 정책에 따르며 하드 실시간 기한을 보장하지 않습니다.
동일 `MonitorSession`을 재사용해 **제어 시도 여부는 연결을 넘어 유지**합니다.
제어 요청 전에 플래그를 설정하므로 응답·완료가 유실돼도 제어를 다시 보내지 않습니다.
소켓 복구나 상태 Read는 이전 제어의 실행 여부를 확정하거나 취소하지 않습니다.
앱 재부팅을 넘는 영속 실행 기록은 없으며 실제 출력에는 별도의 운영자 확인이 필요합니다.

PC 시험은 바로 이 `MonitorSession.cs`를 링크하여 페이지 탐색·보고·Test 무변경,
제어 응답 유실 뒤 재접속에서 실행 횟수 1회 유지,
원본 가상 IED 종료/재시작 후 재구독을 검사합니다.
MCU 빌드와 공유 세션 로직의 PC 상호운용은 구분하며, 이 예제 앱 자체의 보드 배포를 뜻하지 않습니다.

## MCU Client 실물 손상 응답·복구 시험

다음 명령은 예제 앱이 아닌 메모리 모델 전용 Client 시험기를 격리된 COM3 평가 보드에
배포합니다. PC 손상 응답 피어와 원본 libiec61850 정상 피어가 같은 LAN TCP 18102를
번갈아 사용하므로 다른 시험과 동시에 실행하지 마십시오. `-UseTryRead`는 정상 복구
순회에서 접근 오류를 예외 대신 반환값으로 확인하며, 손상 응답의 예외를 숨기지 않습니다.

```powershell
.\Build\Test-Iec61850Hardware.ps1 -AllowDeviceChanges -MalformedRecovery -UseTryRead
```

손상 12종은 COTP CC의 TPKT 버전·예약 필드 오류, 짧거나 초과한 길이, 잘못된 TPDU 형식, 부분 헤더 뒤 EOF,
잘린 Association 응답, 정상 연결 뒤 Read 응답의 잘못된 TPKT/COTP, 길이가 확정되지 않은 BER,
부분 프레임 뒤 EOF, invoke ID 불일치입니다.
각 경우 MCU의 정확한 프로토콜·I/O 예외, Faulted 상태 진입·Associated 해제,
후속 Read 거절과 추가 송신 없음을 확인합니다. 매 장애 뒤 Socket/Client를 새로 만들고
원본 libiec61850에서 탐색·복합 값·읽기·쓰기와 접근·서비스 오류 복구를 확인합니다.

추가 1종은 PC 메모리 서버가 Operate를 실제 실행한 뒤 생성한 정상 응답을 **송신 전에**
폐기합니다. MCU의 실제 소켓 대기 기한, Operate 송신 정확히 1회, Faulted 뒤 재호출
추가 송신 0회와 PC 서버 검증·실행 각각 1회를 확인합니다. `fault-12.bin`은 폐기한
응답이며 실제 송신 캡처가 아닙니다. `fault-*.bin.requests`는 PC에서 수신한 요청입니다.
손상 응답 피어는 저장소 Server 코덱을 사용하고, 정상 복구는 독립 원본 피어로 검사합니다.
물리 패킷 유실·재부팅을 넘는 중복 제거·재접속 후 앱의 제어 정책·exactly-once 보장은 아닙니다.

2026-10-28 결과 `bin\iec61850-client-malformed-20261028-0400`:
1,726개 검증, 손상 12종/응답 폐기 1종, 정상 15순회(초기 2회 + 장애 뒤 13회),
스칼라 값 900개/접근 오류 300개 확인을 완료했습니다. 기준 측정 이후 장애·복구 표본의 GC 후 가용
힙은 759,240 B로 유지됐고 원래 Modbus 복원·COM6 읽기도 성공했습니다.
응답 폐기에서 읽기 준비 대기는 2,990ms, 예외가 시험 코드에 반환되기까지는 7,510ms였습니다.
이는 기존 예외 API의 기록입니다. 소켓 대기 기한과 예외 경로의 추가 시간을 구분하며
3초 내 API 반환을 보장하지 않습니다.

기존 전체 Client 시험도 `bin\iec61850-client-regression-20261028-0410`에서
시간 초과·피어 종료 후 복구와 control/enhanced/SBO enhanced/data-report 각각 3순회를
완료하고 Modbus를 복원했습니다. `-MalformedRecovery`를 생략하면 이 기존 흐름을 실행합니다.
현재 시험기의 진단 헤더는 `77,77,72,58`이며 기본 응답은 측정값 48개를 담은 208바이트입니다.
실행기와 같은 버전을 사용해야 합니다. 위 결과의 MMH9 형식과 구분합니다.

### 시간 초과 예외와 반환 상태의 실물 비교

```powershell
.\Build\Test-Iec61850Hardware.ps1 -AllowDeviceChanges -TimeoutComparison -UseTryRead
```

동일 ESP32-S3에서 Read/Operate 각각 예외 방식과 반환 방식으로 3회씩 실행합니다.
`-MalformedRecovery`와 동시에 지정할 수 없습니다.
Read는 PC 서버가 생성한 응답, Operate는 실행 후 생성한 응답을 송신 전에 폐기합니다.
모든 경우 새 Socket/Client이며 각 장애 뒤 원본 libiec61850 Server를 재시작해
정상 탐색·읽기·쓰기를 확인합니다.
반환 방식은 `TryRead(..., out errorCode, out timedOut)`와
`TryOperate(..., out errorCode, out timedOut)`이며 소켓부터 전체 경로에서 시간 초과 예외를 생성하지 않습니다.
Faulted·Associated 해제·후속 재호출 무송신 및 PC Operate 실행 정확히 1회도 검사합니다.

최종 소스의 `bin\iec61850-timeout-comparison-final-20261028` 결과
(`comparison.json`에 방식별 3회 평균/최소/최대 저장):

| 호출 | 기존 예외 방식 평균 (범위) | 시간 초과 반환 방식 평균 (범위) |
|---|---|---|
| Read | 6,253.3ms (6,250~6,260) | 3,006.7ms (3,000~3,010) |
| Operate | 7,510ms (모두 동일) | 3,010ms (모두 동일) |

기한 3초를 줄인 결과가 아닙니다. 채널 읽기 전 시간은 0~20ms,
채널 읽기 종료 후 최상위 예외 포착/반환까지는 모든 표본에서 0ms였습니다.
`MmsSocketChannel.Read`를 포함한 채널 읽기 구간은 예외 Read 6,250ms/Operate 7,490ms,
비예외 Read 3,000ms/Operate 2,990ms였습니다.
따라서 이번 A/B에서 제거한 추가 지연은 요청 인코딩/송신이나 채널 반환 뒤 상위 정리보다는
**채널 내부의 시간 초과 예외 생성·발생 및 그 과정의 런타임 처리 경로**에 집중됐습니다.
nanoCLR 내부의 예외 메타데이터·디버그 출력 등 개별 원인까지 분리한 것은 아닙니다.
단순히 상위에서 catch하여 false로 바꾼 결과가 아닙니다.

12회 비교(예외 6회/반환 6회), 정상 14순회/값 840개/접근 오류 280개 확인,
기준 측정 후 가용 힙 759,060 B 유지, 원래 Modbus 복원·COM6 읽기를 완료했습니다.
기존 전체 흐름도 `bin\iec61850-timeout-legacy-regression-20261028-0800`에서
control/enhanced/SBO enhanced/data-report 각각 3순회까지 완료하고 복원했습니다.
실측 개선이며 모든 환경·콜백·소켓 오류에 대한 3초 내 반환 보장은 아닙니다.
