#if !DNP3_MASTER_MAPPING_SERIAL_SAMPLE
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.DNP3.TCP;

namespace VagabondK.nF.DNP3.MasterMapping.Sample
{
    // 저장된 네트워크 설정과 전용 Outstation(192.168.0.210:20000)이 필요합니다. Master 2가 Class 0~3을 주기적으로 읽습니다.
    internal static class Program
    {
        // 요청 생성과 RunOnce는 같은 흐름에서 실행합니다. 오류 후 새 소켓과 세션을 만들고 Endpoint 상태를 초기화합니다.
        public static void Main()
        {
            long deadline = Environment.TickCount64 + 90000;
            bool ready = false;
            while (!ready)
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string ip = network.IPv4Address;
                    if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0" && ip != "127.0.0.1") ready = true;
                }
                if (Environment.TickCount64 > deadline) throw new TimeoutException("Saved Wi-Fi required.");
                if (!ready) Thread.Sleep(1000);
            }
#if DNP3_MASTER_MAPPING_SOAK
            MasterSoak.Run();
#elif DNP3_MASTER_MAPPING_TEST
            MasterHardware.Run();
#else
            var model = new ReceivedData();
            var sync = new object();
            var floatData = new FloatData();
            model.Observer = new FloatData.Dnp3MasterBinding(floatData, sync);
            var binding = new ReceivedData.Dnp3MasterBinding(model, sync);
            var master = new Dnp3Master(2, 1, binding, maximumMeasurements: 64);
            while (true)
            {
                try
                {
                    using (var connection = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    {
                        connection.Connect(new IPEndPoint(IPAddress.Parse("192.168.0.210"), 20000));
                        master.ResetConnection();
                        var session = new Dnp3TcpSession(connection, master);
                        long next = 0;
                        while (true)
                        {
                            long now = Environment.TickCount64;
                            if (!master.Busy && now >= next)
                            {
                                if (master.Result != Dnp3MasterResult.Idle && master.Result != Dnp3MasterResult.Success)
                                    throw new IOException("DNP3 request failed: " + master.Result);
                                session.Send(master.ReadClasses(15, now));
                                next = now + 3000;
                            }
                            session.RunOnce();
                        }
                    }
                }
                catch (IOException error) { Debug.WriteLine("DNP3 Master mapping disconnected: " + error.Message); }
                catch (SocketException error) { Debug.WriteLine("DNP3 Master mapping socket: " + error.Message); }
                catch (TimeoutException error) { Debug.WriteLine("DNP3 Master mapping timeout: " + error.Message); }
                catch (Dnp3ProtocolException error) { Debug.WriteLine("DNP3 Master mapping protocol: " + error.Message); }
                Thread.Sleep(3000);
            }
#endif
        }
    }
}
#endif
