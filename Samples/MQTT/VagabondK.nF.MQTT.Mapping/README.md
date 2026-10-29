# MQTT 매핑 샘플

**A의 모델 값 → MQTT 브로커 → B의 다른 이름을 가진 모델**과 반대 방향을 시연합니다.
로컬 설정의 저장/복원 예제가 아닙니다. MQTT는 메시지 전달만 담당하며 JSON은 이 샘플의
명시적 데이터 계약입니다. JSON/Sparkplug가 MQTT의 필수 조건인 것은 아닙니다.
[MQTT.Mapping 생성기](../../../Source/MQTT/VagabondK.nF.MQTT.Mapping/README.md)가
명시적 토픽과 고정 JSON 스칼라 계약을 컴파일 시 생성합니다. 범용 JSON 매핑은 아닙니다.
폴더·프로젝트·어셈블리·네임스페이스는 `VagabondK.nF.MQTT.Mapping`입니다.
이전 `.DataSharing` 샘플의 이름을 통일한 것이며 토픽·JSON 계약은 바꾸지 않았습니다.
동명 `Source` 프로젝트는 PC 생성기이고 이 프로젝트는 MCU 실행 앱입니다.

| 송신 | 토픽(QoS1, retain=false) | 수신 |
|---|---|---|
| PC의 장치 A 역할 | `vknf/sample/device-a/sensor` | ESP32-S3의 장치 B 역할 |
| ESP32-S3의 장치 B 역할 | `vknf/sample/device-b/sensor` | PC의 장치 A 역할 |

PC는 임베디드 MQTTnet 브로커와 A 역할 클라이언트를 함께 실행합니다.
보드는 [자체 MQTT 라이브러리](../../../Source/MQTT/VagabondK.nF.MQTT/README.md)의
`MqttConnection`과 `SocketMqttChannel`을 사용합니다.
`SensorConnection.cs`가 연결·QoS 1 구독·발행과 수신 모델을 연결하며 M2Mqtt에 의존하지 않습니다.
**실물 MCU 1대+PC 1대**이며 MCU 두 대 사이의 실측이나 실제 센서/GPIO 시험은 아닙니다.

## 공유 계약과 매핑

```json
{"sequence":1,"temperatureDeciC":235,"running":true}
```

`SharingContract.cs`의 `SensorSnapshot`을 송신하고 수신 측 `RemoteSensorMapping`이 다음과 같이 반영합니다.

| 송신 멤버 | 수신 `ControllerView` 멤버 | 계약 |
|---|---|---|
| Sequence | SourceSequence | 1~Int32.MaxValue, 같은 스트림에서 단조 증가 |
| TemperatureDeciC | ExternalTemperatureDeciC | -500~1500, 0.1°C 단위 정수 |
| Running | SourceRunning | Boolean |

수신은 최대 160바이트, 정확한 세 필드만 받으며 필드 순서·JSON 공백은 허용합니다.
중복/미지정/알 수 없는 필드, 범위 초과, 소수, 문자열, null, 배열, 이스케이프 키 등은 거절합니다.
범용 JSON 파서가 아니라 크기와 형식이 제한된 이 스키마 전용 디코더입니다.
토픽·스키마·순서 검증을 모두 통과한 후 잠금 안에서 불변 수신 스냅샷을 한 번에 교체합니다.
앱은 수신 필드에 장치별 이름·단위 변환·도메인 검증을 추가할 수 있습니다.
현재 샘플은 값을 공유할 뿐 수신 값으로 물리 출력을 자동 실행하지 않습니다.

### 생성 코드와 앱 정책의 경계

`SensorSnapshot`은 `partial class`이며 다음 특성으로 기존 전송 형식 계약을 선언합니다.

```csharp
[MqttJsonMap("vknf/sample/device-b/sensor", MaximumBytes = 160)]
public sealed partial class SensorSnapshot
{
    [MqttJsonMember(0, "sequence", Minimum = 1)]
    public int Sequence { get; private set; }
    [MqttJsonMember(1, "temperatureDeciC", Minimum = -500, Maximum = 1500)]
    public int TemperatureDeciC { get; private set; }
    [MqttJsonMember(2, "running")]
    public bool Running { get; private set; }
    // 기존 생성자의 의미 검증도 유지합니다.
}
```

`SnapshotJson.Encode/TryDecode`는 이제 수동 코덱이 아니라
`SensorSnapshot.MqttBinding.Encode/TryDecode`를 호출하는 호환 래퍼입니다.
별도 모델 파일은 필요하지 않습니다. `.nfproj`는 생성기 프로젝트를 분석기로만 참조하며 MCU에 생성기 DLL을 넣지 않습니다.
생성기는 `Topic/MaximumBytes`, 검증한 불변 `Snapshot`, `Apply/TryApply`,
`Publish(model, publisher)`도 제공합니다. 발행 콜백은 앱이 선택한 MQTT 라이브러리를 호출합니다.
`Apply`는 private setter도 직접 접근하지만 setter의 네트워크 통신을 자동 생성하지 않습니다.

수신 `RemoteSensorMapping`의 **토픽·보관(retained) 메시지·연결·순번·신선도·잠금·불변 뷰 교체**는
도메인·앱 정책으로 여전히 샘플에 남습니다. 수신과 발행 토픽은 다르므로 수신 래퍼는
`TryDecode` 후 자체 토픽/순번 정책을 적용합니다. `TryApply`를 무조건 호출해 정책을 우회하지 않습니다.
`SensorConnection.Publish`는 PUBACK까지 동기적으로 기다립니다. 생성기의 `MqttBinding.Publish` 자체는
전달받은 발행 함수만 호출하며 ACK 정책을 정하지 않습니다. 구독은 QoS 1 승인까지 확인하며 낮은 QoS 승인은 거절합니다.
수신은 `Poll`을 호출하는 스레드에서 처리합니다. 일반 실행은 게시 사이의 대기 중에도 Poll을 호출하고,
진단 실행은 UDP 요청을 기다리는 동안에도 Poll을 계속 호출합니다. 콜백에서 같은 연결에 재진입하지 않습니다.
JSON 이름은 ASCII만 지원하며 유니코드 이름이나 `\u`를 포함한 이름 이스케이프를 거부합니다.
구문·범위 오류는 setter 호출 전에 거부하고 앱 getter/setter/발행 콜백의 예외는 숨기지 않습니다.

독립 생성기 진단/경계값/예외/트랜잭션 시험:
`dotnet run -c Release --project .\Tests\MQTT\VagabondK.nF.MQTT.Mapping.Tests`.

중복되거나 역순인 sequence는 값과 신선도를 갱신하지 않습니다.
보관 메시지의 재전송을 새 측정으로 보지 않으며 로컬 수신 시각부터 **3초 미만**인 값만 사용 가능합니다.
단절 시 즉시 사용 불가가 되며, 재연결만으로는 복구되지 않고 새로운 sequence 값이 필요합니다.
`Read()`는 마지막 진단 값을 유지하므로 사용 전에 `IsAvailable(now)`도 확인해야 합니다.
여기서 신선도의 기준은 **생산 시각이 아닌 수신 후 경과 시간**입니다. 브로커 지연이나 생산 측 시계 동기화는 검증하지 않습니다.
QoS1 PUBACK는 브로커 수락이지 상대 모델 반영이나 물리 동작 완료가 아닙니다.
작업 제한 시간은 10초이며 ACK 재시도는 0회로 설정합니다.
앱은 불확실한 게시를 자동으로 재실행하거나 연결을 자동 복구하지 않습니다.
단절·전송 실패 시 수신 값을 사용 불가로 표시하며, 명시적 재연결은 새 소켓과 연결 객체를 사용합니다.
sequence는 RAM에만 있으며 송신자 재부팅으로 1로 돌아가면 기존 수신 모델은 이를 거절합니다.
실제품에는 스트림·세션 ID나 영속 카운터·명시적 재동기화 정책이 필요합니다.
명령·exactly-once·재부팅 간 중복 제거·Sparkplug birth/death 계약은 구현하지 않습니다.

## 정상 샘플 실행

평가 전용 LAN의 PC `192.168.0.210:18883`, 보드는 저장된 Wi-Fi DHCP/AutoConnect를 사용합니다.
주소는 보드 `Program.cs`의 상수로 명시하며 자격 증명은 코드에 넣지 않습니다.
이 샘플 브로커는 **TLS/인증 없는 평가용**입니다. 인터넷·실설비망에 노출하지 마십시오.
방화벽 변경은 자동으로 하지 않습니다.

```powershell
nuget restore .\packages.config -PackagesDirectory .\packages -NonInteractive
nuget restore .\Samples\MQTT\VagabondK.nF.MQTT.Mapping\packages.config -PackagesDirectory .\packages -NonInteractive
dotnet run -c Release --project .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests -- --peer 192.168.0.210
```

PC는 60초 동안 A의 메모리 값을 1초 간격으로 게시하고 `B -> A`로 수신 모델 값을 출력합니다.
PC의 READY 후 별도 터미널/Device Explorer에서 정상 보드 앱을 빌드·배포합니다.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  .\Samples\MQTT\VagabondK.nF.MQTT.Mapping\VagabondK.nF.MQTT.Mapping.nfproj `
  /t:Rebuild /p:Configuration=Release /warnaserror
```

보드는 SUBACK 확인 후 30회 게시하며 매 게시 PUBACK를 기다립니다.
Debug Output의 `A -> B`에서 수신 측에 매핑된 값을 확인합니다.
두 역할의 메모리 값을 실제 센서 스냅샷으로 교체하면 같은 계약으로 공유할 수 있습니다.
프로세스/보드를 각각 다시 시작하여 새 평가 스트림으로 사용하십시오.
위 수동 실행은 앱 자동 복원이 아니므로 평가 종료 후 원래 앱을 직접 복원해야 합니다.

샘플의 `System.Net` 참조는 자체 MQTT 라이브러리와 같은 `1.11.66`으로 맞췄습니다.
nanoFramework에서 binding redirect 경고를 숨겨 버전을 혼용하지 않습니다.

## 자동 시험과 안전 복원

PC 계약 시험은 보드 없이 `dotnet run -c Release --project .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests`로 실행합니다.
가능한 모든 길이의 잘림, 범위 오류·중복·이상 필드, 토픽 불일치·보관 메시지·단절·순서 오류의 거절,
3초 경계와 재접속을 검사합니다.
샘플의 `SensorConnection.cs`도 PC 테스트에 직접 링크하여 루프백 MQTTnet 브로커와 QoS 1 양방향 교환,
보관 메시지·중복 거절, 재연결 후 새 순번에 의한 복구와 전송 실패 시 사용 불가 전환을 검사합니다.

```powershell
.\Build\Test-MqttDataSharingHardware.ps1 -AllowDeviceChanges `
  -OutputPath .\bin\mqtt-sharing-new
.\Build\Test-MqttDataSharingHardware.ps1 -AllowDeviceChanges -NormalSample `
  -OutputPath .\bin\mqtt-sharing-normal-new
```

격리된 COM3 ESP32-S3, MCU `192.168.0.187`, PC `192.168.0.210` 및 COM6 RS-485 전용입니다.
브로커가 준비된 뒤 계측 앱을 배포하고, A→B 필드별 독립 변경 64회를 포함한 정상 적용 68회와
B→A 스냅샷 두 개의 모델 값을 확인합니다. 중복 키·누락·순번·정수 넘침·길이·모든 절단 위치 등
거절 65회마다 이전 모델 보존을 검사하고, 보관 메시지 재전송·재접속·오래된 값에서 새 값으로의 복구도 확인합니다.
UDP 20005는 `MqttHardwareTest=true` 시험 빌드에서만 활성화하며 운영용 관리 인터페이스가 아닙니다.
`MQS1` 요청 순번은 1..255를 순환하고 동일 순번 재전송은 명령을 중복 실행하지 않습니다.
별도 `GCM1` 요청은 GC 후 가용 관리 힙을 반환하며 명령 순번을 소비하지 않습니다.
성공/실패 모두 PC 프로세스를 정리하고 복원 이미지 SHA256을 검사한 원래 Modbus 앱으로 복구합니다.
COM6 holding 0..2=123/456/65535 확인까지 완료해야 `Restored=true`입니다.
결과는 새 출력 폴더의 `status.json`, `peer.log`, 배포/복원 로그에 보존합니다.

`-NormalSample`은 진단 UDP 없는 일반 보드 앱과 위 PC `--peer`를 실제로 실행합니다.
보드의 30회 게시가 모두 A 모델에 반영되고 마지막 sequence=30/temperatureDeciC=280/running=false인지 검사합니다.
이 모드는 B의 수신 모델을 UDP로 조회하지 않으므로 A→B 값 검증은 진단 모드 결과와 구분합니다.
2026-10-28 최종 `bin\mqtt-sharing-20261028-final`은 양방향/장애 시험,
`bin\mqtt-sharing-normal-20261028-final`은 일반 앱의 30회 공유를 완료했습니다.
두 실행 모두 `Status=completed`, `Restored=true`, `Error=null`입니다.
위 실물 기록은 M2Mqtt를 사용하던 이전 버전의 결과입니다.
자체 MQTT로 전환한 현재 매핑 샘플의 진단 시험은 2026-10-29
`bin\mqtt-sharing-20261029-124920`에서 적용 68회·거절 65회·검사 613회를 완료했습니다.
혼합 구간의 보존 힙 증가는 12 B로 허용값 512 B 이내였으며 원래 앱 복원·COM6 읽기를 확인했습니다.
이 유한 반복 결과를 장기 누수 부재나 일반 모드 30회 공유의 최신 이미지 재실행으로 해석하지 마십시오.
샘플 이름 변경 후에는 PC 회귀와 MCU 빌드를 수행했으며 이름 변경만으로 보드 재시험 결과를 새로 집계하지 않습니다.

모든 프로토콜의 샘플 형식은 [매핑 형식 대조표](../../MappingCoverage.md)를 참조하십시오.
Sparkplug birth/death·alias·NCMD 공유는 별도의
[자체 MQTT 기반 샘플](../VagabondK.nF.MQTT.Sparkplug.Mapping/README.md)에 있습니다.
두 샘플 모두 별도 [MQTT DLL](../../../Source/MQTT/VagabondK.nF.MQTT/README.md)을 사용합니다.
JSON 샘플은 기존 JSON 계약을, Sparkplug 샘플은 바이너리 Will과 Birth/Death 계약을 사용합니다.

`Main`은 통신 전에 `SnapshotJson.DemonstrateBufferEncoding()`을 실행해 재사용 버퍼의 offset·기록 길이와
기존 배열 인코딩 결과의 일치를 확인합니다.
`SnapshotJson.Encode(snapshot, buffer, offset)`은 기록 길이를 반환하므로 그 구간만 사용해야 합니다.
현재 `MqttConnection.Publish`는 배열 전체를 받으므로 실제 송신에는 정확한 길이의 배열을 반환하는
`Encode(snapshot)`을 사용합니다. 이 API도 중간 문자열 없이 결과 배열 하나만 생성합니다.
