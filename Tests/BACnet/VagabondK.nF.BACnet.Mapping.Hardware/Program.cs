using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.BACnet;
using VagabondK.nF.BACnet.IP;

namespace BacnetScaling
{
    /// <summary>객체 수별 생성 저장소의 메모리·처리 시간을 보드에서 측정하는 펌웨어입니다(PC 쪽은 Mapping.Tests의 <c>--scaling</c>).</summary>
    /// <remarks>
    /// 시작 시 GC 여유 메모리와 AV/BV 쓰기·읽기 왕복 시간을 측정한 뒤, UDP 47808에서 BACnet 요청을 처리하고
    /// UDP 47809에서 "BNS1"+명령(0=조회, 1=다음 루프에서 GC 샘플, 2/3/4=첫/중간/마지막 AV/BV 로컬 변경) 진단 요청에 측정값 14개(빅엔디언 uint32)로 응답합니다.
    /// </remarks>
    internal static class Program
    {
        private static readonly uint[] Metrics = new uint[14];
        private static readonly byte[] DiagnosticRequest = new byte[6];
        private static readonly byte[] DiagnosticResponse = new byte[60];
        private static bool sampleRequested;
        private static Socket diagnostics;
        private static Model.BacnetStore mapped;
        private static Model model;
        private static BacnetDevice device;

        public static void Main()
        {
            bool ready = false;
            while (!ready)
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string ip = network.IPv4Address;
                    if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0" && ip != "127.0.0.1") ready = true;
                }
                if (!ready) Thread.Sleep(1000);
            }
            Metrics[0] = Model.Count;
            Metrics[1] = nanoFramework.Runtime.Native.GC.Run(true);
            model = new Model();
            mapped = new Model.BacnetStore(model, new object());
            Metrics[2] = nanoFramework.Runtime.Native.GC.Run(true);
            device = new BacnetDevice(mapped, 12345, 0, "BACnet scaling evaluation");
            Metrics[3] = nanoFramework.Runtime.Native.GC.Run(true);
            Benchmark();
            Metrics[4] = nanoFramework.Runtime.Native.GC.Run(true);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 47808));
            diagnostics = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            diagnostics.Bind(new IPEndPoint(IPAddress.Any, 47809));
            var session = new BacnetIpSession(socket, device);
            while (true)
            {
                try { session.ProcessNext(1); }
                catch (BacnetProtocolException error) { Debug.WriteLine("Invalid BACnet packet: " + error.Message); }
                if (diagnostics.Poll(0, SelectMode.SelectRead)) DiagnosticPacket();
                if (sampleRequested)
                {
                    sampleRequested = false;
                    Metrics[10] = nanoFramework.Runtime.Native.GC.Run(true);
                    Metrics[11]++;
                }
            }
        }
        private static void Benchmark()
        {
            for (int i = 0; i < Model.Count; i++) mapped.Read(i);
            nanoFramework.Runtime.Native.GC.Run(true);
            long start = Environment.TickCount64;
            for (int round = 0; round < 100; round++)
                for (int i = 0; i < Model.Count / 2; i++)
                {
                    mapped.QueueWrite(i, 0x80000001U);
                    if (mapped.Read(i).Bits != 0x80000001U) throw new InvalidOperationException("Analog roundtrip.");
                }
            Metrics[5] = (uint)(Environment.TickCount64 - start);
            start = Environment.TickCount64;
            for (int round = 0; round < 100; round++)
                for (int i = Model.Count / 2; i < Model.Count; i++)
                {
                    mapped.QueueWrite(i, 1);
                    if (mapped.Read(i).Bits != 1) throw new InvalidOperationException("Binary roundtrip.");
                }
            Metrics[6] = (uint)(Environment.TickCount64 - start);
            Metrics[7] = 100;
            Metrics[13] = 0x600D;
        }
        private static void DiagnosticPacket()
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int count = diagnostics.ReceiveFrom(DiagnosticRequest, ref remote);
            if (count != 5 || DiagnosticRequest[0] != 0x42 || DiagnosticRequest[1] != 0x4e ||
                DiagnosticRequest[2] != 0x53 || DiagnosticRequest[3] != 0x31 || DiagnosticRequest[4] > 4)
            {
                Debug.WriteLine("Rejected BACnet fixture diagnostic datagram.");
                return;
            }
            sampleRequested = DiagnosticRequest[4] == 1;
            if (DiagnosticRequest[4] >= 2) model.ChangeLocal(DiagnosticRequest[4] - 2);
            Metrics[9] = Debugger.IsAttached ? 1U : 0U;
            DiagnosticResponse[0] = 0x42; DiagnosticResponse[1] = 0x4e;
            DiagnosticResponse[2] = 0x53; DiagnosticResponse[3] = 0x31;
            for (int i = 0; i < Metrics.Length; i++)
            {
                uint value = Metrics[i];
                int p = 4 + i * 4;
                DiagnosticResponse[p] = (byte)(value >> 24);
                DiagnosticResponse[p + 1] = (byte)(value >> 16);
                DiagnosticResponse[p + 2] = (byte)(value >> 8);
                DiagnosticResponse[p + 3] = (byte)value;
            }
            if (diagnostics.SendTo(DiagnosticResponse, remote) != DiagnosticResponse.Length)
                throw new InvalidOperationException("Incomplete diagnostic send.");
        }
    }
}
