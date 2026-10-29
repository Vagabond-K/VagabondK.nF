# MQTT·Sparkplug

[전체 프로젝트](../../README.md) · [빌드·패키징·공통 검증](../../Build/README.md)

MQTT 클라이언트와 제한형 Sparkplug B 계층을 샘플과 독립된 라이브러리로 제공합니다.
이 문서의 명령과 코드 경로는 저장소 루트 기준입니다.

## 라이브러리 선택

| 프로젝트 문서 | 역할 |
|---|---|
| [VagabondK.nF.MQTT](VagabondK.nF.MQTT/README.md) | MQTT 3.1/3.1.1/5의 동기식 Client·채널·세션·선택형 Worker |
| [VagabondK.nF.MQTT.Sparkplug](VagabondK.nF.MQTT.Sparkplug/README.md) | 실시간 상태 전달용으로 범위를 제한한 Sparkplug B 스칼라 코덱·토픽·송수신 상태 |
| [VagabondK.nF.MQTT.Mapping](VagabondK.nF.MQTT.Mapping/README.md) | 토픽·평면 JSON 계약과 사용자 모델의 정적 매핑 생성 |
| [VagabondK.nF.MQTT.Sparkplug.Mapping](VagabondK.nF.MQTT.Sparkplug.Mapping/README.md) | 모델 멤버와 Sparkplug 메트릭의 송신·수신 코드 생성 |

런타임 폴더의 `.nfproj`는 nanoFramework DLL을, 같은 폴더의 `.csproj`는 PC 검증용 빌드를 만듭니다.
두 `.Mapping` 프로젝트는 PC 빌드 전용 Roslyn 생성기이며 MCU 런타임 DLL이 아닙니다.

| 구성 | 필요한 런타임 |
|---|---|
| MQTT 게시·구독 Client | `VagabondK.nF.MQTT` |
| 자체 MQTT Client로 Sparkplug 송신·수신 | `VagabondK.nF.MQTT` + `VagabondK.nF.MQTT.Sparkplug` |
| 다른 MQTT Client로 Sparkplug 송신·수신 | `VagabondK.nF.MQTT.Sparkplug` + 앱이 선택한 MQTT Client |
| Sparkplug 페이로드·토픽 처리만 사용 | `VagabondK.nF.MQTT.Sparkplug` |

MQTT 게시와 구독은 같은 Client에서 함께 사용할 수 있으며 별도 Publisher/Subscriber DLL은 없습니다.
평문 소켓·인증을 마친 스트림·네이티브 TLS용 채널도 같은 MQTT DLL에 포함됩니다.
**브로커(Server)는 구현하지 않으므로** 통신에는 외부 MQTT 브로커가 필요합니다.
Sparkplug DLL은 MQTT DLL·고정 장치 이름·네트워크 주소를 직접 참조하지 않습니다.
다른 MQTT 전송 구현에서도 페이로드·토픽·상태 API를 사용할 수 있지만 실제 통신에는 MQTT 클라이언트와 브로커가 필요합니다.
샘플 전송 처리를 라이브러리에 복사하거나 샘플 클래스를 소스 링크할 필요는 없습니다.

## 지원 범위와 사용 계약

MQTT는 QoS0/1/2·사용자/암호·바이너리 Last Will·UTF-8 이름·다중 구독/해제·keepalive를 지원합니다.
선택형 RAM 세션·단계별 재전송·워커 스레드, 인증을 마친 스트림용 어댑터,
MQTT 5 속성·AUTH/재인증·브로커 제한 협상·여러 확인 응답을 동시에 기다리는 배치 전송·nanoFramework 네이티브 TLS 채널을 제공합니다.

기본 전송은 자체 `VagabondK.nF.MQTT`입니다. 기존 M2Mqtt와 호출 규약이 다르므로 참조만 바꾸어 대체할 수는 없습니다.
JSON·Sparkplug 데이터 공유 샘플도 자체 MQTT를 사용합니다.
실행 샘플 이름은 각각 `VagabondK.nF.MQTT.Mapping`과 `VagabondK.nF.MQTT.Sparkplug.Mapping`입니다.
동명 `Source` 프로젝트는 Analyzer이고 `Samples` 프로젝트는 MCU 앱입니다.
한 연결의 작업과 콜백은 차례로 실행해야 하며, 수신 콜백 안에서 같은 Client를 다시 호출하면 안 됩니다.
오류는 예외·Faulted로 드러나며 새 채널·연결 객체로 복구합니다.
메시지 ACK는 상대 모델에 값이 반영되었거나 물리 동작이 완료되었다는 뜻이 아닙니다.
애플리케이션의 외부 동작이 정확히 한 번 실행된다는 보장도 아닙니다.
인증 정보는 검증된 TLS 위에서 전송하고 장치 UTC·신뢰 루트·소켓 소유권을 확인하십시오.

Sparkplug는 설정 가능한 스칼라 스키마와 Birth/Data/Death·별칭·Rebirth·bdSeq/seq를 제공합니다.
수신기 하나는 지정한 원격 노드의 장치 하나를 처리합니다. 전체 명세를 구현하거나 TCK 인증을 받은 것은 아닙니다.
스키마는 메트릭 1~8개, 페이로드는 최대 512바이트이며 수신 실패·연결 단절 시 현재 값을 사용 불가 상태로 표시합니다.
연결·구독·UTC·재접속·bdSeq의 영속 저장과 Rebirth 요청 주기는 앱이 관리해야 합니다.
마지막 진단 값과 현재 가용 상태를 구분해야 합니다.

## 정적 객체 매핑

다른 그룹과 마찬가지로 특성을 지정한 `partial class`에서 C# 7.3 연결 코드를 생성합니다.
모델에 직접 접근하므로 리플렉션을 사용하지 않습니다. 속성에 값을 대입하는 것만으로 통신하지 않고
게시·수신 반영은 앱이 명시적으로 호출합니다. 앱의 연결 수명·스레드 동기화·업무 유효성 정책은 별도입니다.

[MQTT.Mapping](VagabondK.nF.MQTT.Mapping/README.md)은 토픽과 제한된 평면 JSON 계약을 명시합니다.
MQTT는 페이로드의 표준 구조를 정하지 않으므로 임의의 JSON·바이너리·타사 메시지 형식을 추정하지 않습니다.
[Sparkplug.Mapping](VagabondK.nF.MQTT.Sparkplug.Mapping/README.md)은 메트릭 이름·형식·별칭을 모델 멤버에 연결합니다.
Birth/Death·순서·수신 값의 유효 시간은 기존 `SparkplugReceiver`가 검증하며 생성기는 이 처리를 중복 구현하지 않습니다.
지원 타입·모델 모양·상한·진단·설치법은 각 생성기 문서를 참조하십시오.
두 데이터 공유 샘플은 직접 작성한 코덱·메트릭 변환 대신 생성 API를 사용하며, 장치별 이름과 업무 규칙 검증은 샘플에 남깁니다.

## 샘플과 검증

| 문서·프로젝트 | 내용 |
|---|---|
| [MQTT.Mapping 샘플](../../Samples/MQTT/VagabondK.nF.MQTT.Mapping/README.md) | 자체 MQTT 기반 장치 간 양방향 JSON 공유 |
| [MQTT.Sparkplug.Mapping 샘플](../../Samples/MQTT/VagabondK.nF.MQTT.Sparkplug.Mapping/README.md) | 두 DLL을 참조하는 Birth/Data/Death 기반 공유 |
| [DataSharing.Tests](../../Tests/MQTT/VagabondK.nF.MQTT.DataSharing.Tests) | MQTT·Sparkplug·공유 계약과 Broker·TLS·반복 시험 |
| [MQTT.Mapping.Tests](../../Tests/MQTT/VagabondK.nF.MQTT.Mapping.Tests) | 생성기 진단·JSON 계약·C# 7.3 생성 코드 |
| [Sparkplug.Mapping.Tests](../../Tests/MQTT/VagabondK.nF.MQTT.Sparkplug.Mapping.Tests) | 생성기 진단·메트릭 모델 변환·C# 7.3 생성 코드 |

```powershell
dotnet run -c Release --project .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests
.\Build\Test-All.ps1 -Filter '*MQTT*'
.\Build\Test-Soak.ps1 -Protocol MQTT
.\Build\Pack.ps1 -LibrarySet MQTT
.\Build\Test-Packages.ps1 -LibrarySet MQTT
.\Build\Pack.ps1 -LibrarySet Sparkplug
.\Build\Test-Packages.ps1 -LibrarySet Sparkplug
.\Build\Pack-MqttMapping.ps1 -LibrarySet MQTT
.\Build\Test-MqttMapping.ps1 -LibrarySet MQTT
.\Build\Pack-MqttMapping.ps1 -LibrarySet Sparkplug
.\Build\Test-MqttMapping.ps1 -LibrarySet Sparkplug
```

`Test-All.ps1`에 두 생성기 테스트와 MQTT 공유 계약을 포함하며 필터로 MQTT 세 프로젝트만 실행할 수 있습니다.
매핑 샘플 통합 검증 `Build\Test-MappingSamples.ps1`에도 생성기·PC 계약·두 MCU 공유 샘플 빌드가 포함됩니다.
매핑 패키지 검증은 Analyzer 전용 구성·고정 버전의 런타임 의존성·별도 경로의 의존성 복원을 확인합니다.
NuGet 패키지만 참조한 MCU 프로젝트가 빌드되고 PC용 DLL은 출력에 포함되지 않는지도 검사합니다.
MQTT 장시간 반복 시험은 평문 MQTT 3.1.1·TLS를 사용하는 MQTT 5의 지속 연결과 세션 수명주기를 검사하며,
기본 6개 프로토콜의 장시간 시험과 구분합니다.
외부 브로커·실물이 필요한 조건, 워밍업 후 ASCII 토픽으로 QoS0 송신 시 임시 할당이 없는 조건과 성능 비교의 한계는
[MQTT 상세 문서](VagabondK.nF.MQTT/README.md)를 참조하십시오.
과거 실물 2시간 시험·실패와 재시험 구분은 해당 문서와 [장애·복구 기록](../../Tests/FaultRecovery.md)에 있습니다.

추가 매핑 견고성 시험은 2026-10-29 실제 ESP32-S3에서 완료했습니다.
JSON은 필드 독립 변경 64회·정상 적용 68회·거절 65회, Sparkplug는 단일 메트릭 부분 갱신 264회와
순번 순환·별칭 교체·비정상 후반 메트릭의 값 보존을 확인했습니다.
외부 세션 피어 Eclipse Tahu 3.2.4도 별도로 사용하며, alias-only 값 해석 제약을 구분합니다.
정확한 로그·힙 수치·명령은 [매핑 대조표](../../Samples/MappingCoverage.md#추가-견고성-실물-시험)를 참조하십시오.
