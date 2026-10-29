using VagabondK.nF.MMS.Server;
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
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

internal static class Program
{
    private static readonly object gate = new object();
    private static readonly uint[] metrics = new uint[34];
    private static MmsControlRequest enhancedRequest;
    private static readonly MmsServerControl enhanced = new MmsServerControl("NFTESTLD0", "GGIO1$CO$Enhanced",
        MmsControlModel.DirectEnhanced, completionTimeoutMilliseconds: 300);
    private static readonly MmsServerControl sboEnhanced = new MmsServerControl("NFTESTLD0", "GGIO1$CO$SboEnhanced",
        MmsControlModel.SboEnhanced, completionTimeoutMilliseconds: 300);
    private static uint status;
    private static string failure;
    private static Socket listener;
    private static readonly MmsServerControl[] controls = {
        new MmsServerControl("NFTESTLD0", "GGIO1$CO$Direct", MmsControlModel.DirectNormal) { Enabled = true },
        new MmsServerControl("NFTESTLD0", "GGIO1$CO$Selected", MmsControlModel.SboNormal) { Enabled = true }
    };
    private static readonly MmsServerVariable[] model = {
        new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Status$stVal", true),
        new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Count$stVal", -123456),
        new MmsServerVariable("NFTESTLD0", "GGIO1$MX$Analog$mag", 12.5f),
        new MmsServerVariable("NFTESTLD0", "bundle", new MmsServerVariable[] {
            new MmsServerVariable("NFTESTLD0", "flag", true),
            new MmsServerVariable("NFTESTLD0", "samples", new MmsServerVariable[] {
                new MmsServerVariable("NFTESTLD0", "e0", 100),
                new MmsServerVariable("NFTESTLD0", "e1", 101),
                new MmsServerVariable("NFTESTLD0", "e2", 102)
            }, true)
        })
    };
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
        lock (gate) metrics[1]++;
    }
    // 저장된 Wi-Fi 설정 후 TCP 18103과 진단 UDP 20004를 개방하는 보드 시험 진입점입니다.
    // 외부 시험 드라이버가 정상/오류 시나리오를 선택하며 제어는 메모리 모델에만 연결합니다. 격리망에서 실행하십시오.
    public static void Main()
    {
        enhanced.SetHandlers(ValidateEnhanced, ExecuteEnhanced);
        enhanced.Enabled = true;
        sboEnhanced.SetHandlers(ValidateSboEnhanced, ExecuteSboEnhanced);
        sboEnhanced.Enabled = true;
        for (int i = 0; i < controls.Length; i++)
        {
            controls[i].SetHandlers(ValidateControl, ExecuteControl);
            controls[i].ReportControlErrors = true;
            controls[i].Enabled = true;
        }
        model[0].EnableWrite();
        model[1].EnableWrite(ValidateInteger);
        model[2].EnableWrite();
        long deadline = Environment.TickCount64 + 90000;
        bool ready = false;
        while (!ready)
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                if (!string.IsNullOrEmpty(network.IPv4Address) && network.IPv4Address != "0.0.0.0" &&
                    network.IPv4Address != "127.0.0.1") ready = true;
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Saved Wi-Fi required.");
            if (!ready) Thread.Sleep(1000);
        }
        listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Any, 18103));
        listener.Listen(1);
        using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            udp.Bind(new IPEndPoint(IPAddress.Any, 20004));
            byte[] request = new byte[6];
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int count = udp.ReceiveFrom(request, ref remote);
                if (count != 5 || request[0] != 77 || request[1] != 83 || request[2] != 83 ||
                    request[3] != 54 || request[4] > 2)
                {
                    Debug.WriteLine("Rejected MMS Server diagnostic datagram.");
                    continue;
                }
                lock (gate)
                {
                    if (request[4] != 0 && status == 0)
                    {
                        status = 1;
                        new Thread(request[4] == 2 ? new ThreadStart(RunRecovery) : new ThreadStart(Run)).Start();
                    }
                    byte[] reply = new byte[144];
                    reply[0] = 77; reply[1] = 83; reply[2] = 83; reply[3] = 54;
                    Write(reply, 4, status);
                    metrics[16] = Debugger.IsAttached ? 1U : 0U;
                    metrics[19] = 0x600d;
                    for (int i = 0; i < metrics.Length; i++) Write(reply, 8 + i * 4, metrics[i]);
                    if (failure != null)
                    {
                        byte[] text = Encoding.UTF8.GetBytes(failure);
                        byte[] all = new byte[reply.Length + text.Length];
                        Array.Copy(reply, all, reply.Length);
                        Array.Copy(text, 0, all, reply.Length, text.Length);
                        reply = all;
                    }
                    if (udp.SendTo(reply, remote) != reply.Length) throw new IOException("Diagnostic send failed.");
                }
            }
        }
    }
    private static void RunRecovery()
    {
        try
        {
            for (int scenario = 0; scenario < 14; scenario++)
            {
                int sequence = scenario + 1;
                model[0].SetBoolean(sequence % 2 != 0);
                model[1].SetInteger(-123456 + sequence);
                model[2].SetFloatingPoint(12.5f + sequence);
                RecoveryFault(scenario);
                RecoveryStep();
                Process(0);
                lock (gate) metrics[17] = (uint)sequence;
                RecoveryStep();
            }
            Check(metrics[3] == 13 && metrics[13] == 14 && metrics[21] == 29,
                "Recovery faults/associations/executions totals.");
            listener.Close();
            lock (gate) status = 2;
        }
        catch (Exception error)
        {
            listener.Close();
            lock (gate) { failure = error.ToString(); status = 3; }
            Debug.WriteLine("MMS SERVER RECOVERY FAILED: " + failure);
        }
    }
    private static void RecoveryStep()
    {
        lock (gate)
        {
            metrics[0]++;
            metrics[5] = nanoFramework.Runtime.Native.GC.Run(true);
            if (metrics[6] == 0) metrics[6] = metrics[7] = metrics[8] = metrics[5];
            if (metrics[5] < metrics[7]) metrics[7] = metrics[5];
            if (metrics[5] > metrics[8]) metrics[8] = metrics[5];
        }
    }
    private static void RecoveryFault(int scenario)
    {
        uint validations = metrics[20], executions = metrics[21];
        using (var socket = listener.Accept())
        {
            var session = new Iec61850ServerSession(new MmsSocketChannel(socket), model,
                timeoutMilliseconds: 10000, controls: controls);
            bool rejected = false;
            try
            {
                session.Associate();
                Check(scenario >= 7, "Malformed association was accepted.");
                if (scenario == 13)
                {
                    Check(session.ProcessRead() && metrics[20] == validations && metrics[21] == executions,
                        "Control model query must not execute a command.");
                    Check(session.ProcessRead(), "Lost response command was not processed.");
                    Check(!session.ProcessRead() && !session.Faulted && !session.Associated, "Lost response peer EOF.");
                }
                else
                    session.ProcessRead();
            }
            catch (MmsProtocolException) { Check(scenario != 4 && scenario != 10 && scenario != 13, "Unexpected protocol fault."); rejected = true; }
            catch (IOException) { Check(scenario == 4 || scenario == 10, "Unexpected I/O fault."); rejected = true; }
            if (scenario != 13)
            {
                Check(rejected && session.Faulted && !session.Associated, "Malformed input must fault session.");
                bool refused = false;
                try { session.ProcessRead(); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "Faulted session must not accept further requests.");
                lock (gate) metrics[3]++;
            }
            else
                Check(!rejected && metrics[20] == validations + 1 && metrics[21] == executions + 1 &&
                    controls[0].Value, "One executed command despite lost response.");
            if (scenario != 13)
                Check(metrics[20] == validations && metrics[21] == executions, "Corrupt command reached handlers.");
            session.Close();
        }
    }
    private static void Run()
    {
        try
        {
            int sequence = 0;
            for (int step = 0; step < 15; step++)
            {
                int mode = step >= 12 ? 7 : step == 11 ? 6 : step == 10 ? 5 : step == 9 ? 4 : step == 8 ? 3 :
                    step == 2 || step == 5 ? 1 : step == 3 || step == 6 ? 2 : 0;
                if (mode == 0)
                {
                    sequence++;
                    model[0].SetBoolean(sequence % 2 != 0);
                    model[1].SetInteger(-123456 + sequence);
                    model[2].SetFloatingPoint(12.5f + sequence);
                }
                Process(mode);
                lock (gate)
                {
                    metrics[0]++;
                    metrics[17] = (uint)sequence;
                    metrics[5] = nanoFramework.Runtime.Native.GC.Run(true);
                    if (metrics[6] == 0) metrics[6] = metrics[7] = metrics[8] = metrics[5];
                    if (metrics[5] < metrics[7]) metrics[7] = metrics[5];
                    if (metrics[5] > metrics[8]) metrics[8] = metrics[5];
                }
            }
            Check(metrics[20] == 524 && metrics[21] == 522 && metrics[22] == 2, "Control callback totals.");
            Check(metrics[26] == 4 && metrics[27] == 3 && metrics[28] == 4, "Enhanced callback/completion totals.");
            Check(metrics[29] == 522 && metrics[30] == 259 && metrics[31] == 260, "SBO enhanced callback/completion totals.");
            Check(metrics[32] == 27 && metrics[33] == 3, "Data/report totals.");
            listener.Close();
            lock (gate) status = 2;
        }
        catch (Exception error)
        {
            listener.Close();
            lock (gate)
            {
                failure = error.ToString();
                Debug.WriteLine("MMS SERVER FAILED: " + failure);
                status = 3;
            }
        }
    }
    private static void Process(int mode)
    {
        using (Socket socket = listener.Accept())
        {
            uint before = nanoFramework.Runtime.Native.GC.Run(true);
            var channel = new MeasuredChannel(new MmsSocketChannel(socket));
            MmsServerVariable[] dataModel = null;
            MmsServerDataSet dataSet = null;
            MmsServerReportControl report = null;
            if (mode == 7)
            {
                dataModel = new MmsServerVariable[] {
                    new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Status$stVal", true),
                    new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Count$stVal", -123456),
                    new MmsServerVariable("NFTESTLD0", "GGIO1$MX$Analog$mag", 12.5f),
                    Iec61850ServerData.CreateQuality("NFTESTLD0", "GGIO1$ST$Status$q", IecQuality.Invalid | IecQuality.Test | IecQuality.OldData),
                    Iec61850ServerData.CreateTimestamp("NFTESTLD0", "GGIO1$ST$Status$t", 1700000000125, 0x3f)
                };
                dataSet = new MmsServerDataSet("NFTESTLD0", "GGIO1$Measurements", new MmsServerVariable[] { dataModel[0], dataModel[1], dataModel[2] });
                report = new MmsServerReportControl("NFTESTLD0", "GGIO1$RP$Events", "NFREPORT", dataSet);
            }
            var session = new Iec61850ServerSession(channel, mode == 7 ? dataModel : model,
                timeoutMilliseconds: mode == 2 ? 3000 : 15000, maximumNamesPerPage: 1,
                controls: mode == 7 ? null : mode >= 4 ? new MmsServerControl[] { mode == 4 ? enhanced : sboEnhanced } : controls,
                dataSets: mode == 7 ? new MmsServerDataSet[] { dataSet } : null,
                reports: mode == 7 ? new MmsServerReportControl[] { report } : null);
            uint after = nanoFramework.Runtime.Native.GC.Run(true);
            Check(after <= before, "Session allocation measurement.");
            lock (gate) metrics[9] = before - after;
            long started = Environment.TickCount64;
            bool rejected = false, timedOut = false;
            try { session.Associate(); }
            catch (MmsProtocolException)
            {
                if (mode != 1) throw;
                rejected = true;
            }
            catch (TimeoutException error)
            {
                if (mode != 2) throw;
                const string prefix = "MMS socket readiness deadline expired; waited=";
                Check(error.Message.IndexOf(prefix) == 0, "Expected readiness timeout.");
                uint waited = (uint)int.Parse(error.Message.Substring(prefix.Length));
                Check(waited >= 2950 && waited < 3500, "Three-second readiness deadline.");
                lock (gate) metrics[14] = waited;
                timedOut = true;
            }
            if (mode == 1 || mode == 2)
            {
                Check((mode == 1 ? rejected : timedOut) && session.Faulted && !session.Associated, "Faulted connection.");
                lock (gate)
                {
                    if (mode == 1) metrics[3]++;
                    else { metrics[4]++; metrics[15] = (uint)(Environment.TickCount64 - started); }
                }
                return;
            }
            Check(session.Associated && !session.Faulted && session.NegotiatedPduSize == 4096, "Association limits.");
            lock (gate) metrics[10] = (uint)(Environment.TickCount64 - started);
            int count = 0;
            while (true)
            {
                if (mode >= 4)
                {
                    if (mode == 7)
                    {
                        int sent = session.ProcessReports();
                        lock (gate) metrics[33] += (uint)sent;
                    }
                    if (enhancedRequest != null)
                    {
                        Thread.Sleep(50);
                        if (enhancedRequest.OriginCategory != 7)
                            (mode == 4 ? enhanced : sboEnhanced).Complete(enhancedRequest, enhancedRequest.OriginCategory != 8,
                                enhancedRequest.OriginCategory == 8 ? 9 : 0);
                        enhancedRequest = null;
                    }
                    if (session.ProcessControlCompletion()) { lock (gate) metrics[mode == 4 ? 28 : 31]++; }
                    if (!socket.Poll(1000, SelectMode.SelectRead)) { Thread.Sleep(1); continue; }
                }
                long readStarted = Environment.TickCount64;
                long ioStarted = channel.IoMilliseconds;
                if (!session.ProcessRead()) break;
                uint elapsed = (uint)(Environment.TickCount64 - readStarted);
                uint io = (uint)(channel.IoMilliseconds - ioStarted);
                Check(elapsed >= io, "Processing timing bounds.");
                uint processing = elapsed - io;
                count++;
                lock (gate)
                {
                    metrics[2]++;
                    if (mode == 7) metrics[32]++;
                    metrics[11] += elapsed;
                    if (elapsed > metrics[12]) metrics[12] = elapsed;
                    metrics[23] += processing;
                    if (processing > metrics[24]) metrics[24] = processing;
                    metrics[25] += io;
                }
            }
            Check(count == (mode == 7 ? 9 : mode == 6 ? 770 : mode == 5 ? 18 : mode == 4 ? 10 : mode == 3 ? 1294 : 96) &&
                !session.Associated && !session.Faulted,
                "Control/write/composite/discovery requests and orderly EOF.");
            Check(controls[0].Value && controls[1].Value, "Memory controls applied.");
            if (mode == 4) Check(enhanced.Value && !enhanced.HasPendingOperation && !session.HasPendingControl, "Enhanced final state.");
            if (mode == 5 || mode == 6) Check(sboEnhanced.Value && !sboEnhanced.HasPendingOperation && !session.HasPendingControl, "SBO enhanced final state.");
            lock (gate) metrics[13]++;
        }
    }
    private static int ValidateInteger(MmsValue value)
    { return value.Integer < -1000000 || value.Integer > 1000000 ? 11 : -1; }
    private static int ValidateEnhanced(MmsControlRequest request)
    { lock (gate) metrics[26]++; return -1; }
    private static int ExecuteEnhanced(MmsControlRequest request)
    {
        Check(!request.Test, "Enhanced Test must not execute.");
        enhancedRequest = request;
        lock (gate) metrics[27]++;
        return -1;
    }
    private static int ValidateSboEnhanced(MmsControlRequest request)
    { lock (gate) metrics[29]++; return -1; }
    private static int ExecuteSboEnhanced(MmsControlRequest request)
    {
        Check(!request.Test, "SBO enhanced Test must not execute.");
        enhancedRequest = request;
        lock (gate) metrics[30]++;
        return -1;
    }
    private static int ValidateControl(MmsControlRequest request)
    {
        lock (gate) metrics[20]++;
        return -1;
    }
    private static int ExecuteControl(MmsControlRequest request)
    {
        Check(!request.Test, "Test cannot reach execution handler.");
        lock (gate)
        {
            metrics[21]++;
            if (request.OriginCategory == 8) { metrics[22]++; return 3; }
        }
        return -1;
    }
    private sealed class MeasuredChannel : IMmsChannel
    {
        private readonly IMmsChannel inner;
        internal long IoMilliseconds;
        internal MeasuredChannel(IMmsChannel inner) { this.inner = inner; }
        public long Milliseconds { get { return inner.Milliseconds; } }
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            long started = Environment.TickCount64;
            try { return inner.Read(buffer, offset, count, timeoutMilliseconds); }
            finally { IoMilliseconds += Environment.TickCount64 - started; }
        }
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            long started = Environment.TickCount64;
            try { return inner.Write(buffer, offset, count, timeoutMilliseconds); }
            finally { IoMilliseconds += Environment.TickCount64 - started; }
        }
    }
    private static void Write(byte[] b, int p, uint value)
    {
        b[p] = (byte)(value >> 24); b[p + 1] = (byte)(value >> 16);
        b[p + 2] = (byte)(value >> 8); b[p + 3] = (byte)value;
    }
}
