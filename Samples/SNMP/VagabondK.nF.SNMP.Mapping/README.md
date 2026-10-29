# SNMP 정적 매핑 MCU UDP 샘플

실제 nanoFramework MCU를 SNMPv1/v2c Agent로 구동합니다. PC 루프백 샘플이 아닙니다.
저장된 Wi-Fi 설정으로 연결하고 UDP **1161**에서 요청을 받습니다.
GPIO나 실제 액추에이터는 구동하지 않으며 값은 RAM 모델입니다.
평가용 community는 읽기 `public`, 쓰기 `private`입니다. 평문이므로 격리 LAN에서만 사용하십시오.
SNMPv3/인증·암호화는 지원하지 않습니다.

`DeviceData.cs`의 `[SnmpInteger]`·`[SnmpOctetString]` 등 형식별 특성으로 정의한 값이 `DeviceData.SnmpStore`를 통해 Agent와
직접 연결됩니다. setter가 private인 `Setpoint`도 외부 SET 후 GET으로 확인할 수 있습니다.
기존 `[SnmpScalar(oid, SnmpType.형식)]`도 같은 실행 코드를 생성하며 자동 Trap 송신은 하지 않습니다.
모든 OID의 접두사는 문서용 `1.3.6.1.4.1.32473.10`입니다.

| 접미사 | 모델 | SMI 형식 | 초기값 | SET |
|---|---|---|---|---|
| `.1.0` | Setpoint | Integer32 | -250 | 허용 |
| `.2.0` | Label | OctetString | pump, 최대 32바이트 | 허용 |
| `.3.0` | BootCount | Counter32 | 1 (예시 상수, 실제 재부팅 횟수 아님) | 거절 |
| `.4.0` | Level | Gauge32 | 50 | 허용 |
| `.5.0` | Delay | TimeTicks | 1500 | 허용 |
| `.6.0` | Total | Counter64 | 4294967296 | 허용 |
| `.7.0` | Peer | IpAddress | 192.0.2.10 | 허용 |
| `.8.0` | Profile | ObjectIdentifier | 1.3.6.1.4.1.32473.10 | 허용 |

GETNEXT/GETBULK는 선언 순서가 아닌 숫자 OID 순서입니다.
SNMPv1에서는 Counter64가 보이지 않습니다. 앱에서 값에 접근할 때는 생성 저장소에
넘긴 것과 동일한 잠금 객체를 사용해야 합니다. 배열 복사와 읽기별 값 할당은 남아 있으며 무할당 샘플이 아닙니다.
추가 계약과 제한은 `Source\SNMP\VagabondK.nF.SNMP.Mapping\README.md`를 참조하십시오.
8/32/128개 매핑의 플래시 사용량·힙·처리 시간과 반복 후 GC 회복을 측정하는 별도 시험기는
`Tests\SNMP\VagabondK.nF.SNMP.Mapping.Hardware\README.md`에 있습니다.

## 빌드

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  .\Samples\SNMP\VagabondK.nF.SNMP.Mapping\VagabondK.nF.SNMP.Mapping.nfproj `
  /t:Rebuild /p:Configuration=Release
dotnet run -c Release --project .\Tests\SNMP\VagabondK.nF.SNMP.Mapping.Tests
```

`bin\Release\VagabondK.nF.SNMP.Mapping.bin`은 보드 펌웨어의 시스템 어셈블리와
Wi-Fi 설정을 확인한 뒤 배포하십시오. Generator/Roslyn DLL은 MCU에 배포하지 않습니다.

## 전용 실물 시험기

다음 명령은 이 저장소의 **Geekble Nano ESP32-S3 COM3**, 저장된 Wi-Fi와
기본 IP `192.168.0.187`, 원래 Modbus 앱/RS-485 **COM6** 구성 전용입니다.
일반 사용자 보드에 그대로 실행하지 마십시오. 시리얼 모니터/디버거는 닫아 두십시오.

```powershell
.\Build\Test-SnmpMappingHardware.ps1 -AllowDeviceChanges -Iterations 128
```

PC 테스트와 MCU 빌드를 먼저 수행하고, 원래 앱의 SHA-256과 COM3를 확인한 뒤 배포합니다.
PC 피어는 기존 UDPHost 상호운용 시험에서도 사용하는 외부 **SharpSnmpLib 12.5.7**로
요청과 응답을 인코딩·디코딩합니다. 기본 PC 시험도 실제 루프백 UDP로 같은 피어를 실행합니다.
8종 자료형, 정수 최솟값/최댓값, OID/바이너리 배열, GETNEXT/GETBULK/WALK, v1 Counter64 비노출,
readonly/wrongType/wrongLength, 다중 SET의 사전검증 거절 후 기존 값 보존을 확인합니다.
기동 대기 중 시간 초과·연결 초기화만 제한적으로 재시도하고 본 시험의 통신 실패는 그대로 실패로 처리합니다.
SharpSnmpLib 12.5.7은 첫 OID arc가 2인 경우에도 두 번째 arc를 40 미만으로 제한하므로,
일반 OID 왕복은 `2.39.*`로 검사하고 합법적인 경계값 `2.100.4294967295`는
보조 `Wire.cs`로 별도 SET/GET합니다. 기존 직접 BER·잘린 입력 시험도 유지합니다.
외부 라이브러리 상호운용 통과가 SNMP 적합성 인증을 뜻하지는 않습니다.

장치 배포를 시작한 뒤에는 실패 여부와 관계없이 `finally`에서 원래 Modbus 앱을 복원하고
COM6 RTU 9600 8E1, holding 0..2 = 123/456/65535를 읽어 확인합니다.
복원 실패는 경고와 로그를 남기고 한 번만 재시도하며 두 번째 실패는 오류로 종료합니다.
로그/이미지는 `bin\snmp-mapping-hardware-<시각>`에 보관됩니다.
원래 이미지는 `bin\hardware\restore-Modbus.SerialServer.bin`이며 SHA-256은
`66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7`입니다.
이 시험기는 펌웨어·Wi-Fi·방화벽·자격 증명을 변경하지 않습니다.

이미 이 샘플이 올라간 다른 평가 보드는 아래 피어 명령으로 시험할 수 있습니다.
이 명령은 **모델의 쓰기 가능한 값을 변경**하며 앱을 자동으로 복원하지 않습니다.

```powershell
dotnet run -c Release --project .\Tests\SNMP\VagabondK.nF.SNMP.Mapping.Tests -- --hardware 192.168.0.187 128
```

## 실물 확인 기록

2026-10-29 `bin\snmp-mapping-hardware-20261029-122940`에서는 SharpSnmpLib 12.5.7로
256회 SMI 형식 왕복과 첫/중간/마지막 로컬 변경의 격리를 확인했습니다.
별도 견고성 이미지의 시작 전 검사에서 byte[]/OID 배열 소유권과 동시 스냅샷을 확인하고,
64회 혼합 고장 순회에서 마지막 varbind의 wrongType으로 다중 SET 전체를 사전 거절한 뒤
모든 모델 값 보존 및 비정상 데이터그램 이후 정상 GET을 확인했습니다.
혼합 구간의 GC 후 보존 힙 증가는 144 B로 허용값 256 B 이내였습니다.
이는 setter 실패 시 롤백이나 요청 전체의 원자적 스냅샷을 보장하지 않습니다.
큰 비정상 UDP의 애플리케이션 도달 여부도 별도 계수하지 않으므로 크기 제한 검증과 구분합니다.
원래 앱 복원·COM6 읽기를 포함한 상세 범위는 [실물 시험기](../../../Tests/SNMP/VagabondK.nF.SNMP.Mapping.Hardware/README.md)를 참조하십시오.

`bin\snmp-mapping-hardware-20261029-105719`에서 SharpSnmpLib 기반 SET→GET 128회,
8종 SMI 형식·조회 순회·오류·다중 SET 사전검증 실패 시 값 보존 및 보조 OID 경계값을 검증했습니다(1,082개 검사).
원래 Modbus 복원과 COM6 레지스터 확인까지 통과했습니다.
아래 이전 기록은 직접 BER 피어를 사용한 결과이며 검사 횟수는 외부 피어 시험과 직접 비교할 수 없습니다.

`bin\snmp-mapping-hardware-20261025-173408`에서 8종 값의 SET→GET 128회,
조회 순회·버전 차이·오류 거절을 포함한 18,154개 검증과 원래 Modbus 앱 자동 복원을 확인했습니다.
기동 중 UDP 시간 초과 2회는 준비 대기에서만 재시도했으며 본 시험 실패는 없었습니다.
앞선 실행에서 복원 시 nanoff E2002 연결 오류가 한 번 발생하여 별도 재시도로 복원한 뒤,
시험기에 복원 대기 및 한 번의 명시적 재시도를 추가했습니다.
위 최종 실행에서는 복원이 첫 시도에 통과했습니다.
반복 SET/GET 시험이며 장시간 반복 시험, 힙 최대 사용량 측정, 수명 시험이나 SNMP 인증은 아닙니다.
