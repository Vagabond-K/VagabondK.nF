# SNMPv1/v2c MCU Agent

`VagabondK.nF.SNMP`(BER/Agent/Trap 생성)와 `VagabondK.nF.SNMP.UDP`(UDP 어댑터)는
Modbus·DNP3·BACnet에 의존하지 않습니다.
이 Host는 `DemoStore`로 system 그룹과 기업 OID `1.3.6.1.4.1.32473.1`(문서용 예시 PEN) 아래
8개 스칼라를 제공합니다. 저장소는 RAM 값만 갱신하며 물리 I/O를 수행하지 않습니다.

| OID | 이름 | 형식 | 쓰기 | 초기값/규칙 |
|---|---|---|---|---|
| `.1.0` | Setpoint | Integer32 | 가능 | 250, -100000..100000 |
| `.2.0` | Label | OCTET STRING | 가능 | `pump-1`, 최대 32바이트 |
| `.3.0` | AcceptedWrites | Counter32 | 불가 | 적용된 Set 수 |
| `.4.0` | Level | Gauge32 | 가능 | 50, 100 이하 |
| `.5.0` | Delay | TimeTicks | 가능 | 1500 |
| `.6.0` | Total | Counter64 | 가능 | 2^32 (v1에서는 숨김) |
| `.7.0` | Peer | IpAddress | 가능 | 192.0.2.10 |
| `.8.0` | Profile | OBJECT IDENTIFIER | 가능 | 1.3.6.1.4.1.32473.2.1 |

community는 읽기 `public`, 읽기/쓰기 `private`입니다. 시험용 값이므로 실제 망에서는 반드시 바꾸십시오.

기본 빌드는 소켓을 열지 않습니다. 격리된 실물 시험을 실행하려면 `SnmpHardwareTest=true`를 명시해야 하며,
기존 Wi-Fi AutoConnect 설정으로 UDP 161을 열고 `SNMP MCU READY`를 출력합니다.
이 실물 시험과 PC 독립 UDP 시험은 `DemoStore(true)`로 읽기 전용 Counter32 `.9.0`
`RejectedDatagrams`도 노출합니다. 수신 루프가 잡은 `SnmpProtocolException` 수이며
잘못된 community나 네트워크 단계 폐기는 세지 않습니다. 기본 `DemoStore()`의 8개 객체 계약은 유지합니다.

## 지원 범위와 저장소 계약

SNMPv1/v2c의 Get, GetNext, Set과 v2c 전용 GetBulk 및 SNMPv2-Trap 생성/송신을 지원합니다.
v2c는 noSuchObject/noSuchInstance/endOfMibView 예외 값을, v1은 noSuchName을 사용하며
v1에서는 Counter64 객체를 노출하지 않습니다. 응답이 최대 메시지 크기를 넘으면 tooBig으로 응답합니다.
Set은 모든 varbind의 형식·범위를 먼저 검증한 뒤 순서대로 적용하며,
사전 검증에서 하나라도 실패하면 아무것도 바꾸지 않고 오류 index를 반환합니다.
적용 도중 사용자 setter/저장소 예외가 발생하면 앞선 변경을 되돌리지 않으며, 요청 전체의 원자성은 보장하지 않습니다.
잘못된 BER, 비최소 정수 인코딩, 알 수 없는 PDU/값 형식, 잘못된 community에는 응답하지 않습니다
(형식 오류는 `SnmpProtocolException`으로 알리므로 수신 루프에서 잡고 계속 처리합니다).
SNMPv3(USM 인증/암호화), Inform, v1 Trap, 테이블 객체 자동 생성은 지원하지 않습니다.
community는 평문이므로 신뢰할 수 있는 관리망과 방화벽 제한이 필요합니다.

`ISnmpValueStore`의 객체 개수/OID/형식은 생성 이후 불변입니다. `Read`는 값 스냅샷을,
`AcceptsWrite`는 적용 전 검증을, `QueueWrite`는 검증된 값의 적용을 구현합니다.

`SnmpAgent.Process`와 `SnmpUdpSession.ProcessNext`는 한 스레드에서 직렬 호출합니다.
소켓 생성·바인딩·시간 제한·수명 관리·해제는 호출자 책임이며 세션은 소켓을 닫지 않습니다.
UDP 데이터그램은 최대 1472바이트이며 크기 초과·0바이트·형식 오류 입력 뒤에도 다음 요청을 처리합니다.

## 자동 재실행

기존 COM3 MCU에 배포하고 독립 SharpSnmpLib 12.5.7 Manager가 LAN에서 직접 통신합니다.
COM6 데이터선은 사용하지 않습니다.

```powershell
.\Build\Test-SnmpHardware.ps1 -AllowDeviceChanges -Iterations 256
# 보드 없는 독립 UDP 회귀:
dotnet run --project .\Tests\SNMP\VagabondK.nF.SNMP.Interop -c Release -- --stress 1024
# 독립 2종 패키지 / 타 프로토콜 비의존 소비 이미지:
.\Build\Pack.ps1 -LibrarySet SNMP
.\Build\Test-Packages.ps1 -LibrarySet SNMP
```

하드웨어 실행기는 배포 후 재부팅·Wi-Fi 연결을 최대 90초 기다린 뒤 시험하며,
장치 변경 전에 복원 이미지 SHA256을 검사합니다.
성공/실패 모두 `finally`에서 `Build\Restore-ModbusHardware.ps1`로 원래 Modbus 앱을 복원하고
COM6 holding `123,456,65535` 읽기를 요구합니다. 기본 장치 IP는 격리 평가 보드 `192.168.0.187`입니다.
방화벽을 자동 변경하지 않습니다. MCU 시험은 Trap 수신을 확인하지 않으며, Trap은 PC 회귀에서 확인합니다.

독립 Manager는 정상 2-varbind SET을 잘라 보내거나 sequence/version tag, BER 길이,
trailing byte를 손상하는 10종을 실행합니다. 각각 `.9.0`이 정확히 1 증가하고 무응답이어야 하며,
두 값과 AcceptedWrites가 변하지 않아야 합니다. 매번 뒤따르는 정상 SET/GET과 쓰기 1회 증가도 확인합니다.
1473/4000바이트 UDP 두 경우도 송신 후 무응답·값 보존·정상 GET/SET을 확인합니다.
PC loopback은 초과 크기도 정확한 장치 측 거절을 요구하지만, 실물에서는 IP 분할로 소켓에
도달하지 않을 수 있습니다. 거절 계수가 증가하지 않으면 `TRANSPORT ONLY`로 명시하며
MCU 애플리케이션의 크기 제한 검증으로 인정하지 않습니다. 거절 2회 이상 증가 등은 실패합니다.

## 실물 결과

2026-10-25 `bin\snmp-hardware-20261025-123524`에서 독립 SharpSnmpLib과 직접 UDP로
system 그룹, v1/v2c Walk·BulkWalk, GetBulk, 예외 값, tooBig, 전체 형식 Set과 Set 오류 행렬,
잘못된 community 무응답, 256회 난수 Set/Get/GetNext를 포함한 3,994개 검증에 성공했습니다.
종료 후 원본 Modbus를 복원하고 COM6 `123,456,65535` 읽기도 성공했습니다.
최초 실행은 배포 직후 Agent 준비 전 요청에서 시간 제한을 초과해 실패했으며, 준비 대기를 추가한 다음 실행이 성공했습니다.

2026-10-27 `bin\snmp-hardware-20261027-221235`에서 BER 10종 각각 장치 거절·복구,
초과 UDP 2종 송신 후 정상 복구와 256회 난수 SET/GET을 포함한 6,416개 검증을 완료했습니다.
두 초과 UDP는 모두 장치 거절 계수 증가가 없어 애플리케이션 도달·크기 제한은 미확인입니다.
IP 분할 단계 폐기 가능성이 있으나 정확한 폐기 원인은 확정하지 않았습니다.
직전 `bin\snmp-hardware-20261027-220910`은 이를 장치 거절로 요구해 실패한 기록이며,
최종 실행은 관측 가능한 BER 거절과 전송 경로만 확인한 초과 UDP를 분리해 보고합니다.
두 실행 모두 원래 Modbus 복원과 COM6 읽기에 성공했습니다.
