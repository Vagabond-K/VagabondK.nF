using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>클라이언트 보드에 비정상 응답·지연·연결 유실을 제공하는 데스크톱 전용 시험 peer입니다.</summary>
/// <remarks>전용 보드 시험 드라이버와 격리망에서 실행하십시오. 오류 주입 트래픽은 실제 설비에 보내지 않습니다.</remarks>
internal static class HardwareClientFaultPeer
{
    internal const int Cases = 14;

    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException("Hardware Client fault peer: " + message); }

    internal static void Run(string address, int port, int scenario, string capture)
    {
        Check(scenario >= 0 && scenario < Cases, "scenario");
        var listener = new TcpListener(IPAddress.Parse(address), port);
        listener.Start(1);
        try
        {
            Console.WriteLine("READY");
            Check(listener.Pending() || listener.Server.Poll(120000000, SelectMode.SelectRead), "accept deadline");
            using (Socket socket = listener.AcceptSocket())
            {
                socket.SendTimeout = 5000;
                var channel = new FaultChannel(socket, scenario, capture);
                int executions = 0, validations = 0;
                var control = new MmsServerControl("NFTESTLD0", "GGIO1$CO$Direct", MmsControlModel.DirectNormal);
                control.SetHandlers(request => { validations++; return -1; },
                    request => { executions++; return -1; });
                control.Enabled = true;
                var session = new Iec61850ServerSession(channel, new[] {
                    new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Status$stVal", true)
                }, controls: new[] { control }, timeoutMilliseconds: 15000);
                bool injected = false;
                try
                {
                    session.Associate();
                    if (scenario == 12) Check(session.ProcessRead(), "ctlModel query");
                    session.ProcessRead();
                }
                catch (InjectionComplete) { injected = true; }
                finally { session.Close(); }
                Check(injected && channel.Injections == 1, "exactly one response injection");
                channel.Drain();
                Check(executions == (scenario == 12 ? 1 : 0) && validations == executions &&
                    channel.OperateRequests == executions && (scenario != 12 || control.Value),
                    "execution/transmission count and no replay");
                Console.WriteLine("MCU CLIENT FAULT PASS scenario=" + scenario + " injections=1 executions=" +
                    executions + " operateTransmissions=" + channel.OperateRequests + " closure=" + channel.Closure);
            }
            Check(Console.ReadLine() == "quit", "runner shutdown");
        }
        finally { listener.Stop(); }
    }

    private sealed class InjectionComplete : Exception { }

    private sealed class FaultChannel : IMmsChannel
    {
        private readonly Socket socket;
        private readonly MmsSocketChannel inner;
        private readonly int scenario;
        private readonly string capture;
        private int packets;
        private readonly MemoryStream requests = new MemoryStream();
        internal int Injections, OperateRequests;
        internal string Closure;
        internal FaultChannel(Socket socket, int scenario, string capture)
        {
            this.socket = socket; this.scenario = scenario; this.capture = capture;
            inner = new MmsSocketChannel(socket);
        }
        public long Milliseconds { get { return inner.Milliseconds; } }
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            int n = inner.Read(buffer, offset, count, timeoutMilliseconds);
            requests.Write(buffer, offset, n);
            return n;
        }
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            packets++;
            int target = scenario <= 5 ? 1 : scenario == 6 ? 2 : scenario == 12 ? 4 : 3;
            // 이 시험기는 TPDU1024를 사용하므로 각 응답은 완전한 TPKT 하나여야 합니다.
            Check(offset == 0 && count >= 7 && (buffer[2] * 256 + buffer[3]) == count, "single response packet");
            var bytes = new byte[count];
            Array.Copy(buffer, offset, bytes, 0, count);
            if (packets == target)
            {
                Injections++;
                if (scenario == 0 || scenario == 7) bytes[0] = 4;
                else if (scenario == 1) bytes[1] = 1;
                else if (scenario == 2) bytes[3] = 6;
                else if (scenario == 3) { bytes[2] = 255; bytes[3] = 255; }
                else if (scenario == 4) bytes[5] = 0xe0;
                else if (scenario == 5) Array.Resize(ref bytes, 3);
                else if (scenario == 6)
                {
                    Array.Resize(ref bytes, bytes.Length - 1);
                    bytes[2] = (byte)(bytes.Length >> 8); bytes[3] = (byte)bytes.Length;
                }
                else if (scenario == 8) bytes[5] = 0xe0;
                else if (scenario == 9) bytes = Data(new byte[] { 0xa1, 0x80, 2, 1, 1, 0, 0 });
                else if (scenario == 10) Array.Resize(ref bytes, bytes.Length / 2);
                else if (scenario == 11)
                {
                    var data = MmsEncoding.UnwrapPdv(MmsEncoding.Single(bytes, 11, bytes.Length - 11, 0x61), 3);
                    var fields = new BerCursor(data.Buffer, data.Offset, data.Length).Read(0xa1).Children();
                    BerElement invoke = fields.Read(2);
                    Check(invoke.Length == 1, "small invoke ID");
                    bytes[invoke.Offset] ^= 0x40;
                }
                File.WriteAllBytes(capture, bytes);
                if (scenario < 12) Send(bytes);
                if (scenario == 5 || scenario == 10) socket.Shutdown(SocketShutdown.Send);
                throw new InjectionComplete();
            }
            Send(bytes);
            return count;
        }
        private static byte[] Data(byte[] pdu)
        {
            byte[] body = MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, pdu));
            var bytes = new byte[7 + body.Length];
            bytes[0] = 3; bytes[2] = (byte)(bytes.Length >> 8); bytes[3] = (byte)bytes.Length;
            bytes[4] = 2; bytes[5] = 0xf0; bytes[6] = 0x80;
            Array.Copy(body, 0, bytes, 7, body.Length); return bytes;
        }
        private void Send(byte[] bytes)
        {
            for (int p = 0; p < bytes.Length;)
            {
                int n = socket.Send(bytes, p, bytes.Length - p, SocketFlags.None);
                if (n == 0) throw new IOException("Fault response send made no progress.");
                p += n;
            }
        }
        internal void Drain()
        {
            socket.ReceiveTimeout = 20000;
            var buffer = new byte[1024];
            int n;
            try
            {
                while ((n = socket.Receive(buffer)) != 0) requests.Write(buffer, 0, n);
                Closure = "EOF";
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
            { Closure = "RESET"; }
            byte[] bytes = requests.ToArray();
            File.WriteAllBytes(capture + ".requests", bytes);
            string text = Encoding.ASCII.GetString(bytes);
            int p = 0;
            while ((p = text.IndexOf("$Oper", p, StringComparison.Ordinal)) >= 0) { OperateRequests++; p += 5; }
        }
    }
}
