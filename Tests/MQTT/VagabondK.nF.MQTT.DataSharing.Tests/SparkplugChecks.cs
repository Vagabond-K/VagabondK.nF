using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Sparkplug.Reference;
using VagabondK.nF.MQTT.Sparkplug.Mapping;
using VagabondK.nF.MQTT.Sparkplug;
using Metric = Sparkplug.Reference.Payload.Types.Metric;

/// <summary>Sparkplug 샘플 매핑/인코더를 참조 protobuf 구현(Sparkplug.Reference)으로 만든 페이로드와 교차 검증합니다.</summary>
/// <remarks>
/// <see cref="Run"/>은 오프라인 검사(BIRTH/DATA/DEATH, 별칭, seq 누락, 잘린 페이로드 거부)이고,
/// <see cref="Peer"/>는 PC 브로커를 띄워 보드의 Sparkplug 샘플과 실제로 주고받습니다(<c>board</c>가 있으면 UDP 20005 진단으로 자동 검증).
/// </remarks>
internal static class SparkplugChecks
{
    private static int checks;
    private static void Check(bool value, string message)
    { Interlocked.Increment(ref checks); if (!value) throw new InvalidOperationException(message); }
    private static ulong Now { get { return (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }
    private static Payload Message(int sequence, params Metric[] metrics)
    {
        var result = new Payload { Timestamp = Now };
        if (sequence >= 0) result.Seq = (ulong)sequence;
        result.Metrics.Add(metrics);
        return result;
    }
    private static Metric Bd(int value) { return new Metric { Name = "bdSeq", Datatype = 4, LongValue = (ulong)value }; }
    private static Payload NodeBirth(int bd) { return Message(0, Bd(bd), new Metric { Name = "Node Control/Rebirth", Datatype = 11, BooleanValue = false }); }
    private static Payload DeviceBirth(int sequence, int aliasBase = 10)
    {
        return Message(sequence,
            new Metric { Name = "Sensor/Energy", Alias = (ulong)(aliasBase + 3), Datatype = 10, DoubleValue = 123456789.125 },
            new Metric { Name = "Sensor/Temperature", Alias = (ulong)(aliasBase + 2), Datatype = 9, FloatValue = 23.5f },
            new Metric { Name = "Sensor/Count", Alias = (ulong)(aliasBase + 1), Datatype = 3, IntValue = unchecked((uint)-123) },
            new Metric { Name = "Sensor/Running", Alias = (ulong)aliasBase, Datatype = 11, BooleanValue = true });
    }
    private static SparkplugResult Apply(SparkplugMapping mapping, string verb, Payload payload, long now = 0, bool retain = false)
    { return mapping.Apply(SparkplugMapping.Topic(verb), payload.ToByteArray(), retain, now); }
    private static SparkplugMapping Born(int bd = 0)
    {
        var mapping = new SparkplugMapping(); mapping.SetConnected(true);
        Check(Apply(mapping, "NBIRTH", NodeBirth(bd)) == SparkplugResult.NodeBirth, "NBIRTH.");
        Check(!mapping.IsAvailable(0), "NBIRTH alone is not device birth.");
        Check(Apply(mapping, "DBIRTH", DeviceBirth(1)) == SparkplugResult.Applied, "DBIRTH.");
        return mapping;
    }
    internal static void Run()
    {
        TahuBirthDeath();
        var mapping = Born();
        Check(mapping.Read().SourceCount == -123 && mapping.Read().SourceRunning &&
            mapping.Read().ExternalTemperature == 23.5f && mapping.Read().ImportedEnergy == 123456789.125, "Four named metric mappings.");
        Check(mapping.IsAvailable(2999) && !mapping.IsAvailable(3000), "Sample local age boundary.");
        Check(Apply(mapping, "DDATA", Message(2, new Metric { Alias = 11, IntValue = uint.MaxValue }), 4000) == SparkplugResult.Applied &&
            mapping.Read().SourceCount == -1 && mapping.Read().ExternalTemperature == 23.5f, "Alias-only partial update.");
        Check(Apply(mapping, "NDATA", Message(3), 6000) == SparkplugResult.Applied && !mapping.IsAvailable(7000), "Node heartbeat does not refresh device.");
        Check(Apply(mapping, "NDEATH", Message(-1, Bd(255))) == SparkplugResult.StaleDeath && mapping.Read().SourceCount == -1, "Old death session ignored.");
        Check(Apply(mapping, "NDEATH", Message(-1, Bd(0))) == SparkplugResult.NodeDeath && !mapping.IsAvailable(4000), "Matching death invalidates.");
        Check(Apply(mapping, "DDATA", Message(4, new Metric { Alias = 11, IntValue = 1 })) == SparkplugResult.NeedBirth, "Data cannot resurrect.");
        mapping = Born(255);
        Check(Apply(mapping, "DDEATH", Message(2)) == SparkplugResult.DeviceDeath && !mapping.IsAvailable(0), "DDEATH.");
        Check(Apply(mapping, "DDATA", Message(3, new Metric { Alias = 10, BooleanValue = false })) == SparkplugResult.NeedBirth, "DBIRTH required.");
        Check(Apply(mapping, "DBIRTH", DeviceBirth(3, 100)) == SparkplugResult.Applied, "Device return with replacement aliases.");
        Check(Apply(mapping, "DDATA", Message(4, new Metric { Alias = 10, BooleanValue = false })) == SparkplugResult.Malformed &&
            mapping.NeedsRebirth && mapping.Read().SourceRunning, "Old alias never reused.");
        mapping = Born();
        for (int seq = 2; seq <= 257; seq++)
            Check(Apply(mapping, "DDATA", Message(seq & 255, new Metric { Alias = 11, IntValue = (uint)seq })) == SparkplugResult.Applied, "Sequence modulo 256.");
        Check(mapping.Read().Sequence == 1 && mapping.Read().SourceCount == 257, "Wrap applied.");
        Check(Apply(mapping, "DDATA", Message(3, new Metric { Alias = 11, IntValue = 999 })) == SparkplugResult.SequenceGap &&
            !mapping.IsAvailable(0) && mapping.Read().SourceCount == 257, "Sequence gap invalidates without assigning.");
        Check(Apply(mapping, "NBIRTH", NodeBirth(1)) == SparkplugResult.NodeBirth &&
            Apply(mapping, "DBIRTH", DeviceBirth(1)) == SparkplugResult.Applied, "Fresh birth recovery.");
        mapping.SetConnected(false); mapping.SetConnected(true);
        Check(!mapping.IsAvailable(0) && mapping.NeedsRebirth &&
            Apply(mapping, "DDATA", Message(2)) == SparkplugResult.NeedBirth, "Local reconnect requires new births.");
        mapping = Born();
        Check(Apply(mapping, "NBIRTH", NodeBirth(0), retain: true) == SparkplugResult.Retained && mapping.IsAvailable(0), "Retained birth rejected.");
        Check(mapping.Apply(SparkplugMapping.Prefix + "/DDATA/other/sensor-a", Message(2).ToByteArray(), false, 0) == SparkplugResult.Ignored, "Exact identity.");
        foreach (Payload invalid in InvalidData())
        {
            mapping = Born();
            Check(Apply(mapping, "DDATA", invalid) == SparkplugResult.Malformed && !mapping.IsAvailable(0) &&
                mapping.Read().SourceCount == -123, "Invalid metric atomicity.");
        }
        foreach (float real in new[] { float.MinValue, float.MaxValue, float.Epsilon, -float.Epsilon, 0f, -0.5f })
        {
            mapping = Born();
            Check(Apply(mapping, "DDATA", Message(2, new Metric { Alias = 12, FloatValue = real })) == SparkplugResult.Applied &&
                BitConverter.SingleToInt32Bits(mapping.Read().ExternalTemperature) == BitConverter.SingleToInt32Bits(real), "Float wire bits.");
        }
        foreach (double real in new[] { double.MinValue, double.MaxValue, double.Epsilon, -double.Epsilon, -12.125 })
        {
            mapping = Born();
            Check(Apply(mapping, "DDATA", Message(2, new Metric { Alias = 13, DoubleValue = real })) == SparkplugResult.Applied &&
                mapping.Read().ImportedEnergy == real, "Double wire bits.");
        }
        byte[] birth = DeviceBirth(1).ToByteArray();
        // 잘린 protobuf 앞부분도 그 자체로는 유효할 수 있으므로, 매핑은 메트릭/seq 누락을 따로 거부해야 합니다.
        for (int n = 0; n < birth.Length; n++)
        {
            mapping = new SparkplugMapping(); mapping.SetConnected(true);
            Apply(mapping, "NBIRTH", NodeBirth(0));
            Check(mapping.Apply(SparkplugMapping.Topic("DBIRTH"), birth.Take(n).ToArray(), false, 0) != SparkplugResult.Applied &&
                mapping.Read() == null, "All birth truncations.");
        }
        SparkplugPayload parsed;
        Check(!SparkplugPayload.TryDecode(new byte[513], out parsed), "Bounded payload.");
        foreach (byte[] invalid in new[] { new byte[] { 8, 128 }, new byte[] { 18, 255, 255, 255, 255, 127 },
            new byte[] { 8, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2 }, new byte[] { 24, 0, 24, 1 },
            Enumerable.Repeat((byte)128, 12).ToArray(), new byte[] { 0 }, new byte[] { 18, 0 } })
            Check(!SparkplugPayload.TryDecode(invalid, out parsed), "Malformed protobuf.");
        var command = Payload.Parser.ParseFrom(SparkplugPayload.RebirthRequest());
        Check(!command.HasSeq && command.Metrics.Count == 1 && command.Metrics[0].Name == "Node Control/Rebirth" &&
            command.Metrics[0].Datatype == 11 && command.Metrics[0].BooleanValue, "Independent generated decoder checks NCMD encoding.");
        foreach (Payload invalid in new[] { Message(1, Bd(0)), Message(-1, Bd(0), Bd(0)),
            Message(-1, new Metric { Name = "bdSeq", Alias = 1, Datatype = 4, LongValue = 0 }),
            Message(-1, new Metric { Name = "bdSeq", Datatype = 4, LongValue = 256 }) })
        {
            mapping = Born();
            Check(Apply(mapping, "NDEATH", invalid) == SparkplugResult.Malformed && !mapping.IsAvailable(0), "Strict death schema.");
        }
        EncoderChecks();
        var observation = new NativeObservation();
        observation.Receive(new MqttApplicationMessageBuilder().WithTopic(SparkplugEncoder.CommandTopic)
            .WithPayload(SparkplugPayload.RebirthRequest()).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
        observation.Receive(new MqttApplicationMessageBuilder().WithTopic(SparkplugEncoder.Topic("NBIRTH"))
            .WithPayload(SparkplugEncoder.NodeBirth(Now, 200)).Build());
        observation.EnsureHealthy();
        Check(observation.LastBd == 200 && observation.DataCount == 0, "Observer distinguishes NCMD from Edge data.");
        Console.WriteLine("SPARKPLUG CONTRACT PASS checks=" + checks);
    }
    private static void TahuBirthDeath()
    {
        foreach (int bd in new[] { 0, 200, 255 })
        {
            var mapping = new SparkplugMapping();
            mapping.SetConnected(true);
            var birth = NodeBirth(bd);
            birth.Metrics[0].Datatype = 8;
            Check(Apply(mapping, "NBIRTH", birth) == SparkplugResult.NodeBirth, "Tahu UInt64 bdSeq NBIRTH.");
            Check(Apply(mapping, "DBIRTH", DeviceBirth(1)) == SparkplugResult.Applied, "Tahu session device birth.");
            var death = Message(-1, Bd(bd));
            death.Metrics[0].Datatype = 8;
            var stale = Message(-1, Bd((bd + 1) & 255));
            stale.Metrics[0].Datatype = 8;
            Check(Apply(mapping, "NDEATH", stale) == SparkplugResult.StaleDeath && mapping.IsAvailable(0),
                "Tahu stale UInt64 bdSeq does not invalidate current device.");
            Check(Apply(mapping, "NDEATH", death) == SparkplugResult.NodeDeath && !mapping.IsAvailable(0),
                "Tahu UInt64 bdSeq NDEATH.");
        }
        foreach (Metric invalid in new[] {
            new Metric { Name = "bdSeq", Datatype = 8, LongValue = 256 },
            new Metric { Name = "bdSeq", Datatype = 8, LongValue = ulong.MaxValue },
            new Metric { Name = "bdSeq", Alias = 1, Datatype = 8, LongValue = 0 },
            new Metric { Name = "Other", Datatype = 8, LongValue = 0 },
            new Metric { Name = "bdSeq", Datatype = 8, IntValue = 0 }
        })
            Check(!SparkplugPayload.TryDecode(Message(0, invalid).ToByteArray(), out _), "UInt64 remains bounded to unaliased bdSeq.");
        var current = Born();
        Check(Apply(current, "DDATA", Message(2, new Metric { Alias = 11, Datatype = 8, LongValue = 1 })) ==
            SparkplugResult.Malformed && current.Read().SourceCount == -123, "UInt64 device metrics remain unsupported.");
    }

    private static void EncoderChecks()
    {
        const ulong timestamp = 1793140000123;
        foreach (byte bd in new byte[] { 0, 127, 128, 200, 255 })
        {
            var birth = Payload.Parser.ParseFrom(SparkplugEncoder.NodeBirth(timestamp, bd));
            Check(birth.HasTimestamp && birth.Timestamp == timestamp && birth.HasSeq && birth.Seq == 0 &&
                birth.Metrics.Count == 2 && birth.Metrics[0].Name == "bdSeq" && birth.Metrics[0].Datatype == 4 &&
                birth.Metrics[0].LongValue == bd && birth.Metrics[1].Name == "Node Control/Rebirth" &&
                birth.Metrics[1].Datatype == 11 && birth.Metrics[1].HasBooleanValue && !birth.Metrics[1].BooleanValue,
                "Generated reference decodes native NBIRTH.");
            var death = Payload.Parser.ParseFrom(SparkplugEncoder.NodeDeath(bd));
            Check(!death.HasSeq && death.Metrics.Count == 1 && death.Metrics[0].Name == "bdSeq" &&
                death.Metrics[0].Datatype == 4 && death.Metrics[0].LongValue == bd, "Binary NDEATH varints.");
        }
        foreach (int count in new[] { int.MinValue, -1, 0, int.MaxValue })
        foreach (float temperature in new[] { float.MinValue, float.Epsilon, -0.0f, float.MaxValue })
        foreach (double energy in new[] { double.MinValue, double.Epsilon, -0.0, double.MaxValue })
        foreach (bool birth in new[] { true, false })
        {
            var payload = Payload.Parser.ParseFrom(SparkplugEncoder.Values(birth, timestamp, 255, false, count, temperature, energy));
            Check(payload.Timestamp == timestamp && payload.Seq == 255 && payload.Metrics.Count == 4, "Native snapshot envelope.");
            string[] names = { "Sensor/Running", "Sensor/Count", "Sensor/Temperature", "Sensor/Energy" };
            uint[] types = { 11, 3, 9, 10 };
            for (int i = 0; i < 4; i++)
            {
                var metric = payload.Metrics[i];
                Check(metric.HasAlias && metric.Alias == (ulong)(10 + i) && metric.HasName == birth && metric.HasDatatype == birth &&
                    (!birth || metric.Name == names[i] && metric.Datatype == types[i]), "Native name/type/alias definitions.");
            }
            Check(payload.Metrics[0].HasBooleanValue && !payload.Metrics[0].BooleanValue &&
                payload.Metrics[1].HasIntValue && payload.Metrics[1].IntValue == unchecked((uint)count) &&
                BitConverter.SingleToInt32Bits(payload.Metrics[2].FloatValue) == BitConverter.SingleToInt32Bits(temperature) &&
                BitConverter.DoubleToInt64Bits(payload.Metrics[3].DoubleValue) == BitConverter.DoubleToInt64Bits(energy),
                "Generated reference preserves signed integers and floating-point boundary bits.");
        }
        var deviceDeath = Payload.Parser.ParseFrom(SparkplugEncoder.DeviceDeath(timestamp, 0));
        Check(deviceDeath.Timestamp == timestamp && deviceDeath.HasSeq && deviceDeath.Seq == 0 && deviceDeath.Metrics.Count == 0, "Native DDEATH.");
    }
    private static IEnumerable<Payload> InvalidData()
    {
        yield return Message(2, new Metric { Alias = 99, IntValue = 1 });
        yield return Message(2, new Metric { Alias = 11, Datatype = 7, IntValue = 1 });
        yield return Message(2, new Metric { Alias = 11, IntValue = 1 }, new Metric { Alias = 11, IntValue = 2 });
        yield return Message(2, new Metric { Alias = 11, IntValue = 1 }, new Metric { Alias = 12, FloatValue = float.NaN });
        yield return Message(2, new Metric { Alias = 13, DoubleValue = double.PositiveInfinity });
        yield return Message(2, new Metric { Alias = 11, IntValue = 1, IsNull = true });
        yield return Message(2, new Metric { Alias = 10, BooleanValue = false, IsHistorical = true });
        yield return Message(2, new Metric { Alias = 12, DoubleValue = 1 });
        yield return Message(2, new Metric { Name = "Sensor/Count", Alias = 10, IntValue = 1 });
    }

    internal static async Task Peer(string host, string board)
    {
        var factory = new MqttFactory();
        using (var broker = factory.CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Parse(host)).WithDefaultEndpointPort(18883).Build()))
        using (var source = factory.CreateMqttClient())
        using (var observer = factory.CreateMqttClient())
        using (var udp = new UdpClient())
        using (var control = new SemaphoreSlim(1))
        {
            int bd = 0, seq = 1, requests = 0;
            var native = new NativeObservation();
            bool automatic = board == null;
            Exception sourceFailure = null;
            Action ensureHealthy = () => {
                native.EnsureHealthy();
                Exception error = Volatile.Read(ref sourceFailure);
                if (error != null) throw new InvalidOperationException("Sparkplug source callback failed.", error);
            };
            Func<string, Payload, Task> send = async (verb, payload) => {
                var response = await source.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(SparkplugMapping.Topic(verb))
                    .WithPayload(payload.ToByteArray()).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce).Build());
                Check(response.IsSuccess, verb + " publish.");
            };
            Func<Task> births = async () => {
                seq = 1;
                await send("NBIRTH", NodeBirth(bd));
                await send("DBIRTH", DeviceBirth(seq));
            };
            await broker.StartAsync();
            var connectOptions = new Func<int, MqttClientOptions>(session =>
                new MqttClientOptionsBuilder().WithTcpServer(host, 18883).WithClientId("sparkplug-edge-a").WithCleanSession()
                .WithWillTopic(SparkplugMapping.Topic("NDEATH")).WithWillPayload(Message(-1, Bd(session)).ToByteArray())
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithWillRetain(false).Build());
            source.ApplicationMessageReceivedAsync += async args => {
                try
                {
                Check(args.ApplicationMessage.Topic == SparkplugMapping.CommandTopic && !args.ApplicationMessage.Retain, "NCMD topic/retain.");
                var command = Payload.Parser.ParseFrom(args.ApplicationMessage.PayloadSegment.ToArray());
                Check(command.Metrics.Count == 1 && command.Metrics[0].Name == "Node Control/Rebirth" &&
                    command.Metrics[0].Datatype == 11 && command.Metrics[0].BooleanValue, "NCMD request from actual board.");
                Interlocked.Increment(ref requests);
                if (Volatile.Read(ref automatic))
                {
                    await control.WaitAsync();
                    try { if (Volatile.Read(ref automatic)) await births(); }
                    finally { control.Release(); }
                }
                }
                catch (Exception error) { Volatile.Write(ref sourceFailure, error); throw; }
            };
            await source.ConnectAsync(connectOptions(bd));
            var subscription = await source.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(SparkplugMapping.CommandTopic, MqttQualityOfServiceLevel.AtLeastOnce).Build());
            Check(subscription.Items.Count == 1 && subscription.Items.All(i => i.ResultCode == MqttClientSubscribeResultCode.GrantedQoS1), "NCMD SUBACK.");
            observer.ApplicationMessageReceivedAsync += args => {
                native.Receive(args.ApplicationMessage);
                return Task.CompletedTask;
            };
            await observer.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, 18883).WithClientId("sparkplug-native-observer").Build());
            await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(SparkplugMapping.Prefix + "/+/" + SparkplugEncoder.Node + "/#", MqttQualityOfServiceLevel.AtLeastOnce).Build());
            if (board == null)
            {
                Console.WriteLine("READY: Sparkplug edge-a/sensor-a; start the MCU consumer within 60 seconds.");
                try
                {
                    for (int i = 0; i < 60; i++)
                    {
                        ensureHealthy();
                        await control.WaitAsync();
                        try
                        {
                            if (Volatile.Read(ref requests) > 0)
                            {
                                seq = (seq + 1) & 255;
                                await send("DDATA", Message(seq, new Metric { Alias = 11, IntValue = (uint)i },
                                    new Metric { Alias = 12, FloatValue = 20 + i * 0.25f }));
                                Console.WriteLine("DDATA seq=" + seq + " count=" + i);
                                Console.WriteLine("MCU edge-b -> PC: data=" + native.DataCount + " count=" + native.Count);
                            }
                        }
                        finally { control.Release(); }
                        await Task.Delay(1000);
                    }
                    await control.WaitAsync();
                    try
                    {
                        Volatile.Write(ref automatic, false);
                        await send("DDEATH", Message((seq + 1) & 255));
                        await send("NDEATH", Message(-1, Bd(bd)));
                        await source.DisconnectAsync();
                    }
                    finally { control.Release(); }
                    ensureHealthy();
                    Check(native.BirthCount >= 1 && native.DataCount == 30 && native.DeviceDeaths == 1 &&
                        native.LastDeathBd == 200 && native.WillCount == 0 && native.Count == 30 &&
                        native.Temperature == 32.5f && native.Energy == 30.125 && !native.Running,
                        "Normal MCU complete stream and graceful death: " + native.Describe());
                    Console.WriteLine("SPARKPLUG NORMAL PASS nativeData=30 birth/death/bidirectional=PASS");
                }
                finally { await broker.StopAsync(); }
                return;
            }
            Console.WriteLine("READY");
            Check(Console.ReadLine() == "go", "Runner handshake.");
            udp.Connect(board, 20005); udp.Client.ReceiveTimeout = 1000;
            int commandSeq = 0;
            Func<int, double[]> commandBoard = code => {
                int id = commandSeq++ % 255 + 1;
                byte[] request = { 77, 81, 83, 49, (byte)id, (byte)code };
                long deadline = Environment.TickCount64 + 30000;
                while (Environment.TickCount64 < deadline)
                {
                    ensureHealthy();
                    udp.Send(request, request.Length);
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] reply;
                    try { reply = udp.Receive(ref remote); }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut) { continue; }
                    string[] text = Encoding.UTF8.GetString(reply).Split('|');
                    Check(text.Length == 12 && text[9] == "OK", "Sparkplug board diagnostic: " + Encoding.UTF8.GetString(reply));
                    if (int.Parse(text[0], CultureInfo.InvariantCulture) != id) continue;
                    double[] values = new double[12];
                    for (int i = 0; i < 12; i++) if (i != 9) values[i] = double.Parse(text[i], CultureInfo.InvariantCulture);
                    return values;
                }
                throw new TimeoutException("Sparkplug board command.");
            };
            Func<Func<double[], bool>, double[]> wait = predicate => {
                long deadline = Environment.TickCount64 + 10000;
                do
                {
                    var state = commandBoard(0);
                    if (predicate(state)) return state;
                    Thread.Sleep(100);
                } while (Environment.TickCount64 < deadline);
                throw new TimeoutException("Sparkplug mapping state.");
            };
            wait(s => s[3] == 1 && s[8] >= 1);
            await births();
            var current = wait(s => s[4] == 1 && s[5] == 1);
            Check(current[6] == -123 && current[7] == 1 && current[10] == 23.5 && current[11] == 123456789.125, "All four metrics on real MCU.");
            await send("DDATA", Message(2, new Metric { Alias = 11, IntValue = unchecked((uint)-456) },
                new Metric { Alias = 12, FloatValue = -12.25f }));
            current = wait(s => s[5] == 2);
            Check(current[6] == -456 && current[10] == -12.25 && current[7] == 1, "Alias partial update on MCU.");
            commandBoard(1);
            await native.Wait(() => native.DataCount == 1);
            Check(native.Count == 1 && native.Temperature == 25.25f && native.Energy == 1.125 && native.Running, "Native MCU -> PC all four metrics.");
            wait(s => s[4] == 0);
            await send("DDATA", Message(3, new Metric { Alias = 10, BooleanValue = false }));
            current = wait(s => s[5] == 3 && s[4] == 1);
            Check(current[6] == -456 && current[7] == 0, "Fresh data restores local age.");
            await send("DDATA", Message(5, new Metric { Alias = 11, IntValue = 999 }));
            current = wait(s => s[2] >= 1 && s[4] == 0);
            Check(current[6] == -456, "Gap never assigns.");
            await births();
            wait(s => s[5] == 1 && s[4] == 1);
            await send("DDEATH", Message(2));
            wait(s => s[4] == 0);
            await send("DDATA", Message(3, new Metric { Alias = 11, IntValue = 777 }));
            current = wait(s => s[2] >= 2);
            Check(current[6] == -123 && current[4] == 0, "Data after DDEATH cannot resurrect.");
            await send("DBIRTH", DeviceBirth(3, 100));
            wait(s => s[4] == 1 && s[5] == 3);
            await send("DDATA", Message(4, new Metric { Alias = 11, IntValue = 999 }));
            current = wait(s => s[2] >= 3 && s[4] == 0);
            Check(current[6] == -123, "Obsolete alias rejected.");
            commandBoard(2); current = commandBoard(3);
            Check(current[4] == 0, "MQTT consumer reconnect invalidates.");
            await native.Wait(() => native.BirthCount >= 2 && native.LastBd == 201);
            var rebirth = await source.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(SparkplugEncoder.CommandTopic)
                .WithPayload(SparkplugPayload.RebirthRequest()).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
            Check(rebirth.IsSuccess, "NCMD to native MCU.");
            await native.Wait(() => native.BirthCount >= 3);
            Check(native.LastBd == 201, "Rebirth uses current CONNECT bdSeq.");
            await births();
            wait(s => s[4] == 1 && s[5] == 1);
            commandBoard(4);
            await native.Wait(() => native.WillCount == 1);
            Check(native.LastDeathBd == 201, "MCU binary Last Will with non-ASCII varint.");
            current = commandBoard(3);
            Check(current[4] == 0, "New MQTT session awaits remote birth.");
            await native.Wait(() => native.LastBd == 202);
            await births();
            current = wait(s => s[4] == 1 && s[5] == 1);
            int expectedCount = -123;
            BoardHeap.Sample(udp);
            uint warmHeap = BoardHeap.Sample(udp), minimumHeap = warmHeap;
            double expectedTemperature = 23.5, expectedEnergy = 123456789.125, expectedRunning = 1;
            for (int i = 2; i <= 265; i++)
            {
                Metric metric;
                switch (i % 4)
                {
                    case 0:
                        expectedCount = (i & 4) == 0 ? int.MinValue : int.MaxValue;
                        metric = new Metric { Alias = 11, IntValue = unchecked((uint)expectedCount) }; break;
                    case 1:
                        expectedTemperature = (i & 4) == 0 ? -123.25f : 16777216f;
                        metric = new Metric { Alias = 12, FloatValue = (float)expectedTemperature }; break;
                    case 2:
                        expectedEnergy = (i & 4) == 0 ? -123456789.125 : 987654321.5;
                        metric = new Metric { Alias = 13, DoubleValue = expectedEnergy }; break;
                    default:
                        expectedRunning = 1 - expectedRunning;
                        metric = new Metric { Alias = 10, BooleanValue = expectedRunning == 1 }; break;
                }
                int sequence = i & 255;
                await send("DDATA", Message(sequence, metric));
                current = wait(s => s[5] == sequence);
                Check(current[4] == 1 && current[6] == expectedCount && current[7] == expectedRunning &&
                    current[10] == expectedTemperature && current[11] == expectedEnergy,
                    "Partial single metric update preserves other typed members across seq wrap.");
                if (i % 16 == 0) minimumHeap = Math.Min(minimumHeap, BoardHeap.Sample(udp));
            }
            minimumHeap = Math.Min(minimumHeap, BoardHeap.Sample(udp));
            Check((long)warmHeap - minimumHeap <= 512, "Bounded Sparkplug partial-update retained heap growth.");
            Console.WriteLine("SPARKPLUG BOARD HEAP PASS growth=" + ((long)warmHeap - minimumHeap) + " limit=512");
            double rejectedBefore = current[2];
            await send("DDATA", Message(265 & 255, new Metric { Alias = 11, IntValue = 777 }));
            current = wait(s => s[2] > rejectedBefore);
            Check(current[4] == 0 && current[6] == expectedCount && current[10] == expectedTemperature &&
                current[11] == expectedEnergy && current[7] == expectedRunning, "Duplicate seq cannot partially assign or refresh.");
            await births();
            current = wait(s => s[4] == 1 && s[5] == 1);
            double receivedBeforeStale = current[1];
            await send("NDEATH", Message(-1, Bd(255)));
            current = wait(s => s[1] > receivedBeforeStale);
            Check(current[4] == 1 && current[6] == -123, "Old bdSeq death cannot kill current session.");
            foreach (Payload invalid in InvalidData())
            {
                rejectedBefore = current[2];
                await send("DDATA", invalid);
                current = wait(s => s[2] > rejectedBefore);
                Check(current[4] == 0 && current[6] == -123 && current[7] == 1 &&
                    current[10] == 23.5 && current[11] == 123456789.125, "Late invalid metric preserves whole board model.");
                await births();
                current = wait(s => s[4] == 1 && s[5] == 1);
            }
            Console.WriteLine("SPARKPLUG ROBUST PASS independent-fields=264 seq-wrap/duplicate/stale-bdSeq/late-invalid-atomicity.");
            // 생성된 참조 디코더로 브로커가 대신 발행한 PC Edge의 Last Will(NDEATH)도 관찰합니다.
            int deaths = 0;
            using (var deathObserver = factory.CreateMqttClient())
            {
            deathObserver.ApplicationMessageReceivedAsync += args => {
                var death = Payload.Parser.ParseFrom(args.ApplicationMessage.PayloadSegment.ToArray());
                Check(!args.ApplicationMessage.Retain && args.ApplicationMessage.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce &&
                    !death.HasSeq && death.Metrics.Count == 1 && death.Metrics[0].Name == "bdSeq" &&
                    death.Metrics[0].LongValue == 0, "Binary NDEATH broker Will.");
                Interlocked.Increment(ref deaths); return Task.CompletedTask;
            };
            await deathObserver.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, 18883).WithClientId("sparkplug-will-observer").Build());
            await deathObserver.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(SparkplugMapping.Topic("NDEATH"), MqttQualityOfServiceLevel.AtLeastOnce).Build());
            double receivedBefore = current[1];
            source.Dispose();
            current = wait(s => s[4] == 0 && s[1] > receivedBefore);
            for (int i = 0; i < 50 && Volatile.Read(ref deaths) == 0; i++) await Task.Delay(100);
            Check(deaths == 1 && current[6] == -123, "Broker Will reached consumer; last value retained but invalid.");
            await deathObserver.DisconnectAsync();
            }
            commandBoard(2);
            await observer.DisconnectAsync();
            await broker.StopAsync();
            Console.WriteLine("SPARKPLUG HARDWARE PASS types=4 native-mqtt/bidirectional/birth/alias/partial/stale/gap/device-death/reconnect/binary-will=PASS checks=" + checks);
        }
    }
    private sealed class NativeObservation
    {
        private readonly object gate = new object();
        internal int BirthCount, DataCount, WillCount, LastBd = -1, LastDeathBd = -1, Count;
        internal int DeviceDeaths;
        internal float Temperature;
        internal double Energy;
        internal bool Running;
        private int sequence = -1;
        private Exception failure;
        internal string Describe()
        {
            lock (gate) return "births=" + BirthCount + ", data=" + DataCount + ", deviceDeaths=" + DeviceDeaths +
                ", wills=" + WillCount + ", bd=" + LastBd + ", deathBd=" + LastDeathBd + ", count=" + Count +
                ", temperature=" + Temperature + ", energy=" + Energy + ", running=" + Running;
        }
        internal void EnsureHealthy()
        {
            lock (gate) if (failure != null) throw new InvalidOperationException("Native MQTT Sparkplug callback failed.", failure);
        }
        internal void Receive(MqttApplicationMessage message)
        {
            lock (gate)
            {
                try
                {
                var payload = Payload.Parser.ParseFrom(message.PayloadSegment.ToArray());
                Check(!message.Retain, "Native retain=false.");
                if (message.Topic == SparkplugEncoder.CommandTopic)
                {
                    Check(message.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce && !payload.HasSeq &&
                        payload.Metrics.Count == 1 && payload.Metrics[0].Name == "Node Control/Rebirth" &&
                        payload.Metrics[0].Datatype == 11 && payload.Metrics[0].HasBooleanValue &&
                        payload.Metrics[0].BooleanValue, "Observed NCMD is not part of the Edge's shared sequence.");
                    return;
                }
                if (message.Topic == SparkplugEncoder.Topic("NDEATH"))
                {
                    Check(!payload.HasSeq && payload.Metrics.Count == 1 && payload.Metrics[0].Name == "bdSeq" &&
                        payload.Metrics[0].Datatype == 4, "Native NDEATH schema.");
                    LastDeathBd = (int)payload.Metrics[0].LongValue;
                    if (message.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce) WillCount++;
                    sequence = -1; return;
                }
                Check(message.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce && payload.HasTimestamp &&
                    payload.Timestamp >= 1577836800000UL, "Native data QoS/timestamp: " + message.Topic +
                    ", QoS=" + message.QualityOfServiceLevel + ", timestamp=" + payload.Timestamp);
                if (message.Topic == SparkplugEncoder.Topic("NBIRTH"))
                {
                    Check(payload.Seq == 0 && payload.Metrics.Count == 2, "Native NBIRTH.");
                    LastBd = (int)payload.Metrics.Single(m => m.Name == "bdSeq").LongValue;
                    Check(payload.Metrics.Single(m => m.Name == "bdSeq").Datatype == 4 &&
                        !payload.Metrics.Single(m => m.Name == "Node Control/Rebirth").BooleanValue, "Native node metrics.");
                    sequence = 0; return;
                }
                Check(sequence >= 0 && payload.Seq == (ulong)((sequence + 1) & 255), "Native shared sequence.");
                sequence = (int)payload.Seq;
                if (message.Topic == SparkplugEncoder.Topic("DDEATH"))
                { Check(payload.Metrics.Count == 0, "Native DDEATH."); DeviceDeaths++; return; }
                bool birth = message.Topic == SparkplugEncoder.Topic("DBIRTH");
                Check(birth || message.Topic == SparkplugEncoder.Topic("DDATA"), "Native data topic.");
                Check(payload.Metrics.Count == 4, "Native complete snapshot.");
                Check(payload.Metrics.Select(m => m.Alias).Distinct().Count() == 4, "Native unique aliases.");
                string[] names = { "Sensor/Running", "Sensor/Count", "Sensor/Temperature", "Sensor/Energy" };
                uint[] types = { 11, 3, 9, 10 };
                foreach (var metric in payload.Metrics)
                {
                    Check(metric.HasAlias && metric.Alias >= 10 && metric.Alias <= 13, "Native alias.");
                    if (birth) Check(metric.HasName && metric.Name == names[(int)metric.Alias - 10] &&
                        metric.HasDatatype && metric.Datatype == types[(int)metric.Alias - 10], "Native birth definitions.");
                    else Check(!metric.HasName && !metric.HasDatatype, "Native alias-only data.");
                    if (metric.Alias == 10) Running = metric.BooleanValue;
                    if (metric.Alias == 11) Count = unchecked((int)metric.IntValue);
                    if (metric.Alias == 12) Temperature = metric.FloatValue;
                    if (metric.Alias == 13) Energy = metric.DoubleValue;
                }
                if (birth) BirthCount++; else DataCount++;
                }
                catch (Exception error) { failure = error; throw; }
            }
        }
        internal async Task Wait(Func<bool> predicate)
        {
            for (int i = 0; i < 100; i++)
            {
                lock (gate) { EnsureHealthy(); if (predicate()) return; }
                await Task.Delay(100);
            }
            throw new TimeoutException("Native MQTT Sparkplug observation.");
        }
    }
}
