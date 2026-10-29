using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;
using VagabondK.nF.MQTT.Mapping;

/// <summary>MQTT 데이터 공유 샘플과 MQTT/Sparkplug 라이브러리를 PC에서 검증하는 테스트 진입점입니다.</summary>
/// <remarks>
/// 인수 없이 실행하면 외부 장비 없이 JSON 계약, Sparkplug, 가짜 채널 기반 MQTT 3.1.1/5 검사와
/// 루프백 MQTTnet 브로커 상호 운용 검사를 차례로 수행합니다. 추가 모드:
/// <list type="bullet">
/// <item><c>--soak ...</c>: 평문/TLS 브로커와의 장시간 반복 검사만 실행합니다.</item>
/// <item><c>--peer &lt;호스트 IPv4&gt;</c>: PC가 브로커와 장치 A가 되어 보드의 일반 샘플(장치 B)과 60초간 값을 주고받습니다.</item>
/// <item><c>--hardware &lt;호스트 IPv4&gt; &lt;보드 IPv4&gt;</c>: MQTT_HARDWARE_TEST로 빌드한 보드를 UDP 20005 진단 명령으로 조작하며 자동 검증합니다.</item>
/// <item><c>--sparkplug-peer</c>/<c>--sparkplug-hardware</c>: Sparkplug 샘플에 대해 같은 방식으로 실행합니다.</item>
/// <item><c>--tahu-hardware &lt;호스트 IPv4&gt; &lt;보드 IPv4&gt; &lt;peer.js 경로&gt;</c>: Eclipse Tahu 정식 클라이언트로 별도 상호운용 시험을 실행합니다.</item>
/// </list>
/// 보드 모드는 PC 브로커가 호스트 IPv4의 TCP 18883에 바인드하므로 보드와 같은 LAN이어야 하고 방화벽에서 포트를 허용해야 합니다.
/// </remarks>
internal static class Program
{
    private const string ToBoard = "vknf/sample/device-a/sensor", FromBoard = "vknf/sample/device-b/sensor";
    private static int checks;
    private static void Check(bool value, string message)
    { Interlocked.Increment(ref checks); if (!value) throw new InvalidOperationException(message); }
    private static byte[] Bytes(string value) { return Encoding.UTF8.GetBytes(value); }
    private static async Task Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--tahu-hardware")
        { await TahuChecks.Run(args[1], args[2], args[3]); return; }
        if (args.Length != 0 && args[0] == "--soak") { await MqttSoakChecks.Run(args); return; }
        ContractTests();
        SparkplugChecks.Run();
        SparkplugLibraryChecks.Run();
        NativeMqttChecks.Run();
        Mqtt5Checks.Run();
        await NativeMqttBrokerChecks.Run();
        await SharingSampleChecks.Run();
        if (args.Length == 2 && args[0] == "--sparkplug-peer") await SparkplugChecks.Peer(args[1], null);
        else if (args.Length == 3 && args[0] == "--sparkplug-hardware") await SparkplugChecks.Peer(args[1], args[2]);
        else if (args.Length == 2 && args[0] == "--peer") await SharingPeer(args[1]);
        else if (args.Length != 0)
        {
            Check(args.Length == 3 && args[0] == "--hardware", "Use --hardware <host IPv4> <board IPv4>.");
            await Hardware(args[1], args[2]);
        }
        Console.WriteLine("MQTT DATA SHARING PASS checks=" + checks);
    }

    private static async Task SharingPeer(string host)
    {
        var factory = new MqttFactory();
        using (var broker = factory.CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Parse(host)).WithDefaultEndpointPort(18883).Build()))
        using (var source = factory.CreateMqttClient())
        {
            var target = new RemoteSensorMapping(FromBoard, 3000);
            await broker.StartAsync();
            source.ApplicationMessageReceivedAsync += args =>
            {
                byte[] payload = args.ApplicationMessage.PayloadSegment.ToArray();
                ApplyResult result = target.Apply(args.ApplicationMessage.Topic, payload, args.ApplicationMessage.Retain, Environment.TickCount64);
                var view = target.Read();
                Console.WriteLine("B -> A: " + result + (view == null ? "" : " sequence=" + view.SourceSequence +
                    " externalTemperatureDeciC=" + view.ExternalTemperatureDeciC + " sourceRunning=" + view.SourceRunning));
                return Task.CompletedTask;
            };
            source.DisconnectedAsync += args => { target.SetConnected(false); return Task.CompletedTask; };
            try
            {
                await source.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, 18883).WithClientId("device-a").Build());
                target.SetConnected(true);
                var subscription = await source.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(FromBoard, MqttQualityOfServiceLevel.AtLeastOnce).Build());
                Check(subscription.Items.Count == 1, "Peer subscription count.");
                foreach (var item in subscription.Items) Check(item.ResultCode == MqttClientSubscribeResultCode.GrantedQoS1, "Peer SUBACK.");
                Console.WriteLine("READY: device A and broker; start the normal board sample within 60 seconds.");
                for (int i = 1; i <= 60; i++)
                {
                    var snapshot = new SensorSnapshot(i, 200 + i, i % 2 == 0);
                    var response = await source.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(ToBoard)
                        .WithPayload(SnapshotJson.Encode(snapshot)).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
                    Check(response.IsSuccess, "Peer PUBACK.");
                    Console.WriteLine("A -> B: sequence=" + i + " temperatureDeciC=" + snapshot.TemperatureDeciC +
                        " running=" + snapshot.Running + "; B available=" + target.IsAvailable(Environment.TickCount64));
                    await Task.Delay(1000);
                }
            }
            finally
            {
                target.SetConnected(false);
                if (source.IsConnected) await source.DisconnectAsync();
                await broker.StopAsync();
            }
        }
    }
    private static void ContractTests()
    {
        for (int i = 0; i < 100; i++)
        {
            var source = new SensorSnapshot(i + 1, -500 + i * 20, i % 2 == 0);
            SensorSnapshot decoded;
            Check(SnapshotJson.TryDecode(SnapshotJson.Encode(source), out decoded) &&
                decoded.Sequence == source.Sequence && decoded.TemperatureDeciC == source.TemperatureDeciC &&
                decoded.Running == source.Running, "JSON round trip.");
        }
        SensorSnapshot result;
        Check(SnapshotJson.TryDecode(Bytes(" { \"running\": false, \"sequence\": 2147483647, \"temperatureDeciC\": -500 } "), out result), "JSON order and whitespace.");
        foreach (string value in new[] {
            "", "{}", "null", "{\"sequence\":01,\"temperatureDeciC\":0,\"running\":true}",
            "{\"sequence\":2147483648,\"temperatureDeciC\":0,\"running\":true}",
            "{\"sequence\":0,\"temperatureDeciC\":0,\"running\":true}",
            "{\"sequence\":1,\"temperatureDeciC\":1501,\"running\":true}",
            "{\"sequence\":1,\"temperatureDeciC\":-501,\"running\":true}",
            "{\"sequence\":1,\"temperatureDeciC\":0.5,\"running\":true}",
            "{\"sequence\":1,\"sequence\":2,\"running\":true}",
            "{\"sequence\":1,\"temperatureDeciC\":0,\"running\":1}",
            "{\"sequence\":1,\"temperatureDeciC\":0,\"running\":true}x",
            "{\"sequence\":1,\"temperatureDeciC\":0,\"running\":true,\"extra\":1}"
        }) Check(!SnapshotJson.TryDecode(Bytes(value), out result) && result == null, "Reject malformed JSON.");
        Check(!SnapshotJson.TryDecode(new byte[161], out result), "Payload bound.");
        byte[] valid = SnapshotJson.Encode(new SensorSnapshot(1, 235, true));
        for (int n = 0; n < valid.Length; n++)
        {
            byte[] cut = new byte[n]; Array.Copy(valid, cut, n);
            Check(!SnapshotJson.TryDecode(cut, out result), "Reject truncated snapshot.");
        }
        var target = new RemoteSensorMapping(ToBoard, 3000);
        Check(target.Apply(ToBoard, valid, false, 0) == ApplyResult.Disconnected, "Disconnected.");
        target.SetConnected(true);
        Check(target.Apply("wrong", valid, false, 0) == ApplyResult.WrongTopic, "Topic binding.");
        Check(target.Apply(ToBoard, valid, true, 0) == ApplyResult.Retained, "Retained not fresh.");
        Check(target.Apply(ToBoard, valid, false, 0) == ApplyResult.Applied, "Device A -> B.");
        Check(target.Read().ExternalTemperatureDeciC == 235 && target.Read().SourceRunning, "Target model mapping.");
        Check(target.IsAvailable(2999) && !target.IsAvailable(3000), "Age boundary.");
        Check(target.Apply(ToBoard, valid, false, 3001) == ApplyResult.OldSequence && !target.IsAvailable(3001), "Duplicate cannot refresh.");
        Check(target.Apply(ToBoard, Bytes("{}"), false, 3001) == ApplyResult.Malformed &&
            target.Read().SourceSequence == 1, "Invalid cannot mutate.");
        target.SetConnected(false); target.SetConnected(true);
        Check(!target.IsAvailable(1) && target.Apply(ToBoard, valid, false, 1) == ApplyResult.OldSequence, "Reconnect cannot replay.");
        Check(target.Apply(ToBoard, SnapshotJson.Encode(new SensorSnapshot(2, 240, false)), false, 4000) == ApplyResult.Applied &&
            target.IsAvailable(4000), "New sample restores availability.");
    }

    // 보드 자동 검증: PC 브로커 실행 → 러너가 "go" 입력 → 정상 값/중복/잘못된 형식/신선도 만료/연결 해제/보관 메시지 재전송/재연결을
    // 차례로 만들고, 보드 진단 응답(sequence|applied|rejected|subscribed|available|값...|published|OK)으로 결과를 확인합니다.
    private static async Task Hardware(string host, string board)
    {
        var factory = new MqttFactory();
        using (var broker = factory.CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Parse(host)).WithDefaultEndpointPort(18883).Build()))
        using (var publisher = factory.CreateMqttClient())
        using (var observer = factory.CreateMqttClient())
        using (var udp = new UdpClient())
        {
            await broker.StartAsync();
            Console.WriteLine("READY");
            Check(Console.ReadLine() == "go", "Runner ready handshake.");
            var remote = new RemoteSensorMapping(FromBoard, 3000);
            remote.SetConnected(true);
            var observations = new List<int>();
            observer.ApplicationMessageReceivedAsync += args =>
            {
                var payload = args.ApplicationMessage.PayloadSegment;
                byte[] bytes = new byte[payload.Count];
                Array.Copy(payload.Array, payload.Offset, bytes, 0, bytes.Length);
                var applied = remote.Apply(args.ApplicationMessage.Topic, bytes, args.ApplicationMessage.Retain, Environment.TickCount64);
                Check(applied == ApplyResult.Applied, "Board snapshot mapping: " + applied);
                lock (observations) observations.Add(remote.Read().SourceSequence);
                return Task.CompletedTask;
            };
            await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, 18883).WithClientId("device-a-source").Build());
            await observer.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, 18883).WithClientId("device-a-receiver").Build());
            var subscribed = await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(FromBoard, MqttQualityOfServiceLevel.AtLeastOnce).Build());
            Check(subscribed.Items.Count == 1, "PC subscription count.");
            foreach (var item in subscribed.Items)
                Check(item.ResultCode == MqttClientSubscribeResultCode.GrantedQoS1, "PC subscription.");
            udp.Connect(board, 20005);
            udp.Client.ReceiveTimeout = 1000;
            int commandSequence = 0;
            Func<int, int[]> command = code =>
            {
                int sequence = commandSequence++ % 255 + 1;
                byte[] request = { 77, 81, 83, 49, (byte)sequence, (byte)code };
                long deadline = Environment.TickCount64 + 30000;
                while (Environment.TickCount64 < deadline)
                {
                    udp.Send(request, request.Length);
                    IPEndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    byte[] reply;
                    try { reply = udp.Receive(ref source); }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut) { continue; }
                    string[] text = Encoding.UTF8.GetString(reply).Split('|');
                    Check(text.Length == 10 && text[9] == "OK", "Board diagnostic: " + Encoding.UTF8.GetString(reply));
                    if (int.Parse(text[0]) != sequence) continue;
                    int[] values = new int[9];
                    for (int i = 0; i < 9; i++) values[i] = int.Parse(text[i]);
                    return values;
                }
                throw new TimeoutException("Board command " + code);
            };
            Func<Func<int[], bool>, int[]> wait = predicate =>
            {
                long deadline = Environment.TickCount64 + 15000;
                do
                {
                    int[] state = command(0);
                    if (predicate(state)) return state;
                    Thread.Sleep(100);
                } while (Environment.TickCount64 < deadline);
                throw new TimeoutException("Board mapping state.");
            };
            Func<byte[], bool, Task> send = async (payload, retain) =>
            {
                var response = await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(ToBoard)
                    .WithPayload(payload).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(retain).Build());
                Check(response.IsSuccess, "PC publish.");
            };
            wait(s => s[3] == 1);
            await send(SnapshotJson.Encode(new SensorSnapshot(1, 235, true)), false);
            int[] current = wait(s => s[1] == 1);
            Check(current[4] == 1 && current[5] == 1 && current[6] == 235 && current[7] == 1, "PC A -> MCU B values.");
            await send(SnapshotJson.Encode(new SensorSnapshot(1, 999, false)), false);
            await send(Bytes("{\"sequence\":2}"), false);
            current = wait(s => s[2] == 2);
            Check(current[5] == 1 && current[6] == 235 && current[7] == 1, "Invalid/duplicate unchanged.");
            wait(s => s[4] == 0);
            await send(SnapshotJson.Encode(new SensorSnapshot(2, -125, false)), false);
            current = wait(s => s[1] == 2);
            Check(current[4] == 1 && current[6] == -125 && current[7] == 0, "New data recovers age.");
            command(1);
            await WaitObservation(observations, 1);
            Check(remote.Read().ExternalTemperatureDeciC == 251 && remote.Read().SourceRunning, "MCU B -> PC A values.");
            current = command(2);
            Check(current[4] == 0, "Disconnect invalidates.");
            // B가 오프라인일 때 보관된 메시지는 재구독 시 RETAIN=true로 재전송되므로 최신 값으로 인정되면 안 됩니다.
            await send(SnapshotJson.Encode(new SensorSnapshot(99, 999, true)), true);
            command(3);
            current = wait(s => s[3] == 2 && s[2] == 3);
            Check(current[4] == 0 && current[5] == 2, "Retained replay not current.");
            await send(SnapshotJson.Encode(new SensorSnapshot(2, 999, true)), false);
            current = wait(s => s[2] == 4);
            Check(current[5] == 2 && current[4] == 0, "Duplicate after reconnect not current.");
            await send(SnapshotJson.Encode(new SensorSnapshot(3, 300, true)), false);
            current = wait(s => s[1] == 3);
            Check(current[4] == 1 && current[5] == 3 && current[6] == 300, "Reconnect new data.");
            BoardHeap.Sample(udp);
            uint warmHeap = BoardHeap.Sample(udp), minimumHeap = warmHeap;
            int temperature = 300, running = 1;
            for (int i = 0; i < 64; i++)
            {
                int sequence = i + 4;
                if ((i & 1) == 0) temperature = (i & 2) == 0 ? -500 : 1500;
                else running ^= 1;
                await send(Bytes(" {\"running\":" + (running != 0 ? "true" : "false") +
                    ",\"temperatureDeciC\":" + temperature + ",\"sequence\":" + sequence + "} "), false);
                current = wait(s => s[5] == sequence);
                Check(current[4] == 1 && current[6] == temperature && current[7] == running,
                    "Real board independent temperature/running update and reordered JSON.");
                if (i % 8 == 7) minimumHeap = Math.Min(minimumHeap, BoardHeap.Sample(udp));
            }
            int rejected = current[2];
            byte[][] invalids = {
                Bytes("{\"sequence\":68,\"temperatureDeciC\":0}"),
                Bytes("{\"sequence\":68,\"temperatureDeciC\":0,\"running\":true,\"sequence\":69}"),
                Bytes("{\"sequence\":2147483648,\"temperatureDeciC\":0,\"running\":true}"),
                Bytes("{\"sequence\":68,\"temperatureDeciC\":1501,\"running\":true}"),
                Bytes("{\"sequence\":68,\"temperatureDeciC\":-501,\"running\":true}"),
                Bytes("{\"sequence\":68,\"temperatureDeciC\":0,\"running\":1}"),
                Bytes("{\"sequence\":68,\"temperatureDeciC\":0,\"running\":true}x"),
                Bytes("{\"sequence\":68,\"temperatureDeciC\":0,\"running\":true,\"extra\":1}"),
                new byte[161],
                SnapshotJson.Encode(new SensorSnapshot(66, 0, true))
            };
            foreach (byte[] invalid in invalids)
            {
                await send(invalid, false);
                int expectedRejected = ++rejected;
                current = wait(s => s[2] == expectedRejected);
                Check(current[5] == 67 && current[6] == temperature && current[7] == running,
                    "Late/duplicate/overflow/oversize input preserves complete MCU snapshot.");
            }
            byte[] whole = SnapshotJson.Encode(new SensorSnapshot(68, 0, true));
            for (int length = 0; length < whole.Length; length++)
            {
                await send(whole.Take(length).ToArray(), false);
                int expectedRejected = ++rejected;
                current = wait(s => s[2] == expectedRejected);
                Check(current[5] == 67 && current[6] == temperature && current[7] == running,
                    "Every truncated JSON preserves model.");
            }
            await send(SnapshotJson.Encode(new SensorSnapshot(68, -500, false)), false);
            current = wait(s => s[5] == 68);
            Check(current[6] == -500 && current[7] == 0, "Valid input recovers after all malformed boundaries.");
            minimumHeap = Math.Min(minimumHeap, BoardHeap.Sample(udp));
            Check((long)warmHeap - minimumHeap <= 512, "Bounded JSON mapping retained heap growth.");
            Console.WriteLine("MQTT BOARD HEAP PASS growth=" + ((long)warmHeap - minimumHeap) + " limit=512");
            Console.WriteLine("MQTT ROBUST PASS independent-fields=64 all-JSON-truncations overflow/duplicate/order/size/late-failure-preservation.");
            command(1);
            await WaitObservation(observations, 2);
            Check(remote.Read().ExternalTemperatureDeciC == 252 && !remote.Read().SourceRunning, "Second MCU publication.");
            command(2);
            await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(ToBoard).WithPayload(Array.Empty<byte>()).WithRetainFlag().Build());
            await publisher.DisconnectAsync(); await observer.DisconnectAsync();
            await broker.StopAsync();
            Console.WriteLine("MQTT HARDWARE PASS sourceToBoard=68 boardToSource=2 rejects=" + rejected + " freshness/reconnect=PASS");
        }
    }
    private static async Task WaitObservation(List<int> observed, int sequence)
    {
        for (int i = 0; i < 100; i++)
        {
            lock (observed) if (observed.Contains(sequence)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Board publication.");
    }
}
