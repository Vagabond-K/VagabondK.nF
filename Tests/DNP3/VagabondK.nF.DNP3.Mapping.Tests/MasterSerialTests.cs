using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VagabondK.nF.DNP3.Serial;

// 물리 COM 없이 오프라인 가상 채널의 정확한 Link 재시도 횟수와 부분 프레임의 시간 초과·복구를 검사합니다.
internal static class MasterSerialTests
{
    private sealed class Offline : IDnp3ByteChannel
    {
        public long Milliseconds { get; private set; }
        public byte[] Prefix;
        internal int Writes;
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            Milliseconds += timeoutMilliseconds;
            if (Prefix == null) return 0;
            int length = Prefix.Length;
            Array.Copy(Prefix, 0, buffer, offset, length);
            Prefix = null;
            return length;
        }
        public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds) { Writes++; }
    }
    internal static void Run()
    {
        var channel = new Offline();
        var validation = new MasterValidation();
        validation.InitializeSerial(channel);
        Program.Check(validation.AwaitSerialTimeout(true) == 4 && validation.SerialRetries == 2 && channel.Writes == 3,
            "Serial offline Link reset acknowledgement fails after exactly two retries.");
        int writes = channel.Writes;
        Program.Check(validation.AwaitSerialTimeout(false) == 5 && validation.SerialRetries == 0 && channel.Writes == writes + 1,
            "Serial response timeout is distinct from Link acknowledgement timeout.");
        channel.Prefix = new byte[] { 5, 100, 20 };
        validation.AwaitSerialPartial();
        Program.Check(validation.SerialFrameTimeouts == 1 && validation.Received == 0,
            "Incomplete UART prefix expires without model delivery.");
        foreach (bool master in new[] { true, false })
        {
            for (int kind = 0; kind < 3; kind++)
            {
                var crcChannel = new CrcChannel { Input = SerialCrcValidation.Input(master, kind) };
                var probe = new SerialCrcValidation(master);
                probe.Run(crcChannel, kind);
                SerialCrcValidation.CheckOutput(master, crcChannel.Output.ToArray());
                Program.Check(probe.CrcErrors == (kind == 2 ? 6 : 4), "CRC probe exact rejection counts.");
            }
            var missing = new CrcChannel();
            bool failed = false;
            try { new SerialCrcValidation(master).Run(missing, 0); }
            catch (InvalidOperationException) { failed = true; }
            Program.Check(failed, "CRC probe must reject absent injection/recovery.");
        }
        Console.WriteLine("DNP3 Master Serial preparation PASS: ACK retries, response timeout, Reset and partial frame expiry.");
        Console.WriteLine("DNP3 Serial CRC preparation PASS: both roles, header/data/mixed corruption, exact discard and response checks.");
        TcpRecovery();
    }

    private static void TcpRecovery()
    {
        foreach (bool master in new[] { true, false })
            for (int kind = 0; kind < 4; kind++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    peer.Connect(listener.LocalEndpoint);
                    using var target = listener.AcceptSocket();
                    var probe = new SerialCrcValidation(master);
                    var running = Task.Run(() => probe.RunTcp(target, kind));
                    try
                    {
                        if (kind == 3)
                        {
                            Program.Check(peer.Send(new byte[] { 5, 100, 20 }) == 3, "TCP partial prefix send.");
                            Thread.Sleep(1200);
                        }
                        byte[] bytes = SerialCrcValidation.Input(master, kind);
                        for (int p = 0; p < bytes.Length;)
                        {
                            int sent = peer.Send(bytes, p, Math.Min(7, bytes.Length - p), SocketFlags.None);
                            if (sent == 0) throw new IOException("TCP fixture send made no progress.");
                            p += sent;
                        }
                        running.GetAwaiter().GetResult();
                        using var output = new MemoryStream();
                        var buffer = new byte[1024];
                        while (peer.Available > 0)
                        {
                            int count = peer.Receive(buffer);
                            if (count == 0) throw new IOException("Unexpected TCP fixture EOF.");
                            output.Write(buffer, 0, count);
                        }
                        SerialCrcValidation.CheckOutput(master, output.ToArray());
                        Program.Check(probe.CrcErrors == (kind == 3 ? 0 : kind == 2 ? 6 : 4) &&
                            probe.FrameTimeouts == (kind == 3 ? 1 : 0), "TCP exact CRC/partial recovery counts.");
                    }
                    finally
                    {
                        target.Close();
                        if (!running.IsCompleted) running.GetAwaiter().GetResult();
                    }
                }
                finally { listener.Stop(); }
            }
        Console.WriteLine("DNP3 TCP recovery preparation PASS: both roles, header/data/mixed CRC and partial expiry.");
    }

    private sealed class CrcChannel : IDnp3ByteChannel
    {
        public long Milliseconds { get; private set; }
        internal byte[] Input = new byte[0];
        internal readonly List<byte> Output = new List<byte>();
        private int position;
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            Milliseconds += timeoutMilliseconds;
            int length = Math.Min(Math.Min(count, 7), Input.Length - position);
            Array.Copy(Input, position, buffer, offset, length);
            position += length;
            return length;
        }
        public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            for (int i = 0; i < count; i++) Output.Add(buffer[offset + i]);
        }
    }
}
