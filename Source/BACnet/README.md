# BACnet/IP

[전체 프로젝트](../../README.md) · [빌드·패키징·공통 검증](../../Build/README.md)

제한된 LAN 프로파일의 BACnet/IP Device와 AV/BV 객체 매핑을 제공합니다.
이 문서의 명령과 코드 경로는 저장소 루트 기준입니다.

## DLL 선택과 지원 경계

| DLL | 역할 | 프로젝트 의존성 |
|---|---|---|
| `VagabondK.nF.BACnet` | BACnet/IP 프레임·Device·AV/BV 객체·조회/설정과 저장소 계약 | 없음 |
| `VagabondK.nF.BACnet.IP` | 호출자 소유 UDP 소켓의 데이터그램 처리 | BACnet |

다른 프로토콜을 참조하지 않으며 값 저장소는 앱이 `IBacnetValueStore`로 구현합니다.
UDP Device를 구성하려면 `VagabondK.nF.BACnet`과 `VagabondK.nF.BACnet.IP`를 함께 참조합니다.
`BacnetDevice.Process`에 요청 바이트를 직접 전달하는 앱은 IP 어댑터 DLL 없이 코어만 사용할 수 있습니다.
현재 역할은 요청에 응답하는 **Device 측**이며, 원격 장치에 조회·설정을 요청하는 Client API는 제공하지 않습니다.
Who-Is 수신에 대한 I-Am 응답, ReadProperty와 non-commandable WriteProperty 요청 처리를 지원합니다.
소켓의 생성·설정·수명 관리와 모델 접근의 동기화는 앱의 책임입니다.
COV·BBMD·MS/TP·메시지 분할(segmentation)·BTL 인증은 지원하지 않습니다.

## 정적 객체 매핑

선택형 [VagabondK.nF.BACnet.Mapping](VagabondK.nF.BACnet.Mapping/README.md)은
`[BacnetMap] partial class`의 `[BacnetPoint(instance)]` float/bool에서 Device 측 AV/BV 저장소를 생성합니다.
private setter와 읽기 전용 멤버를 지원하며, bool 품질과 값을 같은 잠금 안에서 읽어 스냅샷으로 제공합니다.
빌드 시 객체 종류와 인스턴스 번호의 조합을 기준으로 중복·범위를 검사하고 숫자 순서로 정렬합니다.
기존의 최대 **64개** 객체 제한을 유지하며 리플렉션이나 추가 MCU 런타임 DLL은 사용하지 않습니다.

생성 저장소를 직접 호출하는 정상 경로에서는 메모리를 할당하지 않지만, 기존 Device/UDP 통신은 메모리를 할당합니다.
Client 매핑·COV·commandable/priority array·MS/TP·BTL 인증은 포함하지 않습니다.

```powershell
dotnet run -c Release --project .\Tests\BACnet\VagabondK.nF.BACnet.Mapping.Tests
.\Build\Pack-BacnetMapping.ps1 -OutputPath .\bin\nuget\bacnet-mapping-new
.\Build\Test-BacnetMapping.ps1 -FeedPath .\bin\nuget\bacnet-mapping-new
```

## 샘플과 검증

| 문서·프로젝트 | 내용 |
|---|---|
| [IPHost](../../Samples/BACnet/VagabondK.nF.BACnet.IPHost/README.md) | 독립 MCU Device·UDP 사용과 재실행 |
| [Mapping](../../Samples/BACnet/VagabondK.nF.BACnet.Mapping/README.md) | AV/BV 모델 저장소와 MCU 실행 |
| [매핑 실물 Fixture](../../Tests/BACnet/VagabondK.nF.BACnet.Mapping.Hardware/README.md) | 외부 클라이언트·8/32/64 Point 비용·원래 앱 복원 |

```powershell
dotnet run -c Release --project .\Tests\BACnet\VagabondK.nF.BACnet.Tests
dotnet run -c Release --project .\Tests\BACnet\VagabondK.nF.BACnet.Interop -- --stress 1024
.\Build\Pack.ps1 -LibrarySet BACnet
.\Build\Test-Packages.ps1 -LibrarySet BACnet
```

PC 회귀는 호출자 소유 UDP 세션과 비정상 데이터그램 복구를 확인합니다.
Interop은 독립 BACnet 4.0.0 클라이언트로 조회·설정·오류·배열 경계와 잘린/무작위 입력 복구를 검사합니다.
`--seed UINT`로 재현 시드를 지정할 수 있습니다.
이 시험은 실물 네트워크·장치 타이밍이나 BTL 적합성 검증의 대체재가 아닙니다.
