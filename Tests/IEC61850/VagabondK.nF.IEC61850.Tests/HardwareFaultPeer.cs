using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.TCP;

// 데스크톱 전용 원시 입력 주입기입니다. 정상 복구는 네이티브 피어가 별도로 검사합니다.
/// <summary>서버 보드에 잘린 프레임 등 비정상 입력을 보내며 정상 복구는 별도 peer가 확인하는 오류 주입기입니다.</summary>
/// <remarks>보드 전용 시험망에서만 실행하십시오. 잘못된 형식의 요청 거절과 이후 새 연결의 복구는 별도 검증 단계입니다.</remarks>
internal static class HardwareFaultPeer
{
    internal const int Cases = 14;

    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException("Hardware fault peer: " + message); }

    internal static void Run(string address, int scenario, string capture)
    {
        Check(scenario >= 0 && scenario < Cases, "scenario");
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            socket.Connect(IPAddress.Parse(address), 18103);
            socket.ReceiveTimeout = 15000;
            socket.SendTimeout = 5000;
            if (scenario <= 5)
            {
                byte[] bad = Convert.FromHexString("0300001611E00000000100C0010AC1020001C2020001");
                if (scenario == 0) bad[0] = 4;
                else if (scenario == 1) bad[1] = 1;
                else if (scenario == 2) bad[3] = 6;
                else if (scenario == 3) { bad[2] = 0xff; bad[3] = 0xff; }
                else if (scenario == 4) Array.Resize(ref bad, 3);
                else bad[5] = 0xd0;
                File.WriteAllBytes(capture, bad);
                Send(socket, bad);
                if (scenario == 4) socket.Shutdown(SocketShutdown.Send);
                Closed(socket);
            }
            else if (scenario == 6)
            {
                Send(socket, Convert.FromHexString("0300001611E00000000100C0010AC1020001C2020001"));
                byte[] cc = Packet(socket);
                Check(cc.Length == 22 && cc[5] == 0xd0, "COTP accepted before malformed association");
                byte[] association = MmsEncoding.Association(4096);
                association[association.Length - 1] ^= 0x80;
                // 길이를 명시한 Session 연결 요청이지만 Presentation 본문은 잘려 있습니다.
                Array.Resize(ref association, association.Length - 1);
                byte[] bad = Frame(association);
                File.WriteAllBytes(capture, bad);
                Send(socket, bad);
                Closed(socket);
            }
            else
            {
                var channel = new LossChannel(socket);
                var client = new Iec61850Client(channel, timeoutMilliseconds: 1000);
                client.Associate();
                Check(client.Associated && !client.Faulted, "association before service corruption");
                if (scenario == 13)
                {
                    var control = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$Direct", new byte[] { 70 });
                    channel.Drop = true;
                    int error;
                    bool failed = false;
                    try { control.Operate(true, 0, out error); }
                    catch (TimeoutException) { failed = true; }
                    Check(failed && client.Faulted && !client.Associated && channel.Drops == 1 &&
                        channel.Writes == 1, "one command, one lost response, faulted client");
                    bool refused = false;
                    try { control.Operate(false, 0, out error); }
                    catch (InvalidOperationException) { refused = true; }
                    Check(refused && channel.Writes == 1, "faulted command cannot replay");
                    File.WriteAllBytes(capture, channel.LastWrite);
                    socket.Shutdown(SocketShutdown.Send);
                    Closed(socket);
                }
                else
                {
                    byte[] body = MmsEncoding.Join(new byte[] { 1, 0, 1, 0 },
                        MmsEncoding.Pdv(3, new byte[] { 0xa0, 0x80, 2, 1, 1, 0, 0 }));
                    if (scenario == 11 || scenario == 12)
                    {
                        string item = scenario == 12 ? "GGIO1$CO$Direct$Oper" : "GGIO1$ST$Count$stVal";
                        byte[] name = MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0,
                            MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier("NFTESTLD0"), MmsEncoding.Identifier(item))));
                        byte[] access = MmsEncoding.Tlv(0xa0, name);
                        byte[] data = MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x85, 777));
                        // 중복된 값 목록은 쓰기·제어 콜백을 하나라도 호출하기 전에 거부해야 합니다.
                        byte[] pdu = MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1),
                            MmsEncoding.Tlv(0xa5, access, data, data));
                        body = MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, pdu));
                    }
                    byte[] bad = Frame(body);
                    if (scenario == 8) bad[0] = 4;
                    else if (scenario == 9) bad[5] = 0xe0;
                    else if (scenario == 10) Array.Resize(ref bad, bad.Length / 2);
                    File.WriteAllBytes(capture, bad);
                    Send(socket, bad);
                    if (scenario == 10) socket.Shutdown(SocketShutdown.Send);
                    Closed(socket);
                }
            }
        }
        Console.WriteLine("MMS HARDWARE FAULT PEER PASS scenario=" + scenario +
            (scenario == 13 ? " operateTransmissions=1 lostResponse=1 noReplay=PASS" : " malformedInput=1 peerClosed=PASS"));
    }

    private static byte[] Frame(byte[] body)
    {
        var result = new byte[body.Length + 7];
        result[0] = 3; result[2] = (byte)(result.Length >> 8); result[3] = (byte)result.Length;
        result[4] = 2; result[5] = 0xf0; result[6] = 0x80;
        Array.Copy(body, 0, result, 7, body.Length);
        return result;
    }
    private static void Send(Socket socket, byte[] bytes)
    {
        for (int p = 0; p < bytes.Length;)
        {
            int count = socket.Send(bytes, p, bytes.Length - p, SocketFlags.None);
            if (count == 0) throw new IOException("Fault send made no progress.");
            p += count;
        }
    }
    private static byte[] Packet(Socket socket)
    {
        var header = new byte[4];
        Read(socket, header, 0, 4);
        int length = header[2] * 256 + header[3];
        Check(header[0] == 3 && header[1] == 0 && length >= 7 && length <= 8192, "packet bounds");
        var result = new byte[length];
        Array.Copy(header, result, 4);
        Read(socket, result, 4, length - 4);
        return result;
    }
    private static void Read(Socket socket, byte[] bytes, int offset, int count)
    {
        while (count > 0)
        {
            int n = socket.Receive(bytes, offset, count, SocketFlags.None);
            if (n == 0) throw new IOException("EOF inside peer packet.");
            offset += n; count -= n;
        }
    }
    private static void Closed(Socket socket)
    {
        socket.ReceiveTimeout = 15000;
        var buffer = new byte[256];
        try { Check(socket.Receive(buffer) == 0, "unexpected response to malformed input"); }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { }
    }
    private sealed class LossChannel : IMmsChannel
    {
        private readonly Socket socket;
        private readonly MmsSocketChannel inner;
        internal bool Drop;
        internal int Drops, Writes;
        internal byte[] LastWrite;
        internal LossChannel(Socket socket) { this.socket = socket; inner = new MmsSocketChannel(socket); }
        public long Milliseconds { get { return inner.Milliseconds; } }
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            if (Drop)
            {
                Packet(socket); // 완전한 서버 응답을 확인한 뒤 로컬에서 폐기합니다.
                Drop = false; Drops++;
                throw new TimeoutException("Injected loss of an already received Operate response.");
            }
            return inner.Read(buffer, offset, count, timeoutMilliseconds);
        }
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            int sent = inner.Write(buffer, offset, count, timeoutMilliseconds);
            if (Drop)
            {
                Writes++;
                LastWrite = new byte[sent];
                Array.Copy(buffer, offset, LastWrite, 0, sent);
            }
            return sent;
        }
    }
}
