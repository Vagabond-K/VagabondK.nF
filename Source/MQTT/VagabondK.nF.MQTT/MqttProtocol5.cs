using System;
using System.IO;

namespace VagabondK.nF.MQTT
{
    public sealed partial class MqttConnection
    {
        private byte[] PacketProperties(MqttProperty[] properties, MqttPropertyContext context)
        {
            if (protocolLevel == 5) return properties == null || properties.Length == 0 ? Empty : EncodeProperties(properties, context);
            if (properties != null) throw new ArgumentException("Properties require MQTT 5.");
            return Empty;
        }
        private void ValidateAuthentication(MqttProperty[] properties)
        {
            var method = Find(properties, MqttPropertyId.AuthenticationMethod);
            if (method != null && (authenticationMethod == null || method.Text != authenticationMethod) ||
                Find(properties, MqttPropertyId.AuthenticationData) != null && authenticationMethod == null)
                throw new IOException("MQTT authentication method mismatch.");
        }
        private void HandleAuthentication(Packet packet, long deadline, bool established)
        {
            if (protocolLevel != 5) throw new IOException("AUTH requires MQTT 5.");
            byte reason = packet.Bytes.Length == 0 ? (byte)0 : packet.Bytes[0];
            int position = 1;
            var properties = packet.Bytes.Length > 1 ? ParseProperties(packet.Bytes, ref position, MqttPropertyContext.Auth) : NoProperties;
            if (packet.Bytes.Length > 1 && position != packet.Bytes.Length ||
                reason != 0 && reason != 24 || !established && reason != 24)
                throw new IOException("MQTT unexpected AUTH reason/length.");
            ValidateAuthentication(properties);
            if (reason == 0) { LastReasonCode = 0; AcknowledgmentProperties = properties; return; }
            if (authenticationMethod == null || authenticate == null) throw new IOException("MQTT AUTH handler/method required.");
            var response = authenticate(reason, properties);
            if (response == null) throw new InvalidOperationException("MQTT AUTH callback returned no response.");
            ValidateAuthentication(response);
            byte[] encoded = EncodeProperties(response, MqttPropertyContext.Auth);
            outgoing.Reset(); outgoing.Byte(24); WriteProperties(encoded);
            Send(0xf0, outgoing.Bytes, outgoing.Count, deadline);
        }
        /// <summary>CONNECT에서 협상한 인증 방식으로 MQTT 5 재인증(AUTH 사유 25)을 시작하고 성공 응답까지 기다립니다.</summary>
        /// <remarks>한 작업 제한 시간 안에 브로커 챌린지를 최대 16회 <see cref="MqttConnectionOptions.Authenticate"/>로 처리하며,
        /// 그 사이 도착한 PUBLISH도 처리합니다. 실패하면 연결은 Faulted가 됩니다.</remarks>
        /// <param name="properties">AUTH 속성입니다. Authentication Method를 넣는다면 CONNECT와 같아야 합니다.</param>
        /// <exception cref="InvalidOperationException">MQTT 5가 아니거나 CONNECT에 인증 방식이 없었던 경우, 또는 연결되지 않았거나 재진입한 경우.</exception>
        /// <exception cref="IOException">인증 방식 불일치, 잘못된 AUTH, 챌린지 횟수 초과, 전송 오류.</exception>
        /// <exception cref="TimeoutException">제한 시간 안에 인증이 끝나지 않은 경우.</exception>
        public void Reauthenticate(MqttProperty[] properties)
        {
            if (protocolLevel != 5 || authenticationMethod == null) throw new InvalidOperationException("No MQTT 5 authentication method.");
            ValidateAuthentication(properties);
            byte[] encoded = EncodeProperties(properties, MqttPropertyContext.Auth);
            Enter(); bool success = false;
            try
            {
                long deadline = channel.Milliseconds + operationTimeout;
                outgoing.Reset(); outgoing.Byte(25); WriteProperties(encoded);
                Send(0xf0, outgoing.Bytes, outgoing.Count, deadline);
                for (int i = 0; i < 17; i++)
                {
                    Packet packet = Receive(deadline, false);
                    if (packet.Header != 0xf0) { Process(packet, deadline); continue; }
                    HandleAuthentication(packet, deadline, true);
                    if (packet.Bytes.Length == 0 || packet.Bytes[0] == 0) { success = true; return; }
                }
                throw new IOException("MQTT authentication challenge bound.");
            }
            finally { active = false; if (!success) Fault(); }
        }
        private bool ValidReason(MqttPacketType type, byte reason)
        {
            if (protocolLevel != 5) return type == MqttPacketType.SubAck ? reason <= 2 || reason == 128 : reason == 0;
            switch (type)
            {
                case MqttPacketType.ConnAck:
                    return reason == 0 || reason == 128 || reason == 129 || reason == 130 || reason == 131 ||
                        reason == 132 || reason == 133 || reason == 134 || reason == 135 || reason == 136 ||
                        reason == 137 || reason == 138 || reason == 140 || reason == 144 || reason == 149 ||
                        reason == 151 || reason == 153 || reason == 154 || reason == 155 || reason == 156 ||
                        reason == 157 || reason == 159;
                case MqttPacketType.PubAck: case MqttPacketType.PubRec:
                    return reason == 0 || reason == 16 || reason == 128 || reason == 131 || reason == 135 ||
                        reason == 144 || reason == 145 || reason == 151 || reason == 153;
                case MqttPacketType.PubRel: case MqttPacketType.PubComp: return reason == 0 || reason == 146;
                case MqttPacketType.SubAck:
                    return reason <= 2 || reason == 128 || reason == 131 || reason == 135 || reason == 143 ||
                        reason == 145 || reason == 151 || reason == 158 || reason == 161 || reason == 162;
                case MqttPacketType.UnsubAck:
                    return reason == 0 || reason == 17 || reason == 128 || reason == 131 || reason == 135 ||
                        reason == 143 || reason == 145;
                case MqttPacketType.Disconnect:
                    return reason == 0 || reason == 4 || reason == 128 || reason == 129 || reason == 130 ||
                        reason == 131 || reason == 135 || reason == 137 || reason == 139 || reason == 141 ||
                        reason == 142 || reason == 143 || reason == 144 || reason == 147 || reason == 148 ||
                        reason == 149 || reason == 150 || reason == 151 || reason == 152 || reason == 153 ||
                        reason == 154 || reason == 155 || reason == 156 || reason == 157 || reason == 158 ||
                        reason == 159 || reason == 160 || reason == 161 || reason == 162;
                default: return false;
            }
        }
        private int AckProperties(Packet packet)
        {
            if (protocolLevel != 5) return 2;
            int position = 2;
            AcknowledgmentProperties = ParseProperties(packet.Bytes, ref position, (MqttPropertyContext)(packet.Header >> 4));
            return position;
        }
        private byte ValidateAcknowledgment(Packet packet, bool throwOnRefusal)
        {
            if (packet.Bytes.Length < 2) throw new IOException("MQTT missing acknowledgment identifier.");
            byte reason = 0;
            MqttProperty[] properties = NoProperties;
            if (protocolLevel == 5 && packet.Bytes.Length > 2)
            {
                reason = packet.Bytes[2];
                int position = 3;
                if (position < packet.Bytes.Length) properties = ParseProperties(packet.Bytes, ref position, (MqttPropertyContext)(packet.Header >> 4));
                if (position != packet.Bytes.Length) throw new IOException("MQTT acknowledgment trailing bytes.");
            }
            else if (packet.Bytes.Length != 2) throw new IOException("MQTT acknowledgment length.");
            if (!ValidReason((MqttPacketType)(packet.Header >> 4), reason)) throw new IOException("MQTT acknowledgment reason.");
            AcknowledgmentProperties = properties; LastReasonCode = reason;
            if (throwOnRefusal && reason >= 128)
            {
                session.ClearOutgoing();
                throw new MqttReasonException((MqttPacketType)(packet.Header >> 4), reason, properties);
            }
            return reason;
        }
        private void ServerDisconnect(Packet packet)
        {
            byte reason = packet.Bytes.Length == 0 ? (byte)0 : packet.Bytes[0];
            int position = 1;
            var properties = packet.Bytes.Length > 1 ? ParseProperties(packet.Bytes, ref position, MqttPropertyContext.Disconnect) : NoProperties;
            if (packet.Bytes.Length > 1 && position != packet.Bytes.Length || !ValidReason(MqttPacketType.Disconnect, reason) || reason == 4)
                throw new IOException("MQTT malformed server DISCONNECT.");
            throw new MqttReasonException(MqttPacketType.Disconnect, reason, properties);
        }
    }
}
