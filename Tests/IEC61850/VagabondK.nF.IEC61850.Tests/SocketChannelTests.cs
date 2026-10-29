using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.MMS.TCP;

/// <summary>로컬 TCP 소켓으로 준비 대기 기한·부분 전송·EOF와 잘못된 인수의 예외 계약을 확인합니다.</summary>
internal static class SocketChannelTests
{
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception("MMS socket: " + message);
    }

    private static void Reject(Action action, Type type)
    {
        try { action(); }
        catch (Exception error) { Check(error.GetType() == type, "Expected " + type + ", got " + error); return; }
        throw new Exception("MMS socket expected " + type);
    }

    internal static void Run()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Connect(listener.LocalEndpoint);
                using (var remote = listener.AcceptSocket())
                {
                    var channel = new MmsSocketChannel(socket);
                    var bytes = new byte[8];
                    Reject(() => channel.Read(null, 0, 1, 10), typeof(ArgumentNullException));
                    Reject(() => channel.Read(bytes, 0, 0, 10), typeof(ArgumentOutOfRangeException));
                    Reject(() => channel.Read(bytes, 7, 2, 10), typeof(ArgumentOutOfRangeException));
                    Reject(() => channel.Read(bytes, -1, 1, 10), typeof(ArgumentOutOfRangeException));
                    Reject(() => channel.Read(bytes, 0, 1, 0), typeof(ArgumentOutOfRangeException));
                    Reject(() => channel.Write(null, 0, 1, 10), typeof(ArgumentNullException));
                    Reject(() => channel.Write(bytes, 0, 0, 10), typeof(ArgumentOutOfRangeException));
                    Reject(() => channel.Write(bytes, int.MaxValue, 1, 10), typeof(ArgumentOutOfRangeException));
                    Reject(() => channel.Write(bytes, 0, 1, -1), typeof(ArgumentOutOfRangeException));
                    var clock = Stopwatch.StartNew();
                    int transferred;
                    Check(!channel.TryRead(bytes, 0, 8, 100, out transferred) && transferred == 0, "No-throw Read timeout.");
                    clock.Restart();
                    Reject(() => channel.Read(bytes, 0, 8, 300), typeof(TimeoutException));
                    Check(clock.ElapsedMilliseconds >= 280 && clock.ElapsedMilliseconds < 1500, "Silent Read deadline.");
                    remote.Send(new byte[] { 42 });
                    Check(channel.Read(bytes, 2, 6, 1000) == 1 && bytes[2] == 42, "Partial read and offset.");
                    bytes[3] = 99;
                    Check(channel.Write(bytes, 3, 1, 1000) == 1 && remote.Receive(bytes, 4, 1, SocketFlags.None) == 1 &&
                        bytes[4] == 99, "Write and offset.");
                    remote.Shutdown(SocketShutdown.Send);
                    Check(channel.Read(bytes, 0, 8, 1000) == 0, "TCP EOF, not timeout.");
                    Check(channel.TryRead(bytes, 0, 8, 1000, out transferred) && transferred == 0, "TryRead EOF is not timeout.");
                }
            }
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Connect(listener.LocalEndpoint);
                using (var remote = listener.AcceptSocket())
                {
                    var client = new Iec61850Client(new MmsSocketChannel(socket), timeoutMilliseconds: 300);
                    var clock = Stopwatch.StartNew();
                    Reject(client.Associate, typeof(TimeoutException));
                    Check(client.Faulted && !client.Associated && clock.ElapsedMilliseconds >= 280 &&
                        clock.ElapsedMilliseconds < 1500, "Silent Association deadline and state.");
                    Reject(client.Associate, typeof(InvalidOperationException));
                }
            }
        }
        finally { listener.Stop(); }
        Console.WriteLine("MMS socket readiness: argument guards, silent Read/Association deadlines, partial I/O and EOF PASS.");
    }
}
