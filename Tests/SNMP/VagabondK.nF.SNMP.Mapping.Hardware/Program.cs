using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.SNMP;

namespace SnmpScaling
{
    /// <summary>OID 수별 생성 저장소의 메모리·처리 시간을 보드에서 측정하는 펌웨어입니다(PC 쪽은 Mapping.Tests의 <c>--scaling</c>).</summary>
    /// <remarks>
    /// 시작 시 GC 여유 메모리와 Integer32 쓰기·읽기, OctetString 길이 검사 시간을 측정한 뒤 UDP 1161에서 SNMP 요청을 처리합니다.
    /// 측정값 14개는 <c>1.3.6.1.4.1.32473.31.N.0</c>(Gauge32)로 읽고, 13번에 1을 Set하면 다음 루프에서 GC 샘플을 갱신합니다.
    /// </remarks>
    internal static class Program
    {
        private static readonly uint[] Metrics = new uint[14];
        private static MetricStore store;
        private static SnmpAgent agent;
        private static Socket socket;

        public static void Main()
        {
            bool ready = false;
            while (!ready)
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                    if (!string.IsNullOrEmpty(network.IPv4Address) && network.IPv4Address != "0.0.0.0" &&
                        network.IPv4Address != "127.0.0.1") ready = true;
                if (!ready) Thread.Sleep(1000);
            }
            Metrics[0] = (uint)Model.Count;
            Metrics[1] = nanoFramework.Runtime.Native.GC.Run(true);
            var mapped = new Model.SnmpStore(new Model(), new object());
            Metrics[2] = nanoFramework.Runtime.Native.GC.Run(true);
            store = new MetricStore(mapped);
            agent = new SnmpAgent(store, new SnmpSystem("SNMP scaling fixture", "scale"), "public", "private");
            Metrics[3] = nanoFramework.Runtime.Native.GC.Run(true);
            Benchmark(mapped);
            Metrics[4] = nanoFramework.Runtime.Native.GC.Run(true);
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 1161));
            while (true)
            {
                ProcessPacket();
                // 패킷 처리 도우미의 요청/응답 지역 변수가 범위를 벗어난 뒤에만 샘플링합니다.
                if (store.SampleRequested)
                {
                    store.SampleRequested = false;
                    Metrics[10] = nanoFramework.Runtime.Native.GC.Run(true);
                    Metrics[11]++;
                }
            }
        }

        private static void Benchmark(Model.SnmpStore mapped)
        {
            var number = SnmpValue.Integer32(123);
            var text = SnmpValue.OctetString(new byte[512]);
            for (int i = 0; i < 20; i++) mapped.AcceptsWrite(Model.Count - 1, text);
            nanoFramework.Runtime.Native.GC.Run(true);
            long start = Environment.TickCount64;
            for (int round = 0; round < 100; round++)
                for (int i = 0; i < Model.Count - 1; i++)
                {
                    mapped.QueueWrite(i, number);
                    if (mapped.Read(i).Integer != 123) throw new InvalidOperationException("Direct mapping.");
                }
            Metrics[5] = (uint)(Environment.TickCount64 - start);
            start = Environment.TickCount64;
            for (int i = 0; i < 3000; i++)
                if (!mapped.AcceptsWrite(Model.Count - 1, text)) throw new InvalidOperationException("Octet length.");
            Metrics[6] = (uint)(Environment.TickCount64 - start);
            Metrics[7] = 100;
            Metrics[8] = 3000;
        }

        private static void ProcessPacket()
        {
            var buffer = new byte[4096];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int length = socket.ReceiveFrom(buffer, ref remote);
            var request = new byte[length];
            Array.Copy(buffer, request, length);
            byte[] response;
            try { response = agent.Process(request); }
            catch (SnmpProtocolException error)
            {
                Debug.WriteLine("Invalid fixture request: " + error.Message);
                return;
            }
            if (response != null && socket.SendTo(response, remote) != response.Length)
                throw new InvalidOperationException("Incomplete UDP response.");
        }

        // 생성 저장소 뒤에 측정값 OID 14개를 덧붙이는 저장소입니다. 측정 OID 중 쓰기는 GC 샘플 요청(Gauge32 1)만 허용합니다.
        private sealed class MetricStore : ISnmpValueStore
        {
            private readonly Model.SnmpStore mapped;
            private readonly SnmpObject[] objects = new SnmpObject[14];
            internal bool SampleRequested;
            internal MetricStore(Model.SnmpStore mapped)
            {
                this.mapped = mapped;
                for (int i = 0; i < objects.Length; i++)
                    objects[i] = new SnmpObject(new uint[] { 1, 3, 6, 1, 4, 1, 32473, 31, (uint)i + 1, 0 },
                        SnmpType.Gauge32, i == 12);
            }
            public int Count { get { return mapped.Count + objects.Length; } }
            public SnmpObject GetObject(int index)
            {
                return index < mapped.Count ? mapped.GetObject(index) : objects[index - mapped.Count];
            }
            public SnmpValue Read(int index)
            {
                if (index < mapped.Count) return mapped.Read(index);
                Metrics[9] = Debugger.IsAttached ? 1U : 0U;
                Metrics[13] = 0x600D;
                return SnmpValue.Gauge32(Metrics[index - mapped.Count]);
            }
            public bool AcceptsWrite(int index, SnmpValue value)
            {
                return index < mapped.Count ? mapped.AcceptsWrite(index, value) :
                    index == mapped.Count + 12 && value != null && value.Type == SnmpType.Gauge32 && value.Unsigned == 1;
            }
            public void QueueWrite(int index, SnmpValue value)
            {
                if (index < mapped.Count) mapped.QueueWrite(index, value);
                else
                {
                    if (!AcceptsWrite(index, value)) throw new ArgumentException("Invalid metric command.");
                    SampleRequested = true;
                }
            }
        }
    }
}
