namespace VagabondK.nF.MQTT
{
    /// <summary>미완료 QoS 핸드셰이크와 패킷 ID를 보관하는 RAM 전용 MQTT 세션 상태입니다.</summary>
    /// <remarks>같은 클라이언트 ID·브로커로 재연결할 때 새 채널과 새 <see cref="MqttConnection"/>에 넘겨 재사용합니다.
    /// 장치를 재부팅하면 사라지며, 한 번에 하나의 연결만 점유할 수 있습니다. 동시에 사용하려 하면 Connect가 거부됩니다.</remarks>
    public sealed class MqttSessionState
    {
        internal readonly MqttPendingPublication[] Incoming;
        internal readonly int MaximumInflight;
        internal MqttPendingSend[] Batch;
        internal long ExpiresAt;
        /// <summary>정해진 용량의 RAM 세션 상태를 만듭니다.</summary>
        /// <param name="incomingCapacity">PUBREL을 기다리는 수신 QoS2 메시지를 보관할 슬롯 수(1..65535)입니다. MQTT 5 Receive Maximum 기본값으로도 광고됩니다.</param>
        /// <param name="maximumInflight">PublishBatch 한 번에 보낼 수 있는 최대 요청 수이자 동시에 확인 응답을 기다릴 수 있는 최대 요청 수(1..65535)입니다.</param>
        /// <exception cref="System.ArgumentOutOfRangeException">용량이 범위를 벗어난 경우.</exception>
        public MqttSessionState(int incomingCapacity = 16, int maximumInflight = 16)
        {
            if (incomingCapacity < 1 || incomingCapacity > 65535 || maximumInflight < 1 || maximumInflight > 65535)
                throw new System.ArgumentOutOfRangeException("incomingCapacity/maximumInflight");
            Incoming = new MqttPendingPublication[incomingCapacity]; MaximumInflight = maximumInflight;
        }
        internal MqttConnection Owner;
        internal string ClientId;
        internal byte ProtocolLevel;
        internal ushort Identifier;
        internal byte[] OutgoingBody;
        internal byte OutgoingHeader, OutgoingStage;
        internal ushort OutgoingId;
        internal readonly ushort[] CompletedIds = new ushort[16];
        internal readonly byte[] CompletedHeaders = new byte[16];
        internal readonly byte[][] CompletedReasons = new byte[16][];
        internal int CompletedPosition;
        /// <summary>브로커와의 핸드셰이크가 끝나지 않은 QoS1/2 발행(단일 또는 배치)이 남아 있으면 <see langword="true"/>입니다.</summary>
        /// <remarks>지속 세션으로 재연결하면 남은 발행은 Connect 중에 재전송되어 완료됩니다.</remarks>
        public bool HasPendingPublication
        {
            get
            {
                if (OutgoingStage != 0) return true;
                if (Batch != null) foreach (var pending in Batch) if (!pending.Completed) return true;
                return false;
            }
        }
        internal void Clear()
        {
            ClearIncoming();
            ClearOutgoing(); Batch = null; ExpiresAt = 0; Identifier = 0;
            for (int i = 0; i < CompletedIds.Length; i++) { CompletedIds[i] = 0; CompletedReasons[i] = null; }
            CompletedPosition = 0;
        }
        internal void ClearIncoming()
        { for (int i = 0; i < Incoming.Length; i++) Incoming[i] = null; }
        internal void ClearOutgoing() { OutgoingBody = null; OutgoingId = 0; OutgoingStage = 0; }
        internal void Complete(byte header, ushort id, byte[] reasons = null)
        {
            CompletedIds[CompletedPosition] = id; CompletedHeaders[CompletedPosition] = header;
            if (reasons != null)
            {
                var copy = new byte[reasons.Length]; System.Array.Copy(reasons, copy, copy.Length);
                CompletedReasons[CompletedPosition] = copy;
            }
            else CompletedReasons[CompletedPosition] = null;
            CompletedPosition = (CompletedPosition + 1) % CompletedIds.Length;
        }
    }

    internal sealed class MqttPendingPublication
    {
        internal ushort Id;
        internal byte[] Body;
        internal byte Header;
        internal string Topic;
        internal int PayloadOffset;
        internal MqttProperty[] Properties;
    }
}
