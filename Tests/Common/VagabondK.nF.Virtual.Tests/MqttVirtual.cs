using System;
using System.IO;
using System.Text;
using System.Threading;
using VagabondK.nF.MQTT;

namespace VagabondK.nF.Virtual.Tests
{
    /// <summary>외부 브로커 없이 응답 바이트를 주입해 MQTT 재전송·QoS2·세션 복구와 MQTT 5 협상을 검증합니다.</summary>
    internal static class MqttVirtual
    {
        private delegate void Written(byte[] bytes, int offset, int count);
        private sealed class Channel : IMqttSegmentChannel
        {
            private readonly byte[] input = new byte[4096];
            private int position, length;
            internal byte[] Last;
            internal Written Sent;
            internal int Publications, Duplicates;
            public long Milliseconds { get; private set; }
            internal void Feed(params byte[] bytes)
            {
                if (position == length) position = length = 0;
                Array.Copy(bytes, 0, input, length, bytes.Length); length += bytes.Length;
            }
            public bool TryRead(byte[] bytes, int offset, int count, int timeoutMilliseconds, out int transferred)
            {
                transferred = 0;
                if (position == length) { Milliseconds += timeoutMilliseconds; Thread.Sleep(1); return false; }
                bytes[offset] = input[position++]; transferred = 1; return true;
            }
            public void Write(byte[] bytes, int count, int timeoutMilliseconds)
            { Write(bytes, 0, count, timeoutMilliseconds); }
            public void Write(byte[] bytes, int offset, int count, int timeoutMilliseconds)
            {
                Last = new byte[count]; Array.Copy(bytes, offset, Last, 0, count);
                if ((Last[0] >> 4) == 3) { Publications++; if ((Last[0] & 8) != 0) Duplicates++; }
                if (Sent != null) Sent(bytes, offset, count);
            }
        }
        private static MqttConnection Connect(Channel wire, MqttSessionState state, bool present = false, int retries = 0)
        {
            wire.Feed(0x20, 2, present ? (byte)1 : (byte)0, 0);
            var client = new MqttConnection(wire, state);
            client.Connect(new MqttConnectionOptions { ClientId = "virtual", CleanSession = false,
                KeepAliveSeconds = 0, AcknowledgmentRetries = retries });
            return client;
        }
        internal static void Run()
        {
            // 첫 응답을 누락시켜 DUP 재전송을 유도하고, QoS2 수신은 PUBREL 이후 한 번만 전달되는지 확인합니다.
            var wire = new Channel(); var state = new MqttSessionState();
            var client = Connect(wire, state, retries: 1);
            wire.Sent = (bytes, offset, count) => { if (bytes[offset] == 0x3a) wire.Feed(0x40, 2, 0, 1); };
            client.Publish("a", new byte[] { 0, 255, 128 }, MqttQos.AtLeastOnce);
            Program.Check(wire.Publications == 2 && wire.Duplicates == 1 && !state.HasPendingPublication,
                "MQTT native retry preserves pending stage and DUP.");
            wire.Feed(0x50, 2, 0, 2, 0x70, 2, 0, 2);
            client.Publish("a", new byte[] { 255 }, MqttQos.ExactlyOnce);
            Program.Check(wire.Last[0] == 0x62, "MQTT native QoS2 sender.");
            int deliveries = 0;
            client.Received = (topic, body, retained, duplicate) => {
                Program.Check(topic == "a" && body.Length == 1 && body[0] == 255, "MQTT native QoS2 body.");
                deliveries++;
            };
            wire.Feed(0x34, 6, 0, 1, 97, 0, 7, 255, 0x3c, 6, 0, 1, 97, 0, 7, 255);
            client.Poll(); client.Poll();
            Program.Check(deliveries == 0 && wire.Last[0] == 0x50, "MQTT native QoS2 defers callback.");
            wire.Feed(0x62, 2, 0, 7, 0x62, 2, 0, 7); client.Poll(); client.Poll();
            Program.Check(deliveries == 1 && wire.Last[0] == 0x70, "MQTT native QoS2 deduplication.");
            string unicode = "\uC7A5\uCE58/\ud83d\ude80";
            client.Publish(unicode, new byte[0]);
            var utf8 = Encoding.UTF8.GetBytes(unicode);
            bool exact = wire.Last[3] == utf8.Length;
            for (int i = 0; i < utf8.Length; i++) exact &= wire.Last[i + 4] == utf8[i];
            Program.Check(exact && utf8.Length == 11, "MQTT native UTF-8 supplementary encoding.");
            wire.Feed(0x90, 4, 0, 3, 0, 2);
            var grants = client.Subscribe(new[] { "a/+", "b/#" }, new byte[] { 1, 2 });
            Program.Check(grants[0] == 0 && grants[1] == 2, "MQTT native lower/batch grants.");
            wire.Feed(0xb0, 2, 0, 4); client.Unsubscribe("a/+", "b/#");
            Program.Check(wire.Last[0] == 0xa2, "MQTT native unsubscribe.");
            client.Abort();
            wire = new Channel(); client = Connect(wire, state, true);
            Program.Check(client.SessionPresent, "MQTT native RAM session ownership reused.");
            client.Abort();

            // 연결은 교체하되 RAM 세션 상태는 유지하여 미완료 QoS2 송신이 재연결 후 재개되는지 검증합니다.
            wire = new Channel(); state = new MqttSessionState(); client = Connect(wire, state);
            Program.Throws(() => client.Publish("a", new byte[0], MqttQos.ExactlyOnce), "MQTT native timeout explicit.");
            Program.Check(client.Faulted && state.HasPendingPublication, "MQTT native persistent pending state.");
            wire = new Channel();
            wire.Sent = (bytes, offset, count) => {
                if (bytes[offset] == 0x3c) wire.Feed(0x50, 2, 0, 1);
                if (bytes[offset] == 0x62) wire.Feed(0x70, 2, 0, 1);
            };
            client = Connect(wire, state, true);
            Program.Check(!state.HasPendingPublication && client.Connected, "MQTT native QoS2 reconnect resume.");
            var done = new AutoResetEvent(false);
            using (var worker = new MqttWorker(client, 2))
            {
                Exception failure = null;
                worker.Completed += (id, error) => { failure = error; done.Set(); };
                worker.Publish("worker", new byte[512]);
                Program.Check(done.WaitOne(5000, false) && failure == null, "MQTT native worker completion.");
            }
            Program.Check(client.Connected, "MQTT worker leaves transport ownership with caller.");
            client.Abort();
            Protocol5();
        }
        private static void Protocol5()
        {
            var wire = new Channel();
            wire.Feed(0xf0, 7, 24, 5, 21, 0, 2, 110, 102, 0x20, 6, 0, 0, 3, 33, 0, 2);
            var client = new MqttConnection(wire);
            int challenges = 0;
            client.Connect(new MqttConnectionOptions { ClientId = "virtual5", ProtocolLevel = 5,
                KeepAliveSeconds = 0, Properties = new[] { new MqttProperty(MqttPropertyId.AuthenticationMethod, "nf"), new MqttProperty(MqttPropertyId.TopicAliasMaximum, 1u) },
                Authenticate = (reason, properties) => { challenges++; return new[] { new MqttProperty(MqttPropertyId.AuthenticationMethod, "nf") }; } });
            Program.Check(challenges == 1 && client.Connected && client.ConnackProperties[0].Number == 2,
                "MQTT 5 native enhanced authentication/properties.");
            wire.Feed(0x90, 4, 0, 1, 0, 2);
            var grants = client.Subscribe(new[] { "v5" }, new byte[] { 2 }, new[] { new MqttProperty(MqttPropertyId.SubscriptionIdentifier, 99u) });
            Program.Check(grants[0] == 2, "MQTT 5 native subscription identifier.");
            int delivered = 0;
            client.MessageReceived = message => {
                Program.Check(message.Topic == "v5" && message.Payload.Length == 1 && message.Payload[0] == 255 &&
                    message.Properties[0].Id == MqttPropertyId.TopicAlias, "MQTT 5 native alias metadata.");
                delivered++;
            };
            wire.Feed(0x30, 9, 0, 2, 118, 53, 3, 35, 0, 1, 255);
            wire.Feed(0x30, 7, 0, 0, 3, 35, 0, 1, 255);
            client.Poll(); client.Poll();
            Program.Check(delivered == 2, "MQTT 5 native alias initial/reuse delivery.");
            int sends = 0;
            wire.Sent = (bytes, offset, count) => {
                if (bytes[offset] == 0x32 || bytes[offset] == 0x34)
                {
                    if (++sends == 2) wire.Feed(0x50, 2, 0, 3, 0x40, 2, 0, 2);
                }
                else if (bytes[offset] == 0x62) wire.Feed(0x70, 2, 0, 3);
            };
            var reasons = client.PublishBatch(new[] {
                new MqttPublishRequest { Topic = "v5", Payload = new byte[] { 1 }, Qos = MqttQos.AtLeastOnce },
                new MqttPublishRequest { Topic = "v5", Payload = new byte[] { 2 }, Qos = MqttQos.ExactlyOnce,
                    Properties = new[] { new MqttProperty("k", "v") } } });
            Program.Check(reasons.Length == 2 && reasons[0] == 0 && reasons[1] == 0 && sends == 2,
                "MQTT 5 native pipelined out-of-order QoS1/2.");
            client.Abort();
        }
    }
}
