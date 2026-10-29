using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VagabondK.nF.MQTT.Mapping
{
    /// <summary>
    /// 자체 MQTT 클라이언트로 두 장치가 MQTT 3.1.1 JSON 스냅샷을 주고받는 샘플(장치 B 역할)입니다.
    /// </summary>
    /// <remarks>
    /// 실행 흐름: 저장된 네트워크로 IPv4 주소 획득 대기(최대 90초) → 브로커 연결과 장치 A 토픽 QoS 1 구독 →
    /// 1초마다 자기 스냅샷을 QoS 1로 발행(PUBACK 대기)하고 수신한 A 값과 유효성을 출력 → 30회 후 연결 해제.
    /// 외부 MQTT 브로커가 필요하며 <see cref="BrokerAddress"/>/<see cref="BrokerPort"/>를 실제 환경에 맞게 바꾸십시오.
    /// 자동 재연결·재전송은 하지 않습니다. MQTT_HARDWARE_TEST 정의 시 UDP 20005 포트의 진단 명령 모드로 동작합니다.
    /// </remarks>
    internal static class Program
    {
        // 평가용 브로커 주소입니다. 보드와 같은 LAN의 브로커로 바꾸십시오(평문 TCP, TLS 없음).
        private const string BrokerAddress = "192.168.0.210";
        private const int BrokerPort = 18883;
        private const string IncomingTopic = "vknf/sample/device-a/sensor";
        private const string OutgoingTopic = "vknf/sample/device-b/sensor";
        private static readonly SensorConnection connection = new SensorConnection(BrokerAddress, BrokerPort, IncomingTopic, OutgoingTopic);
        private static RemoteSensorMapping mapping { get { return connection.Mapping; } }
#if MQTT_HARDWARE_TEST
        private static string failure;
#endif
        private static void Publish()
        {
            // 이 메모리 값은 실제 장치에서 일관되게 읽은 로컬 스냅샷으로 바꾸십시오.
            int next = connection.Published + 1;
            connection.Publish(new SensorSnapshot(next, 250 + next, next % 2 != 0));
        }
        public static void Main()
        {
            SnapshotJson.DemonstrateBufferEncoding();
            long deadline = Environment.TickCount64 + 90000;
            while (true)
            {
                bool ready = false;
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                    if (!string.IsNullOrEmpty(network.IPv4Address) && network.IPv4Address != "0.0.0.0" &&
                        network.IPv4Address != "127.0.0.1") ready = true;
                if (ready) break;
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Saved network required.");
                Thread.Sleep(1000);
            }
            try
            {
                connection.Connect();
#if MQTT_HARDWARE_TEST
                Diagnostics();
#else
                for (int i = 0; i < 30; i++)
                {
                    Publish();
                    ControllerView remote = mapping.Read();
                    Debug.WriteLine(remote == null ? "No remote value." :
                        "A -> B: sequence=" + remote.SourceSequence + ", temperatureDeciC=" +
                        remote.ExternalTemperatureDeciC + ", running=" + remote.SourceRunning +
                        ", available=" + mapping.IsAvailable(Environment.TickCount64));
                    long next = Environment.TickCount64 + 1000;
                    while (Environment.TickCount64 < next) connection.Poll(100);
                }
#endif
            }
            finally { connection.Dispose(); }
        }
#if MQTT_HARDWARE_TEST
        // 시험 전용 MQS1 명령: 0=조회, 1=발행, 2=해제, 3=연결. 순번은 1..255를 순환하며 재전송은 재실행하지 않습니다.
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
                    if (failure == null)
                    {
                        try { if (connection.Connected) connection.Poll(10); }
                        catch (Exception error) { mapping.SetConnected(false); failure = error.ToString(); }
                    }
                    if (!udp.Poll(10000, SelectMode.SelectRead)) continue;
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int n = udp.ReceiveFrom(request, ref remote);
                    if (n == 4 && request[0] == 71 && request[1] == 67 && request[2] == 77 && request[3] == 49)
                    {
                        uint free = nanoFramework.Runtime.Native.GC.Run(true);
                        byte[] reply = { 71, 67, 77, 49, (byte)(free >> 24), (byte)(free >> 16), (byte)(free >> 8), (byte)free };
                        if (udp.SendTo(reply, remote) != reply.Length) throw new IOException("GC diagnostic send.");
                        continue;
                    }
                    if (n != 6 || request[0] != 77 || request[1] != 81 || request[2] != 83 || request[3] != 49)
                        continue;
                    int incoming = request[4];
                    if (incoming == sequence % 255 + 1 && failure == null)
                    {
                        sequence = incoming;
                        try
                        {
                            if (request[5] == 1) Publish();
                            else if (request[5] == 2) connection.Dispose();
                            else if (request[5] == 3) connection.Connect();
                            else if (request[5] != 0) throw new ArgumentException("Diagnostic command.");
                        }
                        catch (Exception error) { mapping.SetConnected(false); failure = error.ToString(); }
                    }
                    {
                        ControllerView value = mapping.Read();
                        string status = sequence + "|" + connection.Applied + "|" + connection.Rejected + "|" + connection.Subscribed + "|" +
                            (mapping.IsAvailable(Environment.TickCount64) ? 1 : 0) + "|" +
                            (value == null ? 0 : value.SourceSequence) + "|" +
                            (value == null ? 0 : value.ExternalTemperatureDeciC) + "|" +
                            (value != null && value.SourceRunning ? 1 : 0) + "|" + connection.Published + "|" +
                            (failure == null ? "OK" : failure);
                        byte[] bytes = Encoding.UTF8.GetBytes(status);
                        if (udp.SendTo(bytes, remote) != bytes.Length) throw new IOException("Diagnostic send.");
                    }
                }
            }
        }
#endif
    }
}
