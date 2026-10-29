using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using VagabondK.nF.MMS;

// 독립 libiec61850 peer와 실제 생성 MMS/IEC 61850 매핑 바인딩을 보드에서 맞대는 상호운용 시험입니다.
// UDP 20005 "MIO1" 명령(순번 포함)으로 역할(1 IEC Server, 2 IEC Client, 3 MMS Server, 4 MMS Client)을 순서대로 실행합니다. 격리 시험망 전용입니다.
internal static class Program
{
    private static readonly object gate = new object();
    private static uint status, role, iterations, sessions, faults, reports, requests, writes, heap, baseline, minimum;
    private static string failure;
    private static bool stop;
    private static IPAddress peer;
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
            var request = new byte[16];
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int size = udp.ReceiveFrom(request, ref remote);
                if (size != 11 || request[0] != 77 || request[1] != 73 || request[2] != 79 || request[3] != 49 || request[4] > 2) continue;
                lock (gate)
                {
                    if (request[4] == 1 && (status == 0 || status == 2) && request[5] >= 1 && request[5] <= 4 && request[6] >= 1)
                    {
                        role = request[5]; iterations = request[6]; peer = ((IPEndPoint)remote).Address;
                        sessions = faults = reports = requests = writes = heap = baseline = minimum = 0;
                        InteropStats.Checks = InteropStats.Applied = InteropStats.Rejected = 0;
                        stop = false; status = 1; new Thread(Run).Start();
                    }
                    if (request[4] == 2) stop = true;
                    // 늦게 도착한 이전 응답을 PC가 구분하도록 요청 순번(7..10)을 응답 64..67에 그대로 돌려줍니다.
                    var reply = new byte[68];
                    reply[0] = 77; reply[1] = 73; reply[2] = 79; reply[3] = 49;
                    Write(reply, 4, status); Write(reply, 8, sessions); Write(reply, 12, faults); Write(reply, 16, reports);
                    Write(reply, 20, requests); Write(reply, 24, heap); Write(reply, 28, baseline); Write(reply, 32, minimum);
                    Write(reply, 36, Debugger.IsAttached ? 1U : 0U); Write(reply, 40, 0x600d); Write(reply, 44, role);
                    Write(reply, 48, writes); Write(reply, 52, (uint)InteropStats.Checks);
                    Write(reply, 56, (uint)InteropStats.Rejected); Write(reply, 60, (uint)InteropStats.Applied);
                    Array.Copy(request, 7, reply, 64, 4);
                    if (failure != null)
                    {
                        byte[] text = Encoding.UTF8.GetBytes(failure), all = new byte[68 + text.Length];
                        Array.Copy(reply, all, 68); Array.Copy(text, 0, all, 68, text.Length); reply = all;
                    }
                    if (udp.SendTo(reply, remote) != reply.Length) throw new IOException("Interop diagnostics send failed.");
                }
            }
        }
    }
    private static void Run()
    {
        try
        {
            if (role == 2 || role == 4)
            {
                for (uint n = 0; n < iterations; n++)
                {
                    lock (gate) { if (stop) throw new InvalidOperationException("Interop client stopped."); }
                    if (role == 2) InteropClients.RunIec(peer, InteropNames.PeerPort);
                    else InteropClients.RunMms(peer, InteropNames.PeerPort);
                    Record(false, 2, 0, 0, 0);
                }
                lock (gate) status = 2;
                return;
            }
            var iec = role == 1 ? new InteropIecServer() : null;
            var mms = role == 3 ? new InteropMmsServer() : null;
            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, InteropNames.Port)); listener.Listen(1);
                while (true)
                {
                    lock (gate) { if (stop) { status = 2; return; } }
                    if (!listener.Poll(10000, SelectMode.SelectRead)) continue;
                    bool fault = false;
                    using (Socket socket = listener.Accept())
                    {
                        try { if (iec != null) iec.Serve(socket); else mms.Serve(socket); }
                        catch (IOException error) { Debug.WriteLine("Expected interop transport: " + error.Message); fault = true; }
                        catch (SocketException error) { Debug.WriteLine("Expected interop socket: " + error.ErrorCode); fault = true; }
                        catch (MmsProtocolException error) { Debug.WriteLine("Expected interop frame: " + error.Message); fault = true; }
                    }
                    if (iec != null) Record(fault, 0, iec.Writes, iec.Reports, iec.Requests);
                    else Record(fault, 0, mms.Writes, mms.Reports, mms.Requests);
                }
            }
        }
        catch (Exception error) { lock (gate) { failure = error.ToString() + InteropStats.Describe(); status = 3; } }
    }
    private static void Record(bool fault, uint clientWrites, int serverWrites, int serverReports, int serverRequests)
    {
        lock (gate)
        {
            sessions++;
            if (fault) faults++;
            if (clientWrites != 0) writes += clientWrites;
            else { writes = (uint)serverWrites; reports = (uint)serverReports; requests = (uint)serverRequests; }
            heap = nanoFramework.Runtime.Native.GC.Run(true);
            uint warm = role == 2 || role == 4 ? 2U : 4U;
            if (sessions == warm) baseline = minimum = heap;
            if (sessions > warm && heap < minimum) minimum = heap;
        }
    }
    private static void Write(byte[] b, int p, uint v)
    { b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v; }
}