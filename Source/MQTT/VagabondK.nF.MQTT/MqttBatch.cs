using System;
using System.IO;

namespace VagabondK.nF.MQTT
{
    /// <summary><see cref="MqttConnection.PublishBatch(MqttPublishRequest[])"/>로 한꺼번에 보낼 발행 요청 하나입니다.</summary>
    /// <remarks>배치의 모든 요청을 검증·인코딩한 뒤 전송을 시작합니다. 호출이 반환될 때까지 요청과 페이로드·속성을 변경하면 안 됩니다.</remarks>
    public sealed class MqttPublishRequest
    {
        /// <summary>와일드카드가 없는 전체 토픽 이름입니다.</summary>
        public string Topic { get; set; }
        /// <summary>보낼 페이로드입니다. 연결의 최대 페이로드 크기 이하여야 하며 <see langword="null"/>은 허용되지 않습니다.</summary>
        public byte[] Payload { get; set; }
        /// <summary>전달 보장 수준(QoS0/1/2)입니다.</summary>
        public MqttQos Qos { get; set; }
        /// <summary>브로커에 보관 메시지로 남길지 여부입니다.</summary>
        public bool Retain { get; set; }
        /// <summary>MQTT 5 PUBLISH 속성입니다. 없거나 MQTT 3.x이면 <see langword="null"/>입니다.</summary>
        public MqttProperty[] Properties { get; set; }
    }
    internal sealed class MqttPendingSend
    {
        internal byte Header, Stage, Reason;
        internal ushort Id;
        internal byte[] Body;
        internal bool Sent, Completed;
        internal long Deadline;
        internal int Retries;
    }
    public sealed partial class MqttConnection
    {
        /// <summary>여러 발행을 파이프라인으로 보내 QoS 핸드셰이크 여러 개를 동시에 진행하고, 모두 끝날 때까지 기다립니다.</summary>
        /// <remarks>
        /// <para>동시에 확인을 기다리는 QoS1/2 수는 <see cref="MqttSessionState"/>의 maximumInflight와 MQTT 5 서버 Receive Maximum 중 작은 값으로 제한됩니다.
        /// 확인 시간이 지난 요청은 AcknowledgmentRetries만큼 DUP 재전송합니다.</para>
        /// <para>전송 실패 시 연결은 Faulted가 되고, 완료되지 않은 요청은 세션에 남아 지속 세션 재연결 때 이어서 처리됩니다.</para>
        /// </remarks>
        /// <param name="requests">보낼 요청입니다. 개수는 1..세션의 maximumInflight이며 <see langword="null"/> 요소는 허용되지 않습니다.</param>
        /// <returns>요청별 완료 사유 코드입니다. QoS0과 MQTT 3.x는 0이고, MQTT 5 거부(128 이상)는 예외 없이 결과에 그대로 담깁니다.</returns>
        /// <exception cref="ArgumentNullException">요청 요소가 <see langword="null"/>인 경우.</exception>
        /// <exception cref="ArgumentException">배열이 비었거나 용량을 넘거나, 토픽·페이로드·QoS·속성이 잘못되었거나 서버 Maximum Packet Size를 넘는 경우.</exception>
        /// <exception cref="InvalidOperationException">연결되지 않았거나 재진입했거나, 브로커의 QoS·Retain·토픽 별칭 한도를 넘는 경우.</exception>
        /// <exception cref="IOException">전송·프로토콜 오류. 연결은 Faulted가 됩니다.</exception>
        /// <exception cref="TimeoutException">재시도 후에도 확인이 오지 않은 경우. 연결은 Faulted가 됩니다.</exception>
        public byte[] PublishBatch(MqttPublishRequest[] requests)
        {
            if (requests == null || requests.Length == 0 || requests.Length > session.MaximumInflight)
                throw new ArgumentException("MQTT batch capacity.");
            if (!Connected || active) throw new InvalidOperationException("MQTT disconnected, faulted or reentrant.");
            var pending = new MqttPendingSend[requests.Length];
            for (int i = 0; i < requests.Length; i++)
            {
                var request = requests[i];
                if (request == null) throw new ArgumentNullException("requests");
                Name(request.Topic, false, maximumNameBytes);
                if (request.Payload == null || request.Payload.Length > maximumPayload || request.Qos > MqttQos.ExactlyOnce)
                    throw new ArgumentException("MQTT publication payload/QoS.");
                if ((byte)request.Qos > Number(ConnackProperties, MqttPropertyId.MaximumQos, 2) ||
                    request.Retain && Number(ConnackProperties, MqttPropertyId.RetainAvailable, 1) == 0 ||
                    Find(request.Properties, MqttPropertyId.TopicAlias) != null && Number(request.Properties, MqttPropertyId.TopicAlias, 0) > Number(ConnackProperties, MqttPropertyId.TopicAliasMaximum, 0))
                    throw new InvalidOperationException("MQTT broker publication limit.");
                byte[] encoded = PacketProperties(request.Properties, MqttPropertyContext.Publish);
                outgoing.Reset(); outgoing.Text(request.Topic); if (request.Qos != MqttQos.AtMostOnce) outgoing.Word(0);
                if (protocolLevel == 5) WriteProperties(encoded);
                outgoing.Raw(request.Payload);
                if (outgoing.Count + 1 + VariableSize(outgoing.Count) > serverPacketSize)
                    throw new ArgumentException("MQTT batch packet exceeds server Maximum Packet Size.");
                pending[i] = new MqttPendingSend { Header = (byte)(0x30 | (byte)request.Qos << 1 | (request.Retain ? 1 : 0)),
                    Stage = (byte)request.Qos, Body = CaptureOutgoing(), Retries = acknowledgmentRetries };
            }
            Enter(); bool success = false;
            try
            {
                for (int i = 0; i < pending.Length; i++)
                    if (pending[i].Stage != 0)
                    {
                        ushort id = Identifier(); pending[i].Id = id;
                        int position = Utf8Length(requests[i].Topic) + 2;
                        pending[i].Body[position] = (byte)(id >> 8); pending[i].Body[position + 1] = (byte)id;
                    }
                session.Batch = pending;
                DrainBatch(pending, false);
                byte[] reasons = new byte[pending.Length];
                for (int i = 0; i < pending.Length; i++) reasons[i] = pending[i].Reason;
                session.Batch = null; success = true;
                return reasons;
            }
            finally { active = false; if (!success) Fault(); }
        }
        private void ResumeBatch()
        {
            var pending = session.Batch;
            foreach (var item in pending) { item.Sent = false; item.Retries = acknowledgmentRetries; }
            DrainBatch(pending, true);
            session.Batch = null;
            foreach (var item in pending)
                if (item.Reason >= 128) throw new MqttReasonException((MqttPacketType)(item.Header >> 4), item.Reason, NoProperties);
        }
        private void SendPending(MqttPendingSend pending, bool replay)
        {
            pending.Deadline = channel.Milliseconds + operationTimeout;
            if (pending.Stage == 3) Acknowledge(0x62, pending.Id, pending.Deadline);
            else Replay((byte)(pending.Header | (replay && pending.Stage != 0 ? 8 : 0)), pending.Body, pending.Deadline);
            pending.Sent = true;
            if (pending.Stage == 0) pending.Completed = true;
        }
        private void DrainBatch(MqttPendingSend[] pending, bool replay)
        {
            int window = (int)Number(ConnackProperties, MqttPropertyId.ReceiveMaximum, (uint)session.MaximumInflight);
            while (true)
            {
                int inflight = 0, unfinished = 0;
                foreach (var item in pending) if (!item.Completed) { unfinished++; if (item.Sent) inflight++; }
                if (unfinished == 0) return;
                foreach (var item in pending)
                    if (!item.Completed && !item.Sent && (item.Stage == 0 || inflight < window))
                    { SendPending(item, replay); if (item.Stage != 0) inflight++; }
                if (inflight == 0) continue;
                KeepAlive();
                long deadline = channel.Milliseconds + operationTimeout;
                foreach (var item in pending) if (!item.Completed && item.Sent && item.Deadline < deadline) deadline = item.Deadline;
                long packetDeadline = deadline;
                if (keepAliveMilliseconds != 0)
                {
                    long keepDeadline = pingPending ? pingSent + operationTimeout : lastSent + keepAliveMilliseconds / 2;
                    if (keepDeadline < deadline) deadline = keepDeadline;
                }
                Packet packet;
                try { packet = Receive(deadline, false, packetDeadline); }
                catch (ResponseTimeoutException)
                {
                    long now = channel.Milliseconds;
                    foreach (var item in pending)
                        if (!item.Completed && item.Sent && now >= item.Deadline)
                        {
                            if (item.Retries-- == 0) throw;
                            SendPending(item, true);
                        }
                    continue;
                }
                bool handled = false;
                if (packet.Header == 0x40 || packet.Header == 0x50 || packet.Header == 0x70)
                {
                    if (packet.Bytes.Length < 2) throw new IOException("MQTT batch acknowledgment identifier.");
                    ushort id = (ushort)(packet.Bytes[0] << 8 | packet.Bytes[1]);
                    foreach (var item in pending)
                        if (!item.Completed && item.Sent && item.Id == id)
                        {
                            byte expected = item.Stage == 1 ? (byte)0x40 : item.Stage == 2 ? (byte)0x50 : (byte)0x70;
                            if (packet.Header != expected && !(item.Stage == 3 && packet.Header == 0x50))
                                throw new IOException("MQTT batch acknowledgment stage.");
                            byte reason = ValidateAcknowledgment(packet, false);
                            handled = true;
                            if (reason >= 128 || item.Stage == 1 || packet.Header == 0x70)
                            {
                                item.Reason = reason; item.Completed = true;
                                session.Complete(packet.Header, id);
                            }
                            else
                            {
                                item.Stage = 3; item.Retries = acknowledgmentRetries;
                                SendPending(item, false);
                            }
                            break;
                        }
                }
                if (!handled) Process(packet, channel.Milliseconds + operationTimeout);
            }
        }
    }
}
