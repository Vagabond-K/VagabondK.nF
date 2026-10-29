using VagabondK.nF.MMS;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.MMS.TCP;

/// <summary>지정 시간 동안 보고를 반복 수신하고 세션/요청 수를 확인하는 외부 호스트 및 로컬 루프백 시험입니다.</summary>
internal static class ReportSoakTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("Report soak: " + message); }
    internal static void Run(string host, int port, int seconds)
    {
        if (seconds < 10 || seconds > 86400) throw new ArgumentOutOfRangeException("seconds");
        long started = Environment.TickCount64, deadline = started + seconds * 1000L;
        int sessions = 0, total = 0, gi = 0, integrity = 0, change = 0, quality = 0, wraps = 0, malformed = 0;
        while (Environment.TickCount64 < deadline || sessions < 6)
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Connect(IPAddress.Parse(host), port);
                var client = new Iec61850Client(new MmsSocketChannel(socket), timeoutMilliseconds: 5000, maximumTpduSize: 128);
                client.Associate();
                var set = client.GetDataSetDirectory(ReportSoakModel.Domain, ReportSoakModel.SetName);
                int received = 0, expectedSequence = 0;
                var rcb = new MmsReportClient(client, ReportSoakModel.Domain, ReportSoakModel.ReportName, set, report => {
                    Check(report.Count == 3 && report.SequenceNumber == (byte)expectedSequence, "sequence/count");
                    expectedSequence = (expectedSequence + 1) & 255;
                    if (expectedSequence == 0) wraps++;
                    Check(report.GetValue(0) != null && report.GetValue(1) != null && report.GetValue(2) != null, "coherent three-field inclusion");
                    long value = report.GetValue(0).Integer;
                    Check(value >= 0 && Iec61850Data.ReadQuality(report.GetValue(1)) ==
                        (value % 2 == 0 ? IecQuality.Good : IecQuality.Test), "quality matches count");
                    Check(Iec61850Data.ReadTimestampMilliseconds(report.GetValue(2)) == 1700000000000L + value * 250L, "timestamp matches count");
                    byte reason = report.GetReason(0);
                    if (reason == 4) { Check(report.GetReason(1) == 4 && report.GetReason(2) == 4, "GI causes"); gi++; }
                    else if (reason == 8) { Check(report.GetReason(1) == 8 && report.GetReason(2) == 8, "integrity causes"); integrity++; }
                    else { Check(reason == 64 && report.GetReason(1) == 32 && report.GetReason(2) == 64, "change causes"); change++; quality++; }
                    received++; total++;
                });
                rcb.Reserve(true); rcb.Enable(); rcb.GeneralInterrogation();
                long connectionDeadline = Environment.TickCount64 + 90000;
                int target = sessions == 0 ? 270 : 20;
                while (received < target && Environment.TickCount64 < connectionDeadline)
                {
                    client.ReceiveReport();
                    if (received % 7 == 0) client.Read(ReportSoakModel.Domain, "GGIO1$ST$Count$stVal");
                    if (sessions == 0 && received >= 20 && received < target) rcb.GeneralInterrogation();
                }
                Check(received >= target && !client.Faulted, "connection target");
                if (sessions % 3 == 0) { rcb.Disable(); rcb.Reserve(false); client.Release(); }
                else if (sessions % 3 == 1) socket.LingerState = new LingerOption(true, 0);
                // 나머지 모드는 예약·활성화된 RCB를 비활성화하지 않고 연결을 닫습니다.
            }
            sessions++;
            if (sessions % 3 == 0)
            {
                using (var partial = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    partial.Connect(IPAddress.Parse(host), port);
                    byte[] prefix = { 3, 0, 0, 22, 17, 0xe0, 0 };
                    int sent = 0;
                    while (sent < prefix.Length) sent += partial.Send(prefix, sent, prefix.Length - sent, SocketFlags.None);
                }
                malformed++;
            }
            Console.WriteLine("REPORT SOAK session=" + sessions + " reports=" + total + " GI=" + gi + " integrity=" + integrity +
                " changes=" + change + " qchg=" + quality + " wraps=" + wraps + " partial=" + malformed +
                " elapsedMs=" + (Environment.TickCount64 - started));
        }
        Check(wraps > 0 && gi >= sessions && integrity > 0 && change > 0 && quality > 0 && malformed > 0, "coverage");
        Console.WriteLine("REPORT SOAK PASS sessions=" + sessions + " reports=" + total + " elapsedMs=" + (Environment.TickCount64 - started));
    }
    internal static void Loopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        bool stop = false; int transportFaults = 0;
        var model = new ReportSoakModel();
        Task worker = Task.Run(() => {
            while (!Volatile.Read(ref stop))
            {
                if (!listener.Pending()) { Thread.Sleep(1); continue; }
                using (var socket = listener.AcceptSocket())
                {
                    try { model.Serve(socket); }
                    catch (IOException error) { Console.WriteLine("SOAK expected transport: " + error.Message); transportFaults++; }
                    catch (SocketException error) { Console.WriteLine("SOAK expected socket: " + error.SocketErrorCode); transportFaults++; }
                    catch (MmsProtocolException error) { Console.WriteLine("SOAK expected partial frame: " + error.Message); transportFaults++; }
                }
            }
        });
        try { Run("127.0.0.1", port, 10); Thread.Sleep(100); }
        finally
        {
            Volatile.Write(ref stop, true);
            try { worker.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            finally { listener.Stop(); }
        }
        Check(transportFaults >= 4, "reset/partial fault paths");
    }
}
