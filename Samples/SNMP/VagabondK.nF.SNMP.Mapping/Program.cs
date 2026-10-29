using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.SNMP.UDP;

namespace VagabondK.nF.SNMP.Mapping.Sample
{
    /// <summary>
    /// 소스 생성기가 만든 <c>DeviceData.SnmpStore</c>로 모델 속성을 SNMP 스칼라로 노출하는 샘플입니다.
    /// </summary>
    /// <remarks>
    /// 저장된 Wi-Fi로 IPv4 주소를 얻을 때까지 기다린 뒤 모델·잠금 객체·생성 저장소·Agent를 구성하고 UDP 1161에서 요청을 계속 처리합니다.
    /// UDPHost의 표준 161 포트와 구분해 1161 포트를 사용합니다. 앱도 모델과 배열을 바꿀 때 같은 잠금 객체(<c>sync</c>)를 사용해야 합니다.
    /// </remarks>
    public static class Program
    {
        public static void Main()
        {
            // 보드에 저장된 Wi-Fi 설정만 사용합니다. 자격 증명을 코드에 넣지 마십시오.
            bool ready = false;
            while (!ready)
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string address = network.IPv4Address;
                    if (!string.IsNullOrEmpty(address) && address != "0.0.0.0" && address != "127.0.0.1")
                    {
                        Debug.WriteLine("SNMP mapping IP: " + address);
                        ready = true;
                        break;
                    }
                }
                if (!ready) Thread.Sleep(1000);
            }
            var model = new DeviceData();
            var sync = new object();
            var store = new DeviceData.SnmpStore(model, sync);
            // 평가용 community(public=읽기, private=읽기/쓰기)는 평문으로 전송됩니다. 운영 장비가 아닌 격리된 LAN에서만 사용하십시오.
            var agent = new SnmpAgent(store, new SnmpSystem("SNMP static mapping evaluation", "mapping-node"),
                "public", "private");
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, 1161));
                var session = new SnmpUdpSession(socket, agent);
                Debug.WriteLine("SNMP mapping ready: UDP1161");
                while (true)
                {
                    try { session.ProcessNext(1000); }
                    catch (SnmpProtocolException error) { Debug.WriteLine("Rejected SNMP packet: " + error.Message); }
                }
            }
        }
    }
}
