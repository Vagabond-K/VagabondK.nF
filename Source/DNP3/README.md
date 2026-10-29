# DNP3

[전체 프로젝트](../../README.md) · [빌드·패키징·공통 검증](../../Build/README.md)

TCP·시리얼 DNP3 Master/Outstation과 선택형 정적 객체 매핑을 제공합니다.
시리얼도 DNP3 자체 프레임이며 Modbus RTU/ASCII가 아닙니다.
이 문서의 명령과 코드 경로는 저장소 루트 기준입니다.

## DLL 선택

| DLL 접미사 | 역할 | 프로젝트 의존성 |
|---|---|---|
| `DNP3` | CRC·링크·Transport·스트림 재동기화·Point·값·객체·제어 코덱과 Endpoint 계약 | 없음 |
| `DNP3.Master` | 요청·응답·비요청 수신, 읽기·제어·시간 동기화 | DNP3 |
| `DNP3.Outstation` | 값·이벤트 DB, 읽기 응답·비요청 송신과 제어 처리 | DNP3 |
| `DNP3.TCP` | 호출자 소유 소켓의 부분 송수신·기한·폴링 | DNP3 |
| `DNP3.Serial` | UART·confirmed/unconfirmed 링크, ACK·FCB·재시도·native RS485 RTS | DNP3 |

DLL 이름에는 `VagabondK.nF.` 접두사를 붙입니다.
Master와 Outstation은 서로 참조하지 않고 소켓/UART에도 의존하지 않습니다.
TCP·시리얼 모두 Master와 Outstation을 지원하며 필요한 DLL 조합은 다음과 같습니다.

| 구성 | 필요한 런타임 |
|---|---|
| TCP Master | `DNP3` + `DNP3.Master` + `DNP3.TCP` |
| TCP Outstation | `DNP3` + `DNP3.Outstation` + `DNP3.TCP` |
| 시리얼 Master | `DNP3` + `DNP3.Master` + `DNP3.Serial` |
| 시리얼 Outstation | `DNP3` + `DNP3.Outstation` + `DNP3.Serial` |

공통·역할 네임스페이스는 `VagabondK.nF.DNP3`, 전송은 `.TCP`/`.Serial`입니다.
Float32 변환에 사용하는 공유 소스 `RealBits`는 코어 DLL에 내부 코드로 포함해 컴파일합니다.

포트·소켓 연결과 수명은 앱이 관리합니다.
느린 UART의 기한 설정과 사용 계약·실물 한계는
[시리얼 Host](../../Samples/DNP3/VagabondK.nF.DNP3.SerialHost/README.md)를 참조하십시오.
이 시험 결과가 네이티브 RTS 모드의 동작이나 산업 적합성 인증까지 검증했다는 의미는 아닙니다.

## 정적 객체 매핑

선택형 [VagabondK.nF.DNP3.Mapping](VagabondK.nF.DNP3.Mapping/README.md)은
Outstation DB를 모델에서 초기화·갱신하거나 Master 수신을 모델에 반영하는 코드를 생성합니다.
7종 Point, 품질 Flags·UTC 시각, Event Class·Deadband·Variation과 명시적 CROB/Analog 제어 메서드를 지원합니다.
리플렉션이나 추가 MCU 런타임 DLL은 필요하지 않습니다.

`Publish()`는 기존 `Dnp3Database.Update`로 이벤트·Confirmation 계약을 보존합니다.
`Publish(Dnp3PointType.Analog, index)`는 선택한 Point만 갱신하여 전체 모델 반복을 피합니다.
Timestamp만 변경하면 Static 시각은 갱신하지만 이벤트는 생성하지 않습니다.
Flags 변화는 이벤트 조건이며, 같은 Flags의 Analog 값 변화는 마지막 이벤트 기준
(첫 이벤트 전에는 초기값)의 Deadband를 **초과**해야 합니다. EventClass 0은 이벤트를 생성하지 않습니다.
이벤트 버퍼의 용량이 부족해 `false`를 반환하더라도 현재값(Static)은 갱신됩니다.
앱도 같은 잠금으로 모델에 접근해야 하며, 실제 출력 제어·출력 상태 게시·실패 처리는 앱의 책임입니다.

Master는 `[Dnp3MasterMap]` 별도 모델을 사용합니다.
Static·이벤트·비요청 응답으로 수신한 값·품질·시각을 반영하고, `[Dnp3Received]` 메서드에 원본 Fragment를 전달합니다.
같은 Point의 연속 이벤트와 미등록 Point를 생략하지 않습니다.
Master 전용 앱에는 Outstation DLL이 필요하지 않으며, 자동 원격 출력 제어나 실제 장치 드라이버는 포함하지 않습니다.

```powershell
dotnet run -c Release --project .\Tests\DNP3\VagabondK.nF.DNP3.Mapping.Tests
.\Build\Pack-Dnp3Mapping.ps1 -OutputPath .\bin\nuget\dnp3-mapping-new
.\Build\Test-Dnp3Mapping.ps1 -FeedPath .\bin\nuget\dnp3-mapping-new
```

## 샘플과 검증

| 문서·프로젝트 | 내용 |
|---|---|
| [SerialHost](../../Samples/DNP3/VagabondK.nF.DNP3.SerialHost/README.md) | MCU Master·Outstation 시리얼 양방향, 배선·기한·확대 정합성 |
| [TCPHost](../../Samples/DNP3/VagabondK.nF.DNP3.TCPHost/README.md) | MCU Wi-Fi TCP 양방향과 독립 OpenDNP3 |
| [Mapping](../../Samples/DNP3/VagabondK.nF.DNP3.Mapping/README.md) | 모델에서 Outstation DB 게시 |
| [MasterMapping](../../Samples/DNP3/VagabondK.nF.DNP3.MasterMapping/README.md) | Master 수신·명시적 제어 모델 |
| [매핑 실물 Fixture](../../Tests/DNP3/VagabondK.nF.DNP3.Mapping.Hardware/README.md) | Point 수·이벤트 용량별 비용과 원래 앱 복원 |
| [Python·Go 피어](../../Tests/DNP3/VagabondK.nF.DNP3.ExtraPeers/README.md) | 고정 버전·PC TCP·실물 Go 시리얼 범위와 재실행 |

```powershell
dotnet run -c Release --project .\Tests\DNP3\VagabondK.nF.DNP3.Tests
.\Build\Build-Dnp3ValidationMaster.ps1
.\Build\Test-Dnp3Interop.ps1 -Repetitions 1
.\Build\Pack.ps1 -LibrarySet DNP3
.\Build\Test-Packages.ps1 -LibrarySet DNP3
```

기본 PC 시험은 코덱의 경계 조건, 통신 속도를 모사한 가상 시리얼과 실제 TCP 루프백의 1바이트 분할 수신,
잡음·CRC 오류·부분 프레임의 수신 기한 만료를 검사합니다. 상대의 연결 종료나 Endpoint 예외 이후 `Faulted` 상태가 유지되는지도 확인합니다.
`--dnp3-serial`은 시리얼 시험만 실행합니다.
`Test-Dnp3Interop.ps1`은 고정 OpenDNP3로 만든 독립 피어와 네이티브 Station/Master 양방향을 확인합니다.
TCP·가상 시리얼 490 Point·45종 표현·이벤트·비요청·제어·시간 동기화와 손상 입력 복구를 포함합니다.
피어 소스·빌드 결과는 `bin\protocol-tools`에 두며 제품 런타임 의존성이 아닙니다.
패키지 시험은 다른 프로토콜에 의존하지 않는 nanoFramework 사용 프로젝트를 전체 구성·역할별·전송별로 빌드합니다.

### 과거 상호운용 기록과 한계

기존 RS-485 배선/9600 8E1에서 MCU Master·Outstation 양방향, 45종 표현·비요청·음수 Int32 경계와
ACK 유실·CRC 복구를 확인했습니다. 실기에서 발견한 nanoCLR 음수 Int32 승격 문제는 디코더에서 수정했습니다.
원본 OpenDNP3 COM6 시리얼과 MCU Wi-Fi TCP도 양방향 통과했으며 TCP Master의 초기 방화벽 차단은
사용자 허용 후 재시험하여 구분했습니다.

PC의 확대 정합성 시험에서는 45종 × 품질 256개 × 시각 8개(92,160개) 조합과 수치 경계 516개를 검사했습니다.
실물 TCP/RS-485에서는 48비트 시각 최소/최대·역행, 품질/Counter 경계와 동일 Point·동일 시각 이벤트 순서를 비교했습니다.
이는 모든 실물 조합이나 RTC의 정확도·드리프트를 검증했다는 뜻이 아니며, 원본 OpenDNP3의 비정규수(subnormal)·음의 0 해석 한계가 해소되었다는 뜻도 아닙니다.
기본 엄격 검증은 해당 실패를 통과 처리하지 않으며 `-PatchedIeee`는 표시된 테스트 디코더 수정판입니다.

별도 Python `dnp3py` 수신·Go `otfabric/go-dnp3` 양방향 PC TCP에서는
12종 × 84 Point × 3경로의 수치·품질 3,024개를 비트 단위로 비교했습니다.
Python의 연도 9999 초과 시각 80개는 표현 한계로 엄격 모드에서 실패합니다.
MCU↔Go RS-485는 아날로그 12종, 양방향 각각 1,008개(총 2,016개)의 수치 비트·품질·시각이 일치했습니다.
최종 링크 재시도/CRC/프레임 기한 오류는 모두 0이며 Modbus 앱 복원·읽기도 확인했습니다.
Go 송신 시각 0은 공개 계약에 따라 1ms로 구분하고 Python은 PC TCP만 시험했습니다.
이 결과는 원본 OpenDNP3의 실패를 통과로 바꾸지 않습니다.
