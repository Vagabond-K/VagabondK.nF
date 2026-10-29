using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using VagabondK.nF.MQTT;
using VagabondK.nF.MQTT.Sparkplug;

namespace VagabondK.nF.MQTT.Sparkplug.Mapping
{
    /// <summary>
    /// <see cref="MqttConnection"/>과 Sparkplug B 라이브러리로 두 Edge Node가 상태를 공유하는 샘플(edge-b 역할)입니다.
    /// </summary>
    /// <remarks>
    /// 실행 흐름: 네트워크 대기 → NDEATH를 Will로 브로커 연결 → edge-a 토픽과 자기 NCMD 구독 → NBIRTH/DBIRTH 발행 →
    /// 1초마다 DDATA 발행, Poll로 edge-a 메시지 수신·검증, 필요하면 edge-a에 재탄생 요청 → 30회 후 DDEATH/NDEATH 후 연결 해제.
    /// 외부 MQTT 브로커(코드의 192.168.0.210:18883)가 필요하며 보드 UTC 시계가 동기화되어 있어야 합니다(타임스탬프 검사).
    /// MQTT_HARDWARE_TEST 정의 시 UDP 20005 포트 진단 명령 모드로 동작합니다.
    /// </remarks>
    internal static class Program
    {
        private static readonly SparkplugMapping mapping = new SparkplugMapping();
        private static Socket socket;
        private static MqttConnection mqtt;
        private static byte bdSequence = 200;
        private static SparkplugSession session;
        private static int subscribed, received, rejected, rebirthRequests, published;
        private static bool rebirthLocal;
        private static long nextRebirth;
#if MQTT_HARDWARE_TEST
        private static string failure;
#endif
        private static ulong UtcMilliseconds()
        {
            long now = DateTime.UtcNow.Ticks / 10000 - 62135596800000L;
            if (now < 1577836800000L) throw new InvalidOperationException("Set/synchronize the board UTC clock before Sparkplug.");
            return (ulong)now;
        }
        // CONNECT마다 bdSeq를 바꾼 새 세션을 사용합니다(Disconnect에서 증가). 수신 콜백은 Poll 스레드에서 실행됩니다.
        private static void Connect()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("192.168.0.210"), 18883));
            mqtt = new MqttConnection(new SocketMqttChannel(socket));
            session = new SparkplugSession("VKNF", SparkplugEncoder.Node, bdSequence);
            mqtt.Received = (topic, bytes, retained, duplicate) => {
                if (topic == SparkplugEncoder.CommandTopic)
                {
                    SparkplugPayload command;
                    if (retained || !SparkplugPayload.TryDecode(bytes, out command) || command.Metrics.Length != 1 ||
                        command.Metrics[0].Name != "Node Control/Rebirth" || command.Metrics[0].DataType != SparkplugDataType.Boolean ||
                        command.Metrics[0].ValueField != 14 || command.Metrics[0].Integer != 1)
                        throw new IOException("Unsupported Sparkplug NCMD.");
                    rebirthLocal = true;
                    return;
                }
                SparkplugResult result = mapping.Apply(topic, bytes, retained, Environment.TickCount64);
                if (result == SparkplugResult.Ignored) return;
                received++;
                if (result != SparkplugResult.Applied && result != SparkplugResult.NodeBirth &&
                    result != SparkplugResult.NodeDeath && result != SparkplugResult.DeviceDeath) rejected++;
                Debug.WriteLine("Sparkplug: " + result);
            };
            mqtt.Connect("vknf-sparkplug-edge-b", session.Topic("NDEATH"), session.NodeDeath());
            mapping.SetConnected(true);
            if (!mqtt.Subscribe(SparkplugMapping.Prefix + "/+/" + SparkplugMapping.Node + "/#", MqttQos.AtMostOnce) ||
                !mqtt.Subscribe(SparkplugEncoder.CommandTopic, MqttQos.AtLeastOnce)) throw new IOException("Sparkplug SUBACK refused.");
            subscribed++;
            nextRebirth = 0;
            Birth();
        }
        private static void Birth()
        {
            mqtt.Publish(SparkplugEncoder.Topic("NBIRTH"), session.NodeBirth(UtcMilliseconds()));
            mqtt.Publish(SparkplugEncoder.Topic("DBIRTH"),
                session.Next(UtcMilliseconds(), SparkplugEncoder.Metrics(true, true, -100, 25.25f, 987654321.125), true));
            rebirthLocal = false;
        }
        private static void Publish()
        {
            published++;
            mqtt.Publish(SparkplugEncoder.Topic("DDATA"),
                session.Next(UtcMilliseconds(), SparkplugEncoder.Metrics(false, published % 2 != 0, published, 25 + published * 0.25f, published + 0.125)));
        }
        private static void Disconnect()
        {
            mapping.SetConnected(false);
            try
            {
                if (mqtt != null && mqtt.Connected)
                {
                    mqtt.Publish(SparkplugEncoder.Topic("DDEATH"), session.Next(UtcMilliseconds(), new SparkplugMetric[0]));
                    mqtt.Publish(SparkplugEncoder.Topic("NDEATH"), session.NodeDeath());
                    mqtt.Disconnect();
                }
            }
            finally { if (socket != null) socket.Close(); socket = null; bdSequence++; }
        }
        // 수신한 NCMD 재탄생 요청을 처리하고, 상대 노드에 재탄생이 필요하면 3초 간격으로 NCMD를 보냅니다.
        private static void Service()
        {
            if (rebirthLocal) Birth();
            long now = Environment.TickCount64;
            if (mapping.NeedsRebirth && now >= nextRebirth)
            {
                mqtt.Publish(SparkplugMapping.CommandTopic, SparkplugPayload.RebirthRequest(), MqttQos.AtLeastOnce);
                rebirthRequests++; nextRebirth = now + 3000;
            }
        }
        public static void Main()
        {
            long deadline = Environment.TickCount64 + 90000;
            while (true)
            {
                bool ready = false;
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                    if (!string.IsNullOrEmpty(network.IPv4Address) && network.IPv4Address != "0.0.0.0" &&
                        network.IPv4Address != "127.0.0.1") ready = true;
                if (ready) break;
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Saved Wi-Fi required.");
                Thread.Sleep(1000);
            }
            try
            {
                Connect();
#if MQTT_HARDWARE_TEST
                Diagnostics();
#else
                long next = 0;
                while (published < 30)
                {
                    mqtt.Poll(100); Service();
                    if (Environment.TickCount64 >= next)
                    {
                        Publish(); next = Environment.TickCount64 + 1000;
                        var value = mapping.Read();
                        Debug.WriteLine(value == null ? "Awaiting Sparkplug birth." :
                            "A -> B: count=" + value.SourceCount + ", temperature=" + value.ExternalTemperature +
                            ", available=" + mapping.IsAvailable(Environment.TickCount64));
                    }
                }
#endif
            }
            finally { Disconnect(); }
        }
#if MQTT_HARDWARE_TEST
        // 시험 전용 MQS1 명령: 0=조회, 1=발행, 2=정상 해제, 3=연결, 4=강제 종료. Poll을 계속하며 1..255 순번으로 한 번씩 실행합니다.
        // GCM1은 명령 순번을 소비하지 않고 GC 후 가용 힙을 반환합니다.
        private static void Diagnostics()
        {
            using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                udp.Bind(new IPEndPoint(IPAddress.Any, 20005));
                byte[] request = new byte[8];
                int sequence = 0;
                while (true)
                {
                    if (failure == null && mqtt != null && mqtt.Connected)
                    {
                        try { mqtt.Poll(10); Service(); }
                        catch (Exception error) { mapping.SetConnected(false); failure = error.ToString(); }
                    }
                    if (!udp.Poll(10000, SelectMode.SelectRead)) continue;
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int count = udp.ReceiveFrom(request, ref remote);
                    if (count == 4 && request[0] == 71 && request[1] == 67 && request[2] == 77 && request[3] == 49)
                    {
                        uint free = nanoFramework.Runtime.Native.GC.Run(true);
                        byte[] reply = { 71, 67, 77, 49, (byte)(free >> 24), (byte)(free >> 16), (byte)(free >> 8), (byte)free };
                        if (udp.SendTo(reply, remote) != reply.Length) throw new IOException("GC diagnostic send.");
                        continue;
                    }
                    if (count != 6 || request[0] != 77 || request[1] != 81 || request[2] != 83 || request[3] != 49) continue;
                    int incoming = request[4];
                    if (incoming == sequence % 255 + 1 && failure == null)
                    {
                        sequence = incoming;
                        try
                        {
                            if (request[5] == 1) Publish();
                            else if (request[5] == 2) Disconnect();
                            else if (request[5] == 3) Connect();
                            else if (request[5] == 4) { mapping.SetConnected(false); socket.Close(); socket = null; mqtt = null; bdSequence++; }
                            else if (request[5] != 0) throw new ArgumentException("Diagnostic command.");
                        }
                        catch (Exception error) { mapping.SetConnected(false); failure = error.ToString(); }
                    }
                    RemoteProcess value = mapping.Read();
                    string status = sequence + "|" + received + "|" + rejected + "|" + subscribed + "|" +
                        (mapping.IsAvailable(Environment.TickCount64) ? 1 : 0) + "|" +
                        (value == null ? -1 : value.Sequence) + "|" + (value == null ? 0 : value.SourceCount) + "|" +
                        (value != null && value.SourceRunning ? 1 : 0) + "|" + rebirthRequests + "|" +
                        (failure == null ? "OK" : failure) + "|" + (value == null ? 0 : value.ExternalTemperature) + "|" +
                        (value == null ? 0 : value.ImportedEnergy);
                    byte[] bytes = Encoding.UTF8.GetBytes(status);
                    if (udp.SendTo(bytes, remote) != bytes.Length) throw new IOException("Diagnostic send.");
                }
            }
        }
#endif
    }
}
