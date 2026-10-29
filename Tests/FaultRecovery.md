# 프로토콜별 장애·복구 검증 범위

## 2026-10-29 추가 매핑 견고성 보드 검증

현재 지원하는 Modbus·DNP3·BACnet·SNMP·MMS·IEC61850·MQTT·Sparkplug 매핑에 대해
추가 견고성 시험을 실제 ESP32-S3에서 완료했습니다. 항목별 변경·이웃 값 보존·부분 실패·단절 회복·
동시 스냅샷·유한 반복 힙 측정의 프로토콜별 적용 범위와 정확한 재실행 명령은
[매핑 대조표](../Samples/MappingCoverage.md#추가-견고성-실물-시험)에 모았습니다.
아래 2026-10-28 이전 표와 성능 기록은 당시 실행 결과로 보존하며 최신 전체 범위로 해석하지 않습니다.

| 범위 | 추가 실물 결과(`bin` 아래) |
|---|---|
| Modbus | `mapping-hardware-20261029-125737`: 수치 경계 37종의 비트 왕복·이웃 보존, 잘못된 Transaction/Unit ID 등 Master 고장 5종. `mapping-hardware-20261029-110932` 실행은 8/32/128포인트별 60초 이상 혼합 안정성 |
| DNP3 | `dnp3-mapping-hardware-20261029-112507`: 8/32/128 × 이벤트 용량 16/128의 선택 게시·flags/시각·deadband. `dnp3-master-soak-20261029-115537`(Master), `dnp3-master-soak-20261029-120122`(TCP), `dnp3-master-soak-20261029-120508`(Serial CRC), `dnp3-master-soak-20261029-121035`(ACK/응답/부분 만료)도 각각 통과 |
| BACnet | `bacnet-mapping-hardware-20261029-111849`: 8/32/64 객체 × 100라운드, 독립 변경·잘못된 쓰기 후 보존·새 외부 피어 조회 |
| SNMP | `snmp-mapping-hardware-20261029-122940`: SharpSnmpLib 256회, 다중 SET wrongType 원자성·복구 64회·배열 소유권·동시 스냅샷. `snmp-scaling-20261029-123740`은 자체 BER 피어 규모 시험 |
| MMS·IEC61850 | `mapping-interop-hardware-20261029-133546`: 실제 생성 Server/Client 네 역할과 libiec61850, 쓰기·리포트 적용·거절·서버 고장 회복. MMS 리포트의 보조 인코딩 범위는 대조표 참조 |
| MQTT JSON | `mqtt-sharing-20261029-124920`: 자체 MQTT와 생성 매핑으로 적용 68회·거절 65회, 필드별 변경·모든 절단 위치·전체 모델 보존 |
| Sparkplug | `mqtt-sharing-20261029-125046`: 단일 메트릭 갱신 264회·seq 순환·별칭 교체·비정상 후반 메트릭. 공식 Tahu 세션은 `mqtt-sharing-20261029-125212` |

동시성 보조는 Modbus/BACnet/DNP3의 동일 앱 잠금 아래 샘플을 writer/reader 각 800회 확인하고,
SNMP는 배열 소유권과 동시 읽기를 확인했습니다. 여러 독립 요청의 트랜잭션이나 중간 이벤트 전부 전달을
보장하지 않습니다. 외부 구현과 직접 작성한 손상 입력·리포트 시험기는 구분합니다.
각 완료 실행 후 원래 Modbus 앱과 COM6의 `123, 456, 65535`를 확인했습니다.
힙 결과는 유한 구간 측정이며 전체 명세 인증·모든 조합 검증·장기 누수 부재가 아닙니다.

MMS Client의 선행 실패는 수신 콜백의 NaN 검증 예외가 통신 작업 기한을 소모한 fixture 문제였습니다.
독립 리포트 스냅샷을 고정 큐에 넣고 기한 밖에서 적용하도록 바꾼 뒤 네 역할을 통과했습니다.
엔진·펌웨어·timeout 값은 바꾸지 않았습니다. 동시성 fixture의 제네릭 델리게이트 사용 시
관측한 native CLR 부트 실패와 관리 앱 전용 파티션 복원 내역도 대조표에 구분해 남겼습니다.
## MQTT 실물 2시간 재시험 결과

`bin\mqtt-hardware-soak-20261028-140657`은 ESP32-S3와 PC MQTTnet 사이에서
평문 MQTT 3.1.1과 인증서를 검증하는 TLS 1.2 MQTT 5 연결을 동시에 운용한 결과입니다.
512바이트 sequence payload의 QoS0/1/2 echo, 혼합 QoS 배치, PING과 persistent 세션의 정상 재접속을 반복했습니다.
2026-10-28 16:07경 측정 7,200.59초, 7,110 steps, 게시 17,064건, 연결 48회,
재접속 cycle 23회로 통과했습니다. PC peer 종료 코드는 0이며 `Status=completed`, `Restored=true`, `Error=null`입니다.
GC 후 MCU 가용 heap은 기준 755,256바이트, 최소 755,244바이트, 종료 755,292바이트였습니다.
`restored-modbus.log`에서 원래 Modbus 복원과 COM6 holding 0..2=123/456/65535를 확인했습니다.

선행 `bin\mqtt-hardware-soak-20261028-124519`는 약 77분에 PC peer가 비정상 종료하여 실패했습니다.
이 실행은 2시간 통과로 집계하지 않습니다. 당시 종료 코드가 보존되지 않아 원인은 미확인이며,
재시험에는 peer 종료 코드·PC heap/private bytes·handle/thread 수를 기록했습니다.
재시험 통과가 앞선 실패의 원인 해명을 뜻하지 않습니다.

이 실물 시험은 enum 변경 이전 이미지입니다. enum 변경본은 별도의 PC 계약·가상 nanoCLR·MCU 빌드로
검증했으며 실물 2시간 시험을 재실행한 것은 아닙니다.
정상 재접속 시험은 Wi-Fi/브로커 강제 장애·전원 장애·모든 firmware/broker 조합의 검증과 구분합니다.

## 기존 검증 기록

2026-10-28 기준으로 테스트 소스, 실행 진입점, 보존된 로컬 결과를 대조한 기록입니다.
최초 대조 뒤 DNP3 Serial/TCP CRC, BACnet Write, SNMP BER과 IEC61850 양 역할 손상 입력/응답의 PC·실물 시험을 추가 실행했습니다.
그 밖의 행은 기존 소스·결과 대조이며 전체 시험을 재실행한 결과가 아닙니다.
정상 통신·상호운용·soak 통과를 해당 환경의 모든 손상 입력 검증으로 간주하지 않습니다.

## 판정 기준

- **코드 확인**: 실행 경로와 assertion이 존재합니다. 이번 대조에서 재실행했다는 뜻은 아닙니다.
- **실물 기록**: 보존된 PASS 로그 또는 `completed` 상태를 확인했습니다. 아래 결과 경로는 로컬 산출물입니다.
- **부분**: 일부 오류만 검사하거나 정상 왕복만 확인합니다. 손상 입력 뒤 복구까지 입증하지는 않습니다.
- **미확인**: 해당 환경의 소스·결과에서 요구한 장애와 후속 복구를 함께 확인하지 못했습니다.

복구는 같은 연결의 무조건적인 재사용을 뜻하지 않습니다. UDP는 다음 데이터그램 처리,
직렬은 프레임 재동기화 또는 세션 Reset, TCP/MMS는 정책에 따른 연결 폐기·재접속까지 포함합니다.
전송 실패 뒤 제어 명령을 자동 재전송해서는 안 되며, 응답 유실과 미실행을 구분해야 합니다.
오류 계수가 없는 API는 예외·Faulted·데이터 미변경·후속 정상 응답으로 판정합니다.

## 환경별 검증표

| 대상 | PC .NET: 코드 확인 | 가상 nanoCLR: 코드 확인 | 실물 근거 | 남은 범위 |
|---|---|---|---|---|
| Modbus RTU | CRC·길이·부분/연속 수신, 수신 상태, 가상 채널 왕복 | 손상 Write 4종 거절·값 보존 뒤 GET/SET. 메모리 전송이며 실제 UART 없음 | RS-485 120초: 최대 읽기 177회, CRC/부분 요청/유휴 복구 2 cycles, 데이터 보존 | 연속 잡음·손상/정상 혼합 입력을 장시간 반복하는 별도 실물 시험 |
| Modbus ASCII | LRC·Hex·종료/길이·수신 상태, 가상 채널 왕복 | 손상 Write 4종 거절·값 보존 뒤 GET/SET. 실제 UART 없음 | LRC 10회, Hex/CRLF/초과 길이/문자간 만료, 콜론 재시작 뒤 복구 | 연속 손상 입력의 반복·자원 추이 |
| Modbus TCP | MBAP 중간 EOF·잘못된 길이·Transaction ID·전체 ADU 수신 기한, 정상 loopback | TCP 장애 시험 미확인 | 샘플 README에 Server 비정상 MBAP·유휴/부분 수신 후 재접속, Client 오류 후 새 연결 결과 기록 | 직렬·TCP·역할별 결과를 같은 장애 목록으로 통일; 기존 README 기록과 원본 로그의 보존 범위 구분 |
| DNP3 Serial | 모든 바이트 위치의 단일 손상 미전달·정상 프레임 복구, 부분 프레임 만료, ACK/재시도·Reset; 양 역할 CRC fixture | decoder 양 역할 CRC·부분 만료 6종 뒤 Poll/Control. UART 세션 시험 아님 | 양 역할 헤더/데이터/혼합 CRC 6종, 정확히 28회 거절·손상 프레임 미전달, 정상 측정/제어 및 독립 피어 수신 재개. 기존 Master 장애 soak 2시간·Outstation 정상 soak 2시간도 통과 | 연속 잡음·CRC 손상 혼합의 장시간 반복, 물리 단절·전원 장애 |
| DNP3 TCP | 잡음/CRC 뒤 정상 응답, 부분 만료 뒤 복구, EOF/endpoint 예외 후 Faulted; 양 역할 TCP fixture 8종 | TCP 세션 장애 시험 미확인 | 양 역할 8종: CRC 거절28/부분 만료2, 같은 연결 정상 Poll/Control, 앞뒤 독립 피어 매핑 복구. 기존 Master 8시간 재접속 soak도 통과 | 손상 입력 반복 부하·전원 장애·미확인 이벤트 보존 |
| BACnet/IP | BVLC/NPDU/APDU 거절, UDP 빈/짧은/초과 데이터그램 각각 뒤 Who-Is/I-Am | 손상 Write 4종 거절·값/쓰기 횟수 보존 뒤 Read/Write/Who-Is | 손상 APDU Write 32종 각각 Reject/Error/Abort 관측·AV/BV 값 보존·정상 Write/Read; 독립 Client 256 cycles | 반복 오류 부하·내부 QueueWrite 호출 횟수 관측 |
| SNMP UDP | BER 절단·길이/형식/OID 변이, UDP 오류 뒤 SET/GET/Trap, 잘못된 community 무응답; 12종 정확한 거절·값 보존 | 손상 BER SET 7종 거절·두 값/쓰기 횟수 보존 뒤 GET/SET | 독립 Manager: 손상 BER 10종 각각 장치 거절 + 값/쓰기 횟수 보존 + 후속 GET/SET. 초과 UDP 2종 송신 후 복구, 256 SET/GET rounds | **초과 UDP의 MCU 애플리케이션 도달·거절은 미확인**; 반복 오류 부하·Trap 실물 수신 |
| MMS | BER/Report 경계·변이, TPKT 오류 뒤 decoder Reset, Report callback 실패 후 새 세션 정상 교환 | TPKT 손상/부분 EOF 4종 Faulted·Reset, 잘못된 TPKT/COTP Association 거절 뒤 새 메모리 채널 왕복 | IEC 공유 MMS 경로: Server 손상13종/Client 손상응답12종, TPKT/COTP/Association/BER/invoke ID 등 Faulted·후속 호출 거절·새 Association, warmup 후 heap 유지 | 일반 MMS 전 기능 시험·장시간 연속 손상 부하·전원 장애 |
| IEC61850 | TPKT/분할·BER·무응답/EOF, 서비스 오류 후 연결 유지, 잘못된 종료 후 Faulted; 독립 IED 장애는 별도 옵션 | 전용 IEC 장애 시험 없음 | Server 손상13종 제어 미호출·값 보존, 독립 Client14회/1,344 requests. MCU Client 손상응답12종 및 제어 응답 폐기1종, 정상15cycles/900values/300errors, 송신·실행1회/재호출 무송신. 양 역할 warmup 후 heap 유지 | 물리 네트워크 유실·반복 오류 부하·재접속 후 앱 제어 정책・재부팅을 넘는 중복 제거 |

BACnet의 현재 구현은 `SEGMENTATION_NONE`입니다. 분할 메시지 재조립·유실 복구를 지원 기능처럼
시험 범위에 넣지 않습니다. 미지원 분할 요청 거절은 별도 항목입니다.
MMS/IEC의 논리적 서비스 오류 후 연결 유지와 잘못된 전송 프레임 후 세션 폐기를 혼동하지 않습니다.
표의 일부 시험을 통과해도 적합성 인증·전체 입력 공간 검증·누수 부재를 의미하지 않습니다.

## 소스와 실행 진입점

경로는 저장소 루트 기준이며, 메서드 이름은 확인한 범위를 다시 찾기 위한 기준입니다.

| 대상 | 근거 소스 | 실행 진입점 |
|---|---|---|
| Modbus | `Tests\Modbus\VagabondK.nF.Modbus.Tests\Program.cs`: `TransportBoundaries`, `ReceiveStatuses`, `TcpFaults`, `SerialRoundTrip` | `Build\Test-All.ps1 -Filter '*Modbus*'` |
| Modbus 실물 | `Samples\Modbus\VagabondK.nF.Modbus.SerialServer\Test-SerialServer.ps1`: `Test-AsciiRecovery`, endurance; TCPServer/Client README의 실물 절차 | 해당 샘플 README와 `Test-SerialServer.ps1 -Endurance` / `-Extended` |
| DNP3 | `Tests\DNP3\VagabondK.nF.DNP3.Tests\Dnp3SerialCases.cs`: `Decoder`, `Failures`; `Dnp3TcpCases.cs`: `DecoderRecovery`, `HalfFrameTimeout`, `PeerClose` | `Build\Test-All.ps1 -Filter '*DNP3*'` |
| DNP3 실물 | `Tests\DNP3\VagabondK.nF.DNP3.Mapping.Hardware\MasterSerialValidation.cs`, `SerialCrcValidation.cs`, `MasterSoak.cs` | `Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -SerialValidation`; `-SerialSoak`으로 시간 제한 반복, 대신 `-SerialCrc`로 양 역할 CRC 고정 시험 |
| DNP3 Outstation | `Tests\DNP3\VagabondK.nF.DNP3.ExtraPeers\go-peer\serial.go`: `serial-master-soak` 실행 경로 | `Build\Test-Dnp3SerialOutstationSoak.ps1` |
| DNP3 TCP 실물 | `SerialCrcValidation.RunTcp`, `MasterSoak.cs`; PC `MasterSerialTests.TcpRecovery` | `Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -TcpRecovery` |
| BACnet | `Tests\BACnet\VagabondK.nF.BACnet.Tests\BacnetDeviceCases.cs`, `BacnetIpCases.cs`; `Tests\BACnet\VagabondK.nF.BACnet.Interop\MalformedCases.cs`, `BacnetHardwareCases.cs` | `Build\Test-All.ps1 -Filter '*BACnet*'`; 독립 Interop/`Build\Test-BacnetHardware.ps1`은 별도 |
| SNMP | `Tests\SNMP\VagabondK.nF.SNMP.Tests\AgentCases.cs`: `Malformed`, `Dropped`; `UdpCases.cs`; `Tests\SNMP\VagabondK.nF.SNMP.Interop\Program.cs` | `Build\Test-All.ps1 -Filter '*SNMP*'`; 독립 Interop/`Build\Test-SnmpHardware.ps1`은 별도 |
| MMS | `Tests\MMS\VagabondK.nF.MMS.Tests\Program.cs`: `Boundaries`, `Fuzz`, `Loop` | `Build\Test-All.ps1 -Filter '*MMS*'` |
| IEC61850 | `Tests\IEC61850\VagabondK.nF.IEC61850.Tests\Program.cs`: `Transport`; `SocketChannelTests.cs`, `MmsClientTests.cs`, `LifecycleTests.cs` | `Build\Test-All.ps1 -Filter '*IEC61850*'` |
| IEC61850 Server 실물 | `Tests\IEC61850\VagabondK.nF.IEC61850.Server.Hardware\Program.cs`: `RunRecovery`, `RecoveryFault`; PC `HardwareFaultPeer.cs` | `Build\Test-Iec61850ServerHardware.ps1 -AllowDeviceChanges -MalformedRecovery` |
| IEC61850 Client 실물 | `Tests\IEC61850\VagabondK.nF.IEC61850.Hardware\Program.cs`: `MalformedResponse`; PC `HardwareClientFaultPeer.cs` | `Build\Test-Iec61850Hardware.ps1 -AllowDeviceChanges -MalformedRecovery -UseTryRead` |
| IEC timeout 성능 실물 | MCU `CompareTimeout`, `CountingChannel`; PC `HardwareClientFaultPeer`의 응답 폐기; PC `TimeoutResultTests` | `Build\Test-Iec61850Hardware.ps1 -AllowDeviceChanges -TimeoutComparison -UseTryRead` |
| 독립 IED 장애 | `Tests\IEC61850\VagabondK.nF.IEC61850.Tests\VirtualIedFaultTests.cs`: 지연·부분 EOF·단절·응답 유실·재시작·명령 재실행 방지 | IEC 테스트 실행의 `--virtual-faults <peer>` 또는 `--interop <peer>`. **nanoCLR가 아닌 PC 시험** |
| 가상 nanoCLR | `Tests\Common\VagabondK.nF.Virtual.Tests\ModbusVirtual.cs`, `Dnp3Virtual.cs`, `DeviceVirtual.cs`, `MmsVirtual.cs` | `Build\Test-Virtual.ps1`; PC `Test-All.ps1`/CI에 포함되지 않음 |

SNMP Interop의 `MalformedRecovery`는 PC UDP와 `--snmp-hardware` 양쪽에서 실행합니다.
시험용 `.9.0` 거절 계수가 매번 정확히 1 증가해야 BER 거절로 인정합니다.
실물 초과 UDP(1473/4000 bytes)는 IP 분할 뒤 소켓에 도달하지 않을 수 있으므로 별도로 표시합니다.
이번 실물 실행은 두 경우 모두 거절 계수가 증가하지 않았습니다. 무응답·값 보존·후속 정상
GET/SET만 확인했으며, IP 재조립 설정 등 폐기 원인이나 MCU 크기 제한 검증을 주장하지 않습니다.
PC loopback에서는 두 초과 UDP도 애플리케이션 거절 계수 증가를 확인했습니다.
IEC의 `VirtualIedFaultTests`도 기본 무인수 실행에는 없으며 외부 peer 옵션으로만 실행됩니다.
`Build\Test-Soak.ps1`은 기존 PC 오류 시나리오를 반복하며 UART나 nanoCLR 장애 시험을 대신하지 않습니다.

## 확인한 로컬 결과

아래 파일은 Git에 포함되지 않는 로컬 산출물입니다. 새 체크아웃에서는 존재하지 않을 수 있습니다.
이 표의 수치는 각 실행 당시 결과이며 현재 소스 전체를 다시 실행한 결과가 아닙니다.

| 결과 경로 | 확인한 내용 |
|---|---|
| `bin\dnp3-master-serial-soak-2h-20261026\status.json` | completed, Restored=true, Error=null; 161,448 measurements; ACK 4/응답 4/부분 3 |
| `bin\dnp3-serial-fault-20261027-2055\status.json` | 고정 3종 장애, 5 cycles, 7,112 measurements, 44,465 checks, 복원 성공 |
| `bin\dnp3-serial-fault-soak-20261027-2105\status.json` 및 `measurements.jsonl` | ACK 2/응답 1/부분 1, 12,768 measurements, 79,851 checks; 정상 cycle GC 후 가용 heap 727,572 bytes 유지; 복원 성공 |
| `bin\dnp3-serial-crc-20261027-2210\status.json`, `measurements.jsonl`, `crc-*.bin` | completed, CRC 6종/28회 거절, 정상 3 cycles/4,200 measurements; GC 후 heap 726,660 bytes 유지, Restored=true, Error=null |
| `bin\dnp3-master-soak-8h-20261025\status.json` | completed, 1,296,400 measurements, peer restart 44/연결 축출 44, 최종 복구·복원 성공 |
| `bin\dnp3-tcp-recovery-20261028-0005\status.json`, `tcp-recovery-*.bin` | completed, CRC28/부분 만료2, 양 역할 8종, 앞뒤 정상 2순회/2,912 measurements; 각 표본 GC heap 735,996 B, 복원 성공 |
| `bin\bacnet-hardware-20261025-133115\independent.log` | BACNET MCU INDEPENDENT PASS, 손상 데이터그램 복구 포함 |
| `bin\bacnet-hardware-20261027-234751\independent.log`, `restored-modbus.log` | 손상 AV/BV Write32종 장치 거절·값 보존·정상 왕복, 기존 256 cycles, 복원 성공 |
| `bin\snmp-hardware-20261025-133226\independent.log` | SNMP MCU PASS, 3,994 checks/256 SET/GET rounds; 손상 BER 주입 근거는 아님 |
| `bin\snmp-hardware-20261027-221235\independent.log`, `restored-modbus.log` | 6,416 checks/256 rounds; BER 10종 장치 거절·복구, 초과 UDP 2종은 transport-only; Modbus 복원·COM6 읽기 성공 |
| `bin\snmp-hardware-20261027-220910\independent.log`, `restored-modbus.log` | 첫 실행: BER 10종 통과 후 1473-byte UDP의 장치 거절 미관측으로 실패; Modbus 복원 성공. 최종 시험에서 관측 한계를 별도 판정 |
| `bin\iec61850-hardware-deadline\status.json` | completed, Restored=true; silent timeout 1/peer stop 1, 최종 복구, 가용 heap 기준값과 동일 |
| `bin\iec61850-server-malformed-20261028-0110\status.json`, `fault-*.bin`, `native-*.log` | completed, 1,493 checks/손상13종/응답 폐기1종, 독립 Association14/정상 requests1,344, 제어 검증·실행 각각29, warmup 후 GC 가용 heap730,140 B 유지; Restored=true, Error=null |
| `bin\iec61850-server-malformed-20261028-0030\status.json`, `bin\iec61850-server-malformed-20261028-0040\status.json` | 선행 실패: 관리 채널이 변경한 수신 제한시간을 원시 EOF 확인에 재사용. EOF 확인 전에 별도 기한을 다시 설정하도록 시험 도구 수정; 두 실행 모두 복원 성공 |
| `bin\iec61850-server-malformed-20261028-0100\status.json` | 선행 실패: 시험 서버가 제어 생성자의 ctlModel 조회를 Operate로 오인. 조회·실행·EOF를 각각 검사하도록 수정; 복원 성공 |
| `bin\iec61850-client-malformed-20261028-0400\status.json`, `fault-*.bin`, `fault-*.bin.requests`, `peer-*.log` | completed, 1,726 checks/손상응답12종/응답폐기1종, 정상15cycles/900values/300access errors, MCU Operate송신1/재호출무송신1, PC 실행1; baseline 후 heap759,240 B, Restored=true, Error=null |
| `bin\iec61850-client-regression-20261028-0410\status.json` | 기존 전체 Client 흐름 completed: 575 checks/정상5cycles/301values, timeout1/peer stop1, control/enhanced/SBO enhanced/data-report 각각3cycles; 복원 성공 |
| `bin\iec61850-timeout-comparison-20261028-0745\status.json`, `measurements.jsonl`, `peer-*.log` | Read/Operate 각각 예외/비예외3회: 평균 Read6,256.7→3,003.3ms, Operate7,510→3,010ms. 12comparisons/정상14cycles/840values/280errors, Faulted·무재송신·실행1회, baseline 후 heap759,060 B 유지; completed/Restored=true/Error=null |
| `bin\iec61850-timeout-comparison-final-20261028\status.json`, `comparison.json`, `measurements.jsonl` | 최종 기한 경계 검사 후 재확인: Read6,253.3→3,006.7ms, Operate7,510→3,010ms; 각각3회. 12comparisons/정상14cycles/840values/280errors, heap759,060 B 유지, completed/Restored=true/Error=null |
| `bin\iec61850-timeout-legacy-regression-20261028-0800\status.json` | timeout 반환 확장 후 기존 전체 실물 Client 흐름 completed, control/enhanced/SBO enhanced/data-report 각각3cycles 및 복원 성공 |
| `bin\iec61850-client-malformed-20261028-0330\status.json` | 선행 실패: MCU는 손상응답 거절했으나 PC drain이 TCP RESET을 EOF와 구분하지 않아 실패. ConnectionReset만 명시적으로 허용·기록하도록 수정; 복원 성공 |
| `bin\iec61850-client-malformed-20261028-0340\status.json`, `bin\iec61850-client-malformed-20261028-0350\status.json` | 선행 실패: 소켓 readiness 약3초와 예외 전파 후 반환 약7.51초를 하나의 상한으로 판정. 소켓 기한을 별도 검사하고 총 반환 시간을 기록하도록 수정; 복원 성공 |

별도 세션 산출물에도 다음 완료 기록이 있습니다. 저장소가 아닌 해당 시험 세션의 `files` 아래에 있습니다.

| 파일/디렉터리 | 확인한 내용 |
|---|---|
| `hardware-modbus-rtu-20261027.csv` | Passed, 177 reads/2 fault cycles, CRC·부분·유휴 복구와 데이터 보존 |
| `hardware-modbus-ascii-extended.log` | Extended PASS, LRC/Hex/CRLF/초과 길이/문자간 만료 복구 및 원본 값 복원 |
| `dnp3-serial-outstation-soak-20261027-1900\status.json`, `mcu-debug.log` | 2시간, 57 cycles/684 operations/57,456 measurements, retries/CRCerrors/frameTimeouts=0, Modbus 복원 성공. **오류 주입 시험이 아님** |
| `virtual-recovery-20261027.log` | 실제 가상 nanoCLR에서 Modbus/DNP3/BACnet/SNMP/MMS 총 249 checks 통과. 메모리 전송 시험이며 실물 증거 아님 |

soak 시간은 cycle 완료·최종 복구·펌웨어 복원 때문에 설정값을 넘을 수 있습니다.
짧은 soak의 평탄한 가용 heap은 관측 기간의 결과이며 장기 누수 부재 보장이 아닙니다.

## 2026-10-28 보강 기록

완료한 DNP3 CRC 입력/출력 fixture는 이 저장소 코덱을 재사용합니다. 독립 구현 간
CRC 상호운용 결과는 아니며, 앞뒤 정상 수신은 원본 OpenDNP3 피어로 별도 확인했습니다.
SNMP BER 입력과 응답은 독립 SharpSnmpLib을 사용하며, 손상 SET의 두 값과 쓰기 횟수를 함께 검사합니다.

가상 nanoCLR 변경도 미검증 코드로 남기지 않고 249 checks 실행을 완료했습니다.
실물 보드가 있는 환경에서는 다음 실물 검증을 우선하며 가상 시험 범위를 추가 확대하지 않습니다.

IEC Server의 새 시험은 저장소 코덱으로 만든 손상 입력을 사용합니다. 손상 구현 자체가
독립 구현이라는 뜻은 아니며, 매 장애 뒤 정상 통신·scalar 값 보존은 원본 libiec61850
Client로 확인합니다. 값은 손상 주입 전에 설정하며 정상 복구 전에 다시 덮어쓰지 않습니다.
응답 유실은 PC 채널이 실제 MCU 응답을 완전히 수신한 뒤 폐기하는 방식입니다.
MCU 실행 횟수 +1, PC 송신 1회, Faulted 뒤 재호출 송신 0회를 검사하며,
물리 패킷 유실·MCU Client의 재실행 방지·분산 exactly-once 보장을 뜻하지 않습니다.

MCU Client의 별도 시험도 손상응답 12종 뒤 Faulted·후속 Read 무송신 거절·새 연결 복구를
실물 확인했습니다. 제어 1종은 PC 메모리 서버에서 실행 뒤 **응답 송신 전에** 폐기합니다.
MCU 소켓 대기 기한과 MCU 송신 횟수, PC 제어 callback 횟수를 함께 검사합니다.
정상 복구는 매번 원본 libiec61850를 재시작해 수행하며, 손상 응답 자체는 독립 구현이 아닙니다.
실제 readiness 대기2,990ms/시험 코드까지 반환7,510ms였으므로 3초 내 API 반환을 주장하지 않습니다.
현재 확인은 Faulted 세션의 재호출 무송신이며 재접속 이후 앱 정책이나 영속 중복 제거는 별도입니다.

추가 timeout A/B에서는 선택적 `IMmsTimeoutChannel`과 Read/normal Operate 반환 API를 사용해
예외를 생성하지 않을 때 약3초에 반환됨을 실물 확인했습니다.
기존 API는 timeout 예외를 유지합니다. 채널 읽기 전0~20ms/후0ms로 추가 지연은
채널 내부 예외 경로에 집중됐으며, nanoCLR 내부 세부 원인까지 특정한 것은 아닙니다.
EOF·소켓·형식/서비스 오류와 Association/enhanced 등 다른 API는 여전히 예외입니다.
정확한 3회 범위 및 비교 방법은 TCPClient README에 기록했습니다.

MQTT는 범용 Generator에 앞서 장치 간 공유 샘플을 구현했습니다.
당시 M2Mqtt는 기존 의존성이 아니라 샘플에서 새로 추가한 것이며, 현재
[MQTT 매핑 샘플](../Samples/MQTT/VagabondK.nF.MQTT.Mapping/README.md)은 생성기와 자체 MQTT를 사용합니다.
실물 MCU B와 MQTTnet 브로커/PC A 사이에서 JSON 상태를 서로 다른 이름의 수신 모델에 반영합니다.
`bin\mqtt-sharing-20261028-final`에서 A→B 세 값/B→A 두 값, 중복/손상/retained 재생/재접속 중복 거절,
3초 stale 및 새 값 복구, 원래 Modbus 복원·COM6 읽기를 확인했습니다.
`bin\mqtt-sharing-normal-20261028-final`에서는 진단 UDP 없는 일반 보드 앱의 30회 게시가
PC A 모델에 반영되는 것과 원래 Modbus 복원·COM6 읽기를 확인했습니다.
이 시험은 실물 MCU 두 대·실제 센서·명령 실행·영속 exactly-once·Sparkplug를 검증한 것이 아닙니다.
샘플 형식별 범위는 [대조표](../Samples/MappingCoverage.md)에 있습니다.

별도의 [Sparkplug B 공유 샘플](../Samples/MQTT/VagabondK.nF.MQTT.Sparkplug.Mapping/README.md)은
MCU를 자체 MQTT 3.1.1 Edge B로 동작시키고 PC Edge A와 Boolean/Int32/Float/Double을 공유합니다.
M2Mqtt의 일반 Publish가 아니라 CONNECT Last Will의 string 인수 제약을 피하기 위한 구현입니다.
`bin\sparkplug-native-20261028-final-r5`에서 실제 네 값의 양방향 반영, birth·alias 부분 갱신,
stale·gap·rebirth, device death·이전 alias 거절, 재접속·NCMD 및
MCU 강제 TCP 종료 시 bdSeq=201의 바이너리 NDEATH Will을 확인했습니다.
`bin\sparkplug-native-normal-20261028-final-r5`, `r6`, `r7`은 최종 수정 후 세 번 연속
일반 앱 30회 공유와 정상 death/DISCONNECT, 비정상 Will 미발행을 확인한 결과입니다.
일반 모드는 B의 수신 모델을 UDP 조회하지 않으므로 해당 값 검증은 진단 모드 결과와 구분합니다.
네 최종 실행 모두 `Status=completed`, `Restored=true`, `Error=null`이며 원래 Modbus COM6 읽기도 완료했습니다.

반복 중 드러난 첫 읽기 이전 idle poll deadline 만료는 연결 fault가 아닌 빈 poll로 처리하도록 수정했습니다.
ACK·부분 프레임 timeout은 계속 fault입니다. 또한 미수신 데이터가 남은 소켓을 즉시 닫았을 때
마지막 death/DISCONNECT가 누락되고 Will이 발행되던 경로는 DISCONNECT 후 3초 내
브로커 EOF를 확인하고 소켓을 닫도록 수정했습니다. 종료 대기 중 추가 callback/ACK는 하지 않습니다.
PC 관찰자도 NCMD를 Edge 데이터 sequence와 구분하고 callback 오류를 최종 검사에 전파합니다.
실패 실행의 로그·복원 결과도 원래 출력 폴더에 보존했습니다.

최종 `bin\mapping-samples-sparkplug-20261028-final.log`는 여섯 PC suite와
아홉 MCU 샘플 빌드의 통과 기록입니다. Sparkplug 계약 1,296개, 자체 MQTT 계약 102개,
기존 JSON 계약 177개를 포함합니다. 이는 전체 Sparkplug 명세/TCK, 모든 metric 형식,
TLS·인증·영속 재전송·exactly-once 적합성을 뜻하지 않습니다.

이후 정적 매핑 생성기를 추가하고 자체 MQTT로 전환한 결과는 맨 위의 2026-10-29 기록을 참조하십시오.

## MQTT·Sparkplug 라이브러리 분리와 송신 할당 개선

MQTT 전송은 `VagabondK.nF.MQTT.dll`, Sparkplug 코덱·송신 세션·설정 가능한 수신기는
`VagabondK.nF.MQTT.Sparkplug.dll`로 분리했습니다. 고정 IP·Sensor 이름·네 개 device 값·3초 기준은 샘플에만 있습니다.
Sparkplug DLL은 MQTT DLL도 참조하지 않습니다. MCU 샘플은 두 DLL을 ProjectReference로 사용하고
PC 시험은 같은 소스로 만든 별도 PC DLL의 공개 API를 참조합니다.
`bin\mqtt-library-package-test.log`와 `bin\sparkplug-library-package-test.log`는
프로젝트·샘플 소스 없이 로컬 preview 패키지만 복원한 소비 앱의 빌드 기록입니다.

송신의 매번 생성하던 Body·완성 body·packet·topic UTF-8 중간 배열을 연결 소유 버퍼로 바꿨습니다.
128바이트 caller payload, PC Release, discard 채널, warmup 1000회 후 QoS0 10000회에서
게시당 임시 할당은 **1472→0바이트**입니다. 해당 PC 기록 시간은 약 1.48→0.91μs이며
M2Mqtt 비교·MCU 수치·네트워크 성능이나 전체 무할당을 뜻하지 않습니다.
전후 로그는 `bin\native-mqtt-library-perf-before.log`와 `bin\native-mqtt-library-perf-after.log`에 있습니다.
연결당 약 2KB 송신 버퍼 상주라는 절충이 있으며 수신·protobuf 인코딩은 여전히 할당합니다.

분리·최적화 후 `bin\sparkplug-library-hardware-20261028`의 실물 양방향·장애·binary Will과
`bin\sparkplug-library-normal-20261028`의 일반 30회 공유·정상 death가 통과했습니다.
두 실행 모두 `Status=completed`, `Restored=true`, `Error=null`이며 원래 Modbus COM6 읽기를 확인했습니다.
지원 범위는 제한형 MQTT/Sparkplug 프로파일이고 M2Mqtt 전체 기능 동등성이나 전체 Sparkplug TCK를 주장하지 않습니다.
`bin\mapping-samples-libraries-20261028.log`는 여섯 PC suite/아홉 MCU 빌드의 최종 회귀 기록이며,
Sparkplug wire 계약 1296개·공개 라이브러리 계약 295개·MQTT 계약 106개(송신 0 할당 포함)·JSON 계약 177개를 포함합니다.
`bin\mqtt-json-after-library-20261028`에서는 runner 변경 뒤 기존 M2Mqtt JSON 실물 양방향 시험도
다시 통과했고 원래 Modbus 앱 복원·COM6 읽기를 확인했습니다.
SNMP 초과 UDP는 MCU 소켓 도달을 입증할 수 있는 별도 환경에서 최대 길이 거절을 확인해야 합니다.

## MQTT 3.x 기능 확장과 native CLR 회귀

MQTT에 사용자/binary 암호·가변 Will QoS/retain, UTF-8 이름, QoS2, 다중 구독/해제,
선택형 ACK 재전송·RAM 세션·bounded worker와 인증 완료 stream adapter를 추가했습니다.
`bin\mqtt-expanded-pc-final-20261028.log`는 MQTT 계약 200개·MQTTnet broker 검사 19개,
Sparkplug wire 1296개·공개 API 295개·JSON 계약 177개의 통과 기록입니다.
QoS1/2 ACK의 ID·단계·DUP·지연 중복, 재전송 제한, 부분 프레임 재전송 금지,
브로커 Session Present와 독점 세션 소유권을 확인합니다.
TLS 검사는 PC TLS 1.2의 QoS2, 신뢰하지 않는 CA/틀린 서버 이름 거절이며 MCU TLS 검증은 아닙니다.
worker는 완료 전 payload 복사, 큐 초과 거절, terminal 오류와 모든 pending 작업의 실패 전파를 검사합니다.

최초 실물 비교 `bin\mqtt-compare-native-20261028-104422`는 앱 시작 전 CLR 부팅 실패로
데이터를 얻지 못했고 일반 wire 복원도 실패했습니다. 이 실행을 성능 결과에 포함하지 않습니다.
부팅 로그의 deployment 파티션 주소 `0x1B0000`에 체크섬 확인된 원래 Modbus 이미지만
ROM 경로로 복원하고 readback hash 및 COM6 holding 0..2=123/456/65535를 확인했습니다.
실패 당시 status는 보존하며 별도 `recovery.json`과 `bin\mqtt-expanded-rom-restore-20261028.log`가 복원 증거입니다.
부트로더·CLR·설정 파티션은 변경하지 않았습니다.

동일 오류를 가상 nanoCLR에서도 메타데이터 해석 시 AccessViolation으로 재현했습니다.
worker/stream adapter 분리 후 `Func<int,bool>`를 구체 delegate `MqttReadReady`로 바꾸자
전체 형식 해석이 통과했습니다. native 문자열 indexer의 보조 평면 문자 처리 차이도
native UTF-8 바이트 검증으로 보완했습니다.
이제 기존 `Build\Test-Virtual.ps1`이 MQTT DLL 전체 메타데이터와 QoS2·재전송·세션·worker를
함께 실행합니다. `bin\mqtt-expanded-virtual-20261028.log`는 263개 검사의 통과 기록입니다.
`bin\mapping-samples-mqtt-expanded-20261028.log`에는 여섯 PC suite/아홉 MCU 빌드의 회귀 결과가 있습니다.

`bin\mqtt-features-20261028-110018`에서는 ESP32-S3에서 binary 암호, QoS2 송수신,
한글/보조 평면 topic의 원본 바이트, 다중 구독/해제, retained 재수신,
persistent broker 세션 재개, worker 완료 및 MQTT 3.1 인증/QoS2를 확인했습니다.
PC broker는 게시 4개·인증 연결 3개·비정상 Will 0개를 확인했고, 원래 Modbus 앱 복원·COM6 읽기를 마쳤습니다.
Will QoS2/retain은 이 앱의 설정이며 강제 단절 후 실제 Will 발행 검사는 Sparkplug 진단과 구분합니다.
당시 MQTT 5·복수 송신 in-flight는 미구현이었으며 아래 후속 확장에서 추가했습니다.
재부팅 간 영속성과 M2Mqtt 전체 기능 동등성은 현재도 주장하지 않습니다.

최종 동일 이미지 성능 비교는 `bin\mqtt-expanded-pooled-summary-20261028`에 있습니다.
정·역순 두 세트/8번 배포로 구현별 각 조건 360회, warmup 포함 3360개 게시를 검사했습니다.
복사 없는 송신과 256바이트 수신 선읽기 후 전체 평균은 M2Mqtt보다 약 9.5~50.6% 짧았지만,
자체 128바이트 QoS0의 1060/1500ms 두 건을 제외하지 않았으며 그 최초 세트 평균은 오히려 느렸습니다.
추가 한 세트는 병렬 빌드 없이 실행했고 최초/추가 결과를 따로 보존했습니다.
최대 지연 원인은 확정하지 못했으며 개선된 평균을 tail latency 보장으로 해석하지 않습니다.
이미지는 자체 95,512/M2Mqtt 135,808바이트, cold 연결 managed heap 증가는 2,568/8,652바이트입니다.
조건·중앙값/P95·최대·제약은 [MQTT README](../Source/MQTT/VagabondK.nF.MQTT/README.md)에 있습니다.

확장 후 `bin\sparkplug-expanded-hardware-20261028`의 양방향/장애/binary Will 진단과
`bin\sparkplug-expanded-normal-20261028`의 일반 앱 30회 공유·정상 death도 다시 통과했습니다.
모든 최종 비교·기능·Sparkplug 실행은 `Status=completed`, `Restored=true`, `Error=null`이고
원래 Modbus 복원·COM6 읽기를 마쳤습니다. 일부 복원 시 wire 탐지/시작 지연은 기존 제한형 재시도 후 확인했습니다.
최종 MQTT/Sparkplug 로컬 피드는 각각 `bin\nuget\mqtt-expanded-final-20261028`,
`bin\nuget\sparkplug-expanded-final-20261028`이며 대응하는 `*-expanded-final-package-test-20261028.log`가
프로젝트·샘플 소스 없는 소비 빌드의 통과 기록입니다.
native 로딩 수정 전 임시 `mqtt-expanded-20261028`의 nupkg/체크섬 manifest는 회수했습니다.

## MQTT 5·복수 in-flight·native TLS 최종 확장

후속 채택 결정과 PC 지속 연결 검증은 아래 「MQTT 기본 채택과 반복 안정성」 절을 참조하십시오.

위 MQTT 3.x 기록 이후 MQTT 5 표준 속성의 자료형/packet context·중복/UTF-8/길이 검증,
CONNACK 협상·ACK reason·서버 DISCONNECT·AUTH/재인증·메타데이터 callback·수신 alias를 구현했습니다.
broker 할당 ClientId와 Session Expiry, Server Keep Alive도 처리합니다.
`PublishBatch`는 설정형 window와 broker Receive Maximum을 준수하며 순서 역전 QoS1/2 ACK,
배치 내 일부 거절, timeout 후 RAM 세션 재개를 처리합니다.
기본 동기 Publish와 기본 용량은 유지하고 이름·필터·incoming/outgoing capacity·deadline을 설정 가능하게 했습니다.

최종 PC 기록 `bin\mqtt5-pc-final-20261028.log`는 MQTT 3.x 200개·MQTT 5/배치 88개·
MQTTnet broker 29개 및 기존 Sparkplug/JSON 계약의 통과 기록입니다.
`bin\mqtt5-virtual-final-20261028.log`는 native AUTH/alias/pipeline을 포함한 nanoCLR 269개 통과입니다.
`bin\mapping-samples-mqtt5-final-20261028.log`는 6개 PC suite·9개 MCU sample build 통과입니다.
AUTH는 스크립트 및 native memory-channel 시험이며 실물 broker enhanced AUTH 지원 인증은 아닙니다.

`NanoTlsMqttChannel`은 native TLS 1.2/선택형 1.3, explicit CA/host 검증과 optional client certificate를 받습니다.
초기 실물 TLS 음성 시험에서 `VerifyPeer | CertificateRequired` 조합이 잘못된 호스트를 허용했습니다.
native mbedTLS 구현은 이 값을 mask가 아닌 `CertificateRequired` 단일 값과 동등 비교하므로,
단일 값으로 수정하고 잘못된 호스트/무관한 trust root 거절과 정상 TLS를 다시 확인했습니다.
초기 실패 기록은 `bin\mqtt-features-20261028-113055`에 보존했습니다.
이때 wire 복원도 실패했으나 알려진 deployment 0x1B0000만 ROM 복원했으며
`bin\mqtt5-rom-restore-20261028.log`, `bin\mqtt5-restored-com6-20261028.log`가 복원·COM6 정상 읽기 기록입니다.
검증 값 수정 직후 `113301` 재시험에서는 응답 timeout이 한 번 관측됐습니다.
이 기록도 숨기지 않았으며, 당시 단계/원인은 확정하지 못했습니다. ROM 복원·COM6은 완료했습니다.

최종 실물 기능 기록 `bin\mqtt-features-20261028-115505`는 22개 검사,
broker 9 publications/5 connections/Will 0, `completed/Restored=true/Error=null`입니다.
MQTT 5 content type/User Property/Subscription Identifier의 QoS2 왕복과 QoS0/1/2 pipeline,
TLS 1.2 정상/인증서 음성 검사/idle Poll/PING/DISCONNECT를 포함합니다.
앞선 `113433`, `114038` 실행도 같은 TLS 음성/정상 경로를 통과했습니다.
장시간 TLS soak·TLS 1.3·상호 인증·모든 firmware/broker 조합까지 검증했다는 뜻은 아닙니다.
Sparkplug 진단과 일반 30회 공유는 `bin\sparkplug-mqtt5-final-diagnostic-20261028`,
`bin\sparkplug-mqtt5-final-normal-20261028`에서 확인했고 모두 원본 Modbus를 복원했습니다.

최종 제품 소스의 성능 고정 세트는 native115620→M2 115740→M2 115930→native120049입니다.
조건당 구현별 180개, timed 1440 + warmup 240 = byte 검증 1680 publications입니다.
평균 왕복은 128/QoS0 자체13.389/M2 21.056ms, 128/QoS1 15.444/27.667ms,
512/QoS0 19.556/28.000ms, 512/QoS1 21.611/32.278ms입니다.
자체 최대는 20/30/30/40ms, 배포 이미지 110704/135808바이트,
cold managed heap 증가 3036/8652바이트입니다. 최종 native 두 이미지 SHA256은
`79B6A1CAE879374B23C1726A094F32F6951468DFA67F62F40D179447CC3F10A4`로 같습니다.
모든 표본·source/hash·최초 이상치·서로 다른 후보 이미지의 결과를 보존했으며 서로 다른 이미지끼리 합치지 않았습니다.
최종 상세 CSV/JSON은 `bin\mqtt5-clientid-final-summary-20261028`입니다.
마지막 native의 wire 복원 실패는 deployment-only ROM 경로로 복구해 COM6 값을 확인했습니다.
이는 단일 MQTT 3.1.1 echo 부하이며 MQTT 5/TLS/포화 처리량 또는 tail latency 보장이 아닙니다.

최종 로컬 패키지 피드는 `bin\nuget\mqtt5-final-20261028`,
`bin\nuget\sparkplug-mqtt5-final-20261028`입니다. 대응하는 package-test 로그로
프로젝트·샘플 source 참조 없는 소비 빌드와 MQTT 5·batch·native TLS API 참조를 확인합니다.

모든 실물 시험은 격리된 평가 보드와 메모리 모델만 사용합니다.
배포 전 복원 이미지 체크섬을 확인하고, 성공·실패 모두 peer/포트를 정리한 뒤
`Build\Restore-ModbusHardware.ps1`로 원래 앱을 복원하고 COM6 읽기 성공까지 확인합니다.

## MQTT 기본 채택과 반복 안정성

당시 새 MQTT 기능의 기본 전송을 자체 라이브러리로 정하고 JSON/M2Mqtt 실행은 비교 기록으로 보존했습니다.
이후 JSON 샘플도 자체 `MqttConnection`으로 전환했고 전송 토픽·JSON 데이터 계약은 유지했습니다.
현재 두 MCU 샘플의 `.DataSharing` 이름은 `.Mapping`으로 통일했으며 통합 PC 테스트 이름은 유지합니다.
`MqttSoakChecks`를 공유 `SoakRunner`와 `Build\Test-Soak.ps1 -Protocol MQTT`에 연결했습니다.
기존 여섯 프로토콜의 기본 실행 목록과 호출 방법은 유지합니다.

PC .NET 10.0.12/MQTTnet 4.3.7.1207에서 워밍업 후 600.09초, 20회 자원 표본을 수집했습니다.
평문 MQTT 3.1.1·인증된 TLS 1.2 MQTT 5 지속 연결 normal,
메모리 채널 MQTT 5 손상/거절/배치 복구 계약,
worker·구독 해제/재구독·정상 DISCONNECT/persistent session 재개를 각각 5,524회 반복했습니다.
normal 및 transport-cycle은 각각 132,576회의 단일/배치 왕복 묶음을 확인했습니다.
512B payload sequence/내용과 MQTT 5 metadata를 검사하고 QoS0/1/2·혼합 배치를 사용합니다.

관리 힙 최대 증가는 기준 대비 10,504B, 마지막은 -926,168B였습니다.
핸들은 최대 +26/마지막 +1, 스레드는 최대 +3/마지막 -2였습니다.
1MiB/32 handles/8 threads 제한을 모든 표본에서 통과했으며 단조 증가가 없다는 일반적 보장은 아닙니다.
기록: `bin\mqtt-stability-20261028\run-20261028-122313-f923c813b9484f6ab0bb26c744351461`.
기존 계약 회귀도 `bin\mqtt-stability-contract-regression-20261028.log`에서
MQTT 3.x 200/MQTT 5 88/broker 29 및 Sparkplug/JSON 통과를 확인했습니다.

이는 PC loopback 10분 검사이며 MCU 장시간 TLS, TLS idle Poll 반복, broker 재시작·물리 단절,
수시간/수일 운용 또는 MQTTnet 외 broker 적합성을 입증하지 않습니다.
손상 입력은 memory channel 시험이고 transport-cycle은 정상 DISCONNECT 이후의 세션 재개입니다.
실물 보드·펌웨어·방화벽·시스템 trust store는 이번 작업에서 변경하지 않았습니다.
