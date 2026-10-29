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
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.MMS.TCP;

internal static class Program
{
    private static readonly uint[] metrics = new uint[48];
    private static readonly byte[] reply = new byte[208];
    private static uint sequence, status = 2;
    private static byte command;
    private static string failure;
    private static Socket faultSocket;
    private static Iec61850Client faultClient;
    private static string serverAddress;
    private static int serverPort;
    private static bool useTryRead;
    private static int lastAccessError;

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
        metrics[1]++;
    }

    // 저장된 Wi-Fi 설정을 최대 90초 기다린 뒤 UDP 20003의 시험 명령으로 전용 peer에 연결합니다.
    // 명령 순번과 endpoint 고정은 시험 중 중복 방지용일 뿐 인증이 아닙니다. 격리된 보드 시험망에서만 실행합니다.
    public static void Main()
    {
        long deadline = Environment.TickCount64 + 90000;
        bool ready = false;
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
        using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            udp.Bind(new IPEndPoint(IPAddress.Any, 20003));
            var request = new byte[17];
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int count = udp.ReceiveFrom(request, ref remote);
                if (count != 15 || request[0] != 77 || request[1] != 77 || request[2] != 72 || request[3] != 58)
                {
                    Debug.WriteLine("Rejected MMS hardware control datagram.");
                    continue;
                }
                uint incoming = Read(request, 4);
                if (incoming == sequence + 1 && failure == null)
                {
                    sequence = incoming;
                    try
                    {
                        string address = request[9] + "." + request[10] + "." + request[11] + "." + request[12];
                        int port = (request[13] << 8) | request[14];
                        if (serverAddress == null) { serverAddress = address; serverPort = port; }
                        Check(address == serverAddress && port == serverPort && port >= 1024, "Host endpoint changed.");
                        command = request[8];
                        Execute(command, udp, remote);
                        status = 2;
                    }
                    catch (Exception error)
                    {
                        failure = "command " + request[8] + ": " + error +
                            (error is SocketException ? " socket code=" + ((SocketException)error).ErrorCode : "");
                        Debug.WriteLine("MMS HARDWARE FAILED: " + failure);
                        status = 3;
                    }
                }
                else if (incoming != sequence) Debug.WriteLine("Rejected stale MMS hardware command.");
                Send(udp, remote);
            }
        }
    }

    private static Socket Connect()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        long started = Environment.TickCount64;
        try
        {
            socket.Connect(new IPEndPoint(IPAddress.Parse(serverAddress), serverPort));
            return socket;
        }
        catch (SocketException error)
        {
            socket.Close();
            throw new IOException("TCP connect failed: elapsed=" + (Environment.TickCount64 - started) + ", code=" + error.ErrorCode);
        }
        catch { socket.Close(); throw; }
    }

    private static void Cycle(bool withoutAccessExceptions)
    {
        useTryRead = withoutAccessExceptions;
        long started = Environment.TickCount64;
        using (var socket = Connect())
        {
            uint before = nanoFramework.Runtime.Native.GC.Run(true);
            var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
            uint after = nanoFramework.Runtime.Native.GC.Run(true);
            Check(after <= before, "Client allocation measurement.");
            metrics[14] = before - after;
            long associationStarted = Environment.TickCount64;
            client.Associate();
            metrics[16] = (uint)(Environment.TickCount64 - associationStarted);
            Check(client.Associated && !client.Faulted && client.NegotiatedPduSize == 4096, "Association/PDU.");
            Discover(client);
            Composite(client);
            WriteMemory(client);
            for (int round = 0; round < 20; round++)
            {
                MmsValue value = ReadValue(client, "GGIO1$ST$Status$stVal");
                Check(value != null && value.Boolean && lastAccessError == -1, "Boolean.");
                value = ReadValue(client, "GGIO1$ST$Count$stVal");
                Check(value != null && value.Integer == -123456 && lastAccessError == -1, "Integer.");
                value = ReadValue(client, "GGIO1$MX$Analog$mag");
                Check(value != null && value.FloatingPoint == 12.5 && lastAccessError == -1, "Float.");
                metrics[2] += 3;
                bool rejected = false;
                if (useTryRead)
                {
                    value = ReadValue(client, "GGIO1$ST$Missing$stVal");
                    rejected = value == null && lastAccessError == 10;
                }
                else
                {
                    try { ReadValue(client, "GGIO1$ST$Missing$stVal"); }
                    catch (MmsAccessException error) { rejected = error.ErrorCode == 10; }
                }
                Check(rejected && client.Associated && !client.Faulted, "DataAccessError connection remains usable.");
                metrics[3]++;
            }
        }
        metrics[0]++;
        metrics[4] = (uint)(Environment.TickCount64 - started);
    }

    private static void Discover(Iec61850Client client)
    {
        MmsNameListPage domains = client.GetNameList();
        metrics[24]++;
        Check(domains.Names.Length == 1 && domains.Names[0] == "NFTESTLD0" && !domains.MoreFollows, "Domain discovery.");
        MmsNameListPage names = client.GetNameList("NFTESTLD0");
        metrics[24]++;
        Check(!names.MoreFollows && names.Names.Length == 17, "Native variable list.");
        string[] items = { "GGIO1$ST$Status$stVal", "GGIO1$ST$Count$stVal", "GGIO1$MX$Analog$mag" };
        MmsValueKind[] kinds = { MmsValueKind.Boolean, MmsValueKind.Integer, MmsValueKind.FloatingPoint };
        for (int i = 0; i < items.Length; i++)
        {
            bool found = false;
            for (int j = 0; j < names.Names.Length; j++) if (names.Names[j] == items[i]) found = true;
            Check(found, "Scalar name.");
            MmsVariableAttributes attributes = client.GetVariableAccessAttributes("NFTESTLD0", items[i]);
            metrics[25]++;
            Check(attributes.Kind == kinds[i] && attributes.BitWidth == (i == 0 ? 1 : 32) &&
                attributes.ExponentWidth == (i == 2 ? 8 : 0) && !attributes.Deletable, "Scalar attributes.");
        }
        MmsNameListPage after = client.GetNameList("NFTESTLD0", names.Names[0]);
        metrics[24]++;
        Check(!after.MoreFollows && after.Names.Length == names.Names.Length - 1, "Name continuation.");
        bool rejected = false;
        try { client.GetVariableAccessAttributes("NFTESTLD0", "missing"); }
        catch (MmsServiceException error) { rejected = error.ErrorClass == 7 && error.ErrorCode == 2; }
        Check(rejected && client.Associated && !client.Faulted, "ConfirmedError preserves association.");
        metrics[26]++;
    }

    private static void Composite(Iec61850Client client)
    {
        MmsVariableAttributes type = client.GetVariableAccessAttributes("NFTESTLD0", "GGIO1$ST$Status");
        Check(type.Kind == MmsValueKind.Structure && type.ComponentCount == 1 &&
            type.GetComponentName(0) == "stVal" && type.GetComponent(0).Kind == MmsValueKind.Boolean, "Structure type.");
        MmsValue statusValue = client.Read("NFTESTLD0", "GGIO1$ST$Status");
        Check(statusValue.Kind == MmsValueKind.Structure && statusValue.Count == 1 && statusValue.GetElement(0).Boolean,
            "Structure Read.");
        type = client.GetVariableAccessAttributes("NFTESTLD0", "GGIO1$MX$Samples$values");
        Check(type.Kind == MmsValueKind.Array && type.ArrayLength == 3 &&
            type.ElementType.Kind == MmsValueKind.Integer && type.ElementType.BitWidth == 32, "Array type.");
        MmsValue samples = client.Read("NFTESTLD0", "GGIO1$MX$Samples$values");
        Check(samples.Kind == MmsValueKind.Array && samples.Count == 3, "Array Read.");
        for (int i = 0; i < 3; i++) Check(samples.GetElement(i).Integer == 100 + i, "Array element.");
        type = client.GetVariableAccessAttributes("NFTESTLD0", "GGIO1$MX$Samples");
        Check(type.Kind == MmsValueKind.Structure && type.GetComponentName(0) == "values" &&
            type.GetComponent(0).Kind == MmsValueKind.Array, "Nested type.");
        MmsValue nested = client.Read("NFTESTLD0", "GGIO1$MX$Samples");
        Check(nested.Kind == MmsValueKind.Structure && nested.GetElement(0).GetElement(2).Integer == 102 &&
            statusValue.GetElement(0).Boolean && samples.GetElement(0).Integer == 100, "Independent nested Read.");
        metrics[27] += 6;
    }

    private static void WriteMemory(Iec61850Client client)
    {
        const string d = "NFTESTLD0", b = "GGIO1$SP$Writable$flag",
            i = "GGIO1$SP$Writable$count", f = "GGIO1$SP$Writable$real";
        int code;
        Check(client.SupportsWrite && client.TryWrite(d, b, false, out code), "Boolean Write.");
        metrics[28]++;
        Check(!client.Read(d, b).Boolean, "Boolean readback."); metrics[30]++;
        client.Write(d, i, 42); metrics[28]++;
        Check(client.Read(d, i).Integer == 42, "Integer readback."); metrics[30]++;
        client.Write(d, f, 9.25f); metrics[28]++;
        Check(client.Read(d, f).FloatingPoint == 9.25, "Float readback."); metrics[30]++;
        Check(!client.TryWrite(d, "missing", true, out code) && code == 10, "Write missing."); metrics[29]++;
        Check(!client.TryWrite(d, i, true, out code) && code == 7, "Write type."); metrics[29]++;
        Check(!client.TryWrite(d, i, 1000001, out code) && code == 11, "Write range."); metrics[29]++;
        Check(!client.TryWrite(d, "GGIO1$ST$Status$stVal", false, out code) && code == 3, "Read-only Write."); metrics[29]++;
        Check(client.Read(d, i).Integer == 42 && !client.Faulted && client.Associated, "Write rejection unchanged."); metrics[30]++;
        client.Write(d, b, true); client.Write(d, i, -123456); client.Write(d, f, 12.5f); metrics[28] += 3;
    }

    private static MmsValue ReadValue(Iec61850Client client, string item)
    {
        long started = Environment.TickCount64;
        bool completed = false;
        lastAccessError = -1;
        try
        {
            MmsValue value;
            if (useTryRead)
            {
                completed = client.TryRead("NFTESTLD0", item, out value, out lastAccessError);
                return value;
            }
            value = client.Read("NFTESTLD0", item);
            completed = true;
            return value;
        }
        finally
        {
            uint elapsed = (uint)(Environment.TickCount64 - started);
            metrics[17] += elapsed;
            if (elapsed > metrics[18]) metrics[18] = elapsed;
            metrics[19]++;
            int total = completed ? 20 : 22;
            metrics[total] += elapsed;
            if (elapsed > metrics[total + 1]) metrics[total + 1] = elapsed;
        }
    }

    private static void PrepareFault()
    {
        Check(faultSocket == null, "Single fault connection.");
        faultSocket = Connect();
        faultClient = new Iec61850Client(new MmsSocketChannel(faultSocket), timeoutMilliseconds: 3000);
        faultClient.Associate();
        Check(faultClient.Read("NFTESTLD0", "GGIO1$ST$Status$stVal").Boolean, "Pre-fault read.");
        metrics[2]++;
    }

    private static void ReadAfterPeerStop()
    {
        try
        {
            Thread.Sleep(1000);
            bool observed = false;
            try
            {
                MmsValue value;
                int error;
                faultClient.TryRead("NFTESTLD0", "GGIO1$ST$Status$stVal", out value, out error);
            }
            catch (IOException error) { observed = true; Debug.WriteLine("Expected peer EOF: " + error.Message); }
            catch (SocketException error) { observed = true; Debug.WriteLine("Expected peer socket: " + error.ErrorCode); }
            Check(observed && faultClient.Faulted && !faultClient.Associated, "Peer stop must fault association.");
            metrics[5]++;
        }
        finally
        {
            if (faultSocket != null) faultSocket.Close();
            faultSocket = null;
            faultClient = null;
        }
    }

    private static void SilentTimeout()
    {
        using (var socket = Connect())
        {
            var client = new Iec61850Client(new MmsSocketChannel(socket), timeoutMilliseconds: 3000);
            long started = Environment.TickCount64;
            bool timedOut = false;
            try { client.Associate(); }
            catch (TimeoutException error)
            {
                const string prefix = "MMS socket readiness deadline expired; waited=";
                Check(error.Message.IndexOf(prefix) == 0, "Readiness deadline exception.");
                metrics[12] = (uint)int.Parse(error.Message.Substring(prefix.Length));
                timedOut = true;
            }
            long elapsed = Environment.TickCount64 - started;
            Check(timedOut && client.Faulted && !client.Associated &&
                metrics[12] >= 2950 && metrics[12] < 3500 && elapsed >= metrics[12] && elapsed < metrics[12] + 4000,
                "Silent peer timeout: elapsed=" + elapsed + ", readiness wait=" + metrics[12]);
            metrics[6]++;
            metrics[7] = (uint)elapsed;
        }
    }

    private static void MalformedResponse(int scenario)
    {
        using (var socket = Connect())
        {
            var channel = new CountingChannel(socket);
            var client = new Iec61850Client(channel, timeoutMilliseconds: 3000, maximumTpduSize: 1024);
            MmsControlClient control = null;
            bool failed = false;
            uint before = 0;
            long started = 0;
            try
            {
                client.Associate();
                Check(scenario >= 7, "Malformed association response accepted.");
                if (scenario == 12)
                {
                    control = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$Direct", new byte[] { 70 });
                    before = channel.Writes;
                    started = Environment.TickCount64;
                    int error;
                    control.Operate(true, 0, out error);
                }
                else client.Read("NFTESTLD0", "GGIO1$ST$Status$stVal");
            }
            catch (MmsProtocolException)
            {
                Check(scenario != 5 && scenario != 10 && scenario != 12, "Unexpected malformed response protocol fault.");
                failed = true;
            }
            catch (IOException)
            {
                Check(scenario == 5 || scenario == 10, "Unexpected malformed response EOF.");
                failed = true;
            }
            catch (TimeoutException error)
            {
                long elapsed = Environment.TickCount64 - started;
                const string prefix = "MMS socket readiness deadline expired; waited=";
                Check(scenario == 12 && control != null && error.Message.IndexOf(prefix) == 0,
                    "Lost Operate response deadline: elapsed=" + elapsed + ", cause=" + error.Message);
                metrics[12] = (uint)int.Parse(error.Message.Substring(prefix.Length));
                metrics[7] = (uint)elapsed;
                Check(metrics[12] >= 2950 && metrics[12] < 3500 && elapsed >= metrics[12],
                    "Lost Operate response readiness wait.");
                failed = true;
            }
            Check(failed && client.Faulted && !client.Associated, "Malformed response must fault Client.");
            uint writes = channel.Writes;
            bool refused = false;
            try
            {
                if (scenario == 12)
                {
                    int error;
                    control.Operate(false, 0, out error);
                }
                else client.Read("NFTESTLD0", "GGIO1$ST$Status$stVal");
            }
            catch (InvalidOperationException) { refused = true; }
            Check(refused && channel.Writes == writes, "Faulted Client must refuse without transmission.");
            if (scenario == 12)
            {
                Check(writes == before + 1, "One Operate transmission, no implicit retry.");
                metrics[36]++;
                metrics[37] += writes - before;
                metrics[38]++;
            }
            else metrics[35]++;
        }
    }

    private static void CompareTimeout(int mode)
    {
        bool controlRequest = mode >= 2, resultApi = mode % 2 != 0;
        using (var socket = Connect())
        {
            var channel = new CountingChannel(socket);
            var client = new Iec61850Client(channel, timeoutMilliseconds: 3000);
            client.Associate();
            var control = controlRequest ? new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$Direct", new byte[] { 70 }) : null;
            channel.ResetTiming();
            uint before = channel.Writes;
            bool timedOut = false, accepted = false, exception = false;
            int error = -1;
            MmsValue value = null;
            long started = Environment.TickCount64;
            try
            {
                if (controlRequest)
                    accepted = resultApi ? control.TryOperate(true, 0, out error, out timedOut) : control.Operate(true, 0, out error);
                else
                    accepted = resultApi ? client.TryRead("NFTESTLD0", "GGIO1$ST$Status$stVal", out value, out error, out timedOut) :
                        client.TryRead("NFTESTLD0", "GGIO1$ST$Status$stVal", out value, out error);
            }
            catch (TimeoutException) { exception = true; }
            long finished = Environment.TickCount64;
            Check(!accepted && value == null && error == -1 && timedOut == resultApi && exception != resultApi &&
                client.Faulted && !client.Associated, "Timeout comparison state/result.");
            Check(channel.Writes == before + 1, "Timeout comparison single request.");
            uint writes = channel.Writes;
            bool refused = false;
            try
            {
                if (controlRequest) control.TryOperate(false, 0, out error, out timedOut);
                else client.TryRead("NFTESTLD0", "GGIO1$ST$Status$stVal", out value, out error, out timedOut);
            }
            catch (InvalidOperationException) { refused = true; }
            Check(refused && channel.Writes == writes, "Timeout comparison no replay.");
            metrics[39]++;
            metrics[resultApi ? 40 : 41]++;
            metrics[42] = (uint)mode;
            metrics[43] = (uint)(finished - started);
            metrics[44] = (uint)channel.ReadMilliseconds;
            metrics[45] = (uint)channel.WriteMilliseconds;
            metrics[46] = (uint)(channel.FirstRead - started);
            metrics[47] = (uint)(finished - channel.LastRead);
            if (resultApi) Check(metrics[43] >= 2950 && metrics[43] < 3500, "No-throw timeout return budget.");
        }
    }

    private sealed class CountingChannel : IMmsTimeoutChannel
    {
        private readonly MmsSocketChannel inner;
        internal uint Writes;
        internal long ReadMilliseconds, WriteMilliseconds, FirstRead, LastRead;
        internal CountingChannel(Socket socket) { inner = new MmsSocketChannel(socket); }
        public long Milliseconds { get { return inner.Milliseconds; } }
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            long start = Milliseconds;
            if (FirstRead == 0) FirstRead = start;
            try { return inner.Read(buffer, offset, count, timeoutMilliseconds); }
            finally { LastRead = Milliseconds; ReadMilliseconds += LastRead - start; }
        }
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            long start = Milliseconds;
            try { Writes++; return inner.Write(buffer, offset, count, timeoutMilliseconds); }
            finally { WriteMilliseconds += Milliseconds - start; }
        }
        public bool TryRead(byte[] buffer, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            long start = Milliseconds;
            if (FirstRead == 0) FirstRead = start;
            try { return inner.TryRead(buffer, offset, count, timeoutMilliseconds, out transferred); }
            finally { LastRead = Milliseconds; ReadMilliseconds += LastRead - start; }
        }
        public bool TryWrite(byte[] buffer, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            long start = Milliseconds;
            try { Writes++; return inner.TryWrite(buffer, offset, count, timeoutMilliseconds, out transferred); }
            finally { WriteMilliseconds += Milliseconds - start; }
        }
        internal void ResetTiming()
        {
            ReadMilliseconds = WriteMilliseconds = FirstRead = LastRead = 0;
        }
    }

    private static void Execute(byte command, Socket udp, EndPoint remote)
    {
        if (command == 0) return;
        if (command == 1 || command == 4) Cycle(command == 4);
        else if (command == 2)
        {
            PrepareFault();
            status = 1;
            Send(udp, remote);
            ReadAfterPeerStop();
        }
        else if (command == 3) SilentTimeout();
        else if (command == 5)
        {
            using (var socket = Connect())
            {
                var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                client.Associate();
                ControlChecks.Run(client);
                metrics[31]++;
            }
        }
        else if (command == 6)
        {
            using (var socket = Connect())
            {
                var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                client.Associate();
                EnhancedChecks.Run(client);
                metrics[32]++;
            }
        }
        else if (command == 7)
        {
            using (var socket = Connect())
            {
                var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                client.Associate();
                SboEnhancedChecks.Run(client);
                metrics[33]++;
            }
        }
        else if (command == 8)
        {
            using (var socket = Connect())
            {
                var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                client.Associate();
                DataChecks.Run(client); DataChecks.Quality(client);
                metrics[34]++;
            }
        }
        else if (command >= 9 && command <= 21) MalformedResponse(command - 9);
        else if (command >= 22 && command <= 25) CompareTimeout(command - 22);
        else throw new ArgumentOutOfRangeException("command");
    }

    private static void Send(Socket udp, EndPoint remote)
    {
        metrics[8] = nanoFramework.Runtime.Native.GC.Run(true);
        if (status == 2 && (command == 1 || command == 4) && metrics[0] >= 2)
        {
            if (metrics[9] == 0) metrics[9] = metrics[10] = metrics[11] = metrics[8];
            if (metrics[8] < metrics[10]) metrics[10] = metrics[8];
            if (metrics[8] > metrics[11]) metrics[11] = metrics[8];
        }
        metrics[13] = Debugger.IsAttached ? 1U : 0U;
        metrics[15] = 0x600d;
        reply[0] = 77; reply[1] = 77; reply[2] = 72; reply[3] = 58;
        Write(reply, 4, sequence);
        Write(reply, 8, status);
        for (int i = 0; i < metrics.Length; i++) Write(reply, 16 + i * 4, metrics[i]);
        byte[] bytes = reply;
        if (failure != null)
        {
            byte[] text = Encoding.UTF8.GetBytes(failure);
            bytes = new byte[reply.Length + text.Length];
            Array.Copy(reply, bytes, reply.Length);
            Array.Copy(text, 0, bytes, reply.Length, text.Length);
        }
        if (udp.SendTo(bytes, remote) != bytes.Length) throw new IOException("Diagnostic UDP send failed.");
    }
    private static uint Read(byte[] b, int p)
    {
        return ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];
    }
    private static void Write(byte[] b, int p, uint value)
    {
        b[p] = (byte)(value >> 24); b[p + 1] = (byte)(value >> 16);
        b[p + 2] = (byte)(value >> 8); b[p + 3] = (byte)value;
    }
}
