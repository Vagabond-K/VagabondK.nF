# SNMP

[전체 프로젝트](../../README.md) · [빌드·패키징·공통 검증](../../Build/README.md)

SNMPv1/v2c Agent와 선택형 scalar 객체 매핑을 제공합니다.
이 문서의 명령과 코드 경로는 저장소 루트 기준입니다.

## DLL 선택과 지원 경계

| DLL | 역할 | 프로젝트 의존성 |
|---|---|---|
| `VagabondK.nF.SNMP` | v1/v2c BER·Agent·system 그룹·Trap 생성과 저장소 계약 | 없음 |
| `VagabondK.nF.SNMP.UDP` | 호출자 소유 UDP 소켓의 요청 처리와 v2c Trap 송신 | SNMP |

다른 프로토콜을 참조하지 않으며 앱이 `ISnmpValueStore`로 값을 제공합니다.
UDP Agent와 Trap 송신에는 `VagabondK.nF.SNMP`와 `VagabondK.nF.SNMP.UDP`를 함께 참조합니다.
`SnmpAgent.Process`·`CreateTrap`의 바이트 API를 직접 사용하는 앱은 UDP 어댑터 DLL 없이 코어만 사용할 수 있습니다.
현재 역할은 **Agent와 Trap 송신**이며, 원격 Agent를 조회·설정하는 Manager나 Trap 수신 API는 제공하지 않습니다.
v1/v2c의 Get/GetNext/Set 요청 처리, **v2c 전용 GetBulk** 요청 처리와 SNMPv2c Trap 생성을 지원합니다.
Set은 모든 변수 바인딩을 사전 검증한 뒤 순서대로 적용하며, setter에서 실패해도 앞선 변경을 되돌리는 트랜잭션은 아닙니다.
SNMPv3·Inform·v1 Trap은 지원하지 않습니다. **community는 평문**이므로 신뢰할 수 있는 망과 접근 제한이 필요합니다.
소켓 연결·수명과 애플리케이션 모델 동기화는 앱이 관리합니다.

## 정적 객체 매핑

선택형 [VagabondK.nF.SNMP.Mapping](VagabondK.nF.SNMP.Mapping/README.md)은
`[SnmpMap] partial class`의 `[SnmpScalar]`에서 Agent 측 `Model.SnmpStore : ISnmpValueStore`를 생성합니다.
Integer32/Counter32/Gauge32/TimeTicks/Counter64/OctetString/IpAddress/ObjectIdentifier 8종,
readonly 필드와 private setter를 지원하며, 빌드 시 전체 scalar OID를 검증하고 숫자 순서로 정렬합니다.
리플렉션이나 추가 MCU 런타임 DLL은 필요하지 않습니다.

기존 Agent의 OID 병합·정렬과 값·배열·메시지 할당은 그대로 수행하므로 무할당 구현은 아닙니다.
OctetString 길이를 검증할 때는 `SnmpValue.ByteLength`를 사용해 검사용 복사를 피하되,
실제 읽기와 SET에서는 배열의 소유권을 분리하기 위한 복사를 유지합니다.
setter에서 예외가 발생해도 앞선 변경을 되돌리지 않으며, SET 전체의 원자성은 보장하지 않습니다.
Manager 매핑·테이블·동적 OID·자동 Trap 변경 감지는 포함하지 않습니다.

```powershell
dotnet run -c Release --project .\Tests\SNMP\VagabondK.nF.SNMP.Mapping.Tests
.\Build\Pack-SnmpMapping.ps1 -OutputPath .\bin\nuget\snmp-mapping-candidate
.\Build\Test-SnmpMapping.ps1 -FeedPath .\bin\nuget\snmp-mapping-candidate
```

## 샘플과 검증

| 문서·프로젝트 | 내용 |
|---|---|
| [UDPHost](../../Samples/SNMP/VagabondK.nF.SNMP.UDPHost/README.md) | MCU Agent·UDP 운용과 독립 Manager 재실행 |
| [Mapping](../../Samples/SNMP/VagabondK.nF.SNMP.Mapping/README.md) | MCU UDP 1161 모델 매핑·SharpSnmpLib 외부 피어·보조 BER 경계 검사·원래 앱 복원 |
| [매핑 실물 Fixture](../../Tests/SNMP/VagabondK.nF.SNMP.Mapping.Hardware/README.md) | 외부 피어 견고성·배열 소유권·동시 스냅샷과 자체 BER 기반 8/32/128 OID 비용 측정 |

```powershell
dotnet run -c Release --project .\Tests\SNMP\VagabondK.nF.SNMP.Tests
dotnet run -c Release --project .\Tests\SNMP\VagabondK.nF.SNMP.Interop -- --stress 1024
.\Build\Pack.ps1 -LibrarySet SNMP
.\Build\Test-Packages.ps1 -LibrarySet SNMP
```

PC 회귀는 테스트 전용 BER 인코더로 알려진 벡터·OID 경계·GetNext/GetBulk 순회,
Set 검증·잘림/변조 입력·Trap·UDP 루프백을 검사합니다.
Interop은 독립 SharpSnmpLib 12.5.7 Manager로 v1/v2c 조회·Walk·BulkWalk와 Set의 오류 조건별 조합,
tooBig·잘못된 community 무응답·Trap 해석·변조 요청을 검사합니다.
과거 기록은 PC 회귀 151,157개, Interop seed 1~4 각각 약 169,000개이며 현재 실행 건수와는 구분합니다.

실물 시험은 보드를 변경하므로 절차와 복원을 확인한 뒤 명시적으로 실행하십시오.

```powershell
.\Build\Test-SnmpHardware.ps1 -AllowDeviceChanges -Iterations 256
```
