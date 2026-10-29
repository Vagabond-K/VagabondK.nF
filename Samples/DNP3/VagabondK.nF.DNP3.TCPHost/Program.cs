using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.DNP3.SerialHost;
using VagabondK.nF.DNP3.TCP;

namespace VagabondK.nF.DNP3.TCPHost
{
    /// <summary>Serial과 같은 데이터 검증 시나리오를 TCP에서 실행하는 보드 예제입니다.</summary>
    /// <remarks>DNP3_HARDWARE_TEST가 필요하며 전용 LAN과 저장된 네트워크 설정을 사용합니다.
    /// Master는 PC 192.168.0.210:20000에 접속하고 Outstation은 TCP 20000을 수신합니다. 운영 장치에 연결하지 않습니다.</remarks>
    public static class Program
    {
        private sealed class TcpValidationSession : ValidationSession
        {
            private readonly Dnp3TcpSession _session;

            public TcpValidationSession(Dnp3TcpSession session)
            {
                _session = session;
            }

            public override long Milliseconds
            {
                get
                {
                    return Environment.TickCount64;
                }
            }

            public override bool TransmitIdle
            {
                get
                {
                    return true;
                }
            }

            public override void Send(byte[] bytes)
            {
                _session.Send(bytes);
            }

            public override void RunOnce()
            {
                _session.RunOnce(10);
            }
        }

        /// <summary>선택한 역할을 실행하고 Outstation 재접속 시 Database는 유지하되 연결 상태를 초기화합니다.</summary>
        public static void Main()
        {
#if DNP3_HARDWARE_TEST
            WaitForNetwork();
            var validation = new SerialValidation();
#if DNP3_MASTER_TEST
            using (var connection = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                // 운영 장비가 아닌 격리된 PC 시험기만 사용합니다.
                connection.Connect(new IPEndPoint(IPAddress.Parse("192.168.0.210"), 20000));
                var master = new Dnp3Master(2, 1, validation, responseTimeout: 30000, transportTimeout: 30000);
                var session = new Dnp3TcpSession(connection, master, 3000, 3000);
                validation.RunMaster(master, new TcpValidationSession(session), 100);
                Debug.WriteLine("DNP3 MCU TCP MASTER PASS: checks=" + validation.Checks +
                    "; CRCerrors=" + session.Decoder.CrcErrors);
                Thread.Sleep(Timeout.Infinite);
            }
#else
            var station = new Dnp3Station(validation.Database, 1, 2, validation, validation,
                fragmentSize: 256, confirmationTimeout: 30000, transportTimeout: 30000);
            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, 20000));
                listener.Listen(1);
                Debug.WriteLine("DNP3 MCU TCP OUTSTATION READY: TCP20000");
                while (true)
                    using (Socket connection = listener.Accept())
                    {
                        station.ResetConnection();
                        var session = new Dnp3TcpSession(connection, station, 3000, 3000);
                        try
                        {
                            while (true)
                                session.RunOnce();
                        }
                        catch (IOException error)
                        {
                            Debug.WriteLine("DNP3 TCP disconnected: " + error.Message);
                        }
                        catch (SocketException error)
                        {
                            Debug.WriteLine("DNP3 TCP socket error: " + error.Message);
                        }
                        catch (TimeoutException error)
                        {
                            Debug.WriteLine("DNP3 TCP timeout: " + error.Message);
                        }
                        catch (Dnp3ProtocolException error)
                        {
                            Debug.WriteLine("DNP3 TCP protocol error: " + error.Message);
                        }
                    }
            }
#endif
#else
            Debug.WriteLine("DNP3 TCP test disabled. Requires Dnp3HardwareTest=true and an isolated LAN.");
            Thread.Sleep(Timeout.Infinite);
#endif
        }

        private static void WaitForNetwork()
        {
            long deadline = Environment.TickCount64 + 60000;
            while (Environment.TickCount64 < deadline)
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string address = network.IPv4Address;
                    if (!string.IsNullOrEmpty(address) && address != "0.0.0.0" && address != "127.0.0.1")
                    {
                        Debug.WriteLine("DNP3 TCP IP: " + address);
                        return;
                    }
                }
                Thread.Sleep(1000);
            }
            throw new TimeoutException("DNP3 TCP requires saved Wi-Fi AutoConnect configuration.");
        }
    }
}
