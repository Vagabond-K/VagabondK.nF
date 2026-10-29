using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using VagabondK.nF.IEC61850;

internal static class Program
{
    private static readonly object gate = new object();
#if MAPPING_CLIENT
    private static readonly MappingClientModel model = new MappingClientModel();
#elif MAPPING
    private static readonly MappingServerModel model = new MappingServerModel();
#elif TYPED_CLIENT
    private static readonly TypedControlClientModel model = new TypedControlClientModel();
#elif TYPED
    private static readonly TypedControlModel model = new TypedControlModel();
#else
    private static readonly ReportSoakModel model = new ReportSoakModel();
#endif
    private static uint status, sessions, faults, heap, baseline, minimum;
    private static string failure;
    private static bool stop;
    // 저장된 Wi-Fi와 전용 데스크톱 peer가 필요한 장시간 보고·형식별 제어·매핑 시험입니다.
    // 빌드 심볼에 따라 모델을 선택하며 UDP 20005로 시작·중지·힙 진단을 제공합니다. 격리망에서 실행하십시오.
    public static void Main()
    {
        long deadline = Environment.TickCount64 + 90000;
        bool ready = false;
        while (!ready)
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                if (!string.IsNullOrEmpty(network.IPv4Address) && network.IPv4Address != "0.0.0.0" && network.IPv4Address != "127.0.0.1") ready = true;
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Saved Wi-Fi required.");
            if (!ready) Thread.Sleep(1000);
        }
        using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            udp.Bind(new IPEndPoint(IPAddress.Any, 20005));
            var request = new byte[6];
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int size = udp.ReceiveFrom(request, ref remote);
                if (size != 5 || request[0] != 77 || request[1] != 82 || request[2] != 83 || request[3] != 49 || request[4] > 2) continue;
                lock (gate)
                {
                    if (request[4] == 1 && status == 0)
                    {
#if TYPED_CLIENT || MAPPING_CLIENT
                        model.Peer = ((IPEndPoint)remote).Address;
#endif
                        status = 1; new Thread(Run).Start();
                    }
                    if (request[4] == 2) stop = true;
#if MAPPING || MAPPING_CLIENT
                    var reply = new byte[72];
#else
                    var reply = new byte[48];
#endif
                    reply[0] = 77; reply[1] = 82; reply[2] = 83; reply[3] = 49;
                    Write(reply, 4, status); Write(reply, 8, sessions); Write(reply, 12, faults);
                    Write(reply, 16, (uint)model.Reports); Write(reply, 20, (uint)model.Requests);
                    Write(reply, 24, heap); Write(reply, 28, baseline); Write(reply, 32, minimum);
                    Write(reply, 36, Debugger.IsAttached ? 1U : 0U); Write(reply, 40, 0x600d);
#if MAPPING || MAPPING_CLIENT
                    Write(reply, 44, MappingBenchmarks.Iterations); Write(reply, 48, MappingBenchmarks.IdleMilliseconds);
                    Write(reply, 52, MappingBenchmarks.ChangedMilliseconds); Write(reply, 56, MappingBenchmarks.ApplyMilliseconds);
                    Write(reply, 60, MappingBenchmarks.HeapBefore); Write(reply, 64, MappingBenchmarks.HeapAfter);
#endif
                    if (failure != null)
                    {
                        byte[] text = Encoding.UTF8.GetBytes(failure), all = new byte[48 + text.Length];
                        Array.Copy(reply, all, 48); Array.Copy(text, 0, all, 48, text.Length); reply = all;
                    }
                    if (udp.SendTo(reply, remote) != reply.Length) throw new IOException("Soak diagnostics send failed.");
                }
            }
        }
    }
    private static void Run()
    {
        try
        {
#if TYPED_CLIENT || MAPPING_CLIENT
#if MAPPING_CLIENT
            for (int i = 0; i < 16; i++)
#else
            for (int i = 0; i < 8; i++)
#endif
            {
                lock (gate) { if (stop) throw new InvalidOperationException("Typed Client test stopped."); }
                model.Serve(); Record(false);
            }
            lock (gate) status = 2;
#else
            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, 18104)); listener.Listen(1);
                while (true)
                {
                    lock (gate) { if (stop) { status = 2; return; } }
                    if (!listener.Poll(10000, SelectMode.SelectRead)) continue;
                    bool fault = false;
                    using (Socket socket = listener.Accept())
                    {
                        try { model.Serve(socket); }
                        catch (IOException error) { Debug.WriteLine("Expected soak transport: " + error.Message); fault = true; }
                        catch (SocketException error) { Debug.WriteLine("Expected soak socket: " + error.ErrorCode); fault = true; }
                        catch (MmsProtocolException error) { Debug.WriteLine("Expected soak partial frame: " + error.Message); fault = true; }
                    }
                    Record(fault);
                }
            }
#endif
        }
        catch (Exception error) { lock (gate) { failure = error.ToString(); status = 3; } }
    }
    private static void Record(bool fault)
    {
        lock (gate)
        {
            sessions++;
            if (fault) faults++;
            heap = nanoFramework.Runtime.Native.GC.Run(true);
            if (sessions == 6) baseline = minimum = heap;
            if (sessions > 6 && heap < minimum) minimum = heap;
        }
    }
    private static void Write(byte[] b, int p, uint v)
    { b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v; }
}
