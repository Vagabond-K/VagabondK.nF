# DNP3 시리얼 평가 호스트

PLC나 Modbus 없이 MCU에서 직접 실행하는 Outstation 예제입니다.
`VagabondK.nF.DNP3` + `VagabondK.nF.DNP3.Outstation` + `VagabondK.nF.DNP3.Serial`과 각 nanoFramework
시스템 어셈블리를 사용합니다. Master는 Outstation 대신 `VagabondK.nF.DNP3.Master`를 참조합니다.
기본 빌드는 포트·핀을 열지 않습니다. 실기 시험은 명시적으로
`/p:Dnp3HardwareTest=true`를 지정하고 `/p:Dnp3Role=Master` 또는 `Outstation`을 선택합니다.
시험용 저장소와 제어 콜백은 물리 출력을 구동하지 않습니다.

## UART 소유권과 RS485

애플리케이션이 보드별 TX/RX/RTS 핀 매핑, 트랜시버 배선, 보레이트·패리티·정지 비트 설정,
`SerialPort.Open/Dispose`를 담당합니다. 이 예제의 활성 시험 설정은 Geekble Nano의
COM2, TX GPIO17(D8), RX GPIO18(D9), 9600 8E1이며 다른 보드에 그대로 적용하지 마십시오.
`SerialPortChannel.ConfigureRs485(port)`를 **Open 전에** 호출하면
`SerialMode.RS485`, `Handshake.None`을 설정합니다. nanoFramework 네이티브 UART 드라이버가
마지막 정지 비트까지 RTS를 유지하도록 맡기며, `Write` 반환 직후 임의 GPIO를 낮추지 않습니다.
RTS를 DE/RE에 적절히 연결하고 이 네이티브 모드를 지원하는 보드/펌웨어가 필요합니다.
미지원 모드의 예외를 일반 UART로 조용히 대체하지 않습니다.
RS232/TTL UART와 DM2472 같은 자동 방향 RS485 모듈은 기본 Normal 모드의 포트와
`new SerialPortChannel(port)`를 사용합니다. DM2472에 네이티브 RTS/RS485 모드를 켜지 마십시오.
직접 GPIO DE 제어 어댑터는 제공하지 않습니다. 직접 구현할 경우
`IDnp3ByteChannel.Write`의 전송/방향 계약을 지켜야 합니다.

한 포트는 한 세션/스레드가 독점하며 Modbus나 다른 읽기 작업과 공유하지 않습니다.
라이브러리는 포트를 열거나 닫지 않습니다. `Read`는 현재 `BytesToRead`만 요청하여
nanoCLR가 최대 버퍼 전체를 기다리는 문제를 피합니다. 읽기 기한이 지나면 0바이트를 반환하고,
실제 UART 쓰기·모드 오류나 닫힌 포트 오류는 예외로 전달합니다.

## 세션 구동

`Dnp3SerialSession`에는 Master 또는 Station을 동일한 `IDnp3Endpoint`로 전달합니다.
기존 역할 API와 포인트/이벤트/제어 코덱을 그대로 사용하고, transport는 시리얼 패킷에 맞게
새로 만들지 않습니다. Master 작업이 반환한 **전체** 바이트 배열은 직접 UART에 쓰지 말고
`Send`에 한 번 전달합니다.

```csharp
var master = new Dnp3Master(2, 1, measurementHandler,
    responseTimeout: 30000, transportTimeout: 30000);
var channel = new SerialPortChannel(openConfiguredPort); // AutoDirection transceiver
var session = new Dnp3SerialSession(channel, master, ackTimeoutMilliseconds: 2000);
session.Send(master.ReadClasses(1, channel.Milliseconds));
while (master.Busy) session.RunOnce(10);
// Result를 확인하고 이후에도 비요청/확인/재시도를 위해 RunOnce를 호출합니다.
```

Station은 예제처럼 세션의 `RunOnce`를 계속 호출하고 생산자가
`database.Update(type, index, value)`로 값을 공급하면 됩니다.
`Process/Poll/Send/RunOnce`와 Master API 호출은 같은 스레드에서 직렬화합니다.
DB 생산자만 별도 스레드에서 갱신할 수 있습니다.
세션이 endpoint의 `Process/Poll`과 UART 쓰기를 소유하므로 이를 바깥에서 동시에 구동하지 않습니다.

기본 confirmed-link 모드에서는 최초 Reset Link/ACK, 한 프레임씩 ACK 대기,
ACK 뒤 FCB 전환, 유실 시 **동일 바이트** 재전송을 수행합니다. 상대 방향은 confirmed/unconfirmed
모두 받을 수 있습니다. `confirmedLink:false`이면 송신만 unconfirmed이며 application 확인은 유지합니다.
secondary ACK는 일반 송신 큐를 기다리지 않고 우선 송신하여 양방향 대기를 방지합니다.
NACK/DFC busy/not-supported는 명시적 예외이며 성공이나 자동 무한 재시도로 처리하지 않습니다.

송신 큐는 기본 8192바이트(292..32768), 링크 파서는 292바이트 고정 버퍼입니다.
`Send`는 전체 묶음의 CRC/주소/역할/용량을 먼저 검사합니다.
CRC 손상·불필요한 바이트·부분 프레임 기한 만료 뒤에는 다음 05/64 헤더로 재동기화하며
`Decoder.CrcErrors`, `DiscardedBytes`, `FrameTimeouts`를 공개합니다.
필요한 경보/로그는 Host가 이 카운터 변화로 남깁니다. 로컬 echo/다른 주소/방향은
`ForeignFrames`, 송신 재시도는 `Retransmissions`로 관찰합니다.
유효 CRC라도 잘못된 transport/application은 예외로 거부합니다.
파서 카운터는 `Reset`해도 누적되어 고장 원인 관찰에 사용할 수 있습니다.

재시도 소진·큐 초과·I/O/프로토콜 오류는 예외와 `Faulted`를 남기고 사용을 중지합니다.
원인을 해결하고 포트를 재연결하거나 남은 수신 바이트가 없음을 확인한 뒤
명시적으로 `Reset()`해야 합니다. 이 호출은 링크·선택·전송 상태를 초기화하지만 DB와 미확인 이벤트는
보존합니다. 실행 중인 제어의 결과를 추정하여 자동으로 재명령하지 마십시오.

## 느린 회선의 기한

프로파일은 baud와 최대 fragment, 양방향 트래픽, 재시도, 상대 처리 시간을 함께 고려해야 합니다.
8N1의 최대 링크 292바이트는 9600baud에서 약 304ms, 1200baud에서는 약 2434ms입니다.
`ackTimeoutMilliseconds`는 `Write` 반환 후 시작하지만 네이티브 구현의 송신 큐에
남아 있을 수 있는 물리 전송 시간과 상대 ACK/turnaround까지 포함해야 합니다.
기본 1000ms를 저속 UART에 그대로 사용하지 마십시오.
`frameTimeoutMilliseconds`는 첫 sync부터 전체 링크 수신까지의 절대 기한입니다.
Master의 `responseTimeout`, Station의 `confirmationTimeout/selectTimeout`,
양쪽 `transportTimeout`도 전체 분할 전송·Reset Link·링크 재시도보다 충분히 길게 설정합니다.
예제는 9600baud, 256바이트 application fragment, ACK/프레임 기한 3초, transport/application 30초의
평가를 시작하기 위한 설정이지 실물 회선에서 보장된 타이밍이 아닙니다. 호출자의 긴 대기나 다른 블로킹 I/O도 기한에 포함됩니다.

지원 범위는 단일 상대 주소의 point-to-point 세션입니다. RS485 멀티드롭 라우팅,
여러 Master, broadcast, 버스 충돌 중재/랜덤 backoff는 구현하지 않았습니다.
반이중 회선에서 Master 요청과 Outstation 비요청이 동시에 선로를 점유하지 않도록
현장 프로파일을 정해야 합니다. 이 코드를 충돌 방지 기능이 있는 버스 컨트롤러로 간주하지 마십시오.

## 검증과 한계

PC 시험은 한 바이트/7바이트/64바이트 분할, 양방향 2048바이트 재조립,
FCB·transport sequence 반복, 로컬 echo, 전체 292 위치의 CRC/헤더 손상·복구,
부분 프레임 기한, Reset/Data ACK 유실·동일 프레임 재시도, 큐 상한, 잘못된 주소,
NACK/DFC, UART 오류, 7종 490포인트·비요청 이벤트·중복 제어 방지를 검사합니다.
UART 어댑터 자체는 test-only SerialPort 계약 대역과 nanoFramework 실제 참조 빌드로 확인합니다.
1200/9600/115200baud 각각 10/11bit 문자 도착 시간을 모사하는 시험도 추가했습니다.
210포인트, CRC 손상, ACK 유실, exactly-once 제어와 1200baud에서 너무 짧은
프레임 기한의 실패를 확인합니다. `--dnp3-serial` 선택 시험은 93,095개 단언을 통과했습니다.
장기 회귀는 2백만 가상 스캔을 포함한 8,356,124개 단언을 통과했습니다.

OpenDNP3 3.1.2와 양방향 역할·45종 표현·비요청·제어·시간 동기화를
`Dnp3SerialSession`의 **TCP 기반 가상 바이트 전달 채널**로도 확인합니다.
7바이트 부분 읽기·noise·confirmed 프레임 유실과 Reset/FCB/재시도를 포함합니다.
OpenDNP3는 confirmed 수신/ACK를 수행하고 반환 데이터는 unconfirmed로 보냅니다.
이는 독립 confirmed 송신 장치와의 시험이나 UART 드라이버/RS485 전기적 검증이 아닙니다.
양방향 confirmed 송신은 자체 가상 피어 시험으로 별도 확인합니다.
원본 디코더 한계와 테스트 전용 IEEE 수정판은 ProtocolHost 문서의 구분을 그대로 따릅니다.
수정판 4096회 x 3 seed와 TCP/가상 시리얼 각각 방향별 552개 float32/64 수치 검사가 통과했습니다.
증거: `bin\protocol-tools\verification-patched-ieee-20261024-003544-527.log`.

## 실제 RS485 검증

기존 Modbus 배선을 바꾸지 않고 ESP32-S3 QUAD nanoCLR 1.18.0.30에서 확인했습니다.
MCU USB/배포는 COM3이며 데이터 선로는 다음과 같습니다.

| 구간 | 설정/연결 |
|---|---|
| MCU UART | COM2, TX D8/GPIO17, RX D9/GPIO18, 9600 8E1 |
| MCU RS485 | DM2472 TTL485-V2.0/MAX13487E, 5V, 자동 방향, Normal UART |
| 레벨 변환 | D8→LV3/HV3→DM2472 RXD, DM2472 TXD→HV2/LV2→D9, 공통 GND |
| PC | COM6 FTDI RS232→CS-428/9AT-mini3→RS485, RTS/DTR=true, Handshake=None |
| CS-428 | DIP1 ON, 2/3/4 OFF, 2선 NonEcho, 종단 OFF |
| 차동 선로 | CS TRX+→DM D+/A, TRX-→D-/B |

PC Master→MCU Outstation 및 MCU Master→PC Outstation 모두
7종 x 8포인트, 45종 static/event 표현, SELECT/OPERATE, 4종 Analog 제어,
시간 동기화, 56개 비요청 이벤트, 100회 반복 읽기와 완료 값 재조회를 통과했습니다.
g41v1 제어와 g40v1/g42v1 읽기는 `-2147483648, -123, -1, 0, 2147483647`도
양방향으로 확인했습니다. MCU에서 혼합 숫자 삼항식의 음수 int32 승격이 잘못되는 문제를
발견하여 공통 객체/제어 디코더가 명시적 부호 비트 산술을 사용하도록 수정했습니다.

| 실제 시험 | 결과 | 증거 (`bin\dnp3-hardware`) |
|---|---|---|
| PC Master, 새 세션 | RX 58,538 / TX 11,777바이트, 재시도/CRC/프레임 기한 오류 0 | `pc-master-boundaries-fresh.log` |
| PC Outstation, 새 세션 | 완료, CRC/프레임 기한 오류 0, startup 링크 재시도 1회 | `pc-outstation-fresh.log` |
| PC Outstation, 오류 주입 | 제어 링크 ACK 1개 유실 + 응답 CRC 1개 손상 후 복구, 제어 실행 정확히 41회 | `pc-outstation-faults.log` |
| MCU Master 완료 콘솔 | PASS, 링크 재시도 1회, CRC 오류 감지 1회, 프레임 기한 오류 0 | `mcu-master-faults-debug.log` |

오류 주입은 실제 UART 송신 어댑터에서 ACK를 생략하고 CRC 바이트를 변경한 시험입니다.
전기적 노이즈 발생기나 오실로스코프에 의한 파형 검사는 아닙니다.
위 오류 주입 PC/MCU는 동일 VagabondK.nF 스택입니다. 아래 독립 실물 시험과 구분합니다.

### 재실행

격리된 시험 장치의 배선을 확인한 뒤에만 실행하십시오. PC 소비자는 .NET 10입니다.
Visual Studio MSBuild로 다음 프로젝트를 Release 빌드합니다.

```powershell
MSBuild .\Samples\DNP3\VagabondK.nF.DNP3.SerialHost\VagabondK.nF.DNP3.SerialHost.nfproj /t:Rebuild /p:Configuration=Release /p:Dnp3HardwareTest=true /p:Dnp3Role=Outstation
dotnet build .\Tests\DNP3\VagabondK.nF.DNP3.SerialFixture\VagabondK.nF.DNP3.SerialFixture.csproj -c Release
nanoff --nanodevice --deploy --serialport COM3 --image .\Samples\DNP3\VagabondK.nF.DNP3.SerialHost\bin\Release\VagabondK.nF.DNP3.SerialHost.bin
dotnet .\Tests\DNP3\VagabondK.nF.DNP3.SerialFixture\bin\Release\net10.0\VagabondK.nF.DNP3.SerialFixture.dll COM6 Master
```

반대 방향은 `Dnp3Role=Master`로 Rebuild/배포한 **직후 같은 명령 흐름에서**
PC fixture를 `COM6 Outstation` 또는 `COM6 OutstationFaults`로 시작합니다.
MCU Master는 시작 2초 대기 후 4초간 startup 비요청 확인을 처리합니다.
PC Outstation을 배포보다 너무 먼저 열면 상대가 없는 동안 링크 재시도가 소진됩니다.
PC Master도 4초간 startup null 비요청/확인을 처리한 뒤 첫 요청을 보냅니다.
이 대기 없이 동시에 송신했을 때 실제 반이중 충돌/CRC 오류가 재현되었습니다.

각 전체 시험은 MCU 재시작과 새 PC 프로세스로 시작합니다. 이미 완료한 MCU 세션에
새 PC 프로세스만 붙이는 것은 이 시험의 재시작 절차가 아닙니다.
이 경우 상대의 기존 confirmed-link 상태 때문에 NACK/재시도 소진이 재현되었으며,
이를 정상 연결 복구 성공으로 간주하지 않습니다.
오류 시 마지막 송수신 바이트를 출력하고 종료합니다.
`COM6 Probe`는 형식별 제어 결과를 출력하는 진단 전용이며 PASS 판정 시험이 아닙니다.

시험 후 원래 `Modbus.SerialServer` 이미지를 복원하고
`Test-SerialServer.ps1 -PortName COM6 -Iterations 1`로 `123,456,65535` 읽기를 재확인했습니다.
증거는 `restored-modbus.log`입니다. 배포 직후 2초만 기다린 첫 읽기는 타임아웃되었고,
장치 앱 정보 확인 후 재조회는 성공했습니다. 앱 시작 준비 시간을 확보하십시오.

네이티브 RTS/DE/RE 모드, 다른 보레이트의 실물 시험, 멀티드롭 충돌 중재, 장기 MCU 힙 사용량·전원 차단,
전기적 내성, 현장 장비 상호운용과 DNP3 인증은 아직 검증하지 않았습니다.

## 독립 OpenDNP3 실물 상호운용

OpenDNP3 3.1.2 원본 커밋 `26b4c01e4839bbbda8866655e086471c4917ee53`의
`DNP3Manager.AddSerial`이 COM6를 직접 엽니다. PC 측 DNP3 프레임 처리·역할·수치 해석은
C++ OpenDNP3가 담당하며 .NET 시험 드라이버는 프로세스 명령/결과만 검사합니다.
TCP 전달 채널, 시리얼 게이트웨이, 자체 VagabondK.nF PC 피어 또는 IEEE 오버레이를 사용하지 않습니다.
시작 시 `--decoder-identity`가 `DECODER ORIGINAL`인지 검사합니다.

| 실제 역할 | 결과 | 증거 (`bin\dnp3-hardware`) |
|---|---|---|
| OpenDNP3 Master → MCU Outstation | 기본 전체 시퀀스 + 시간/품질/Counter 경계 + 동일 시각 이벤트 순서; 7,546개 검사 통과 | `independent-serial-master.log`, `independent-serial-master-mcu.log` |
| MCU Master → OpenDNP3 Outstation | 동일 확대 시퀀스, MCU 15,526개 검사, 제어 45회, UTC `1700000001414`, 재시도/CRC/프레임 기한 오류 0 | `independent-serial-outstation.log`, `independent-serial-outstation-mcu.log` |

두 번째 시험은 PC의 완료 마커뿐 아니라 MCU의 `DNP3 MCU MASTER PASS`도 요구합니다.
독립 피어의 데이터 생성기는 시험용 8포인트 모델을 공급할 뿐 DNP3 인코딩/디코딩을 대체하지 않습니다.
최초 반대 역할 시험은 이벤트 객체의 범위 qualifier 요청을 OpenDNP3가 IIN2=1로 거절했습니다.
단일 잔여 AOS 이벤트의 경계값 시험을 all-objects qualifier로 수정한 후 전체 통과했습니다.
실패를 정상 처리하거나 수치 검사를 생략하지 않았습니다.

### 확대 정합성 검사

PC 코덱 시험은 **45종 표현 x 품질 바이트 256개 x 시간 경계값 8개 = 92,160개** 조합을
검사합니다. 품질 바이트와 6바이트 리틀엔디언 시각의 예상 전송 바이트를 직접 계산하여
비교하므로 인코딩·디코딩이 같은 방식으로 틀려도 통과할 수 있는 단순 왕복 시험과 구분됩니다.
Binary/DoubleBit의 상위 비트는 품질이 아닌 상태 비트이므로 각각 분리해서 확인합니다.
예약 품질 비트의 보존을 확인한 것이며 모든 조합이 현장 의미상 유효하다는 뜻은 아닙니다.

시간 벡터는 밀리초 단위 `0, 1, 65535, 65536, 4294967295, 4294967296,
281474976710654, 281474976710655`입니다. 음수/48비트 초과 입력은 거부되는지도 검사합니다.
시각 없는 표현은 디코딩 시각 0을, 시각 있는 표현은 원값 그대로를 요구합니다.
별도 516개 수치 코덱 사례에서 int16/int32 경계·절삭·포화·overrange 플래그,
float32/64 정확 비트(부호 있는 0·subnormal 포함), Counter uint32 경계와
16비트 표현의 low-word rollover를 확인했습니다. PC 전체 회귀는 707,902개 단언을 통과했습니다.
증거: `bin\dnp3-hardware\metadata-boundary-regression.log`.

실물 독립 시험도 TCP/RS485 양방향 모두 아래 네 세대를 전송합니다.
첫 세대는 위 시각 벡터, 다음 세대는 역순, 마지막 두 세대는 모든 포인트의 시각을 1ms로
같게 하여 읽기 전 동시에 큐에 넣습니다. 각 타입/인덱스의 값·품질·시각을 비교하며
동일 포인트의 같은 시각 이벤트 두 개가 유실/중복/재정렬되지 않는지 확인합니다.
시각만 바뀌는 갱신은 이 DB의 변화 이벤트가 아니므로 값/품질도 확실히 바뀌게 구성했습니다.
그 최초 시험의 누락은 이 조건을 놓친 시험 데이터 문제였으며 이벤트 생성 규칙은 변경하지 않았습니다.
품질 0·개별 비트·복합 패턴, DoubleBit의 네 상태, Counter 0~uint32 최대,
Analog의 signed 경계·소수값도 함께 비교합니다.
기존 실제 오류 주입 표의 41회는 확대 전 결과이고, 현재 전체 시퀀스는 추가 세대 4개를 포함해 45회입니다.

MCU Outstation 시험은 PC 피어 준비와 CLR 재시작을 동기화합니다.
TCP는 MCU listener가 실제로 열렸는지 먼저 확인하여 Wi-Fi 연결 준비 시간을
DNP3 요청 실패와 혼동하지 않습니다. 시리얼 피어 준비 전에 시작하거나 TCP 수신 준비 전에
요청했던 실패를 성공으로 집계하지 않았습니다.

시간 동기화의 수신 콜백/지연 허용 범위 검증은 유지합니다. 실제 RTC 설정·정확도·드리프트
측정, 모든 가능한 수치/시간 조합의 완전 탐색, 독립 원본 OpenDNP3의 subnormal/-0 한계 해소를
의미하지 않습니다. 이번 변경은 시험 코드/샘플 확장이며 라이브러리 동작은 바꾸지 않았습니다.
이후 별도 [Python/Go 교차검증](../../../Tests/DNP3/VagabondK.nF.DNP3.ExtraPeers/README.md)에서는
PC TCP 수치/품질 3,024개 및 **실제 MCU↔Go RS485 양방향 각각 1,008개**의
수치 비트·품질·시각 일치를 확인했습니다. subnormal/음의 0도 포함하며 Go 실물 범위는
아날로그 12종 표현입니다. 최종 양방향 MCU의 링크 재시도/CRC/프레임 기한 오류는 모두 0입니다.
Go Outstation 송신 시각 0은 공개 API의 자동 시각 지정과 구분하여 1ms를 사용합니다.
Python은 PC TCP만 시험했으며 OpenDNP3 원본의 실패 기록을 대체하지 않습니다.
`Dnp3NumericTest=true`로 빌드한 별도 숫자 경계 이미지를 사용하고,
`Build\Test-Dnp3GoSerial.ps1`은 양방향 실행 후 원본 Modbus 복원·실제 읽기를 자동 수행합니다.
정확한 빌드/배포 절차와 증거는 위 교차검증 문서의 실제 RS485 절을 참조하십시오.

```powershell
.\Build\Build-Dnp3ValidationMaster.ps1
dotnet build .\Tests\DNP3\VagabondK.nF.DNP3.SerialFixture\VagabondK.nF.DNP3.SerialFixture.csproj -c Release
# MCU Outstation 이미지를 새로 배포한 뒤:
dotnet .\Tests\DNP3\VagabondK.nF.DNP3.SerialFixture\bin\Release\net10.0\VagabondK.nF.DNP3.SerialFixture.dll COM6 IndependentMaster
# MCU Master 시험 이미지를 Dnp3.Master-independent.bin으로 준비한 뒤:
.\Build\Test-Dnp3IndependentPeer.ps1 -Transport Serial
# MCU Outstation 시험 이미지를 Dnp3.Outstation-boundaries.bin으로 준비한 뒤:
.\Build\Test-Dnp3IndependentPeer.ps1 -Transport Serial -PeerRole Master
# 품질/시각 전체 조합과 수치 경계 코덱 회귀:
dotnet run --project .\Tests\DNP3\VagabondK.nF.DNP3.Tests\VagabondK.nF.DNP3.Tests.csproj -c Release
```

`Test-Dnp3IndependentPeer.ps1`는 기본적으로 MCU Master 이미지를 배포하고 PC 독립 피어 준비 후
MCU를 재시작하여 완료 콘솔을 확인합니다. `-PeerRole Master`는 MCU Outstation 이미지로
독립 Master의 전체 정합성 비교 성공을 요구합니다. 종료 후 자동으로 원래 앱을 복원하는 도구는
아니므로 시험 절차 끝에 보관한 원본 앱을 배포하고 Modbus 기본 읽기를 확인하십시오.
이번 확대 시험 후에도 복원/읽기를 완료했으며 `bin\dnp3-hardware\restored-modbus-boundaries.log`에 남겼습니다.
TCP 실기 결과와 재실행 절차는 [TCP 시험 Host](../VagabondK.nF.DNP3.TCPHost/README.md)에 있습니다.

Rust 기반 `stepfunc/dnp3`는 사용하지 않았습니다. 확인 당시 공개 라이선스 2.6(c)는
다른 프로토콜 구현체를 검증·개선하기 위한 시험 피어 사용을 제한하므로 별도 허가가 필요합니다.
현재 선택은 언어 선호가 아닌 [사용 허용 범위](https://github.com/stepfunc/dnp3/blob/main/LICENSE.txt)에 따른 것입니다.
## DNP3 Serial Outstation 장시간 실물 시험

격리된 COM3/COM6 장시간 시험은 독립 Go Master로 수치 검증용 Outstation을 검사합니다.
확인 응답을 요구하는 제어를 반복하고, 84개 아날로그 입력·출력 전체의 Static/Event 표현을
확인하며, 각 작업 뒤 이벤트 클래스를 모두 읽고 확인 응답을 보내 비웁니다.
장시간 시험 전용 검증 모드는 단조 증가하는 시험 작업 번호를 받습니다.
제어는 시험 데이터만 갱신하며 물리 출력을 구동하지 않습니다.

저장소 루트에서 기본 2시간 시험을 실행합니다.

```powershell
.\Build\Test-Dnp3SerialOutstationSoak.ps1 -AllowDeviceChanges
```

Go, nanoFramework 확장이 설치된 Visual Studio MSBuild, 기존에 보관한 Modbus 복원 이미지와
격리된 COM3/COM6 배선이 필요합니다. 스크립트는 새 출력 폴더에 순회 진행 상황·피어 출력·
디버거 메시지와 기계가 읽을 수 있는 `status.json`을 기록합니다.
조기에 중단하려면 그 폴더에 `stop.request`를 만드십시오. 스크립트는 보관한 Modbus 이미지로
복원을 시도하고 COM6 RTU 읽기 전용 요청으로 확인합니다.
최종 성공으로 판정하려면 독립 Master와 MCU Outstation의 완료 표식 및 복원 성공을 모두 확인해야 합니다.
