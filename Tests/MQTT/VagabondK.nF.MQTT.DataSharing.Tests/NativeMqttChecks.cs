using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using VagabondK.nF.MQTT;

/// <summary>브로커 없이 가짜 채널로 MQTT 3.1.1 클라이언트의 패킷 형식, 조각 수신, 타임아웃, 장애 처리와 처리 성능을 검사합니다.</summary>
/// <remarks><c>Channel</c>은 1바이트씩 돌려주고 가상 시계(<c>Milliseconds</c>)를 대기 시간만큼 진행시켜 실제 대기 없이 타임아웃을 재현합니다.</remarks>
internal static class NativeMqttChecks
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private class Channel : IMqttChannel
    {
        internal readonly Queue<byte> Incoming = new Queue<byte>();
        internal readonly List<byte[]> Sent = new List<byte[]>();
        internal Action<byte[]> Written;
        internal bool Eof;
        internal int Delay;
        internal int ClockStep;
        private long milliseconds;
        public long Milliseconds
        {
            get { long value = milliseconds; milliseconds += ClockStep; return value; }
            set { milliseconds = value; }
        }
        internal void Feed(params byte[] bytes) { foreach (byte value in bytes) Incoming.Enqueue(value); }
        public bool TryRead(byte[] bytes, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            transferred = 0;
            if (Delay > timeoutMilliseconds) { Milliseconds += timeoutMilliseconds; return false; }
            if (Incoming.Count == 0)
            {
                if (Eof) return true;
                Milliseconds += timeoutMilliseconds; return false;
            }
            Milliseconds += Delay;
            bytes[offset] = Incoming.Dequeue(); transferred = 1; return true;
        }
        public void Write(byte[] bytes, int count, int timeoutMilliseconds)
        { byte[] copy = bytes.Take(count).ToArray(); Sent.Add(copy); if (Written != null) Written(copy); }
    }
    private static MqttConnection Connected(Channel channel, ushort keepAlive = 20)
    {
        channel.Feed(0x20, 2, 0, 0);
        var client = new MqttConnection(channel);
        client.Connect("sample", "test/death", new byte[] { 0, 0xff, 0x80, 0, 0xfe }, keepAlive);
        Check(client.Connected && !client.Faulted, "CONNACK after one-byte fragments.");
        return client;
    }
    internal static void Run()
    {
        var channel = new Channel();
        var client = Connected(channel);
        Check((byte)MqttQos.AtMostOnce == 0 && (byte)MqttQos.AtLeastOnce == 1 &&
            (byte)MqttQos.ExactlyOnce == 2, "Public QoS enums preserve wire values.");
        int sentBeforeInvalidQos = channel.Sent.Count;
        Throws<ArgumentOutOfRangeException>(() => client.Subscribe("invalid/qos", (MqttQos)3));
        Throws<ArgumentOutOfRangeException>(() => client.Publish("invalid/qos", new byte[0], (MqttQos)255));
        Check(channel.Sent.Count == sentBeforeInvalidQos && client.Connected, "Undefined QoS enums rejected without sending or faulting.");
        Check(typeof(MqttConnection).Assembly != typeof(NativeMqttChecks).Assembly &&
            typeof(MqttConnection).Assembly.GetName().Name == "VagabondK.nF.MQTT", "Transport tests consume the independent library DLL.");
        byte[] connect = channel.Sent[0];
        Check(connect[0] == 0x10 && connect[9] == 14 && connect.Skip(connect.Length - 5).SequenceEqual(new byte[] { 0, 255, 128, 0, 254 }),
            "Binary Will is byte-exact, Clean Session, QoS1, not retained.");
        channel.Feed(0x90, 3, 0, 1, 1);
        Check(client.Subscribe("spBv1.0/+/NCMD/#", MqttQos.AtLeastOnce), "SUBACK grants requested QoS1.");
        channel.Feed(0x40, 2, 0, 2);
        client.Publish("test/data", new byte[] { 255, 0, 128 }, MqttQos.AtLeastOnce);
        Check(channel.Sent.Last()[0] == 0x32, "QoS1 PUBLISH.");
        client.Publish("test/data", new byte[512]);
        Check((channel.Sent.Last()[1] & 128) != 0, "Multi-byte remaining length.");
        int deliveries = 0;
        client.Received = (topic, bytes, retained, duplicate) => {
            Check(topic == "a" && bytes.SequenceEqual(new byte[] { 255, 0 }) && retained && duplicate == (deliveries == 1), "Inbound flags and bytes.");
            deliveries++;
        };
        channel.Feed(0x33, 7, 0, 1, (byte)'a', 0, 7, 255, 0);
        client.Poll();
        channel.Feed(0x3b, 7, 0, 1, (byte)'a', 0, 7, 255, 0);
        client.Poll();
        Check(deliveries == 2 && channel.Sent.TakeLast(2).All(p => p.SequenceEqual(new byte[] { 0x40, 2, 0, 7 })),
            "QoS1 at least once; duplicate delivery acknowledged, app owns deduplication.");
        channel.Feed(0x32, 5, 0, 1, 97, 0, 8);
        channel.Eof = true;
        client.Disconnect();
        Check(!client.Connected && !client.Faulted && channel.Sent.Last().SequenceEqual(new byte[] { 0xe0, 0 }) &&
            deliveries == 2 && channel.Incoming.Count == 0, "DISCONNECT drains pending data until broker EOF, without callbacks or forbidden ACKs.");
        Throws<InvalidOperationException>(() => client.Poll());
        Throws<InvalidOperationException>(() => client.Connect("sample", "d", new byte[0]));
        foreach (byte[] response in new[] {
            new byte[] { 0x20, 2, 1, 0 }, new byte[] { 0x20, 2, 0, 5 }, new byte[] { 0x21, 2, 0, 0 },
            new byte[] { 0x20, 0 }, new byte[] { 0x20, 128, 0 },
            new byte[] { 0x20, 128, 128, 128, 128, 0 }, new byte[] { 0x20, 255, 127 }
        })
        {
            channel = new Channel(); channel.Feed(response); client = new MqttConnection(channel);
            Throws<IOException>(() => client.Connect("sample", "d", new byte[0]));
            Check(client.Faulted && !client.Connected, "Malformed CONNACK faults.");
        }
        foreach (byte[] response in new[] {
            new byte[] { 0x90, 3, 0, 2, 1 }, new byte[] { 0x90, 3, 0, 1, 2 }, new byte[] { 0x90, 2, 0, 1 }
        })
        {
            channel = new Channel(); client = Connected(channel); channel.Feed(response);
            Throws<IOException>(() => client.Subscribe("a", MqttQos.AtLeastOnce));
            Check(client.Faulted, "Bad SUBACK faults.");
        }
        channel = new Channel(); client = Connected(channel); channel.Feed(0x90, 3, 0, 1, 128);
        Check(!client.Subscribe("a", MqttQos.AtLeastOnce) && client.Connected, "Subscription rejection explicit without corrupting session.");
        foreach (byte[] response in new[] {
            new byte[] { 0x40, 2, 0, 2 }, new byte[] { 0x40, 3, 0, 1, 0 }, new byte[] { 0x40, 0 }
        })
        {
            channel = new Channel(); client = Connected(channel); channel.Feed(response);
            Throws<IOException>(() => client.Publish("a", new byte[0], MqttQos.AtLeastOnce));
            Check(client.Faulted, "Bad PUBACK faults.");
        }
        channel = new Channel(); client = Connected(channel);
        Throws<TimeoutException>(() => client.Publish("a", new byte[] { 1 }, MqttQos.AtLeastOnce));
        Check(client.Faulted && channel.Sent.Count == 2 && channel.Milliseconds == 3000, "Uncertain QoS1 send is not automatically resent.");
        Throws<InvalidOperationException>(() => client.Publish("a", new byte[0], MqttQos.AtLeastOnce));
        foreach (byte[] response in new[] {
            new byte[] { 0x34, 3, 0, 1, 97 }, new byte[] { 0x38, 3, 0, 1, 97 }, new byte[] { 0x32, 5, 0, 1, 97, 0, 0 },
            new byte[] { 0x30, 2, 0, 0 }, new byte[] { 0x30, 3, 0, 1, 0xff }, new byte[] { 0xd0, 0 },
            new byte[] { 0x30, 3, 0, 1, (byte)'+' }
        })
        {
            channel = new Channel(); client = Connected(channel); channel.Feed(response);
            Throws<IOException>(() => client.Poll());
            Check(client.Faulted, "Invalid inbound packet faults.");
        }
        channel = new Channel(); client = Connected(channel); channel.Feed(0x30, 4, 0, 1, 97);
        Throws<TimeoutException>(() => client.Poll());
        Check(client.Faulted && channel.Milliseconds == 3000, "Incomplete body uses bounded packet deadline.");
        channel = new Channel(); client = Connected(channel); channel.Eof = true;
        Throws<IOException>(() => client.Poll()); Check(client.Faulted, "EOF faults.");
        channel = new Channel(); client = Connected(channel); channel.Delay = 1100; channel.Feed(0x40, 2, 0, 1);
        Throws<TimeoutException>(() => client.Publish("a", new byte[0], MqttQos.AtLeastOnce));
        Check(client.Faulted && channel.Milliseconds == 3000, "Trickled ACK shares whole operation deadline.");
        channel = new Channel(); client = Connected(channel);
        client.Received = (topic, bytes, retained, duplicate) => { throw new InvalidOperationException("App failure."); };
        channel.Feed(0x32, 5, 0, 1, 97, 0, 1);
        Throws<InvalidOperationException>(() => client.Poll());
        Check(client.Faulted && channel.Sent.Count == 1, "Application failure is not acknowledged or swallowed.");
        channel = new Channel(); client = Connected(channel);
        MqttConnection reentrant = client;
        client.Received = (topic, bytes, retained, duplicate) => reentrant.Publish("a", new byte[0]);
        channel.Feed(0x32, 5, 0, 1, 97, 0, 1);
        Throws<InvalidOperationException>(() => client.Poll());
        Check(client.Faulted && channel.Sent.Count == 1, "Callback cannot reenter the client or accidentally ACK failed work.");
        channel = new Channel(); client = Connected(channel);
        int interleaved = 0;
        client.Received = (topic, bytes, retained, duplicate) => interleaved++;
        channel.Feed(0x32, 5, 0, 1, 97, 0, 42, 0x40, 2, 0, 1);
        client.Publish("a", new byte[0], MqttQos.AtLeastOnce);
        Check(interleaved == 1 && client.Connected && channel.Sent.Last().SequenceEqual(new byte[] { 0x40, 2, 0, 42 }),
            "Inbound QoS1 can be acknowledged while waiting for the outbound PUBACK.");
        channel = new Channel(); client = Connected(channel, 2);
        Channel pingChannel = channel;
        channel.Written = packet => { if (packet[0] == 0xc0) pingChannel.Feed(0xd0, 0); };
        client.Poll(1000); client.Poll(100);
        Check(client.Connected && channel.Sent.Count == 2 && channel.Sent[1].SequenceEqual(new byte[] { 0xc0, 0 }), "PINGREQ/PINGRESP.");
        channel = new Channel(); client = Connected(channel, 2);
        client.Poll(1000); client.Poll(1000); client.Poll(1000);
        Throws<TimeoutException>(() => client.Poll(1000));
        Check(client.Faulted, "Missing PINGRESP faults.");
        channel = new Channel(); client = Connected(channel);
        Throws<ArgumentException>(() => client.Publish("bad/#", new byte[0]));
        Throws<ArgumentException>(() => client.Subscribe("bad/a+", MqttQos.AtMostOnce));
        Throws<ArgumentOutOfRangeException>(() => client.Publish("a", new byte[513]));
        Check(client.Connected, "Invalid local arguments do not damage connected session.");
        channel = new Channel(); client = Connected(channel); channel.ClockStep = 20;
        Check(!client.Poll(10) && client.Connected, "Idle poll deadline expiring before its first read is ordinary inactivity, not a broken connection.");
        channel = new Channel(); client = Connected(channel);
        Throws<TimeoutException>(() => client.Disconnect());
        Check(client.Faulted && channel.Milliseconds == 3000 && channel.Sent.Last()[0] == 0xe0,
            "Broker close is bounded; missing close is reported, not a success or retransmission.");
        channel = new Channel(); channel.Feed(0x20, 2, 0, 0);
        client = new MqttConnection(channel); client.Connect("without-will");
        Check(channel.Sent[0][9] == 2, "Reusable MQTT client does not require a Last Will.");
        client.Publish("retained", new byte[] { 1 }, MqttQos.AtMostOnce, true);
        Check(channel.Sent.Last()[0] == 0x31, "Retained publication is available independently of Sparkplug profile.");
        Advanced();
        Performance();
        Console.WriteLine("NATIVE MQTT CONTRACT PASS checks=" + checks);
    }
    private sealed class SegmentChannel : Channel, IMqttSegmentChannel
    {
        internal int OffsetWrites;
        public void Write(byte[] bytes, int offset, int count, int timeoutMilliseconds)
        {
            if (offset > 0) OffsetWrites++;
            base.Write(bytes.Skip(offset).Take(count).ToArray(), count, timeoutMilliseconds);
        }
    }
    private static MqttConnection Open(Channel channel, MqttSessionState state = null,
        bool clean = true, byte protocol = 4, int retries = 0, bool present = false, ushort keepAlive = 0)
    {
        channel.Feed(0x20, 2, present ? (byte)1 : (byte)0, 0);
        var client = new MqttConnection(channel, state ?? new MqttSessionState());
        client.Connect(new MqttConnectionOptions { ClientId = "advanced", CleanSession = clean,
            ProtocolLevel = protocol, AcknowledgmentRetries = retries, KeepAliveSeconds = keepAlive });
        return client;
    }
    private static void Advanced()
    {
        foreach (byte protocol in new byte[] { 3, 4 })
        foreach (byte qos in new byte[] { 0, 1, 2 })
        {
            var wire = new Channel(); wire.Feed(0x20, 2, 0, 0);
            var mqtt = new MqttConnection(wire);
            mqtt.Connect(new MqttConnectionOptions { ClientId = "c", ProtocolLevel = protocol,
                UserName = "\uC0AC\uC6A9\uC790", Password = new byte[] { 0, 255, 128 },
                WillTopic = "\uC7A5\uCE58/death", WillPayload = new byte[] { 255, 0 }, WillQos = (MqttQos)(qos),
                WillRetain = true, KeepAliveSeconds = 0 });
            byte[] connect = wire.Sent[0];
            int flags = protocol == 4 ? 9 : 11;
            Check(connect[flags] == (0xc0 | 2 | 4 | qos << 3 | 32) &&
                connect[flags + 1] == 0 && connect[flags + 2] == 0 &&
                connect.TakeLast(5).SequenceEqual(new byte[] { 0, 3, 0, 255, 128 }),
                "CONNECT protocol, authentication, binary password and retained QoS Will.");
            mqtt.Abort();
        }
        var channel = new Channel(); var client = Open(channel);
        foreach (string invalid in new[] { "\0", "\ud800", "\udc00", "\ufdd0", "\ufffe", "\ud83f\udffe" })
            Throws<ArgumentException>(() => client.Publish(invalid, new byte[0]));
        Check(client.Connected, "Unicode validation is local and non-destructive.");
        string unicode = "\uC7A5\uCE58/\ud83d\ude80";
        client.Publish(unicode, new byte[] { 128 });
        Check(channel.Sent.Last().Skip(4).Take(System.Text.Encoding.UTF8.GetByteCount(unicode))
            .SequenceEqual(System.Text.Encoding.UTF8.GetBytes(unicode)), "Unicode topic encoded as exact UTF-8.");
        channel.Feed(0x90, 4, 0, 1, 0, 128);
        Check(client.Subscribe(new[] { "a/+", "b/#" }, new byte[] { 2, 1 })
            .SequenceEqual(new byte[] { 0, 128 }), "Batch subscriptions permit lower grants and explicit refusals.");
        channel.Feed(0xb0, 2, 0, 2);
        client.Unsubscribe("a/+", "b/#");
        Check(channel.Sent.Last()[0] == 0xa2 && client.Connected, "Batch unsubscribe waits for UNSUBACK.");
        foreach (byte[] malformed in new[] {
            new byte[] { 0xb0, 2, 0, 9 }, new byte[] { 0xb1, 2, 0, 1 }, new byte[] { 0xb0, 3, 0, 1, 0 } })
        {
            channel = new Channel(); client = Open(channel); channel.Feed(malformed);
            Throws<IOException>(() => client.Unsubscribe("a"));
        }
        channel = new Channel(); client = Open(channel);
        channel.Feed(0x50, 2, 0, 1, 0x50, 2, 0, 1, 0x70, 2, 0, 1);
        client.Publish("a", new byte[] { 255, 0 }, MqttQos.ExactlyOnce, true);
        Check(channel.Sent[1][0] == 0x35 && channel.Sent.Skip(2).All(p => p.SequenceEqual(new byte[] { 0x62, 2, 0, 1 })) &&
            channel.Sent.Count == 4, "QoS2 sends PUBREL and repeats it for duplicate PUBREC without re-publishing.");
        int delivered = 0;
        client.Received = (topic, body, retained, duplicate) => { delivered++; Check(topic == "a" && body[0] == 9, "QoS2 payload."); };
        channel.Feed(0x34, 6, 0, 1, 97, 0, 7, 9);
        client.Poll();
        channel.Feed(0x3c, 6, 0, 1, 97, 0, 7, 9);
        client.Poll();
        Check(delivered == 0 && channel.Sent.TakeLast(2).All(p => p[0] == 0x50), "QoS2 duplicates remain pending until PUBREL.");
        channel.Feed(0x62, 2, 0, 7, 0x62, 2, 0, 7);
        client.Poll(); client.Poll();
        Check(delivered == 1 && channel.Sent.TakeLast(2).All(p => p[0] == 0x70), "Duplicate PUBREL completes without duplicate delivery.");
        channel.Feed(0x50, 2, 0, 1, 0x70, 2, 0, 1);
        client.Poll(); client.Poll();
        Check(client.Connected && channel.Sent.Last()[0] == 0x62, "Late duplicate PUBREC/PUBCOMP after completion is harmless.");
        foreach (byte[] malformed in new[] {
            new byte[] { 0x50, 2, 0, 2 }, new byte[] { 0x51, 2, 0, 1 }, new byte[] { 0x70, 2, 0, 1 } })
        {
            channel = new Channel(); client = Open(channel); channel.Feed(malformed);
            Throws<IOException>(() => client.Publish("a", new byte[0], MqttQos.ExactlyOnce));
        }
        foreach (bool segmented in new[] { false, true })
        {
            channel = segmented ? new SegmentChannel() : new Channel(); client = Open(channel, retries: 1);
            var responseChannel = channel;
            channel.Written = packet => { if (packet[0] == 0x3a) responseChannel.Feed(0x40, 2, 0, 1); };
            byte[] payload = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
            client.Publish("retry/topic", payload, MqttQos.AtLeastOnce);
            Check(channel.Sent.Count == 3 && channel.Sent[1][0] == 0x32 && channel.Sent[2][0] == 0x3a &&
                channel.Sent[1].Skip(1).SequenceEqual(channel.Sent[2].Skip(1)) &&
                channel.Sent[2].TakeLast(512).SequenceEqual(payload) && channel.Milliseconds == 3000,
                "Opt-in retry preserves packet ID and bytes for fallback and segment channels.");
            if (segmented) Check(((SegmentChannel)channel).OffsetWrites > 0, "Segment channel receives offset body without packet copy.");
            channel.Feed(0x40, 2, 0, 1, 0x40, 2, 0, 2);
            client.Publish("a", new byte[0], MqttQos.AtLeastOnce);
            channel.Feed(0x40, 2, 0, 2); client.Poll();
            Check(client.Connected, "Delayed duplicate ACK from previous or completed retry does not fault the next operation.");
        }
        channel = new Channel(); client = Open(channel, retries: 2);
        Throws<TimeoutException>(() => client.Publish("a", new byte[0], MqttQos.AtLeastOnce));
        Check(channel.Sent.Count == 4 && channel.Milliseconds == 9000, "Retry attempts bounded with fresh full-operation deadlines.");
        channel = new Channel(); client = Open(channel, retries: 2);
        channel.Feed(0x40, 2, 0);
        Throws<TimeoutException>(() => client.Publish("a", new byte[0], MqttQos.AtLeastOnce));
        Check(channel.Sent.Count == 2, "Partial ACK never retries an ambiguously framed stream.");
        channel = new Channel(); client = Open(channel, retries: 1, keepAlive: 1);
        var pingChannel = channel;
        channel.Written = packet => {
            if (packet[0] == 0xc0) pingChannel.Feed(0xd0, 0);
            if (packet[0] == 0x3a) pingChannel.Feed(0x40, 2, 0, 1);
        };
        client.Publish("a", new byte[0], MqttQos.AtLeastOnce);
        Check(client.Connected && channel.Sent.Count(p => p[0] == 0xc0) >= 5, "Keepalive serviced during ACK waits and retries.");
        foreach (byte stage in new byte[] { 1, 2, 3 })
        foreach (byte protocol in new byte[] { 3, 4 })
        foreach (bool present in new[] { false, true })
        {
            if (protocol == 3 && present) continue;
            var state = new MqttSessionState();
            channel = new Channel(); client = Open(channel, state, clean: false, protocol: protocol);
            if (stage == 3) channel.Feed(0x50, 2, 0, 1);
            Throws<TimeoutException>(() => client.Publish("a", new byte[] { 128, 0, 255 }, (MqttQos)(stage == 1 ? (byte)1 : (byte)2)));
            Check(state.HasPendingPublication, "Unacknowledged publication survives persistent connection failure.");
            var reconnect = new Channel();
            reconnect.Written = p => {
                if (p[0] == 0x3a) reconnect.Feed(0x40, 2, 0, 1);
                if (p[0] == 0x3c) reconnect.Feed(0x50, 2, 0, 1);
                if (p[0] == 0x62) reconnect.Feed(0x70, 2, 0, 1);
            };
            client = Open(reconnect, state, clean: false, protocol: protocol, present: present);
            Check(!state.HasPendingPublication && client.SessionPresent == present && reconnect.Sent[1][0] ==
                (stage == 1 ? 0x3a : stage == 2 ? 0x3c : 0x62), "Persistent outgoing stages resume even if broker reports no session.");
            var concurrent = new Channel(); concurrent.Feed(0x20, 2, 0, 0);
            Throws<InvalidOperationException>(() => new MqttConnection(concurrent, state).Connect("advanced"));
            client.Abort();
            Check(client.Faulted && !client.Connected, "Abort releases exclusive session ownership.");
        }
        foreach (bool present in new[] { false, true })
        {
            var state = new MqttSessionState();
            channel = new Channel(); client = Open(channel, state, clean: false);
            channel.Feed(0x34, 6, 0, 1, 97, 0, 7, 9);
            client.Poll(); client.Abort();
            channel = new Channel(); client = Open(channel, state, clean: false, present: present);
            delivered = 0; client.Received = (t, p, r, d) => delivered++;
            channel.Feed(0x62, 2, 0, 7); client.Poll();
            Check(delivered == (present ? 1 : 0), "Incoming QoS2 retained only when MQTT 3.1.1 broker session exists.");
            client.Abort();
        }
        channel = new Channel(); channel.Feed(0x20, 2, 0, 0);
        client = new MqttConnection(channel, new MqttSessionState(), 4096, 3000); client.Connect("large");
        client.Publish("a", new byte[3000]);
        Check(channel.Sent.Last().Length > 3000, "Configured capacity exceeds original 512-byte payload bound.");
        Throws<ArgumentOutOfRangeException>(() => new MqttConnection(channel, new MqttSessionState(), 256));
        foreach (byte[] invalid in new[] {
            new byte[] { 0xc0, 0x80 }, new byte[] { 0xed, 0xa0, 0x80 },
            new byte[] { 0xf4, 0x90, 0x80, 0x80 }, new byte[] { 0xf0, 0x9f }, new byte[] { 0xef, 0xbf, 0xbe } })
        {
            channel = new Channel(); client = Open(channel);
            channel.Feed(new byte[] { 0x30, (byte)(2 + invalid.Length), 0, (byte)invalid.Length }.Concat(invalid).ToArray());
            Throws<IOException>(() => client.Poll());
        }
    }
    private class Sink : IMqttChannel
    {
        private int position;
        public long Milliseconds { get { return 0; } }
        public bool TryRead(byte[] bytes, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            byte[] connack = { 0x20, 2, 0, 0 };
            transferred = Math.Min(count, connack.Length - position);
            Array.Copy(connack, position, bytes, offset, transferred); position += transferred;
            return transferred != 0;
        }
        public void Write(byte[] bytes, int count, int timeoutMilliseconds) { }
    }
    private sealed class SegmentSink : Sink, IMqttSegmentChannel
    { public void Write(byte[] bytes, int offset, int count, int timeoutMilliseconds) { } }
    private static void Performance()
    {
        foreach (bool segmented in new[] { false, true })
        foreach (int size in new[] { 128, 512 })
            Performance(segmented, size);
    }
    private static void Performance(bool segmented, int size)
    {
        var client = new MqttConnection(segmented ? new SegmentSink() : new Sink());
        client.Connect("performance", "death", new byte[0]);
        byte[] payload = new byte[size];
        for (int i = 0; i < 1000; i++) client.Publish("performance/data", payload);
        var watch = new Stopwatch();
        long before = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        for (int i = 0; i < 10000; i++) client.Publish("performance/data", payload);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "Warm QoS0 transmission with caller-owned payload must allocate zero bytes.");
        Console.WriteLine("NATIVE MQTT PERF PC qos0 payload=" + size + " segment=" + segmented + " rounds=10000 bytes/publish=" + allocated / 10000.0 +
            " us/publish=" + watch.Elapsed.TotalMilliseconds * 1000 / 10000);
    }
}
