using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.BACnet.IP;

namespace VagabondK.nF.BACnet.Mapping.Sample
{
    /// <summary>
    /// 소스 생성기가 만든 <c>DeviceData.BacnetStore</c>로 모델 속성을 BACnet AV/BV 객체로 노출하는 샘플입니다.
    /// </summary>
    /// <remarks>
    /// 저장된 Wi-Fi로 IPv4 주소를 얻을 때까지 기다린 뒤 모델·잠금 객체·생성 저장소를 구성하고 UDP 47808에서 요청을 계속 처리합니다.
    /// 앱 코드가 모델을 바꿀 때도 저장소에 넘긴 같은 잠금 객체(<c>sync</c>)를 사용해야 합니다. 인증이 없으므로 격리된 LAN에서만 실행하십시오.
    /// </remarks>
    public static class Program
    {
        public static void Main()
        {
            bool ready = false;
            while (!ready)
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string address = network.IPv4Address;
                    if (!string.IsNullOrEmpty(address) && address != "0.0.0.0" && address != "127.0.0.1")
                    {
                        Debug.WriteLine("BACnet mapping IP: " + address);
                        ready = true;
                        break;
                    }
                }
                if (!ready) Thread.Sleep(1000);
            }
            var model = new DeviceData();
            var sync = new object();
            var store = new DeviceData.BacnetStore(model, sync);
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, 47808));
                var session = new BacnetIpSession(socket, new BacnetDevice(store, 12345, 0, "BACnet mapping evaluation"));
                Debug.WriteLine("BACnet mapping ready: UDP47808; isolated LAN only.");
                while (true)
                {
                    try { session.ProcessNext(1000); }
                    catch (BacnetProtocolException error) { Debug.WriteLine("Rejected BACnet packet: " + error.Message); }
                }
            }
        }
    }
}
