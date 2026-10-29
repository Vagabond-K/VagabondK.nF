using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.Server;
using VagabondK.nF.Modbus.TCP;

namespace VagabondK.nF.Samples.TCPServer
{
    /// <summary>보드의 TCP 502에서 한 연결씩 처리하는 Modbus Server 예제입니다.</summary>
    /// <remarks>Device Explorer에서 네트워크를 설정하고 격리된 테스트망에서 실행합니다.
    /// 메모리 저장소를 사용하며 요청 수와 GC 후 가용 힙은 Input Register 120~127로 노출합니다.</remarks>
    public static class Program
    {
        private static readonly ushort[] Diagnostics = new ushort[8];
        private static uint _requests;
        private static uint _baselineFree;
        private static ushort _heapSamples;

        /// <summary>초기 저장소를 만들고 접속을 수락하며 오류나 시간 초과가 발생한 연결을 닫습니다.</summary>
        public static void Main()
        {
            Debug.WriteLine("VagabondK.nF Modbus TCP Server starting.");
            Debug.WriteLine("Configure Wi-Fi in Device Explorer: DHCP, SSID, password, AutoConnect.");
            WaitForNetwork();

            var store = new ArrayDataStore(64, 64, 128, 128);
            store.WriteHoldingRegisters(0, 3, new ushort[] { 123, 456, 65535 });
            store.SetInputRegisters(0, 3, new ushort[] { 100, 200, 300 });
            store.SetDiscreteInputs(0, 4, new bool[] { true, false, true, false });

            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, 502));
                listener.Listen(1);
                Debug.WriteLine("Ready: TCP 502, Unit ID 1, holding registers 0..2 = 123,456,65535.");
                while (true)
                {
                    using (Socket connection = listener.Accept())
                    {
                        Debug.WriteLine("Client connected.");
                        Serve(connection, store);
                        Debug.WriteLine("Client disconnected; waiting for another connection.");
                    }
                }
            }
        }

        private static void WaitForNetwork()
        {
            long nextNotice = 0;
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
                if (Environment.TickCount64 >= nextNotice)
                {
                    Debug.WriteLine("Waiting for a network address; check saved Wi-Fi configuration.");
                    nextNotice = Environment.TickCount64 + 10000;
                }
                Thread.Sleep(1000);
            }
        }

        private static void Serve(Socket connection, ArrayDataStore store)
        {
            var server = new ModbusServer(1, new TcpTransport(new SocketChannel(connection), 30000), store);
            try
            {
                while (true)
                {
                    server.ProcessNextRequest();
                    _requests++;
                    if (_requests % 1000 == 0)
                        UpdateDiagnostics(store);
                }
            }
            catch (TimeoutException error)
            {
                Debug.WriteLine("Closing timed-out connection: " + error.Message);
            }
            catch (ModbusProtocolException error)
            {
                Debug.WriteLine("Closing connection: " + error.Message);
            }
            catch (SocketException error)
            {
                Debug.WriteLine("Closing connection after socket error: " + error.ErrorCode);
            }
        }

        private static void UpdateDiagnostics(ArrayDataStore store)
        {
            uint free = nanoFramework.Runtime.Native.GC.Run(true);
            if (_heapSamples == 0)
                _baselineFree = free;
            _heapSamples++;
            Diagnostics[0] = 0x4E45;
            Diagnostics[1] = (ushort)(_requests >> 16);
            Diagnostics[2] = (ushort)_requests;
            Diagnostics[3] = (ushort)(_baselineFree >> 16);
            Diagnostics[4] = (ushort)_baselineFree;
            Diagnostics[5] = (ushort)(free >> 16);
            Diagnostics[6] = (ushort)free;
            Diagnostics[7] = _heapSamples;
            store.SetInputRegisters(120, 8, Diagnostics);
        }
    }
}
