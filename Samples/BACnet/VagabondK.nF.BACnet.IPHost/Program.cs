using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

using VagabondK.nF.BACnet.IP;

namespace VagabondK.nF.BACnet.IPHost
{
    /// <summary>
    /// 직접 구현한 <see cref="IBacnetValueStore"/>로 BACnet/IP 장치(Device 12345)를 UDP 47808에 띄우는 샘플입니다.
    /// </summary>
    /// <remarks>
    /// BACNET_HARDWARE_TEST가 정의된 빌드에서만 동작하며, 그 외에는 안내 메시지만 출력하고 대기합니다.
    /// 실행 흐름: 저장된 Wi-Fi로 IPv4 주소 획득 대기(최대 60초) → UDP 47808 바인드 → 1초 단위로 요청 처리 반복.
    /// 평가 전용이며 인증이 없으므로 격리된 LAN에서만 실행하십시오. 실제 PLC/Modbus/물리 출력과 연결되지 않습니다.
    /// </remarks>
    public static class Program
    {
        // 평가용 고정 객체: AV0(읽기 전용, 250.0), AV100(쓰기 가능, 800.0), BV0(쓰기 가능, 1). 값은 float32 원시 비트로 보관합니다.
        private sealed class Values : IBacnetValueStore
        {
            private readonly BacnetObject[] _objects = {
                new BacnetObject(0,false), new BacnetObject(100,false,true), new BacnetObject(0,true,true)
            };
            private readonly uint[] _bits = { 0x437a0000, 0x44480000, 1 };

            public int Count
            {
                get
                {
                    return _objects.Length;
                }
            }

            public BacnetObject GetObject(int index)
            {
                return _objects[index];
            }

            public BacnetSample Read(int index)
            {
                return new BacnetSample(_bits[index], true);
            }

            public void QueueWrite(int index, uint bits)
            {
                if (!_objects[index].Writable)
                    throw new InvalidOperationException("Read-only test point.");
                _bits[index] = bits;
            }
        }

        public static void Main()
        {
#if BACNET_HARDWARE_TEST
            long deadline = Environment.TickCount64 + 60000;
            bool ready = false;
            while (!ready && Environment.TickCount64 < deadline)
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string address = network.IPv4Address;
                    if (!string.IsNullOrEmpty(address) && address != "0.0.0.0" && address != "127.0.0.1")
                    {
                        Debug.WriteLine("BACNET MCU IP: " + address);
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
                socket.Bind(new IPEndPoint(IPAddress.Any, 47808));
                var session = new BacnetIpSession(socket, new BacnetDevice(new Values(), 12345, 0, "VagabondK.nF evaluation"));
                Debug.WriteLine("BACNET MCU READY: UDP47808; no PLC/Modbus/physical outputs.");
                while (true)
                {
                    try
                    {
                        session.ProcessNext(1000);
                    }
                    // 잘못된 데이터그램은 기록만 하고 다음 요청을 계속 처리합니다.
                    catch (BacnetProtocolException error)
                    {
                        Debug.WriteLine("BACnet rejected: " + error.Message);
                    }
                }
            }
#else
            Debug.WriteLine("BACnet hardware disabled. Requires BacnetHardwareTest=true and an isolated LAN.");
            Thread.Sleep(Timeout.Infinite);
#endif
        }
    }
}
