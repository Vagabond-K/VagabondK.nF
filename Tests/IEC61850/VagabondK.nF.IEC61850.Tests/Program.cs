using VagabondK.nF.MMS;
using System;
using System.Formats.Asn1;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;

internal static class Program
{
    private static int checks;

    private static void Check(bool valid)
    {
        checks++;
        if (!valid) throw new Exception("IEC61850 foundation assertion " + checks);
    }

    private static void Reject(Action action, Type expected)
    {
        try { action(); }
        catch (Exception error)
        {
            Check(error.GetType() == expected);
            return;
        }
        throw new Exception("Expected exception " + expected);
    }

    private static void Equal(byte[] a, byte[] b)
    {
        Check(a.Length == b.Length);
        for (int i = 0; i < a.Length; i++) Check(a[i] == b[i]);
    }

    // 기본 실행은 메모리의 통신 기록과 로컬 TCP를 이용한 회귀 시험입니다. 명시적으로 선택한 모드에서만 외부 피어·보드·장시간 시험을 수행합니다.
    // 오류 주입/제어 모드는 격리된 시험 peer에만 사용하고, 캡처 모드의 출력 위치와 포트 충돌을 확인하십시오.
    private static void Main(string[] args)
    {
        if (args.Length == 5 && args[0] == "--hardware-client-fault")
        {
            HardwareClientFaultPeer.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4]); return;
        }
        if (args.Length == 4 && args[0] == "--hardware-fault")
        {
            HardwareFaultPeer.Run(args[1], int.Parse(args[2]), args[3]); return;
        }
        if (SoakRunner.TryRun(args, "IEC61850", LifecycleTests.SoakNormal,
            () => { Transport(); SocketChannelTests.Run(); }, LifecycleTests.Run)) return;
        if (args.Length == 3 && args[0] == "--typed-soak")
        {
            TypedControlTests.Soak(args[1], int.Parse(args[2])); return;
        }
        if (args.Length == 2 && args[0] == "--virtual-faults")
        {
            VirtualIedFaultTests.Run(args[1]); return;
        }
        if (args.Length == 3 && args[0] == "--typed-controls-host")
        {
            for (int i = 0; i < 8; i++) TypedControlTests.Host(args[1], int.Parse(args[2]));
            return;
        }
        if (args.Length == 1 && args[0] == "--report-soak-loopback")
        {
            ReportSoakTests.Loopback(); return;
        }
        if (args.Length == 4 && args[0] == "--report-soak")
        {
            ReportSoakTests.Run(args[1], int.Parse(args[2]), int.Parse(args[3])); return;
        }
        if (args.Length == 2 && args[0] == "--data-capture")
        {
            PeerInterop.Capture(args[1], data: true);
            return;
        }
        if (args.Length == 2 && args[0] == "--sbo-enhanced-capture")
        {
            PeerInterop.Capture(args[1], sboEnhanced: true);
            return;
        }
        if (args.Length == 2 && args[0] == "--enhanced-capture")
        {
            PeerInterop.Capture(args[1], enhanced: true);
            return;
        }
        if (args.Length == 1 && args[0] == "--server-perf")
        {
            ServerPerformanceTests.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "--read")
        {
            if (args.Length != 5) throw new ArgumentException("--read <IPv4> <port> <domain> <item>");
            using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp))
            {
                socket.Connect(System.Net.IPAddress.Parse(args[1]), int.Parse(args[2]));
                var client = new Iec61850Client(new VagabondK.nF.MMS.TCP.MmsSocketChannel(socket));
                client.Associate();
                MmsValue value = client.Read(args[3], args[4]);
                Console.WriteLine("MMS Read type=" + value.Kind);
                switch (value.Kind)
                {
                    case MmsValueKind.Boolean: Console.WriteLine(value.Boolean); break;
                    case MmsValueKind.Integer: Console.WriteLine(value.Integer); break;
                    case MmsValueKind.Unsigned: Console.WriteLine(value.Unsigned); break;
                    case MmsValueKind.FloatingPoint: Console.WriteLine(value.FloatingPoint); break;
                    case MmsValueKind.VisibleString: Console.WriteLine(value.VisibleString); break;
                    case MmsValueKind.BitString:
                        Console.WriteLine("bits=" + value.BitCount + " bytes=" + Convert.ToHexString(value.GetBytes())); break;
                    case MmsValueKind.OctetString:
                    case MmsValueKind.UtcTime: Console.WriteLine(Convert.ToHexString(value.GetBytes())); break;
                }
            }
            return;
        }
        if (args.Length != 0 && (args.Length != 2 || (args[0] != "--capture" && args[0] != "--control-capture" && args[0] != "--interop")))
            throw new ArgumentException("Use no arguments, --interop <peer>, --virtual-faults <peer>, --hardware-fault <IPv4> <scenario> <capture>, --hardware-client-fault <IPv4> <port> <scenario> <capture>, --typed-soak <peer> <seconds>, --capture <peer>, --control-capture <peer>, --enhanced-capture <peer>, --sbo-enhanced-capture <peer>, --server-perf, or --read <IPv4> <port> <domain> <item>.");
        Ber();
        Transport();
        MmsClientTests.Run();
        CompositeTests.Run();
        WriteTests.Run();
        ControlTests.Run();
        EnhancedControlTests.Run();
        SboEnhancedTests.Run();
        SocketChannelTests.Run();
        TimeoutResultTests.Run();
        ServerTests.Run();
        LifecycleTests.Run();
        TypedControlTests.Run();
        DataServiceTests.Run();
        DataBoundaryTests.Run();
        ReportBoundaryTests.Run();
        SclGeneratorTests.Run();
        MonitorSampleTests.Run();
        if (args.Length == 2 && args[0] == "--capture") PeerInterop.Capture(args[1]);
        if (args.Length == 2 && args[0] == "--control-capture") PeerInterop.Capture(args[1], true);
        else if (args.Length == 2 && args[0] == "--interop")
        {
            PeerInterop.Run(args[1]);
            ControlTests.Interop(args[1]);
            EnhancedControlTests.Interop(args[1]);
            EnhancedControlTests.Interop(args[1], true);
            ServerTests.Interop(args[1]);
            DataServiceTests.Interop(args[1]);
            LifecycleTests.Interop(args[1]);
            TypedControlTests.Interop(args[1]);
            VirtualIedFaultTests.Run(args[1]);
            MonitorSampleTests.Interop(args[1]);
        }
        Console.WriteLine("IEC61850 foundation: " + checks + " assertions passed.");
    }

    private static void Ber()
    {
        long[] values = { long.MinValue, long.MinValue + 1, int.MinValue, -32769, -32768,
            -129, -128, -1, 0, 1, 127, 128, 255, 256, 32767, 32768, int.MaxValue,
            (long)uint.MaxValue, long.MaxValue };
        foreach (long value in values)
        {
            var writer = new AsnWriter(AsnEncodingRules.BER);
            writer.WriteInteger(value);
            byte[] expected = writer.Encode();
            var buffer = new byte[20];
            int count = BerEncoding.WriteInteger(buffer, 3, 2, value);
            var actual = new byte[count];
            Array.Copy(buffer, 3, actual, 0, count);
            Equal(actual, expected);
            var reader = new AsnReader(actual, AsnEncodingRules.BER);
            Check(reader.ReadInteger() == value && !reader.HasData);
            int p = 3;
            byte tag;
            int start, length;
            BerEncoding.Read(buffer, ref p, 3 + count, out tag, out start, out length);
            Check(tag == 2 && p == 3 + count);
            Check(BerEncoding.ReadInteger(buffer, start, length) == value);
        }
        int[] lengths = { 0, 1, 127, 128, 255, 256, 65535, 65536 };
        foreach (int length in lengths)
        {
            byte[] content = new byte[length];
            var writer = new AsnWriter(AsnEncodingRules.BER);
            writer.WriteOctetString(content);
            byte[] expected = writer.Encode();
            byte[] actual = new byte[expected.Length];
            int header = BerEncoding.WriteHeader(actual, 0, 4, length);
            Check(header + length == expected.Length);
            Equal(actual, expected);
            int p = 0;
            byte tag;
            int start, size;
            BerEncoding.Read(expected, ref p, expected.Length, out tag, out start, out size);
            Check(tag == 4 && start == header && size == length && p == expected.Length);
        }
        for (int i = 0; i < 256; i++)
            Check(BerEncoding.ReadBoolean(new byte[] { (byte)i }, 0, 1) == (i != 0));
        byte[][] malformed = {
            new byte[0], new byte[] { 2 }, new byte[] { 2, 128, 0, 0 },
            new byte[] { 31, 0 }, new byte[] { 0, 0 }, new byte[] { 2, 255 },
            new byte[] { 2, 132, 255, 255, 255, 255 }, new byte[] { 2, 2, 1 },
            new byte[] { 2, 130, 1 }, new byte[] { 2, 133, 0, 0, 0, 0, 1, 0 }
        };
        foreach (byte[] bytes in malformed)
        {
            int p = 0;
            Reject(() => {
                byte t;
                int o, n;
                BerEncoding.Read(bytes, ref p, bytes.Length, out t, out o, out n);
            }, typeof(MmsProtocolException));
            Check(p == 0);
        }
        // BER는 DER와 달리 길이를 최소 바이트 수보다 길게 명시한 형식도 허용합니다.
        byte[] paddedLength = { 4, 130, 0, 1, 42 };
        int position = 0;
        byte paddedTag;
        int contentStart, contentLength;
        BerEncoding.Read(paddedLength, ref position, paddedLength.Length, out paddedTag, out contentStart, out contentLength);
        Check(contentLength == 1 && contentStart == 4 && position == 5);
        var berReader = new AsnReader(paddedLength, AsnEncodingRules.BER);
        Equal(berReader.ReadOctetString(), new byte[] { 42 });
        Reject(() => BerEncoding.ReadInteger(new byte[] { 0, 1 }, 0, 2), typeof(MmsProtocolException));
        Reject(() => BerEncoding.ReadInteger(new byte[] { 255, 128 }, 0, 2), typeof(MmsProtocolException));
        Reject(() => BerEncoding.ReadInteger(new byte[9], 0, 9), typeof(MmsProtocolException));
        Reject(() => BerEncoding.ReadBoolean(new byte[0], 0, 0), typeof(MmsProtocolException));
        Reject(() => BerEncoding.WriteHeader(new byte[4], 0, 31, 0), typeof(ArgumentOutOfRangeException));
        Reject(() => BerEncoding.WriteInteger(new byte[2], 0, 2, 1), typeof(ArgumentOutOfRangeException));
        Reject(() => BerEncoding.WriteHeader(new byte[6], 0, 2, -1), typeof(ArgumentOutOfRangeException));
        byte[] nested = { 48, 3, 2, 2, 1, 99 };
        int child = 2;
        Reject(() => {
            byte t;
            int o, n;
            BerEncoding.Read(nested, ref child, 5, out t, out o, out n);
        }, typeof(MmsProtocolException));
        Check(child == 2);
        var random = new Random(61850);
        var integerBytes = new byte[8];
        for (int i = 0; i < 2000; i++)
        {
            random.NextBytes(integerBytes);
            long value = BitConverter.ToInt64(integerBytes, 0);
            var writer = new AsnWriter(AsnEncodingRules.BER);
            writer.WriteInteger(value);
            byte[] expected = writer.Encode();
            var actual = new byte[expected.Length];
            Check(BerEncoding.WriteInteger(actual, 0, 2, value) == actual.Length);
            Equal(actual, expected);
            Check(BerEncoding.ReadInteger(actual, 2, actual.Length - 2) == value);
        }
        byte[] shortOutput = { 99, 99 };
        Reject(() => BerEncoding.WriteInteger(shortOutput, 0, 2, 1), typeof(ArgumentOutOfRangeException));
        Equal(shortOutput, new byte[] { 99, 99 });
    }

    private static void Transport()
    {
        // 시험 대상 인코더가 생성한 값이 아닌 고정 통신 벡터입니다.
        byte[] wire = { 3, 0, 0, 10, 2, 240, 128, 2, 1, 42 };
        for (int split = 0; split <= wire.Length; split++)
        {
            int delivered = 0;
            var decoder = new TpktDecoder(32, (bytes, count) => {
                Check(count == wire.Length);
                for (int i = 0; i < count; i++) Check(bytes[i] == wire[i]);
                bool eot;
                Check(CotpData.Read(bytes, count, out eot) == 7 && eot);
                delivered++;
            });
            decoder.Feed(wire, 0, split);
            decoder.Feed(wire, split, wire.Length - split);
            decoder.CompleteInput();
            Check(delivered == 1 && decoder.BufferedBytes == 0 && !decoder.Faulted);
        }
        int packets = 0;
        var stream = new TpktDecoder(10, (bytes, count) => packets++);
        var joined = new byte[wire.Length * 3];
        for (int i = 0; i < 3; i++) Array.Copy(wire, 0, joined, i * wire.Length, wire.Length);
        stream.Feed(joined, 0, joined.Length);
        Check(packets == 3);
        stream.Feed(joined, 0, wire.Length + 5);
        Check(packets == 4 && stream.BufferedBytes == 5);
        stream.Feed(joined, 5, 5);
        Check(packets == 5 && stream.BufferedBytes == 0);
        stream.Reset();
        foreach (byte b in wire) stream.Feed(new byte[] { b }, 0, 1);
        Check(packets == 6);
        for (int prefix = 1; prefix < wire.Length; prefix++)
        {
            stream.Reset();
            stream.Feed(wire, 0, prefix);
            Reject(() => stream.CompleteInput(), typeof(MmsProtocolException));
            Check(stream.Faulted);
            Reject(() => stream.Feed(wire, 0, wire.Length), typeof(InvalidOperationException));
        }
        for (int length = 0; length <= 65535; length++)
        {
            byte[] header = { 3, 0, (byte)(length >> 8), (byte)length };
            stream.Reset();
            if (length < 7 || length > 10)
            {
                Reject(() => stream.Feed(header, 0, 4), typeof(MmsProtocolException));
                Check(stream.Faulted);
            }
            else
            {
                stream.Feed(header, 0, 4);
                Check(!stream.Faulted && stream.BufferedBytes == 4);
            }
        }
        foreach (int index in new int[] { 0, 1 })
        {
            var bad = (byte[])wire.Clone();
            bad[index] = 1;
            stream.Reset();
            Reject(() => stream.Feed(bad, 0, bad.Length), typeof(MmsProtocolException));
        }
        stream.Reset();
        Reject(() => stream.Feed(wire, int.MaxValue, 1), typeof(ArgumentOutOfRangeException));
        Check(!stream.Faulted);
        Reject(() => new TpktDecoder(6, (b, n) => { }), typeof(ArgumentOutOfRangeException));
        Reject(() => new TpktDecoder(65536, (b, n) => { }), typeof(ArgumentOutOfRangeException));
        Reject(() => new TpktDecoder(10, null), typeof(ArgumentNullException));
        var failedCallback = new TpktDecoder(10, (b, n) => { throw new ApplicationException("observer"); });
        Reject(() => failedCallback.Feed(wire, 0, wire.Length), typeof(ApplicationException));
        Check(failedCallback.Faulted);
        TpktDecoder reentrant = null;
        reentrant = new TpktDecoder(10, (b, n) => reentrant.Reset());
        Reject(() => reentrant.Feed(wire, 0, wire.Length), typeof(InvalidOperationException));
        Check(reentrant.Faulted);
        byte[] encoded = new byte[10];
        Check(CotpData.Write(encoded, 0, new byte[] { 2, 1, 42 }, 0, 3, true) == 10);
        Equal(encoded, wire);
        Check(CotpData.Write(encoded, 0, encoded, 7, 3, false) == 10);
        bool end;
        Check(CotpData.Read(encoded, 10, out end) == 7 && !end);
        encoded[6] = 255;
        Check(CotpData.Read(encoded, 10, out end) == 7 && end);
        encoded[5] = 224;
        Reject(() => CotpData.Read(encoded, 10, out end), typeof(MmsProtocolException));
        var maximum = new byte[65535];
        Check(CotpData.Write(maximum, 0, maximum, 7, 65528, true) == 65535);
        int maxDelivered = 0;
        var maxDecoder = new TpktDecoder(65535, (b, n) => { Check(n == 65535); maxDelivered++; });
        maxDecoder.Feed(maximum, 0, maximum.Length);
        Check(maxDelivered == 1);
        Reject(() => CotpData.Write(maximum, 0, maximum, 0, 65529, true), typeof(ArgumentOutOfRangeException));
        int allocationPackets = 0;
        var allocationDecoder = new TpktDecoder(10, (b, n) => allocationPackets++);
        for (int i = 0; i < 100; i++) allocationDecoder.Feed(wire, 0, wire.Length);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) allocationDecoder.Feed(wire, 0, wire.Length);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0 && allocationPackets == 10100);
    }
}
