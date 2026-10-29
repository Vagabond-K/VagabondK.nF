using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.DNP3.TCP;

namespace VagabondK.nF.DNP3.Mapping.Sample
{
    // 네트워크 설정 후 TCP 20000에서 Outstation 1 / Master 2로 실행합니다. 제어는 메모리 모델만 변경합니다.
    internal static class Program
    {
        // 새 연결마다 프로토콜 상태를 초기화하되 Database는 유지하고, 제어 후 Status Point를 명시적으로 게시합니다.
        public static void Main()
        {
            FloatData.Demonstrate();
            bool ready = false;
            long deadline = Environment.TickCount64 + 90000;
            while (!ready)
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string ip = network.IPv4Address;
                    if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0" && ip != "127.0.0.1") ready = true;
                }
                if (Environment.TickCount64 > deadline) throw new TimeoutException("Saved Wi-Fi AutoConnect required.");
                if (!ready) Thread.Sleep(1000);
            }
            var sync = new object();
            var model = new DeviceData();
            var mapped = new DeviceData.Dnp3Binding(model, sync, 32);
            var station = new Dnp3Station(mapped.Database, 1, 2, mapped);
            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, 20000));
                listener.Listen(1);
                Debug.WriteLine("DNP3 mapping evaluation: TCP20000, Outstation 1, Master 2.");
                while (true)
                    using (Socket connection = listener.Accept())
                    {
                        station.ResetConnection();
                        var session = new Dnp3TcpSession(connection, station, 3000, 3000);
                        try
                        {
                            while (true)
                            {
                                bool accepted = mapped.Publish(Dnp3PointType.BinaryOutputStatus, 0);
                                accepted = mapped.Publish(Dnp3PointType.AnalogOutputStatus, 0) && accepted;
                                if (!accepted) Debug.WriteLine("DNP3 mapping event overflow: Static updated, event dropped.");
                                session.RunOnce();
                            }
                        }
                        catch (IOException error) { Debug.WriteLine("DNP3 disconnected: " + error.Message); }
                        catch (SocketException error) { Debug.WriteLine("DNP3 socket error: " + error.Message); }
                        catch (TimeoutException error) { Debug.WriteLine("DNP3 timeout: " + error.Message); }
                        catch (Dnp3ProtocolException error) { Debug.WriteLine("DNP3 protocol error: " + error.Message); }
                    }
            }
        }
    }
}
