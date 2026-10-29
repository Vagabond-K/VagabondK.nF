using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VagabondK.nF.MQTT;

/// <summary>바이트 단위 가짜 채널로 MQTT 5 기능(속성, 사유 코드, 토픽 별칭, 경계값, 일괄 발행, 확장 인증)을 검사합니다.</summary>
/// <remarks>브로커 응답은 <c>Wire.Feed</c>로 미리 넣어 두고, 클라이언트가 보낸 패킷은 <c>Wire.Output</c>에서 바이트 단위로 확인합니다.</remarks>
internal static class Mqtt5Checks
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class Wire : IMqttSegmentChannel
    {
        internal readonly Queue<byte> Input = new Queue<byte>();
        internal readonly List<byte[]> Output = new List<byte[]>();
        internal Action<byte[]> Sent { get; set; }
        public long Milliseconds { get; private set; }
        internal void Feed(byte header, params byte[] body)
        {
            Input.Enqueue(header);
            int remaining = body.Length;
            do { byte digit = (byte)(remaining & 127); remaining >>= 7; Input.Enqueue((byte)(digit | (remaining != 0 ? 128 : 0))); } while (remaining != 0);
            foreach (byte value in body) Input.Enqueue(value);
        }
        public bool TryRead(byte[] bytes, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            transferred = 0;
            if (Input.Count == 0) { Milliseconds += timeoutMilliseconds; return false; }
            bytes[offset] = Input.Dequeue(); transferred = 1; return true;
        }
        public void Write(byte[] bytes, int count, int timeoutMilliseconds) { Write(bytes, 0, count, timeoutMilliseconds); }
        public void Write(byte[] bytes, int offset, int count, int timeoutMilliseconds)
        { var copy = bytes.Skip(offset).Take(count).ToArray(); Output.Add(copy); if (Sent != null) Sent(copy); }
    }
    private static MqttConnection Connect(Wire wire, byte[] properties = null, MqttSessionState state = null)
    {
        properties = properties ?? new byte[0];
        wire.Feed(0x20, new byte[] { 0, 0, (byte)properties.Length }.Concat(properties).ToArray());
        var client = new MqttConnection(wire, state ?? new MqttSessionState());
        client.Connect(new MqttConnectionOptions { ClientId = "v5", ProtocolLevel = 5, KeepAliveSeconds = 0, CleanSession = false,
            Properties = new[] { new MqttProperty(MqttPropertyId.TopicAliasMaximum, 2u), new MqttProperty(MqttPropertyId.SessionExpiryInterval, 30u) } });
        return client;
    }
    internal static void Run()
    {
        Check((byte)MqttPropertyId.SessionExpiryInterval == 17 && (byte)MqttPropertyId.UserProperty == 38 &&
            (byte)MqttPacketType.Disconnect == 14, "Public property/packet enums preserve wire identifiers.");
        Throws<ArgumentException>(() => new MqttProperty((MqttPropertyId)127, 0u));
        Throws<ArgumentException>(() => new MqttProperty((MqttPropertyId)0, "invalid"));
        Throws<ArgumentException>(() => new MqttProperty(MqttPropertyId.ContentType, 1u));
        var wire = new Wire(); var client = Connect(wire, new byte[] { 34, 0, 3, 33, 0, 1, 38, 0, 1, 107, 0, 1, 118 });
        Check(wire.Output[0][8] == 5 && client.ConnackProperties.Length == 3, "MQTT 5 CONNECT and CONNACK properties.");
        wire.Feed(0x90, 0, 1, 0, 2, 135);
        Check(client.Subscribe(new[] { "a", "b" }, new byte[] { 2, 1 }, new[] { new MqttProperty(MqttPropertyId.SubscriptionIdentifier, 321u) })
            .SequenceEqual(new byte[] { 2, 135 }), "MQTT 5 subscription identifier and per-filter refusal.");
        wire.Feed(0xb0, 0, 2, 0, 0, 17);
        Check(client.Unsubscribe(new[] { "a", "b" }, new[] { new MqttProperty("k", "v") }).SequenceEqual(new byte[] { 0, 17 }),
            "MQTT 5 unsubscribe properties/reasons.");
        wire.Feed(0x40, 0, 3, 16, 0);
        client.Publish("a", new byte[] { 0, 255 }, MqttQos.AtLeastOnce, false, new[] {
            new MqttProperty(MqttPropertyId.PayloadFormatIndicator, 0u), new MqttProperty(MqttPropertyId.MessageExpiryInterval, 30u), new MqttProperty(MqttPropertyId.ContentType, "application/octet-stream"),
            new MqttProperty(MqttPropertyId.ResponseTopic, "response"), new MqttProperty(MqttPropertyId.CorrelationData, new byte[] { 255, 0 }),
            new MqttProperty(MqttPropertyId.TopicAlias, 1u), new MqttProperty("k", "v"), new MqttProperty("k", "w") });
        Check(client.LastReasonCode == 16 && wire.Output.Last()[0] == 0x32, "MQTT 5 publish all metadata and success reason.");
        wire.Feed(0x50, 0, 4, 0);
        wire.Feed(0x70, 0, 4);
        client.Publish("a", new byte[0], MqttQos.ExactlyOnce);
        Check(wire.Output.Last().SequenceEqual(new byte[] { 0x62, 2, 0, 4 }), "MQTT 5 optional short QoS2 ACKs.");
        int deliveries = 0;
        client.MessageReceived = message => {
            Check(message.Topic == "a" && message.Payload.SequenceEqual(new byte[] { 255 }) &&
                message.Properties.Any(p => p.Id == MqttPropertyId.TopicAlias && p.Number == 1), "MQTT 5 alias resolved with metadata.");
            deliveries++;
        };
        wire.Feed(0x30, 0, 1, 97, 3, 35, 0, 1, 255); client.Poll();
        wire.Feed(0x30, 0, 0, 3, 35, 0, 1, 255); client.Poll();
        Check(deliveries == 2, "MQTT 5 empty topic uses registered alias.");
        wire.Feed(0x34, 0, 0, 0, 7, 3, 35, 0, 1, 255); client.Poll();
        wire.Feed(0x62, 0, 7, 0, 0); client.Poll();
        Check(deliveries == 3 && wire.Output.Last()[0] == 0x70, "MQTT 5 QoS2 stores resolved alias and payload offset.");
        client.Abort();
        foreach (byte[] malformed in new[] {
            new byte[] { 0 }, new byte[] { 0, 0 }, new byte[] { 0, 0, 1, 127 },
            new byte[] { 0, 0, 6, 33, 0, 1, 33, 0, 2 }, new byte[] { 0, 0, 3, 33, 0, 0 },
            new byte[] { 0, 0, 2, 36, 2 }, new byte[] { 0, 0, 2, 1, 0 },
            new byte[] { 0, 0, 128, 0 }, new byte[] { 0, 0, 1, 38 }, new byte[] { 0, 0, 0, 0 },
            new byte[] { 1, 135, 0 }, new byte[] { 0, 1, 0 }
        })
        {
            wire = new Wire(); wire.Feed(0x20, malformed); client = new MqttConnection(wire);
            Throws<IOException>(() => client.Connect(new MqttConnectionOptions { ClientId = "v5", ProtocolLevel = 5 }));
            Check(client.Faulted, "Malformed MQTT 5 CONNACK faults.");
        }
        foreach (byte[] malformed in new[] {
            new byte[] { 0, 0, 0 }, new byte[] { 0, 0, 3, 35, 0, 1 }, new byte[] { 0, 1, 97, 3, 35, 0, 3 },
            new byte[] { 0, 1, 97, 3, 35, 0, 0 }, new byte[] { 0, 1, 97, 2, 11, 0 },
            new byte[] { 0, 1, 97, 6, 35, 0, 1, 35, 0, 1 },
            new byte[] { 0, 1, 97, 3, 3, 0, 1 }, new byte[] { 0, 1, 97, 4, 3, 0, 1, 255 }
        })
        {
            wire = new Wire(); client = Connect(wire); wire.Feed(0x30, malformed);
            Throws<IOException>(() => client.Poll()); Check(client.Faulted, "Malformed MQTT 5 publication faults.");
        }
        wire = new Wire(); client = Connect(wire, new byte[] { 36, 0, 37, 0, 40, 0, 41, 0, 42, 0 });
        Throws<InvalidOperationException>(() => client.Publish("a", new byte[0], MqttQos.AtLeastOnce));
        Throws<InvalidOperationException>(() => client.Publish("a", new byte[0], MqttQos.AtMostOnce, true));
        Throws<InvalidOperationException>(() => client.Subscribe("a/#", MqttQos.AtMostOnce));
        Throws<InvalidOperationException>(() => client.Subscribe("$share/g/a", MqttQos.AtMostOnce));
        Throws<InvalidOperationException>(() => client.Subscribe(new[] { "a" }, new byte[] { 0 }, new[] { new MqttProperty(MqttPropertyId.SubscriptionIdentifier, 1u) }));
        Check(client.Connected, "Negotiated feature errors do not transmit or fault.");
        client.Abort();
        wire = new Wire(); client = Connect(wire, new byte[] { 39, 0, 0, 0, 8 });
        Throws<IOException>(() => client.Publish("a", new byte[10]));
        Check(client.Faulted, "Server maximum packet size enforced.");
        wire = new Wire(); client = Connect(wire);
        wire.Feed(0x40, 0, 1, 135, 0);
        try { client.Publish("a", new byte[0], MqttQos.AtLeastOnce); throw new InvalidOperationException("Refusal not surfaced."); }
        catch (MqttReasonException error) { Check(error.PacketType == MqttPacketType.PubAck && error.ReasonCode == 135, "PUBACK refusal diagnostic preserved."); }
        wire = new Wire(); client = Connect(wire); wire.Feed(0xe0, 139, 0);
        try { client.Poll(); throw new InvalidOperationException("DISCONNECT not surfaced."); }
        catch (MqttReasonException error) { Check(error.PacketType == MqttPacketType.Disconnect && error.ReasonCode == 139 && client.Faulted, "Server DISCONNECT diagnostic preserved."); }
        Authentication();
        Batch();
        Boundaries();
        Console.WriteLine("NATIVE MQTT CONTRACT PASS mqtt5Checks=" + checks);
    }
    private static void Boundaries()
    {
        var assignedWire = new Wire(); assignedWire.Feed(0x20, 0, 0, 5, 18, 0, 2, 110, 102);
        var assignedClient = new MqttConnection(assignedWire);
        assignedClient.Connect(new MqttConnectionOptions { ClientId = "", ProtocolLevel = 5 });
        Check(assignedClient.ClientId == "nf", "MQTT 5 server-assigned client ID exposed for reconnect.");
        assignedClient.Abort();
        assignedWire = new Wire(); assignedWire.Feed(0x20, 0, 0, 0);
        assignedClient = new MqttConnection(assignedWire);
        Throws<IOException>(() => assignedClient.Connect(new MqttConnectionOptions { ClientId = "", ProtocolLevel = 5 }));
        var wire = new Wire(); wire.Feed(0x20, 0, 0, 0);
        var client = new MqttConnection(wire, new MqttSessionState(), 2200000, 2100000);
        client.Connect(new MqttConnectionOptions { ClientId = "large-frame", ProtocolLevel = 5, OperationTimeoutMilliseconds = 60000 });
        client.Publish("a", new byte[2100000]);
        byte[] frame = wire.Output.Last();
        int remaining = 0, shift = 0, position = 1;
        do { remaining |= (frame[position] & 127) << shift; shift += 7; } while ((frame[position++] & 128) != 0);
        Check(position == 5 && remaining == frame.Length - position && remaining == 2100004,
            "Four-digit remaining length is byte-exact above 2 MiB.");
        client.Abort();
        wire = new Wire(); wire.Feed(0x20, 0, 0, 0);
        client = new MqttConnection(wire);
        client.Connect(new MqttConnectionOptions { ClientId = "receive-window", ProtocolLevel = 5, KeepAliveSeconds = 0,
            Properties = new[] { new MqttProperty(MqttPropertyId.ReceiveMaximum, 1u) } });
        wire.Feed(0x34, 0, 1, 97, 0, 1, 0); client.Poll();
        wire.Feed(0x34, 0, 1, 97, 0, 2, 0);
        Throws<IOException>(() => client.Poll()); Check(client.Faulted, "Advertised Receive Maximum enforced before second PUBREC.");
        wire = new Wire(); client = Connect(wire);
        wire.Feed(0x62, 0, 77); client.Poll();
        Check(wire.Output.Last().SequenceEqual(new byte[] { 0x70, 3, 0, 77, 146 }), "Unknown MQTT 5 PUBREL reports Packet Identifier Not Found.");
        wire.Feed(0x90, 0, 1, 0, 1); client.Subscribe("a", MqttQos.AtLeastOnce);
        wire.Feed(0x90, 0, 1, 0, 1, 0);
        Throws<IOException>(() => client.Poll()); Check(client.Faulted, "Duplicate SUBACK cannot change original grant count.");
        wire = new Wire(); client = Connect(wire);
        wire.Feed(0x40, 0, 1, 16, 1, 127);
        Throws<IOException>(() => client.Publish("a", new byte[0], MqttQos.AtLeastOnce));
        Check(client.Faulted, "Malformed PUBACK properties are not accepted as a valid success reason.");
        wire = new Wire(); client = Connect(wire);
        Throws<ArgumentException>(() => client.Disconnect(0, new[] { new MqttProperty(MqttPropertyId.ResponseTopic, "wrong-context") }));
        Check(client.Connected, "Invalid property context rejected before sending.");
        client.Abort();
    }
    private static void Batch()
    {
        var wire = new Wire(); var client = Connect(wire, new byte[] { 33, 0, 2 });
        int sent = 0;
        wire.Sent = packet => {
            if (packet[0] == 0x32 || packet[0] == 0x34)
            {
                sent++;
                if (sent == 2) { wire.Feed(0x50, 0, 2); wire.Feed(0x40, 0, 1, 16); }
                if (sent == 3) wire.Feed(0x40, 0, 3, 135);
            }
            else if (packet[0] == 0x62) wire.Feed(0x70, 0, 2);
        };
        var reasons = client.PublishBatch(new[] {
            new MqttPublishRequest { Topic = "a", Payload = new byte[] { 1 }, Qos = MqttQos.AtLeastOnce },
            new MqttPublishRequest { Topic = "a", Payload = new byte[] { 2 }, Qos = MqttQos.ExactlyOnce },
            new MqttPublishRequest { Topic = "a", Payload = new byte[] { 3 }, Qos = MqttQos.AtLeastOnce } });
        Check(reasons.SequenceEqual(new byte[] { 16, 0, 135 }) && client.Connected && sent == 3,
            "Pipelined QoS1/2 handles out-of-order ACKs and reports each refusal.");
        client.Abort();
        wire = new Wire(); var state = new MqttSessionState();
        client = Connect(wire, new byte[] { 33, 0, 1 }, state);
        wire.Sent = packet => {
            if (packet[0] == 0x32) Check(wire.Output.Count(p => p[0] == 0x32) == 1, "Receive Maximum one prevents second send before ACK.");
        };
        Throws<TimeoutException>(() => client.PublishBatch(new[] {
            new MqttPublishRequest { Topic = "a", Payload = new byte[] { 1 }, Qos = MqttQos.AtLeastOnce },
            new MqttPublishRequest { Topic = "b", Payload = new byte[] { 2 }, Qos = MqttQos.ExactlyOnce } }));
        Check(state.HasPendingPublication && client.Faulted, "Batch failure retains both sent and not-yet-sent pending requests.");
        wire = new Wire();
        wire.Sent = packet => {
            if (packet[0] == 0x3a) wire.Feed(0x40, 0, 1);
            if (packet[0] == 0x3c) wire.Feed(0x50, 0, 2);
            if (packet[0] == 0x62) wire.Feed(0x70, 0, 2);
        };
        client = Connect(wire, new byte[] { 33, 0, 1 }, state);
        Check(client.Connected && !state.HasPendingPublication && wire.Output.Count(p => (p[0] >> 4) == 3) == 2,
            "Batch RAM-session replay obeys new server window and completes QoS1/2.");
        wire.Feed(0xd0); client.Ping(); Check(wire.Output.Last()[0] == 0xc0, "Manual ping response.");
        client.Abort();
        wire = new Wire(); wire.Feed(0x20, 0, 0, 0);
        client = new MqttConnection(wire, new MqttSessionState(32, 32), 4096, 3000, 1024, 32);
        client.Connect(new MqttConnectionOptions { ClientId = "large", ProtocolLevel = 5 });
        client.Publish(new string('t', 512), new byte[2000], MqttQos.AtMostOnce, false, new[] { new MqttProperty(MqttPropertyId.ContentType, new string('c', 256)) });
        Check(wire.Output.Last().Length > 2500, "Expanded names, properties, payload and session capacities.");
        client.Abort();
    }
    private static void Authentication()
    {
        var wire = new Wire();
        byte[] method = { 21, 0, 1, 109 };
        wire.Feed(0xf0, new byte[] { 24, 8 }.Concat(method).Concat(new byte[] { 22, 0, 1, 99 }).ToArray());
        wire.Feed(0x20, new byte[] { 0, 0, 4 }.Concat(method).ToArray());
        var client = new MqttConnection(wire);
        int calls = 0;
        client.Connect(new MqttConnectionOptions { ClientId = "v5", ProtocolLevel = 5, KeepAliveSeconds = 0,
            Properties = new[] { new MqttProperty(MqttPropertyId.AuthenticationMethod, "m"), new MqttProperty(MqttPropertyId.AuthenticationData, new byte[] { 1 }) },
            Authenticate = (reason, properties) => {
                Check(reason == 24 && properties.Last().Bytes[0] == 99, "AUTH challenge metadata.");
                calls++; return new[] { new MqttProperty(MqttPropertyId.AuthenticationMethod, "m"), new MqttProperty(MqttPropertyId.AuthenticationData, new byte[] { 2 }) };
            } });
        Check(calls == 1 && wire.Output.Last()[0] == 0xf0 && client.Connected, "Enhanced CONNECT authentication.");
        wire.Feed(0xf0, 0); client.Reauthenticate(new[] { new MqttProperty(MqttPropertyId.AuthenticationMethod, "m") });
        Check(wire.Output.Last()[2] == 25 && client.Connected, "AUTH reauthentication success.");
        wire.Feed(0xf0, 24, 4, 21, 0, 1, 110);
        Throws<IOException>(() => client.Poll()); Check(client.Faulted, "AUTH method changes rejected.");
        Throws<ArgumentException>(() => new MqttProperty(MqttPropertyId.ReceiveMaximum, 0u));
        Throws<ArgumentException>(() => new MqttProperty(MqttPropertyId.PayloadFormatIndicator, 2u));
        Throws<ArgumentException>(() => new MqttProperty(MqttPropertyId.ContentType, 1u));
        Throws<ArgumentException>(() => new MqttProperty(MqttPropertyId.SubscriptionIdentifier, 0u));
    }
}
