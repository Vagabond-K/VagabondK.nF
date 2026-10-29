using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.MMS.TCP;

/// <summary>전용 가상 IED에 오류·지연·재시작을 유발하여 연결 실패 후 이전 요청을 재실행하지 않고 새 세션으로 복구하는지 확인합니다.</summary>
internal static class VirtualIedFaultTests
{
    private static void Check(bool valid, string message)
    { if (!valid) throw new Exception("Virtual IED fault: " + message); }

    internal static void Run(string executable)
    {
        int port = FreePort();
        using (var peer = new NativeIed(executable, "typedserver", port))
        {
            for (int scenario = 0; scenario < 5; scenario++)
            {
                using (var socket = Connect(port))
                {
                    var channel = new PacketChannel(socket);
                    var client = new Iec61850Client(channel, timeoutMilliseconds: 500, maximumTpduSize: 1024);
                    client.Associate();
                    string item = scenario == 4 ? "GGIO1$CO$D3" : "GGIO1$CO$D1";
                    var control = new MmsControlClient(client, "NFTESTLD0", item, new byte[] { 70 },
                        valueKind: MmsControlValueKind.DoublePoint);
                    channel.Tag = scenario == 4 ? 0xa3 : 0xa1;
                    channel.Action = scenario == 0 ? FaultAction.Delay : scenario == 1 ? FaultAction.Partial :
                        scenario == 2 ? FaultAction.Disconnect : FaultAction.Drop;
                    bool failed = false;
                    int error;
                    try
                    {
                        if (scenario == 4)
                        {
                            bool accepted;
                            control.OperateAndWait(true, 0, out accepted, out error);
                        }
                        else Check(control.Operate(true, 0, out error), "delayed Operate was rejected");
                    }
                    catch (TimeoutException) { failed = true; }
                    catch (IOException) { failed = true; }
                    catch (SocketException) { failed = true; }
                    Check(channel.Injections == 1 && channel.OperateRequests == 1, "exactly one injection and command transmission");
                    Check(failed == (scenario != 0) && client.Faulted == failed, "faulted state");
                    if (failed)
                    {
                        bool refused = false;
                        try { control.Operate(false, 0, out error); }
                        catch (InvalidOperationException) { refused = true; }
                        Check(refused && channel.OperateRequests == 1, "no reuse or implicit command replay");
                    }
                    else client.Release();
                }
                using (var socket = Connect(port))
                {
                    var client = new Iec61850Client(new MmsSocketChannel(socket));
                    client.Associate();
                    Check(Iec61850Data.ReadDoublePoint(client.Read("NFTESTLD0",
                        scenario == 4 ? "GGIO1$ST$D3$stVal" : "GGIO1$ST$D1$stVal")) == IecDoublePoint.On,
                        "response loss did not roll back the executed command");
                    client.Release();
                }
                Console.WriteLine("VIRTUAL FAULT scenario=" + scenario + " injection=1 operateTransmissions=1 recovery=PASS");
            }
        }
        using (var peer = new NativeIed(executable, "typedserver", port))
        using (var socket = Connect(port))
        {
            var client = new Iec61850Client(new MmsSocketChannel(socket)); client.Associate();
            Check(Iec61850Data.ReadDoublePoint(client.Read("NFTESTLD0", "GGIO1$ST$D1$stVal")) != IecDoublePoint.On,
                "restarted virtual IED did not reset its memory model");
            client.Release();
        }
        ActiveRestart(executable);
        Console.WriteLine("VIRTUAL IED: delayed response, partial EOF, disconnect, lost Write response/CommandTermination, no replay, restart PASS.");
    }
    private static void ActiveRestart(string executable)
    {
        int port = FreePort();
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            Iec61850Client client;
            using (var peer = new NativeIed(executable, "typedserver", port))
            {
                socket.Connect(IPAddress.Loopback, port);
                client = new Iec61850Client(new MmsSocketChannel(socket), timeoutMilliseconds: 500);
                client.Associate(); client.Read("NFTESTLD0", "GGIO1$ST$D1$stVal");
            }
            bool failed = false;
            try { client.Read("NFTESTLD0", "GGIO1$ST$D1$stVal"); }
            catch (IOException) { failed = true; }
            catch (SocketException) { failed = true; }
            Check(failed && client.Faulted && !client.Associated, "active association lost when peer stopped");
        }
        using (var peer = new NativeIed(executable, "typedserver", port))
        using (var socket = Connect(port))
        {
            var client = new Iec61850Client(new MmsSocketChannel(socket)); client.Associate();
            client.Read("NFTESTLD0", "GGIO1$ST$D1$stVal"); client.Release();
        }
    }
    internal static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); return port;
    }
    internal static Socket Connect(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { socket.Connect(IPAddress.Loopback, port); return socket; }
        catch { socket.Dispose(); throw; }
    }
    internal sealed class NativeIed : IDisposable
    {
        private readonly Process process;
        internal NativeIed(string executable, string mode, int port)
        {
            process = Process.Start(new ProcessStartInfo(executable, mode + " " + port) {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
            try
            {
                Check(process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == "READY",
                    "native startup");
            }
            catch { process.Kill(true); process.WaitForExit(); process.Dispose(); throw; }
        }
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.WriteLine("quit");
                    if (!process.WaitForExit(10000)) throw new TimeoutException("Virtual IED exit.");
                }
                Check(process.ExitCode == 0, process.StandardError.ReadToEnd());
            }
            finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } process.Dispose(); }
        }
    }
    internal enum FaultAction { None, Delay, Partial, Disconnect, Drop }
    internal sealed class PacketChannel : IMmsChannel
    {
        private readonly Socket socket;
        private readonly MmsSocketChannel inner;
        private byte[] incoming;
        private int position;
        private bool eof;
        private readonly MemoryStream outgoing = new MemoryStream();
        internal FaultAction Action, ArmOnOperate;
        internal int Tag, Injections, OperateRequests;
        internal PacketChannel(Socket socket) { this.socket = socket; inner = new MmsSocketChannel(socket); }
        public long Milliseconds { get { return inner.Milliseconds; } }
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            if (incoming == null || position == incoming.Length)
            {
                if (eof) return 0;
                long deadline = Milliseconds + timeoutMilliseconds;
                byte[] header = new byte[4];
                if (!ReadExactly(header, 0, 4, deadline)) return 0;
                int size = header[2] * 256 + header[3];
                Check(header[0] == 3 && header[1] == 0 && size >= 7 && size <= 65535, "TPKT bounds");
                incoming = new byte[size]; Array.Copy(header, incoming, 4);
                Check(ReadExactly(incoming, 4, size - 4, deadline), "complete peer packet");
                position = 0;
                if (Action != FaultAction.None && incoming[5] == 0xf0 && incoming[6] == 0x80 && size > 11 &&
                    incoming[7] == 1 && incoming[8] == 0 && incoming[9] == 1 && incoming[10] == 0)
                {
                    var data = MmsEncoding.UnwrapPdv(MmsEncoding.Single(incoming, 11, size - 11, 0x61), 3);
                    var pdu = new BerCursor(data.Buffer, data.Offset, data.Length).Read();
                    if (pdu.Tag == Tag)
                    {
                        Injections++;
                        var action = Action; Action = FaultAction.None;
                        if (action == FaultAction.Delay) Thread.Sleep(50);
                        if (action == FaultAction.Partial) { Array.Resize(ref incoming, size / 2); eof = true; }
                        if (action == FaultAction.Disconnect) { socket.Shutdown(SocketShutdown.Both); eof = true; incoming = null; return 0; }
                        if (action == FaultAction.Drop)
                        {
                            incoming = null;
                            Thread.Sleep(timeoutMilliseconds);
                            throw new TimeoutException("Injected loss after peer transmission.");
                        }
                    }
                }
            }
            int length = Math.Min(count, incoming.Length - position);
            Array.Copy(incoming, position, buffer, offset, length); position += length; return length;
        }
        private bool ReadExactly(byte[] buffer, int offset, int count, long deadline)
        {
            int read = 0;
            while (read < count)
            {
                int remaining = (int)(deadline - Milliseconds);
                if (remaining <= 0) throw new TimeoutException("Packet interception read deadline.");
                int size = inner.Read(buffer, offset + read, count - read, remaining);
                if (size == 0) { Check(read == 0, "partial intercepted packet"); return false; }
                read += size;
            }
            return true;
        }
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            int sent = inner.Write(buffer, offset, count, timeoutMilliseconds);
            outgoing.Write(buffer, offset, sent);
            byte[] data = outgoing.ToArray();
            int consumed = 0;
            while (data.Length - consumed >= 4)
            {
                int length = data[consumed + 2] * 256 + data[consumed + 3];
                Check(length >= 7, "outgoing TPKT size");
                if (data.Length - consumed < length) break;
                if (Encoding.ASCII.GetString(data, consumed, length).Contains("$Oper"))
                {
                    OperateRequests++;
                    if (ArmOnOperate != FaultAction.None) { Action = ArmOnOperate; ArmOnOperate = FaultAction.None; }
                }
                consumed += length;
            }
            outgoing.SetLength(0); outgoing.Write(data, consumed, data.Length - consumed);
            return sent;
        }
    }
}
