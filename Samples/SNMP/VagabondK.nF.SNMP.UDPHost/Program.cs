using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

using VagabondK.nF.SNMP.UDP;

namespace VagabondK.nF.SNMP.UDPHost
{
    /// <summary>
    /// 직접 구현한 <see cref="DemoStore"/>로 SNMPv1/v2c Agent를 UDP 161에 띄우는 샘플입니다.
    /// </summary>
    /// <remarks>
    /// SNMP_HARDWARE_TEST가 정의된 빌드에서만 동작하며, 그 외에는 안내 메시지만 출력하고 대기합니다.
    /// 실행 흐름: 저장된 Wi-Fi로 IPv4 주소 대기(최대 60초) → UDP 161 바인드 → 1초 단위로 요청 처리 반복(거부된 데이터그램 수 집계).
    /// 실제 PLC/Modbus/물리 출력과 연결되지 않는 평가용 장치입니다.
    /// </remarks>
    public static class Program
    {
        public const string Description = "VagabondK.nF SNMP evaluation agent";

        /// <summary>샘플과 테스트가 공유하는 Agent 구성(system group, community)을 만듭니다.</summary>
        /// <param name="store">노출할 객체 저장소입니다.</param>
        /// <returns>구성된 Agent입니다.</returns>
        public static SnmpAgent CreateAgent(ISnmpValueStore store)
        {
            var system = new SnmpSystem(Description, "vagabondk-nf", "operator@example.com", "lab",
                SnmpOid.Parse("1.3.6.1.4.1.32473.2"));
            // 평가용 community입니다. SNMPv1/v2c는 이를 평문으로 보내므로 Agent는 격리된 LAN에서만 운용하십시오.
            return new SnmpAgent(store, system, "public", "private");
        }

        public static void Main()
        {
#if SNMP_HARDWARE_TEST
            long deadline = Environment.TickCount64 + 60000;
            bool ready = false;
            while (!ready && Environment.TickCount64 < deadline)
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string address = network.IPv4Address;
                    if (!string.IsNullOrEmpty(address) && address != "0.0.0.0" && address != "127.0.0.1")
                    {
                        Debug.WriteLine("SNMP MCU IP: " + address);
                        ready = true;
                        break;
                    }
                }
                if (!ready)
                    Thread.Sleep(1000);
            }
            if (!ready)
                throw new TimeoutException("Saved Wi-Fi configuration required.");
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, 161));
                var store = new DemoStore(true);
                var session = new SnmpUdpSession(socket, CreateAgent(store));
                Debug.WriteLine("SNMP MCU READY: UDP161 v1/v2c; no PLC/Modbus/physical outputs.");
                while (true)
                {
                    try
                    {
                        session.ProcessNext(1000);
                    }
                    catch (SnmpProtocolException error)
                    {
                        store.RejectedDatagrams++;
                        Debug.WriteLine("SNMP rejected: " + error.Message);
                    }
                }
            }
#else
            Debug.WriteLine("SNMP hardware disabled. Requires SnmpHardwareTest=true and an isolated LAN.");
            Thread.Sleep(Timeout.Infinite);
#endif
        }
    }
}
