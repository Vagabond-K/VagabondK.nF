using System;
using System.IO;

namespace VagabondK.nF.MQTT
{
    /// <summary>형식이 정해진 MQTT 5 속성 하나입니다.</summary>
    /// <remarks>생성 시 식별자에 맞는 값 형식·범위를 검증하고, 패킷별 허용 여부는 송신 직전에 검증합니다.
    /// 바이너리 값은 생성 시 복사되지만 <see cref="Bytes"/>가 반환하는 배열은 내부 배열이므로 요청 실행 중에 수정하면 안 됩니다.</remarks>
    public sealed class MqttProperty
    {
        /// <summary>MQTT 5 속성 식별자입니다.</summary>
        public MqttPropertyId Id { get; private set; }
        /// <summary>1바이트·2바이트·4바이트·가변 길이 정수 속성의 값입니다. 다른 형식이면 0입니다.</summary>
        public uint Number { get; private set; }
        /// <summary>UTF-8 문자열 속성의 값 또는 User Property의 키입니다.</summary>
        public string Text { get; private set; }
        /// <summary>User Property의 값입니다. 다른 형식이면 <see langword="null"/>입니다.</summary>
        public string Value { get; private set; }
        /// <summary>바이너리 속성(Correlation Data, Authentication Data)의 값입니다. 다른 형식이면 <see langword="null"/>입니다.</summary>
        public byte[] Bytes { get; private set; }
        /// <summary>정수 속성을 만듭니다. 와이어 폭(1/2/4바이트 또는 가변 길이)은 식별자로 결정됩니다.</summary>
        /// <param name="id">정수 형식 속성 식별자입니다.</param>
        /// <param name="number">값입니다. 식별자의 폭과 의미 범위(예: 플래그는 0/1, Receive Maximum은 1 이상)를 지켜야 합니다.</param>
        /// <exception cref="ArgumentException">정수 형식이 아닌 식별자이거나 값이 범위를 벗어난 경우.</exception>
        public MqttProperty(MqttPropertyId id, uint number)
        {
            MqttPropertyKind kind = Kind(id);
            if (kind > MqttPropertyKind.VariableByteInteger || kind == MqttPropertyKind.Byte && number > 255 || kind == MqttPropertyKind.TwoByteInteger && number > 65535 ||
                kind == MqttPropertyKind.VariableByteInteger && (number == 0 || number > 268435455))
                throw new ArgumentException("MQTT property numeric type/range.");
            Id = id; Number = number;
            if ((id == MqttPropertyId.PayloadFormatIndicator || id == MqttPropertyId.RequestProblemInformation || id == MqttPropertyId.RequestResponseInformation || id == MqttPropertyId.MaximumQos || id == MqttPropertyId.RetainAvailable || id == MqttPropertyId.WildcardSubscriptionAvailable || id == MqttPropertyId.SubscriptionIdentifierAvailable || id == MqttPropertyId.SharedSubscriptionAvailable) && number > 1 ||
                (id == MqttPropertyId.ReceiveMaximum || id == MqttPropertyId.TopicAlias || id == MqttPropertyId.MaximumPacketSize) && number == 0)
                throw new ArgumentException("MQTT property value.");
        }
        /// <summary>UTF-8 문자열 속성을 만듭니다.</summary>
        /// <param name="id">문자열 형식 속성 식별자입니다(예: Content Type, Response Topic, Reason String).</param>
        /// <param name="text">값입니다. 송신 시 MQTT UTF-8 규칙과 65535바이트 한도로 검증됩니다.</param>
        /// <exception cref="ArgumentException">문자열 형식이 아닌 식별자이거나 <paramref name="text"/>가 <see langword="null"/>인 경우.</exception>
        public MqttProperty(MqttPropertyId id, string text)
        {
            if (Kind(id) != MqttPropertyKind.Utf8String || text == null) throw new ArgumentException("MQTT property text type.");
            Id = id; Text = text;
        }
        /// <summary>바이너리 속성을 만듭니다. 입력 배열은 복사됩니다.</summary>
        /// <param name="id">바이너리 형식 속성 식별자(Correlation Data, Authentication Data)입니다.</param>
        /// <param name="bytes">값입니다. 최대 65535바이트입니다.</param>
        /// <exception cref="ArgumentException">바이너리 형식이 아닌 식별자이거나 값이 <see langword="null"/>이거나 너무 긴 경우.</exception>
        public MqttProperty(MqttPropertyId id, byte[] bytes)
        {
            if (Kind(id) != MqttPropertyKind.BinaryData || bytes == null || bytes.Length > 65535) throw new ArgumentException("MQTT property binary type/length.");
            Id = id; Bytes = new byte[bytes.Length]; Array.Copy(bytes, Bytes, bytes.Length);
        }
        /// <summary>반복 가능한 User Property(식별자 38)를 만듭니다.</summary>
        /// <param name="key">UTF-8 키입니다.</param>
        /// <param name="value">UTF-8 값입니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> 또는 <paramref name="value"/>가 <see langword="null"/>인 경우.</exception>
        public MqttProperty(string key, string value)
        {
            if (key == null || value == null) throw new ArgumentNullException("key/value");
            Id = MqttPropertyId.UserProperty; Text = key; Value = value;
        }
        internal static MqttPropertyKind Kind(MqttPropertyId id)
        {
            switch (id)
            {
                case MqttPropertyId.PayloadFormatIndicator: case MqttPropertyId.RequestProblemInformation: case MqttPropertyId.RequestResponseInformation: case MqttPropertyId.MaximumQos: case MqttPropertyId.RetainAvailable: case MqttPropertyId.WildcardSubscriptionAvailable: case MqttPropertyId.SubscriptionIdentifierAvailable: case MqttPropertyId.SharedSubscriptionAvailable: return MqttPropertyKind.Byte;
                case MqttPropertyId.ServerKeepAlive: case MqttPropertyId.ReceiveMaximum: case MqttPropertyId.TopicAliasMaximum: case MqttPropertyId.TopicAlias: return MqttPropertyKind.TwoByteInteger;
                case MqttPropertyId.MessageExpiryInterval: case MqttPropertyId.SessionExpiryInterval: case MqttPropertyId.WillDelayInterval: case MqttPropertyId.MaximumPacketSize: return MqttPropertyKind.FourByteInteger;
                case MqttPropertyId.SubscriptionIdentifier: return MqttPropertyKind.VariableByteInteger;
                case MqttPropertyId.ContentType: case MqttPropertyId.ResponseTopic: case MqttPropertyId.AssignedClientIdentifier: case MqttPropertyId.AuthenticationMethod: case MqttPropertyId.ResponseInformation: case MqttPropertyId.ServerReference: case MqttPropertyId.ReasonString: return MqttPropertyKind.Utf8String;
                case MqttPropertyId.CorrelationData: case MqttPropertyId.AuthenticationData: return MqttPropertyKind.BinaryData;
                case MqttPropertyId.UserProperty: return MqttPropertyKind.Utf8StringPair;
                default: throw new ArgumentException("Unknown MQTT property.");
            }
        }
    }

    /// <summary>브로커가 사유 코드로 요청을 거부했거나 서버가 DISCONNECT를 보냈을 때 발생하는 예외입니다.</summary>
    /// <remarks><see cref="ReasonCode"/>와 <see cref="Properties"/>로 MQTT 5 진단 정보(Reason String, Server Reference 등)를 확인할 수 있습니다.</remarks>
    public sealed class MqttReasonException : IOException
    {
        /// <summary>거부 또는 종료를 알린 패킷 종류입니다.</summary>
        public MqttPacketType PacketType { get; private set; }
        /// <summary>MQTT 사유 코드입니다. 일반적으로 128 이상이며, 서버 DISCONNECT는 0일 수도 있습니다.</summary>
        public byte ReasonCode { get; private set; }
        /// <summary>함께 수신해 검증한 MQTT 5 속성입니다. 없으면 빈 배열입니다.</summary>
        public MqttProperty[] Properties { get; private set; }
        /// <summary>패킷 종류와 사유 코드로 예외를 만듭니다.</summary>
        /// <param name="packetType">거부 또는 종료를 알린 패킷 종류입니다.</param>
        /// <param name="reasonCode">MQTT 사유 코드입니다.</param>
        /// <param name="properties">관련 MQTT 5 속성입니다.</param>
        public MqttReasonException(MqttPacketType packetType, byte reasonCode, MqttProperty[] properties)
            : base("MQTT broker reason: packet=" + packetType + " code=" + reasonCode)
        { PacketType = packetType; ReasonCode = reasonCode; Properties = properties; }
    }

    /// <summary>MQTT 5 속성을 포함한 수신 발행 정보입니다. <see cref="MqttConnection.MessageReceived"/> 콜백에 전달됩니다.</summary>
    /// <remarks><see cref="Payload"/>는 메시지마다 새로 복사한 배열이므로 콜백이 보관해도 됩니다.</remarks>
    public sealed class MqttMessage
    {
        /// <summary>수신 토픽 별칭까지 해석한 전체 토픽 이름입니다.</summary>
        public string Topic { get; internal set; }
        /// <summary>페이로드 바이트 복사본입니다.</summary>
        public byte[] Payload { get; internal set; }
        /// <summary>이 메시지를 받은 QoS입니다.</summary>
        public MqttQos Qos { get; internal set; }
        /// <summary>브로커의 보관 메시지로 전달되었는지 여부입니다.</summary>
        public bool Retained { get; internal set; }
        /// <summary>DUP 플래그, 즉 재전송 패킷인지 여부입니다.</summary>
        public bool Duplicate { get; internal set; }
        /// <summary>MQTT 5 PUBLISH 속성입니다. MQTT 3.x에서는 빈 배열입니다.</summary>
        public MqttProperty[] Properties { get; internal set; }
    }
    /// <summary>프로토콜 메타데이터를 포함한 수신 발행을 받는 콜백입니다.</summary>
    /// <remarks>작업을 호출한 스레드에서 동기적으로 실행되며 연결을 재진입 호출하면 안 됩니다.
    /// 콜백의 처리 시간도 통신 기한을 소모하고 예외를 던지면 연결이 Faulted가 됩니다.</remarks>
    /// <param name="message">수신한 메시지입니다.</param>
    public delegate void MqttMessageReceived(MqttMessage message);
    /// <summary>MQTT 5 확장 인증 챌린지(AUTH, 사유 24 Continue Authentication)에 응답합니다.</summary>
    /// <remarks>응답 속성의 Authentication Method는 CONNECT와 같아야 합니다. 예외를 던지면 인증이 중단되고 연결이 Faulted가 됩니다.
    /// 콜백 안에서 연결을 다시 호출하면 안 됩니다.</remarks>
    /// <param name="reasonCode">브로커가 보낸 AUTH 사유 코드(24)입니다.</param>
    /// <param name="challenge">브로커가 보낸 AUTH 속성(Authentication Method/Data 등)입니다.</param>
    /// <returns>브로커에 보낼 AUTH 응답 속성입니다. <see langword="null"/>이면 연결이 실패합니다.</returns>
    public delegate MqttProperty[] MqttAuthenticate(byte reasonCode, MqttProperty[] challenge);
}
