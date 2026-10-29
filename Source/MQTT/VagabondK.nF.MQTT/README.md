# MQTT 3.1/3.1.1/5 라이브러리

`VagabondK.nF.MQTT.dll`은 샘플과 독립된 동기식 nanoFramework MQTT 클라이언트입니다.
장치 주소·토픽·페이로드 계약·Sparkplug 참조를 포함하지 않습니다.
`IMqttChannel`은 전송 인터페이스이고, `SocketMqttChannel`은 호출자가 소유한 연결된 소켓을 감싸는 어댑터입니다.
`StreamMqttChannel`에는 인증을 마친 TLS 스트림도 연결할 수 있습니다.
`MqttConnection`은 CONNECT/구독/게시/keepalive/종료를 담당하고 선택형 `MqttWorker`는
전용 워커 스레드 하나와 크기가 제한된 대기열을 제공합니다.
샘플 전용 클래스의 소스 링크 없이 DLL이나 로컬 NuGet 패키지로 사용할 수 있습니다.

사용자 모델과 토픽·JSON의 연결은 선택형 PC 생성기
[MQTT.Mapping](../VagabondK.nF.MQTT.Mapping/README.md)에서 제공합니다.
전송·연결·QoS·세션은 이 런타임 DLL에 유지하고, 생성기는 제한된 메시지 계약과 모델 접근 코드만 생성합니다.
객체 매핑을 사용하지 않는 앱은 생성기 패키지가 필요하지 않습니다.

## 실물 2시간 시험

ESP32-S3에서 평문 MQTT 3.1.1과 인증서를 검증하는 TLS 1.2 MQTT 5를 동시에
7,200.59초 운용했습니다. 512바이트 QoS0/1/2 echo·혼합 배치·PING·persistent 정상 재접속을
반복하여 게시 17,064건과 재접속 23회를 완료했고, 원래 Modbus 복원·COM6 읽기도 확인했습니다.
GC 후 가용 힙은 기준 755,256바이트, 최소 755,244바이트, 종료 755,292바이트였습니다.
결과는 `bin\mqtt-hardware-soak-20261028-140657`에 있습니다.
앞선 약 77분 시험에서 PC 피어가 종료되어 실패한 기록과 재시험 결과의 구분은
[장애·복구 기록](../../../Tests/FaultRecovery.md)에 명시했습니다.
실물 시험은 enum 변경 이전 이미지이며, enum 변경본은 PC·가상 nanoCLR·MCU 빌드로 별도 검증했습니다.
이후 현재 자체 MQTT 기반 매핑 샘플의 별도 진단 시험도 완료했습니다.
JSON은 `bin\mqtt-sharing-20261029-124920`, Sparkplug는 `bin\mqtt-sharing-20261029-125046`,
Tahu 세션은 `bin\mqtt-sharing-20261029-125212`에 기록했으며
이 세 결과는 위 2시간 런타임 시험을 최신 이미지로 다시 수행한 기록은 아닙니다.
이 결과는 강제 네트워크·전원 장애에서의 복구나 모든 펌웨어·브로커 조합에서 누수가 없음을 보장하지 않습니다.

## 사용

```csharp
using VagabondK.nF.MQTT;

// connectedSocket의 연결과 finally에서의 Close는 호출자 책임입니다.
var client = new MqttConnection(new SocketMqttChannel(connectedSocket));
client.Received = (topic, bytes, retained, duplicate) => {
    // 수신 계약 검증/모델 반영. client 재진입 호출은 금지됩니다.
};
client.Connect("device-b", "plant/device-b/death", new byte[] { 0, 255, 128 });
if (!client.Subscribe("plant/device-a/+", MqttQos.AtLeastOnce))
    throw new System.IO.IOException("Subscription refused.");
client.Publish("plant/device-b/state", new byte[] { 1, 2, 3 }, MqttQos.AtLeastOnce);
client.Poll(100);
client.Disconnect();
```

Will이 필요 없으면 `Connect("device-b")`를 사용합니다.
일반 게시는 `Publish(topic, payload, qos, retain)`로 retain을 선택할 수 있습니다.
기존 Connect 오버로드의 Will 기본값은 바이너리 QoS1/retain=false입니다.
사용자/암호, Will QoS/retain, keepalive, 세션은 다음처럼 설정합니다.

```csharp
client.Connect(new MqttConnectionOptions {
    ClientId = "device-b",
    UserName = "device-user",
    Password = passwordBytes, // binary; 인증 전송에는 반드시 검증된 TLS 채널 사용
    WillTopic = "plant/device-b/death",
    WillPayload = new byte[] { 0, 255, 128 },
    WillQos = MqttQos.ExactlyOnce,
    WillRetain = true,
    CleanSession = true,
    KeepAliveSeconds = 20,
    AcknowledgmentRetries = 0
});
byte[] grants = client.Subscribe(new[] { "plant/a/+", "plant/b/#" }, new byte[] { 2, 1 });
client.Unsubscribe("plant/a/+");
```

ProtocolLevel 3은 MQTT 3.1, 4(기본값)는 3.1.1, 5는 MQTT 5입니다.
keepalive 0은 비활성화, 그 외 ushort 범위의 초를 받습니다.
다중 구독/해제는 기본 요청당 1..16개이며 상한을 생성자에서 늘릴 수 있습니다.
SUBACK의 낮은 QoS 부여와 필터별 거절을 구분합니다.

모든 호출과 콜백을 하나의 스레드에서 차례로 실행하고 `Poll`을 자주 호출하십시오.
콜백 안에서는 후속 작업을 표시하는 플래그만 설정하고, 콜백 밖에서 Publish/Subscribe를 실행합니다.
`IMqttChannel.Write`는 반환 전에 바이트를 소비해야 하며 라이브러리 버퍼 참조를 보관하면 안 됩니다.
ACK를 기다리는 중에도 수신 PUBLISH를 콜백에 전달하고 필요한 ACK와 keepalive를 처리합니다.
콜백은 통신 작업의 같은 호출 스택에서 실행되므로 짧게 끝내야 합니다.
기한은 콜백을 강제로 중단하지 않으며, 느린 검증·예외 처리·외부 작업은 페이로드를 보관한 뒤
작업 반환 후 수행하십시오. 처리 실패를 늦추는 경우에는 앱이 별도의 거절·진단 정책을 구현해야 합니다.

CONNECT/SUBSCRIBE/PUBACK 대기, 부분 프레임 수신, 송신과 종료에는 기본 3초의 시간 제한을 적용합니다.
`OperationTimeoutMilliseconds`로 1..60000ms를 선택할 수 있습니다.
TCP 연결, 전송 채널 생성과 닫기는 별도로 호출자가 관리합니다.
`Poll`의 1..1000ms 무수신 대기는 정상 false이며, 시작된 프레임은 3초 안에 완료해야 합니다.
연결·프로토콜·콜백 오류는 예외로 전달되며 `Faulted=true`가 됩니다.
오류 상태가 된 뒤에는 소켓을 닫고 새 연결 객체를 생성해야 합니다.
기본 재전송은 0회입니다. `AcknowledgmentRetries` 1~10은 ACK의 첫 바이트를 받기 전 발생한 시간 초과에만 적용하며,
같은 packet ID와 DUP PUBLISH/PUBREL로 단계별 재전송합니다.
부분 프레임 수신 시간 초과·EOF·잘못된 ACK·콜백 실패는 재전송하지 않고 오류 상태로 처리합니다.
완료된 최근 16개 요청의 지연 중복 ACK는 무해하게 처리합니다.
QoS1 응답 시간이 초과되면 전달 여부가 불확실할 수 있고, DUP 수신은 앱에 다시 전달됩니다.
PUBACK는 브로커가 수락했다는 뜻이며, 상대 모델 반영·물리 동작이나 정확히 한 번 실행을 보장하지 않습니다.
DISCONNECT 이후에는 추가 송신/ACK 없이 남은 바이트를 비우며 broker EOF를 확인합니다.
이는 미수신 데이터가 남은 소켓의 TCP reset으로 마지막 DISCONNECT가 누락되는 것을 막습니다.
3초 안에 EOF가 없으면 종료 성공으로 숨기지 않고 예외를 냅니다.

## MQTT 5 속성·AUTH·서버 협상

MQTT 5는 별도 외부 MQTT 의존성 없이 같은 DLL에 구현되어 있습니다.
CONNECT/Will/PUBLISH/SUBSCRIBE/UNSUBSCRIBE/DISCONNECT/AUTH 속성을 해당 packet context에서 검사하고,
허용되지 않은 속성·중복을 허용하지 않는 속성의 반복·잘못된 UTF-8/정수·잘린 속성은 거부합니다.
User Property는 반복할 수 있고 수신 PUBLISH의 Subscription Identifier도 여러 개 받을 수 있습니다.

```csharp
var v5 = new MqttConnection(channel, new MqttSessionState(32, 32));
v5.MessageReceived = message => {
    // Topic/Payload/Qos/Retained/Duplicate/Properties. 기존 Received보다 우선합니다.
};
v5.Connect(new MqttConnectionOptions {
    ClientId = "device-v5", ProtocolLevel = 5, CleanSession = false,
    Properties = new[] {
        new MqttProperty(MqttPropertyId.SessionExpiryInterval, 60u),
        new MqttProperty(MqttPropertyId.TopicAliasMaximum, 8u),
        new MqttProperty("device", "b")
    }
});
byte[] reasons = v5.Subscribe(new[] { "plant/a/+" }, new byte[] { 2 },
    new[] { new MqttProperty(MqttPropertyId.SubscriptionIdentifier, 42u) });
if (reasons[0] >= 128) throw new System.IO.IOException("Subscription refused.");
v5.Publish("plant/b/state", payloadBytes, MqttQos.ExactlyOnce, false, new[] {
    new MqttProperty(MqttPropertyId.ContentType, "application/octet-stream"),
    new MqttProperty(MqttPropertyId.ResponseTopic, "plant/b/response"),
    new MqttProperty(MqttPropertyId.CorrelationData, new byte[] { 0, 255 }),
    new MqttProperty("source", "b")
});
byte[] removed = v5.Unsubscribe(new[] { "plant/a/+" }, null);
if (removed[0] >= 128) throw new System.IO.IOException("Unsubscribe refused.");
v5.Disconnect(0, new[] { new MqttProperty(MqttPropertyId.SessionExpiryInterval, 0u) });
```

`MqttProperty(MqttPropertyId id, uint)`의 전송 정수 폭은 표준 속성 ID에 따라 결정됩니다.
문자열·바이너리·사용자 속성은 각각 `(MqttPropertyId, string)`, `(MqttPropertyId, byte[])`, `(string, string)` 생성자를 사용합니다.
주요 ID는 1=Payload Format, 2=Message Expiry, 3=Content Type, 8=Response Topic,
9=Correlation Data, 11=Subscription Identifier, 17=Session Expiry, 21/22=Authentication Method/Data,
24=Will Delay, 33=Receive Maximum, 34=Topic Alias Maximum, 35=Topic Alias, 38=User Property,
39=Maximum Packet Size입니다. 모든 표준 속성 ID의 자료형과 패킷별 사용 가능 여부를 검증합니다.
문자열·바이너리 속성의 프로토콜상 상한은 65535바이트이며 실제 패킷·본문 상한도 함께 적용됩니다.

`ConnackProperties`에 브로커 속성을 보존합니다. Maximum QoS·Retain Available·Maximum Packet Size,
Wildcard/Shared/Subscription-Identifier Available 제한을 송신 전에 적용합니다.
서버 Receive Maximum은 `PublishBatch`에서 동시에 확인을 기다리는 요청 수에도 적용됩니다.
Server Keep Alive를 우선합니다. Topic Alias는 수신 시 해석하며, 송신 시에는 항상 전체 토픽을 포함하므로
재접속 후 재전송이 이전 연결의 별칭 테이블에 의존하지 않습니다.
MQTT 5의 빈 ClientId 요청은 브로커 할당 ID를 `ClientId`에 노출하므로 재접속 시 이 값을 사용할 수 있습니다.
기본 수신 Receive Maximum과 Maximum Packet Size도 CONNECT에서 알립니다.
구독 옵션 바이트는 QoS 외에 No Local/Retain As Published/Retain Handling을 지원합니다.
단일 구독·게시·Will·수신 메시지의 QoS는 `MqttQos`를 사용하고,
다중 구독의 입력 `byte[]`는 QoS 외의 옵션 비트를, 반환 `byte[]`는 거부 사유까지 나타내므로 그대로 유지합니다.
`MqttReasonException.PacketType`은 `MqttPacketType`으로 패킷 종류를 구분합니다.
기존 preview의 숫자 인수를 사용하던 코드는 해당 enum 멤버로 변경해야 합니다.

SUBACK/UNSUBACK은 필터별 사유 코드 배열을 반환하므로 **각 코드가 128 미만인지 확인**하십시오.
`AcknowledgmentProperties`/`LastReasonCode`는 마지막 ACK/AUTH 진단을 제공합니다.
게시 거절·서버 DISCONNECT는 `MqttReasonException`의 PacketType/ReasonCode/Properties로 드러납니다.
서버 DISCONNECT 뒤에는 연결을 정상 상태로 취급하지 않고 오류 상태로 전환합니다.

확장 인증(Enhanced AUTH)은 CONNECT 속성 21/22와 `Authenticate(reason, properties)` 콜백을 사용합니다.
콜백은 응답 속성 배열을 반환하거나 예외를 던져 중단하며, 같은 클라이언트를 다시 호출하면 안 됩니다.
인증 방식 변경·처리기 부재·16회를 초과하는 인증 챌린지·시간 초과는 명시적인 오류로 처리합니다.
연결 후에는 `Reauthenticate(properties)`로 같은 인증 방식의 재인증을 시작할 수 있습니다.
AUTH는 스크립트 채널과 네이티브 CLR에서 검증했으며, 실물 시험의 MQTTnet 브로커가 확장 인증을 제공했다는 뜻은 아닙니다.

## QoS2·세션과 비동기 실행

QoS2는 PUBLISH→PUBREC→PUBREL→PUBCOMP를 처리하고, 수신 콜백은 PUBREL에서 실행합니다.
같은 세션의 중복 PUBLISH/PUBREL은 재전달하지 않습니다. 수신 대기는 기본 16개이며 설정 가능합니다.
기존 Publish는 한 건의 완료를 동기적으로 기다리고, `PublishBatch`는 여러 QoS1/2 확인 절차를 동시에 진행합니다.
콜백이 외부 동작을 일부 수행한 뒤 실패하면 해당 동작을 되돌릴 수 없고,
재부팅 이후까지 정확히 한 번 실행을 보장할 수도 없으므로 앱의 멱등성 정책이 필요합니다.

`new MqttConnection(channel, state)`와 `CleanSession=false`로 RAM 세션을 이어갈 수 있습니다.
재접속 전에 이전 연결을 `Disconnect()` 또는 `Abort()`로 끝내고 전송 채널을 닫으십시오.
`Abort()`는 송신하지 않고 세션 점유를 해제하며 지속 세션의 미완료 송신은 보존합니다.
같은 세션 상태를 여러 활성 연결에서 중복 사용하면 예외가 발생합니다. 클라이언트 ID·프로토콜 변경도 거부하며,
같은 브로커인지 확인하는 것은 호출자 책임입니다.

MQTT 3.1.1 CONNACK Session Present=false이면 이전 수신 QoS2 상태는 지우지만
미확인 송신 PUBLISH/PUBREL은 이어갑니다. MQTT 3.1은 Session Present 비트가 없으므로
로컬 수신 상태를 보존합니다. CleanSession=true는 이전 상태를 초기화합니다.
MQTT 5의 CleanSession은 Clean Start 비트이며, 이후 연결 사이 보존은 Session Expiry Interval로 선택합니다.
브로커가 조정한 만료 시간도 적용하고 로컬 RAM 상태가 만료되면 다음 Connect에서 지웁니다.
종료 시 expiry=0을 더 긴 값으로 바꾸는 요청은 거절합니다.
세션 상태는 전원 차단이나 재부팅 후에도 남는 영속 저장소가 아닙니다.
자동 TCP 재연결은 하지 않으며, 재접속 중 수신될 수 있으므로 Received를 Connect 전에 설정하십시오.

`MqttWorker`는 연결을 마친 클라이언트에 선택적으로 적용합니다. `Enqueue`로 구독·해제 등의 작업을 요청하고,
`Publish`로 페이로드를 복사한 뒤 게시를 요청합니다. 완료는 `Completed(id, error)`로 통지받습니다.
QoS1/2 완료는 큐 수락이 아닌 ACK 절차 완료입니다. 큐가 차면 즉시 예외를 냅니다.
`Failure`는 워커를 중지시킨 오류를 보존하며 대기 중인 작업에도 오류로 완료되었음을 알립니다.
워커가 실행되는 동안 클라이언트를 직접 호출하면 안 되며, 수신 콜백 안에서는 Enqueue만 사용합니다.
대기 중 Poll에서 발생한 종료 오류는 작업 ID 0으로 통지하고, 대기 중이던 각 작업에는 해당 ID로 오류를 통지합니다.
Dispose는 신규 요청을 막고 기존 작업을 처리한 뒤 스레드 종료를 최대 15초 기다립니다.
시간이 초과되면 호출자가 전송 채널을 닫고 Dispose를 다시 호출해야 합니다. 워커는 전송 채널을 소유하지 않습니다.

`PublishBatch(MqttPublishRequest[])`는 배열 전체를 검사·인코딩한 뒤 동시 확인 대기 한도까지 전송하고,
순서가 바뀐 PUBACK/PUBREC/PUBCOMP도 패킷 ID로 구분해 처리합니다.
세션의 기본 송신 용량은 16개이며 `new MqttSessionState(incomingCapacity, maximumInflight)`로
각각 1..65535 범위에서 선택합니다. 실제 메모리가 허용하는 작은 값을 사용하십시오.
결과 배열은 입력 순서에 대응하는 사유 코드이며 일부 요청의 거부도 코드로 명시합니다. 모든 요소가 성공인지 확인하십시오.
전송 실패 시 미완료 배치는 RAM 지속 세션에 보존되며 재연결 시 브로커의 새 동시 확인 대기 한도에 맞춰 재전송됩니다.
워커의 Enqueue에 등록한 작업에서도 `connection.PublishBatch(requests)`를 호출할 수 있습니다.
`Ping()`은 즉시 PINGREQ를 보내고 수신 처리를 계속하면서 PINGRESP까지 기다립니다.

## TLS 연결 경계

`StreamMqttChannel(stream, readable)`은 TLS 협상을 대신하지 않습니다.
호출자가 서버 이름·CA·선택적 클라이언트 인증서를 설정하고 `AuthenticateAsClient`로 상대 인증을 마쳐야 합니다.
nanoFramework에서는 `NanoTlsMqttChannel(connectedSocket, targetHost, authority, clientCertificate)`을
사용해 상대 인증서와 호스트 이름을 검증할 수 있습니다. 이 클래스는 MCU 전용이며 TLS 스트림 생성 성공 후 소켓과 스트림을 소유합니다.
기본 TLS 1.2, 선택적 TLS 1.3과 클라이언트 인증서를 지원하며 허용되지 않은 구버전은 거부합니다.
TLS 1.3과 상호 인증의 실제 가용성은 펌웨어·브로커에도 의존합니다.
장치의 UTC 시각을 맞추고 CA와 서버 이름을 명시하십시오. 인증에 실패해도 평문 연결로 전환하지 않습니다.
직접 SslStream을 구성한다면 검증 값은 **`SslVerification.CertificateRequired` 단독**으로 지정하십시오.
네이티브 mbedTLS 구현은 비트 마스크가 아닌 단일 값으로 비교하므로 `VerifyPeer | CertificateRequired`를 지정하면 검증이 꺼질 수 있습니다.
읽기 준비 콜백은 `SslStream.DataAvailable`의 복호화 버퍼와 `socket.Poll`을 함께 확인하고
시간 제한과 EOF를 올바르게 처리해야 합니다. `socket.Poll`만 확인하면 TLS 내부 버퍼의 데이터를 놓칠 수 있습니다.
`StreamMqttChannel`에 전달하는 스트림은 ReadTimeout/WriteTimeout을 지원해야 하며, 연결·인증·종료와 스트림 해제는 호출자가 관리합니다.
PC 및 ESP32-S3에서 TLS 1.2 QoS2와 틀린 서버 이름·신뢰하지 않은 루트 거절을 확인했습니다.
실물에서는 유휴 상태의 Poll/PING/DISCONNECT도 확인했습니다. 이 기능 시험 자체는 TLS 1.3·상호 인증·장시간 안정성을 검증한 것이 아닙니다.
이후 수행한 TLS 1.2 장시간 시험은 앞의 「실물 2시간 시험」 기록과 구분하십시오.

## 범위와 M2Mqtt 비교

설치된 M2Mqtt 5.1.226의 공개 기능을 대조해 MQTT 5·AUTH·복수 in-flight까지 확장했습니다.
아래는 기능 범주를 비교한 것입니다. **기존 API를 수정하지 않고 대체할 수 있다는 뜻이나 모든 브로커·펌웨어 조합의 적합성 인증은 아닙니다.**

| 항목 | 현재 자체 라이브러리 |
|---|---|
| 프로토콜 / 세션 | MQTT 3.1/3.1.1/5; Clean Session/Start, Session Expiry, 한 연결만 점유할 수 있는 재사용 RAM 세션 |
| QoS / 재전송 | QoS0/1/2, 서버 한도를 따르는 PublishBatch 동시 확인 대기, 수신 QoS2 용량 설정, 선택형 ACK 재전송 |
| 페이로드 / Will | 임의의 바이너리 byte[]; 기본 512바이트, Will QoS0/1/2·retain 선택 |
| 구독 / retain | 다중 필터, `+`/`#`/shared subscriptions, 필터별 거절, Unsubscribe, retain, MQTT 5 구독 옵션 |
| 이름 / 패킷 | UTF-8, 기본 이름 128바이트·필터 16개; 설정으로 확대 가능, 본문 기본 1024바이트 |
| 인증 / TLS | 사용자 이름·바이너리 암호, 확장 AUTH/재인증, 네이티브 TLS 채널·인증 완료 스트림 어댑터 |
| MQTT 5 속성 / 협상 | 표준 속성의 전송 형식·허용 패킷 검사, User Properties, 메타데이터 콜백, 사유 코드 진단, 브로커 제한 |
| 실행 모델 | 동기식 폴링 또는 선택형 단일 워커·크기가 제한된 대기열·완료 이벤트; 클라이언트 재진입·병렬 호출 금지 |
| 앱 책임 / 별도 범위 | TCP 연결·재연결·재시도 간격 조절, 재부팅 후에도 유지되는 영속 저장, 콜백의 외부 동작에 대한 멱등성, 장시간 시험 |

기본 생성자는 본문·페이로드 상한을 각각 1024·512바이트로 유지합니다. 본문은 프로토콜 상한인 268435455바이트까지,
maximumPayload는 0..maximumBody-4, maximumNameBytes/maximumFilters는 1..65535 범위에서 선택하되
전체 토픽·속성·페이로드도 본문에 들어가야 합니다. 큰 값을 설정하기 전에 실제 장치의 RAM 용량을 검토하십시오.
기존 M2Mqtt의 자동 수신·대기열 방식과 이 라이브러리의 순차 Poll·워커 호출 방식은 다릅니다.
MQTT 전송 성공은 재접속·재시도 간격 조절·재부팅 후 상태 복원·업무 응답의 성공까지 뜻하지 않습니다.

기존 nanoFramework.M2Mqtt 5.1.226의 일반 Publish도 `byte[]`를 지원합니다.
차이는 해당 CONNECT Will API가 `string`인 반면 이 라이브러리는 원본 바이너리 Will을 받는다는 점입니다.
JSON 데이터 공유 샘플도 현재는 자체 `MqttConnection`을 사용합니다.
기존 Sparkplug 샘플은 계속 격리 LAN의 인증/TLS 없는 평가 구성입니다. 인터넷·실설비망에 노출하지 마십시오.

## 성능 개선과 측정 한계

프로젝트의 새 MQTT 기능은 이 라이브러리를 기본 전송 계층으로 사용합니다.
JSON·Sparkplug 샘플은 모두 자체 전송을 사용합니다. 아래 M2Mqtt 비교 측정은 별도 비교 앱으로 수행한 과거 기록입니다.
현재 우선순위는 추가 기능보다 지속 연결·자원 추이·세션 수명주기와 상호운용 검증입니다.

반복 송신에서 매번 1024바이트 Body와 패킷·중간 복사 배열을 만들던 경로를
연결 소유 송신 버퍼로 바꿨습니다. 검증된 ASCII 이름은 UTF-8 중간 배열 없이 기록합니다.
현재는 본문 앞에 MQTT 헤더 공간을 확보해 `IMqttSegmentChannel`에서 최종 패킷 복사를 없앴습니다.
`SocketMqttChannel`은 최대 256바이트 선읽기 버퍼를 사용해 작은 헤더를 읽을 때의 소켓 호출 수를 줄입니다.
페이로드는 바이트별로 기록하지 않고 `Array.Copy`로 복사합니다.
기본 송신 버퍼는 헤더 공간을 포함해 1029바이트입니다. 구간 송신을 지원하지 않는 기존 `IMqttChannel` 구현에만 별도 복사 버퍼를 필요할 때 생성합니다.
**워밍업 후 ASCII 토픽과 호출자 페이로드를 사용하는 QoS0 송신의 임시 할당은 0**입니다.
수신 프레임·콜백 페이로드·Sparkplug 인코더와 소켓 내부 할당까지 0이라는 뜻은 아닙니다.

2026-10-28 PC Release 빌드에서 데이터를 버리는 시험 채널과 128바이트 페이로드를 사용해,
워밍업 1000회 후 10000회를 측정했습니다.

| 측정 | 개선 전 | 개선 후 |
|---|---|---|
| 게시당 임시 할당 | 1472 bytes | 0 bytes |
| 해당 기록의 게시당 시간 | 약 1.48 μs | 약 0.91 μs |

기록은 `bin\native-mqtt-library-perf-before.log`와 `bin\native-mqtt-library-perf-after.log`입니다.
이는 자체 구현의 동일 PC 경로 전후 비교이며 네트워크·브로커·상대 수신·MCU 처리량 측정이 아닙니다.
시간은 실행 환경에 따라 달라지므로 성능 임계값으로 고정하지 않고, 임시 할당이 0인지에 대해서만 회귀 테스트에서 단언합니다.
128/512바이트 각각에 대해 구간 송신·대체 버퍼 송신을 조합한 네 경로에서 임시 할당이 없는지 확인합니다.
아래 실물 비교는 단일 메시지 echo 왕복 측정이며 최대 처리량이나 모든 기능의 우열을 뜻하지 않습니다.

## 최종 MQTT 5 확장 이미지의 실물 비교

MQTT 5/AUTH/배치/TLS 코드까지 포함한 당시 최종 자체 구현 이미지로
자체→M2→M2→자체 순서의 한 세트를 새로 측정했습니다. 이전 이미지의 결과와 합치지 않았습니다.
비교 조건은 아래 과거 기록과 같으며, 실제 측정 작업은 MQTT 3.1.1 QoS0/1 메시지 왕복(echo)입니다.
MQTT 5/TLS/배치 처리량 비교로 확대 해석하지 마십시오.
구현별·조건별 측정 표본은 180개이며, 측정 1440개와 워밍업 240개를 합쳐 브로커에서 1680개를 검사했습니다.
모든 표본을 포함했으며 이 고정 세트에서 자체 구현의 최댓값은 조건에 따라 20~40ms였습니다.

| 페이로드 / QoS | M2 평균 ms | 자체 평균 ms | 자체 중앙/P95/최대 ms | 평균 단축 |
|---|---:|---:|---|---:|
| 128 / 0 | 21.056 | 13.389 | 10 / 20 / 20 | 36.4% |
| 128 / 1 | 27.667 | 15.444 | 20 / 20 / 30 | 44.2% |
| 512 / 0 | 28.000 | 19.556 | 20 / 20 / 30 | 30.2% |
| 512 / 1 | 32.278 | 21.611 | 20 / 30 / 40 | 33.0% |

| 용량·메모리 | 자체 | M2Mqtt |
|---|---:|---:|
| 비교 앱 배포 이미지 | 110,704 bytes | 135,808 bytes |
| 첫 연결·구독 후 GC를 수행한 관리 힙 증가량 | 3,036 bytes | 8,652 bytes |
| 첫 배치 워밍업 후 GC를 수행한 관리 힙 증가량 | 3,036 bytes | 8,796 bytes |

이미지 크기는 약 18.5%, 최초 연결 시 관리 힙 증가량은 약 64.9% 작았습니다.
MQTT 3.x 확장 이미지 95,512바이트보다는 15,192바이트 늘었으며 이 비용을 숨기지 않습니다.
이미지에는 계측 코드와 프레임워크가 포함됩니다. 힙은 GC 후 살아 있는 관리 객체를 기준으로 하며
네이티브 RAM·TLS 버퍼·누적 할당량을 비교한 것이 아닙니다. 이 비교에서는 TLS를 사용하지 않았습니다.
네 실행 모두 첫 배치 워밍업 후부터 종료 시점까지 GC 후 힙 변화가 0이었지만, 장시간 실행에서 누수가 없다는 보장은 아닙니다.
10ms 타이머 양자화·단일 echo·고정 실행 순서라는 제한이 있고 통계적 유의성/포화 처리량은 주장하지 않습니다.
이전 버전의 1060/1500ms 이상치 기록도 아래에 보존하며, 일부 요청에서 지연이 길어지는 문제가 해결됐다고 단정하지 않습니다.

기록: `bin\mqtt5-clientid-final-summary-20261028`의 CSV/JSON.
두 자체 구현 이미지의 SHA-256:
`79B6A1CAE879374B23C1726A094F32F6951468DFA67F62F40D179447CC3F10A4`.
각 실행은 소스와 해시를 보존하고 원래 Modbus 앱 복원·COM6 정상 읽기를 완료했습니다.
마지막 자체 구현 실행은 통신을 통한 복원이 실패하여, 확인된 배포 영역만 ROM 부트로더로 복원한 뒤 COM6을 확인했습니다.
ClientId API 추가 전 세트는 `bin\mqtt5-final-comparison-summary-20261028`에 따로 보존했고
이미지 SHA가 다르므로 최종 집계와 합치지 않았습니다.

## 이전 MQTT 3.x 확장 이미지 비교 — 역사적 기록

ESP32-S3 한 대, 같은 Wi-Fi/PC MQTTnet 4.3.7.1207/`System.Net 1.11.64`,
자체 구현과 M2Mqtt 5.1.226의 별도 앱으로 비교했습니다.
자체→M2→M2→자체 순서의 두 세트로 조건당 **각 360회**를 측정했으며,
워밍업을 포함해 브로커가 검사한 게시 총수는 3360개입니다.
송신 시작부터 되돌아온 원본 바이트의 검증까지를 완료 시간으로 측정했으며 QoS1은 PUBACK까지 포함합니다.
M2Mqtt의 API 반환은 비동기 대기열에 수락되었다는 뜻이므로 동기식 자체 구현의 API 반환 시간과 직접 비교하지 않습니다.

| 페이로드 / QoS | 구현 | 평균 ms | 중앙값 ms | P95 ms | 최대 ms |
|---|---|---:|---:|---:|---:|
| 128 / 0 | M2Mqtt | 22.500 | 20 | 30 | 50 |
| 128 / 0 | 자체 | 20.361 | 10 | 20 | 1500 |
| 128 / 1 | M2Mqtt | 27.778 | 30 | 30 | 40 |
| 128 / 1 | 자체 | 13.722 | 10 | 20 | 30 |
| 512 / 0 | M2Mqtt | 28.194 | 30 | 30 | 50 |
| 512 / 0 | 자체 | 19.306 | 20 | 30 | 30 |
| 512 / 1 | M2Mqtt | 32.750 | 30 | 40 | 50 |
| 512 / 1 | 자체 | 22.250 | 20 | 30 | 60 |

전체 표본 평균은 각각 약 9.5%/50.6%/31.5%/32.1% 짧았습니다.
다만 최초 세트의 자체 128/QoS0에서 1060/1500ms 두 건이 있었고,
그 세트만 보면 평균 27.611ms로 M2Mqtt 23.278ms보다 **느렸습니다**.
해당 두 건의 송신 API 반환은 각각 10ms였으며 나머지 지연은 echo 완료 대기였습니다.
원인이 네트워크/브로커/단말 중 어디인지까지 분리하지 못했습니다.
병렬 빌드 없이 추가 정·역순 한 세트를 고정해 재측정했고, 해당 조건의 추가 세트 평균은
자체 13.111ms/M2Mqtt 21.722ms, 최대 30ms였습니다.
위 전체 표에는 **최초 두 이상치도 포함**했으며, 일부 요청에서 발생하는 긴 지연의 개선이나 통계적 유의성을 주장하지 않습니다.
MCU 타이머가 10ms 단위이므로 작은 API 반환 수치는 정밀 처리 시간으로 해석하지 마십시오.

| 용량·메모리 | 자체 | M2Mqtt |
|---|---:|---:|
| 같은 비교 앱 전체 배포 이미지 | 95,512 bytes | 135,808 bytes |
| 재부팅 후 첫 연결·구독에서 GC를 수행한 관리 힙 증가량 | 2,568 bytes | 8,652 bytes |
| 첫 배치 워밍업 후 GC를 수행한 관리 힙 증가량 | 2,568 bytes | 8,796 bytes |

배포 이미지는 약 29.7%, 최초 연결 시 관리 힙 증가량은 약 70.3% 작았습니다.
기능 확장 전 자체 비교 이미지 85,508바이트보다 현재는 10,004바이트 커졌습니다.
공통 UDP·계측 코드·프레임워크를 포함한 이미지이며 힙은 GC 후 살아 있는 관리 객체를 기준으로 합니다.
네이티브 RAM·TLS 버퍼·누적 할당량이나 기능이 동등한 라이브러리의 크기를 비교한 것은 아닙니다.
8개 실행 모두 첫 배치 워밍업 후부터 배치 종료까지 GC 후 가용 힙 변화는 0이었으며,
이 짧은 관측으로 장시간 누수 부재를 보장하지 않습니다.

기록은 `bin\mqtt-expanded-pooled-summary-20261028`의 CSV/JSON입니다.
최초 세트는 `bin\mqtt-expanded-summary-20261028`, 추가 세트는
`bin\mqtt-expanded-quiet-summary-20261028`에 따로 보존했습니다.
네 자체 구현 배포 이미지의 SHA-256은
`41C119320E84A0856B04CBFE147BC8695DC0ACCD81D63830A7738E7BE203021F`로 같습니다.
실패한 부팅 시험과 다른 후보 버전은 이 성능 집계에 포함하지 않았습니다.
각 실행은 원래 Modbus 이미지를 복원하고 COM6 holding 0..2=123/456/65535를 확인했습니다.

## 빌드·시험·패키지

### 반복 안정성 검사

```powershell
.\Build\Test-Soak.ps1 -Protocol MQTT -Seconds 600 -SampleSeconds 30 `
    -MaxManagedGrowthBytes 1048576 -MaxHandleGrowth 32 -MaxThreadGrowth 8
```

공통 장시간 반복 시험 실행기에서 MQTT를 선택해 실행할 수 있습니다. 다른 프로토콜의 기존 기본 실행 목록은 유지합니다.
PC 루프백의 독립 MQTTnet 4.3.7.1207 브로커와 다음 시나리오를 한 프로세스에서 반복합니다.

| 시나리오 | 확인 범위 |
|---|---|
| normal | 동일 연결을 유지하는 평문 MQTT 3.1.1과 인증된 TLS 1.2 MQTT 5; 512바이트 순번·페이로드 왕복, QoS0/1/2, 혼합 QoS 배치, 속성·Subscription Identifier, PING |
| malformed | 스크립트 채널에서 MQTT 5 손상 속성·ACK·별칭, 거부·서버 DISCONNECT, AUTH, 배치 순서 역전·시간 초과·RAM 상태 재전송 등 계약 88개 반복 |
| transport-cycle | 새 연결·워커 생성과 정리, 구독·해제, QoS 배치, 정상 DISCONNECT 후 브로커의 지속 세션을 Session Present로 재개 |

TLS 인증서와 신뢰 설정은 검증 프로그램의 메모리에만 존재하며 시스템 인증서 저장소는 변경하지 않습니다.
TLS 읽기 준비 콜백은 응답이 예상되는 경로만을 위한 구현입니다. 유휴 상태의 TLS Poll, 실물 TLS 장시간 시험,
잘못된 인증서를 거부하는 반복 시험은 이 PC 시험에 포함되지 않습니다. 실물 인증서 검증 근거는 앞 절과 구분합니다.
malformed 시나리오는 실제 브로커에 손상 패킷을 주입하지 않으며,
정상 DISCONNECT 재접속은 물리 단절·브로커 재시작·재부팅 복구와 다릅니다.

2026-10-28의 600.09초 측정 구간에서 세 시나리오가 각각 5,524회 통과했습니다.
normal 왕복 묶음은 132,576회, transport-cycle 묶음은 132,576회입니다.
한 묶음은 단일 게시 또는 4개 혼합 QoS 배치이므로 이를 단일 게시 처리량 수치로 사용하지 않습니다.
세 번의 워밍업 이후 30초 간격/마지막 시점의 총 20개 GC 후 자원 표본을 남겼습니다.
기준 대비 관리 힙의 최대 증가는 10,504바이트, 마지막 변화는 -926,168바이트였고,
핸들 최대 +26/마지막 +1, 스레드 최대 +3/마지막 -2로 지정 임계값 안이었습니다.
음수 변화는 GC·런타임의 자원 회수에 따른 것이며, 장시간 실행에서 누수가 없음을 증명하거나 MCU RAM을 측정한 결과가 아닙니다.
결과는 `bin\mqtt-stability-20261028\run-20261028-122313-f923c813b9484f6ab0bb26c744351461`의
`MQTT.jsonl`/`summary.json`/stdout/stderr에 보존합니다.
10분 PC 시험은 초기 안정성의 근거일 뿐, 수시간·수일의 실물 장시간 시험이나 다른 브로커 구현에 대한 검증을 대신하지 않습니다.

### 계약·native·패키지

`VagabondK.nF.MQTT.nfproj`는 nanoFramework DLL/PE/XML을 생성합니다.
같은 디렉터리의 `.csproj`는 동일 소스를 별도 PC DLL로 빌드하는 계약 시험용이며 MCU 배포용이 아닙니다.
PC 시험은 DLL의 공개 API를 참조하며 샘플 안의 프로토콜 소스를 복사 컴파일하지 않습니다.
당시 MQTT 3.x 계약 200개, MQTT 5/배치 계약 88개, 실제 루프백 MQTTnet 브로커 검사 29개를 확인했습니다.
`Build\Test-Virtual.ps1`에는 MQTT 5를 포함한 nanoCLR 실행 회귀 269개가 있습니다.
Remaining Length 필드가 4바이트인 2MiB 초과 패킷도 PC에서 바이트 단위로 일치하는지 검증합니다.
당시 최종 실물 기능 시험은 `bin\mqtt-features-20261028-115505`의 22개 검사와 브로커 게시 9건·연결 5회입니다.
PC의 UTF-16 문자열 인덱싱과 nanoCLR의 UTF-8 내부 문자열 인덱싱 차이는
네이티브 경로의 UTF-8 바이트 검증으로 처리하며 한글과 보조 평면 문자도 실제 CLR에서 확인합니다.
스트림 준비 콜백은 `MqttReadReady`, 작업은 `MqttWork`라는 명시적 델리게이트 형식을 사용합니다.
`Func<int,bool>`를 사용한 최초 어댑터는 nanoCLR 1.18.0.30의 메타데이터 해석 단계에서
부팅 실패를 일으켰으며, PC 계약/MCU 빌드만으로 검출되지 않았습니다.
명시적 델리게이트 형식으로 변경한 뒤 가상 CLR과 실물 기능 시험을 모두 다시 실행했습니다.

```powershell
dotnet run -c Release --project .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests
.\Build\Test-Virtual.ps1
.\Build\Pack.ps1 -LibrarySet MQTT -OutputPath .\bin\nuget\mqtt-new
.\Build\Test-Packages.ps1 -LibrarySet MQTT -FeedPath .\bin\nuget\mqtt-new
```

기존 루트 packages 복원 후 실행합니다. 패키지는 로컬 preview이며 원격 게시하지 않습니다.
패키지 전용 consumer는 프로젝트·샘플 소스 참조 없이 빌드해 외부 사용 경계를 확인합니다.
최종 MQTT 5 검증 피드는 `bin\nuget\mqtt5-final-20261028`이며
기록은 `bin\mqtt5-final-package-test-20261028.log`입니다.
이전 MQTT 3.x 검증 피드 `bin\nuget\mqtt-expanded-final-20261028`는 역사적 산출물입니다.
네이티브 메타데이터 수정 전의 임시 `mqtt-expanded-20261028` 패키지는 회수했습니다.
[MQTT 매핑 샘플](../../../Samples/MQTT/VagabondK.nF.MQTT.Mapping/README.md)과
[Sparkplug 매핑 샘플](../../../Samples/MQTT/VagabondK.nF.MQTT.Sparkplug.Mapping/README.md)은 이 DLL을 직접 참조합니다.
