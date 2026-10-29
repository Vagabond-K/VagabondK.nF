using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>형식별 제어를 관리 코드 루프백·외부 피어·장시간 시험으로 실행합니다. 대상은 메모리 시험 모델이어야 합니다.</summary>
internal static class TypedControlTests
{
    internal static void Soak(string peer, int seconds)
    {
        if (seconds < 1 || seconds > 86400) throw new ArgumentOutOfRangeException("seconds");
        int port = VirtualIedFaultTests.FreePort(), cycles = 0;
        long started = Environment.TickCount64, deadline = started + seconds * 1000L, baseline = 0, maximum = 0, last = 0;
        using (var server = new VirtualIedFaultTests.NativeIed(peer, "typedserver", port))
        {
            while (Environment.TickCount64 < deadline || cycles < 8)
            {
                Host("127.0.0.1", port, false);
                Loop(peer);
                cycles++;
                last = GC.GetTotalMemory(true);
                if (cycles == 8) baseline = maximum = last;
                if (cycles > 8 && last > maximum) maximum = last;
                if (cycles % 100 == 0)
                    Console.WriteLine("TYPED SOAK cycles=" + cycles + " elapsedMs=" + (Environment.TickCount64 - started) +
                        " managedBytes=" + last);
            }
        }
        Console.WriteLine("TYPED SOAK PASS cycles=" + cycles + " operates=" + (cycles * 48L) +
            " enhancedCompletions=" + (cycles * 24L) + " elapsedMs=" + (Environment.TickCount64 - started) +
            " managedBaseline=" + baseline + " managedMaximum=" + maximum + " managedLast=" + last);
    }
    internal static void Host(string host, int port, bool rangeChecks = true)
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            socket.Connect(IPAddress.Parse(host), port);
            var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
            client.Associate(); TypedControlChecks.Run(client, rangeChecks); client.Release();
        }
        Console.WriteLine("TYPED CONTROL HOST PASS");
    }
    internal static void Run()
    {
        for (int value = 0; value < 4; value++)
        {
            var variable = Iec61850ServerData.CreateDoublePoint("D", "dp", (IecDoublePoint)value);
            var bytes = variable.EncodeValue();
            if (Iec61850Data.ReadDoublePoint(MmsValue.Decode(MmsEncoding.Single(bytes, 0, bytes.Length, 0x84))) != (IecDoublePoint)value)
                throw new Exception("Dbpos wire mapping.");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) variable.SetDoublePoint((IecDoublePoint)value);
            if (GC.GetAllocatedBytesForCurrentThread() != before) throw new Exception("Dbpos setter allocated.");
        }
        var analog = new MmsServerControl("D", "L$CO$A", MmsControlModel.DirectNormal,
            valueKind: MmsControlValueKind.AnalogFloat32);
        analog.Enabled = true;
        object owner = new object();
        if (analog.Apply(owner, 0, ControlTests.Command(true), true) != 7) throw new Exception("APC accepted Boolean ctlVal.");
        foreach (uint bits in new uint[] { 0x7f800000, 0xff800000, 0x7fc00000 })
        {
            var normal = ControlTests.Command(false);
            var fields = normal.Children(); fields.Read(0x83);
            byte[] value = MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0x87,
                new byte[] { 8, (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits }));
            byte[] origin = MmsEncoding.Tlv(0xa2, fields.Read(0xa2).Copy());
            byte[] encoded = MmsEncoding.Tlv(0xa2, value, origin, MmsEncoding.Int(0x86, 1),
                MmsEncoding.Tlv(0x91, new byte[8]), MmsEncoding.Tlv(0x83, new byte[] { 0 }),
                MmsEncoding.Tlv(0x84, new byte[] { 6, 0 }));
            if (analog.Apply(owner, 0, MmsEncoding.Single(encoded, 0, encoded.Length, 0xa2), true) != 11 ||
                analog.AnalogValue != 0) throw new Exception("APC accepted nonfinite value.");
        }
        Loop(null);
        Console.WriteLine("TYPED: DPC Boolean/Dbpos, APC AnalogueValue.f, four control models/Test/cancel/readback, Dbpos setter=0 allocation PASS.");
    }
    private static void Loop(string peer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var model = new TypedControlModel();
        var worker = Task.Run(() => { using (var socket = listener.AcceptSocket()) model.Serve(socket); });
        try
        {
            if (peer == null) Host("127.0.0.1", port);
            else
            {
                using (var process = Process.Start(new ProcessStartInfo(peer, "typedclient " + port + " 127.0.0.1") {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    try
                    {
                        if (!process.WaitForExit(20000)) throw new TimeoutException("Native typed client.");
                        string text = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
                        if (process.ExitCode != 0 || !text.Contains("NATIVE TYPED PASS")) throw new Exception(text + error);
                    }
                    finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
                }
            }
            worker.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { listener.Stop(); }
    }
    internal static void Interop(string peer)
    {
        Loop(peer);
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using (var process = Process.Start(new ProcessStartInfo(peer, "typedserver " + port) {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            try
            {
                if (process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() != "READY") throw new Exception("Typed native server not ready.");
                Host("127.0.0.1", port, false);
                process.StandardInput.WriteLine("quit");
                if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new Exception("Typed native server failed.");
            }
            finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
        }
        Console.WriteLine("NATIVE TYPED: original DPC/APC public APIs, four models, both directions PASS.");
    }
}
