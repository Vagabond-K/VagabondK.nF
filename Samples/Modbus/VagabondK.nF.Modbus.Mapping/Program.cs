// 아래 정의의 주석을 해제하면 MCU를 Slave 대신 TCP Master로 빌드합니다.
// #define MAPPING_MASTER
using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.Server;
using VagabondK.nF.Modbus.TCP;

namespace VagabondK.nF.Modbus.Mapping.Sample
{
    /// <summary>생성된 Client/Server 매핑을 TCP 장치에 연결하는 보드 예제입니다.</summary>
    /// <remarks>Device Explorer의 네트워크 설정과 상대 주소를 먼저 확인합니다. 기본은 Server이며 MAPPING_MASTER로 Client를 선택합니다.
    /// 예제 값은 센서나 GPIO에 연결되지 않습니다. 원격 쓰기를 활성화하려면 반드시 격리된 테스트 장치를 사용합니다.</remarks>
    public static class Program
    {
        private const string ServerAddress = "192.168.0.210";
        private const int ServerPort = 15020;
        private const byte UnitId = 1;
        private const int TimeoutMilliseconds = 30000;

        /// <summary>메모리 내 형식 변환을 확인한 뒤 네트워크를 기다려 선택한 역할을 실행합니다.</summary>
        public static void Main()
        {
            AllFormats.Run();
            Debug.WriteLine("Configure Wi-Fi DHCP/SSID/password/AutoConnect in Device Explorer.");
            WaitForNetwork();
#if MAPPING_MASTER
            RunMaster();
#else
            RunSlave();
#endif
        }

        private static void WaitForNetwork()
        {
            while (true)
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    string address = interfaces[i].IPv4Address;
                    if (!string.IsNullOrEmpty(address) && address != "0.0.0.0" && address != "127.0.0.1")
                    {
                        Debug.WriteLine("IP address: " + address);
                        return;
                    }
                }
                Debug.WriteLine("Waiting for saved network configuration.");
                Thread.Sleep(5000);
            }
        }

        // 상태는 매 반복, 설정은 열 번의 상태 읽기마다 갱신하여 선택 읽기 그룹의 용도를 보여 줍니다.
        private static void RunMaster()
        {
            Debug.WriteLine("TCP Master: " + ServerAddress + ":" + ServerPort);
            Debug.WriteLine("Read-only sample: explicit writes are disabled; use only a dedicated test Slave.");
            var data = new RemoteData();
            try
            {
                using (var connection = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    connection.Connect(new IPEndPoint(IPAddress.Parse(ServerAddress), ServerPort));
                    var client = new ModbusClient(new TcpTransport(new SocketChannel(connection), TimeoutMilliseconds));
                    int settingsCountdown = 0;
                    while (true)
                    {
                        data.ReadModbusGroupStatus(client, UnitId);
                        if (settingsCountdown == 0)
                        {
                            data.ReadModbusGroupSettings(client, UnitId);
                            settingsCountdown = 10;
                        }
                        settingsCountdown--;
                        Debug.WriteLine("Temperature=" + data.Temperature + ", Status=" + data.Status +
                            ", Setpoint=" + data.Setpoint + ", Enabled=" + data.Enabled);
                        // 전용 테스트 장치에서만 활성화하십시오.
                        // data.Setpoint = 1200;
                        // data.WriteModbusSetpoint(client, UnitId);
                        // data.Enabled = true;
                        // data.WriteModbusEnabled(client, UnitId);
                        Thread.Sleep(1000);
                    }
                }
            }
            catch (TimeoutException error)
            {
                Debug.WriteLine("Master stopped after timeout: " + error.Message);
            }
            catch (ModbusProtocolException error)
            {
                Debug.WriteLine("Master stopped after protocol error: " + error.Message);
            }
            catch (ModbusException error)
            {
                Debug.WriteLine("Master stopped after Slave exception: " + error.Code);
            }
            catch (SocketException error)
            {
                Debug.WriteLine("Master stopped after socket error: " + error.ErrorCode);
            }
            Thread.Sleep(Timeout.Infinite);
        }

        private static void RunSlave()
        {
            // 실제 센서 측정값이나 GPIO 출력이 아닌 예제 값입니다.
            var data = new LocalData { Temperature = 12.5f, Status = -1, Setpoint = 1200 };
            var sync = new object();
            var store = new LocalData.ModbusStore(data, sync);
            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, ServerPort));
                listener.Listen(1);
                Debug.WriteLine("TCP Slave ready: port " + ServerPort + ", Unit ID " + UnitId);
                while (true)
                {
                    using (Socket connection = listener.Accept())
                    {
                        Debug.WriteLine("Master connected.");
                        Serve(connection, store, data, sync);
                    }
                }
            }
        }

        // 매핑 저장소와 앱 로그가 동일한 잠금 객체를 사용합니다. 오류 후에는 같은 TCP 스트림을 재사용하지 않습니다.
        private static void Serve(Socket connection, LocalData.ModbusStore store, LocalData data, object sync)
        {
            var server = new ModbusServer(UnitId, new TcpTransport(new SocketChannel(connection), TimeoutMilliseconds), store);
            try
            {
                while (true)
                {
                    server.ProcessNextRequest();
                    lock (sync)
                    {
                        Debug.WriteLine("Setpoint=" + data.Setpoint + ", Enabled=" + data.Enabled);
                    }
                }
            }
            catch (TimeoutException error)
            {
                Debug.WriteLine("Closing timed-out connection: " + error.Message);
            }
            catch (ModbusProtocolException error)
            {
                Debug.WriteLine("Closing invalid connection: " + error.Message);
            }
            catch (SocketException error)
            {
                Debug.WriteLine("Closing connection after socket error: " + error.ErrorCode);
            }
        }
    }
}
