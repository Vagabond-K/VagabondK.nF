namespace VagabondK.nF.MQTT
{
    /// <summary><see cref="MqttConnection.Connect(MqttConnectionOptions)"/>에 넘기는 CONNECT 설정입니다.</summary>
    /// <remarks>연결은 Connect 호출 중에 값을 읽어 검증·인코딩하므로, 호출 후 이 개체를 바꿔도 진행 중인 연결에는 영향이 없습니다.</remarks>
    public sealed class MqttConnectionOptions
    {
        /// <summary>MQTT UTF-8 클라이언트 ID입니다.</summary>
        /// <remarks>빈 문자열은 MQTT 5 또는 클린 세션 MQTT 3.1.1에서만 허용됩니다. MQTT 5에서 비워 두면 브로커가 할당한 ID가
        /// <see cref="MqttConnection.ClientId"/>에 반영됩니다.</remarks>
        public string ClientId { get; set; }
        /// <summary>선택적 MQTT UTF-8 사용자 이름입니다. MQTT 3.x에서 <see cref="Password"/>를 쓰려면 반드시 지정해야 합니다.</summary>
        public string UserName { get; set; }
        /// <summary>선택적 비밀번호 바이트(최대 65535바이트)입니다. 검증된 TLS 위에서 전송하십시오.</summary>
        /// <remarks>Connect가 인코딩하는 동안 배열을 변경하면 안 됩니다. 송신 버퍼에서 인증 정보가 즉시 지워진다는 보장은 없습니다.</remarks>
        public byte[] Password { get; set; }
        /// <summary>Will 토픽입니다. <see cref="WillPayload"/>와 함께 <see langword="null"/>이면 Will을 등록하지 않습니다.</summary>
        public string WillTopic { get; set; }
        /// <summary>비정상 종료 시 브로커가 발행할 바이너리 페이로드입니다. Will을 쓰면 필수이며 최대 페이로드 크기 이하여야 합니다.</summary>
        public byte[] WillPayload { get; set; }
        /// <summary>Will 메시지의 QoS입니다. 기본값은 QoS1입니다.</summary>
        public MqttQos WillQos { get; set; } = MqttQos.AtLeastOnce;
        /// <summary>브로커가 Will 메시지를 보관 메시지로 남길지 여부입니다. Will 없이 <see langword="true"/>로 설정하면 거부됩니다.</summary>
        public bool WillRetain { get; set; }
        /// <summary>keepalive 간격(초)입니다. 0이면 비활성화하며 기본값은 20입니다. MQTT 5 Server Keep Alive가 오면 그 값을 따릅니다.</summary>
        public ushort KeepAliveSeconds { get; set; } = 20;
        /// <summary>MQTT 프로토콜 수준입니다. 3(3.1), 4(3.1.1, 기본값), 5 중 하나입니다.</summary>
        public byte ProtocolLevel { get; set; } = 4;
        /// <summary>MQTT 5 CONNECT 속성입니다. MQTT 3.x에서는 <see langword="null"/>이어야 합니다.</summary>
        /// <remarks>지정하지 않으면 Receive Maximum은 세션의 수신 QoS2 용량, Maximum Packet Size는 설정된 본문 용량 + 헤더(5바이트)로 자동 추가됩니다.
        /// 로컬 용량보다 큰 값을 광고하면 거부됩니다. Session Expiry Interval이 0이 아니면 지속 세션으로 취급합니다.</remarks>
        public MqttProperty[] Properties { get; set; }
        /// <summary>MQTT 5 Will 속성(Will Delay Interval 포함)입니다. Will 없이 지정하면 거부됩니다.</summary>
        public MqttProperty[] WillProperties { get; set; }
        /// <summary>MQTT 5 확장 인증 챌린지에 응답하는 콜백입니다. 연결을 다시 호출(재진입)하면 안 됩니다.</summary>
        /// <remarks>Properties에 Authentication Method가 있어야 사용됩니다. MQTT 3.x에서는 <see langword="null"/>이어야 합니다.</remarks>
        public MqttAuthenticate Authenticate { get; set; }
        /// <summary>MQTT 3.x의 Clean Session 또는 MQTT 5의 Clean Start 여부입니다. <see langword="false"/>이면 이전 세션의 재개를 요청합니다.</summary>
        /// <remarks>MQTT 5에서 연결 종료 후 세션을 유지하려면 Session Expiry Interval도 지정해야 합니다.
        /// 세션을 재개할 때는 같은 <see cref="MqttSessionState"/>를 사용해야 하며, 여러 활성 연결에서 동시에 사용할 수 없습니다.</remarks>
        public bool CleanSession { get; set; } = true;
        /// <summary>확인 응답 제한 시간이 지났을 때 같은 연결에서 DUP 재전송할 추가 횟수(0..10)입니다.</summary>
        /// <remarks>기본값 0은 첫 제한 시간 초과에서 바로 실패(전달 불확실)하는 동작을 유지합니다.</remarks>
        public int AcknowledgmentRetries { get; set; }
        /// <summary>요청 전체·송신·부분 프레임 수신에 적용하는 제한 시간(밀리초), 1..60000입니다. 기본값은 3000입니다.</summary>
        /// <remarks>재시도·배치 단계와 사용자 콜백의 처리 비용을 합한 API 반환 시간의 상한은 아닙니다.</remarks>
        public int OperationTimeoutMilliseconds { get; set; } = 3000;
    }
}
