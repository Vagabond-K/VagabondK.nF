namespace VagabondK.nF.MQTT
{
    /// <summary>MQTT 발행의 전달 보장 수준(QoS)입니다.</summary>
    public enum MqttQos : byte
    {
        /// <summary>QoS0: 확인 응답 없이 한 번 보냅니다. 유실될 수 있습니다.</summary>
        AtMostOnce = 0,
        /// <summary>QoS1: PUBACK으로 확인합니다. 재전송으로 중복 수신이 생길 수 있습니다.</summary>
        AtLeastOnce = 1,
        /// <summary>QoS2: PUBREC/PUBREL/PUBCOMP 확인 절차로 같은 세션의 중복 전달을 방지합니다. 앱의 외부 동작까지 정확히 한 번 실행되는 것은 아닙니다.</summary>
        ExactlyOnce = 2
    }

    /// <summary>고정 헤더 플래그를 제외한 MQTT 제어 패킷 종류(상위 4비트 값)입니다.</summary>
    public enum MqttPacketType : byte
    {
        /// <summary>클라이언트의 연결 요청입니다.</summary>
        Connect = 1,
        /// <summary>연결 요청에 대한 브로커 응답입니다.</summary>
        ConnAck = 2,
        /// <summary>메시지 발행입니다.</summary>
        Publish = 3,
        /// <summary>QoS1 발행 확인입니다.</summary>
        PubAck = 4,
        /// <summary>QoS2 발행을 수신했다는 확인(PUBREC)입니다.</summary>
        PubRec = 5,
        /// <summary>QoS2 수신 확인에 응답하는 해제 요청(PUBREL)입니다.</summary>
        PubRel = 6,
        /// <summary>QoS2 해제 요청을 처리했다는 완료 확인(PUBCOMP)입니다.</summary>
        PubComp = 7,
        /// <summary>구독 요청입니다.</summary>
        Subscribe = 8,
        /// <summary>구독 요청에 대한 응답입니다.</summary>
        SubAck = 9,
        /// <summary>구독 해제 요청입니다.</summary>
        Unsubscribe = 10,
        /// <summary>구독 해제 요청에 대한 응답입니다.</summary>
        UnsubAck = 11,
        /// <summary>keepalive 요청(PINGREQ)입니다.</summary>
        PingReq = 12,
        /// <summary>keepalive 응답(PINGRESP)입니다.</summary>
        PingResp = 13,
        /// <summary>연결 종료 통지입니다. MQTT 5에서는 서버도 보낼 수 있습니다.</summary>
        Disconnect = 14,
        /// <summary>MQTT 5 확장 인증 교환입니다.</summary>
        Auth = 15
    }

    /// <summary>표준 MQTT 5 속성 식별자입니다. 값마다 정해진 와이어 형식과 허용 패킷이 있으며, 연결이 송수신 시 검증합니다.</summary>
    public enum MqttPropertyId : byte
    {
        /// <summary>페이로드 형식(0 바이너리, 1 UTF-8 텍스트)입니다. 1바이트 값입니다.</summary>
        PayloadFormatIndicator = 1,
        /// <summary>메시지 만료 시간(초)입니다.</summary>
        MessageExpiryInterval = 2,
        /// <summary>페이로드의 콘텐츠 형식(MIME 등) 문자열입니다.</summary>
        ContentType = 3,
        /// <summary>요청/응답 패턴에서 응답을 보낼 토픽입니다.</summary>
        ResponseTopic = 8,
        /// <summary>요청과 응답을 짝짓는 바이너리 상관 데이터입니다.</summary>
        CorrelationData = 9,
        /// <summary>구독 식별자(가변 길이 정수, 1..268435455)입니다. 수신 PUBLISH에는 여러 개가 올 수 있습니다.</summary>
        SubscriptionIdentifier = 11,
        /// <summary>연결 종료 후 세션을 유지할 시간(초)입니다. 0이 아니면 지속 세션입니다.</summary>
        SessionExpiryInterval = 17,
        /// <summary>빈 클라이언트 ID로 연결했을 때 브로커가 할당한 ID입니다.</summary>
        AssignedClientIdentifier = 18,
        /// <summary>브로커가 지정한 keepalive 간격(초)으로, 클라이언트 요청 값을 대체합니다.</summary>
        ServerKeepAlive = 19,
        /// <summary>확장 인증 방식 이름입니다.</summary>
        AuthenticationMethod = 21,
        /// <summary>확장 인증 바이너리 데이터입니다. Authentication Method가 필요합니다.</summary>
        AuthenticationData = 22,
        /// <summary>오류 시 Reason String/User Property를 받을지 요청합니다(0 또는 1).</summary>
        RequestProblemInformation = 23,
        /// <summary>연결이 끊긴 뒤 Will을 발행하기까지 기다릴 시간(초)입니다.</summary>
        WillDelayInterval = 24,
        /// <summary>CONNACK에 Response Information을 받을지 요청합니다(0 또는 1).</summary>
        RequestResponseInformation = 25,
        /// <summary>응답 토픽 구성에 쓸 수 있도록 브로커가 알려 주는 문자열입니다.</summary>
        ResponseInformation = 26,
        /// <summary>클라이언트가 대신 접속할 다른 서버를 가리키는 문자열입니다.</summary>
        ServerReference = 28,
        /// <summary>사람이 읽을 수 있는 진단용 사유 문자열입니다.</summary>
        ReasonString = 31,
        /// <summary>동시에 처리할 수 있는 QoS1/2 발행 수(1..65535)입니다.</summary>
        ReceiveMaximum = 33,
        /// <summary>상대가 사용할 수 있는 토픽 별칭 최대값입니다. 0이면 별칭을 받지 않습니다.</summary>
        TopicAliasMaximum = 34,
        /// <summary>토픽 이름 대신 쓰는 정수 별칭(1 이상)입니다.</summary>
        TopicAlias = 35,
        /// <summary>브로커가 지원하는 최대 QoS(0 또는 1, 없으면 2)입니다.</summary>
        MaximumQos = 36,
        /// <summary>보관(retained) 메시지 지원 여부(0 또는 1)입니다.</summary>
        RetainAvailable = 37,
        /// <summary>여러 번 반복할 수 있는 UTF-8 키/값 사용자 속성입니다.</summary>
        UserProperty = 38,
        /// <summary>받을 수 있는 최대 패킷 크기(바이트, 고정 헤더 포함)입니다.</summary>
        MaximumPacketSize = 39,
        /// <summary>와일드카드 구독 지원 여부(0 또는 1)입니다.</summary>
        WildcardSubscriptionAvailable = 40,
        /// <summary>구독 식별자 지원 여부(0 또는 1)입니다.</summary>
        SubscriptionIdentifierAvailable = 41,
        /// <summary>공유 구독(<c>$share/</c>) 지원 여부(0 또는 1)입니다.</summary>
        SharedSubscriptionAvailable = 42
    }

    internal enum MqttPropertyKind : byte
    {
        Byte, TwoByteInteger, FourByteInteger, VariableByteInteger, Utf8String, BinaryData, Utf8StringPair
    }

    // Will 속성에는 독립적인 제어 패킷 종류가 없습니다.
    internal enum MqttPropertyContext : byte
    {
        Connect = 1, ConnAck = 2, Publish = 3, PubAck = 4, PubRec = 5, PubRel = 6,
        PubComp = 7, Subscribe = 8, SubAck = 9, Unsubscribe = 10, UnsubAck = 11,
        Disconnect = 14, Auth = 15, Will = 16
    }
}
