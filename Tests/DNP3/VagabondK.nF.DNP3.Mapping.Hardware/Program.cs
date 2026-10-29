using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.TCP;

namespace Dnp3Scaling
{
    // 보드에서 모델/Binding/Database의 가용 Heap과 전체·선택 게시 비용을 측정합니다.
    // TCP 20000은 DNP3, UDP 20001은 진단용입니다. 저장된 네트워크와 전용 PC 검증 peer를 준비합니다.
    internal static class Program
    {
        private static readonly uint[] Metrics = new uint[24];
        private static readonly byte[] Request = new byte[6];
        private static readonly byte[] Response = new byte[100];
        private static object sync;
        private static bool collect;
        private static Socket diagnostics;
        private static Model model;
        private static Model.Dnp3Binding mapped;
        private static int published;
        public static void Main()
        {
            bool ready = false;
            long deadline = Environment.TickCount64 + 90000;
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
            Metrics[0] = Model.Count;
            Metrics[1] = Model.Capacity;
            Metrics[2] = nanoFramework.Runtime.Native.GC.Run(true);
            model = new Model();
            Metrics[3] = nanoFramework.Runtime.Native.GC.Run(true);
            sync = new object();
            mapped = new Model.Dnp3Binding(model, sync, Model.Capacity);
            Metrics[4] = nanoFramework.Runtime.Native.GC.Run(true);
            Benchmark();
            Metrics[5] = nanoFramework.Runtime.Native.GC.Run(true);
            var station = new Dnp3Station(mapped.Database, 1, 2, mapped, fragmentSize: 256);
            diagnostics = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            diagnostics.Bind(new IPEndPoint(IPAddress.Any, 20001));
            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, 20000));
                listener.Listen(1);
                while (true)
                {
                    Diagnostic();
                    if (!listener.Poll(10000, SelectMode.SelectRead)) continue;
                    using (Socket connection = listener.Accept())
                    {
                        station.ResetConnection();
                        var session = new Dnp3TcpSession(connection, station, 3000, 3000);
                        try
                        {
                            while (true)
                            {
                                if (model.Generation != published)
                                {
                                    if (!mapped.Publish())
                                    {
                                        Metrics[14]++;
                                        Debug.WriteLine("DNP3 scaling: deliberate event overflow; Static updated.");
                                    }
                                    published = model.Generation;
                                }
                                Diagnostic();
                                session.RunOnce(1);
                            }
                        }
                        catch (IOException error) { Debug.WriteLine("DNP3 disconnected: " + error.Message); }
                        catch (SocketException error) { Debug.WriteLine("DNP3 socket: " + error.Message); }
                        catch (TimeoutException error) { Debug.WriteLine("DNP3 timeout: " + error.Message); }
                        catch (Dnp3ProtocolException error) { Debug.WriteLine("DNP3 protocol: " + error.Message); }
                    }
                }
            }
        }
        private static void Benchmark()
        {
            for (int i = 0; i < 10; i++)
                if (!mapped.Publish()) throw new InvalidOperationException("Unchanged values generated overflow.");
            long start = Environment.TickCount64;
            for (int i = 0; i < 100; i++)
                if (!mapped.Publish()) throw new InvalidOperationException("Unchanged values generated overflow.");
            Metrics[6] = (uint)(Environment.TickCount64 - start);
            Metrics[7] = 100;
            Profile();
            Metrics[11] = 0x600D;
        }
        private static void Profile()
        {
            const int rounds = 1000;
            for (int i = 0; i < 10; i++)
            {
                mapped.Publish(Dnp3PointType.Analog, 0);
                mapped.Publish(Dnp3PointType.Analog, (ushort)(Model.Count - 1));
            }
            long start = Environment.TickCount64;
            for (int i = 0; i < rounds; i++)
                if (!mapped.Publish(Dnp3PointType.Analog, 0)) throw new InvalidOperationException("Selective overflow.");
            Metrics[16] = (uint)(Environment.TickCount64 - start);
            start = Environment.TickCount64;
            for (int i = 0; i < rounds; i++)
                if (!mapped.Publish(Dnp3PointType.Analog, (ushort)(Model.Count - 1))) throw new InvalidOperationException("Selective last overflow.");
            Metrics[17] = (uint)(Environment.TickCount64 - start);
            int sum = 0;
            start = Environment.TickCount64;
            for (int i = 0; i < rounds; i++) { lock (sync) { sum += i; } }
            Metrics[18] = (uint)(Environment.TickCount64 - start);
            if (sum != 499500) throw new InvalidOperationException("Lock benchmark.");
            double number = 0;
            long time = 0;
            start = Environment.TickCount64;
            for (int i = 0; i < rounds; i++) { number = model.P0; time = model.Time; }
            Metrics[19] = (uint)(Environment.TickCount64 - start);
            Dnp3Value value = Dnp3Value.Analog(number, 1, time);
            start = Environment.TickCount64;
            for (int i = 0; i < rounds; i++) value = Dnp3Value.Analog(number, 1, time);
            Metrics[20] = (uint)(Environment.TickCount64 - start);
            start = Environment.TickCount64;
            for (int i = 0; i < rounds; i++)
                if (!mapped.Database.Update(Dnp3PointType.Analog, (ushort)(Model.Count - 1), value))
                    throw new InvalidOperationException("Database benchmark overflow.");
            Metrics[21] = (uint)(Environment.TickCount64 - start);
            sum = 0;
            start = Environment.TickCount64;
            for (int i = 0; i < rounds; i++) sum += i;
            Metrics[22] = (uint)(Environment.TickCount64 - start);
            if (sum != 499500 || number != 0 || time != 0 || value.Raw != 0)
                throw new InvalidOperationException("Profile values.");
            Metrics[23] = rounds;
        }
        private static void Diagnostic()
        {
            if (diagnostics.Poll(0, SelectMode.SelectRead)) Packet();
            if (collect)
            {
                collect = false;
                Metrics[8] = nanoFramework.Runtime.Native.GC.Run(true);
                Metrics[9]++;
            }
        }
        private static void Packet()
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int length = diagnostics.ReceiveFrom(Request, ref remote);
            if (length != 5 || Request[0] != 0x44 || Request[1] != 0x4e || Request[2] != 0x4d || Request[3] != 0x31 || Request[4] > 9)
            {
                Debug.WriteLine("Rejected DNP3 fixture diagnostic packet.");
                return;
            }
            if (Request[4] == 1) collect = true;
            if (Request[4] == 2) mapped.Database.ClearOverflow();
            if (Request[4] >= 3)
            {
                int slot = Request[4] - 3;
                ushort index = slot == 1 || slot == 3 ? (ushort)(Model.Count / 2) :
                    slot == 2 || slot == 4 ? (ushort)(Model.Count - 1) : (ushort)0;
                lock (sync)
                {
                    if (slot == 3) model.Quality = 4;
                    else if (slot == 4) model.ExtraTime = 1000;
                    else model.ChangeLocal(slot);
                    if (!mapped.Publish(Dnp3PointType.Analog, index))
                        throw new InvalidOperationException("Selective robustness overflow.");
                    model.Quality = 1;
                    model.ExtraTime = 0;
                }
            }
            Metrics[10] = Debugger.IsAttached ? 1U : 0U;
            Metrics[12] = (uint)mapped.Database.EventCount;
            Metrics[13] = mapped.Database.Overflow ? 1U : 0U;
            Response[0] = 0x44; Response[1] = 0x4e; Response[2] = 0x4d; Response[3] = 0x31;
            for (int i = 0; i < Metrics.Length; i++)
            {
                uint value = Metrics[i];
                int p = 4 + i * 4;
                Response[p] = (byte)(value >> 24); Response[p + 1] = (byte)(value >> 16);
                Response[p + 2] = (byte)(value >> 8); Response[p + 3] = (byte)value;
            }
            if (diagnostics.SendTo(Response, remote) != Response.Length) throw new IOException("Incomplete diagnostic send.");
        }
    }
}
