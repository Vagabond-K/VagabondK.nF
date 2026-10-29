using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Client;
using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS.TCP;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok) { checks++; if (!ok) throw new Exception("MMS assertion " + checks); }
    private static void Reject<T>(Action action) where T : Exception
    {
        bool rejected = false;
        try { action(); } catch (T) { rejected = true; }
        Check(rejected);
    }
    // 기본 실행은 MMS만의 경계·BER·루프백 시험입니다. 인수 1개는 격리된 원본 피어 실행 파일 경로입니다.
    private static void Main(string[] args)
    {
        if (SoakRunner.TryRun(args, "MMS", () => Loop(false, 100),
            () => { Boundaries(); Fuzz(); }, () => { Loop(true); Loop(false); })) return;
        Boundaries();
        Ber();
        ServerVariables();
        SessionConstructors();
        Fuzz();
        Loop(false); Loop(true);
        if (args.Length == 1) Native(args[0]);
        else if (args.Length != 0) throw new ArgumentException("Optional argument: original peer executable.");
        Console.WriteLine("MMS ONLY PASS: " + checks + " checks; no IEC 61850 sources or references.");
    }
    private static byte[] Report(byte[] specification, params byte[][] results)
    { return MmsEncoding.Tlv(0xa3, MmsEncoding.Tlv(0xa0, specification, MmsEncoding.Tlv(0xa0, results))); }
    private static MmsInformationReport Decode(byte[] data)
    { return MmsInformationReport.Decode(MmsEncoding.Single(data, 0, data.Length, 0xa3)); }
    private static byte[] Explicit(params byte[][] names)
    {
        var entries = new byte[names.Length][];
        for (int i = 0; i < entries.Length; i++) entries[i] = MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0, names[i]));
        return MmsEncoding.Tlv(0xa0, entries);
    }
    // 일반 보고의 이름 공간·항목별 오류·버퍼 독립성과 잘림·개수·전체 노드 제한을 함께 검증합니다.
    private static void Boundaries()
    {
        byte[] vmd = MmsEncoding.Tlv(0x80, new byte[] { 86 });
        byte[] association = MmsEncoding.Tlv(0x82, new byte[] { 65 });
        byte[] domain = MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier("Plant"), MmsEncoding.Identifier("Temperature"));
        byte[] bytes = Report(Explicit(vmd, association, domain), MmsEncoding.Int(0x85, 42),
            MmsEncoding.Int(0x80, 10), MmsEncoding.Tlv(0x83, new byte[] { 1 }));
        var report = Decode(bytes);
        Check(report.Count == 3 && report.NamedVariableList == null);
        Check(report.GetVariable(0).Scope == MmsObjectScope.Vmd && report.GetVariable(0).Item == "V");
        Check(report.GetVariable(1).Scope == MmsObjectScope.Association && report.GetResult(1).ErrorCode == 10);
        Check(report.GetVariable(2).Scope == MmsObjectScope.Domain && report.GetVariable(2).Domain == "Plant");
        Check(report.GetResult(0).Value.Integer == 42 && report.GetResult(2).Value.Boolean);
        Array.Clear(bytes, 0, bytes.Length);
        Check(report.GetResult(0).Value.Integer == 42 && report.GetVariable(2).Item == "Temperature");
        foreach (byte[] name in new[] { vmd, association, domain })
        {
            bytes = Report(MmsEncoding.Tlv(0xa1, name), MmsEncoding.Int(0x85, 7));
            var named = Decode(bytes);
            Check(named.NamedVariableList != null && named.Count == 1);
            Reject<InvalidOperationException>(() => named.GetVariable(0));
            for (int cut = 0; cut < bytes.Length; cut++)
            {
                var prefix = new byte[cut]; Array.Copy(bytes, prefix, cut);
                Reject<MmsProtocolException>(() => Decode(prefix));
            }
        }
        Reject<MmsProtocolException>(() => Decode(Report(Explicit(vmd), MmsEncoding.Int(0x80, 12))));
        Reject<MmsProtocolException>(() => Decode(Report(Explicit(vmd))));
        Reject<MmsProtocolException>(() => Decode(Report(Explicit(), MmsEncoding.Int(0x85, 1))));
        Reject<MmsProtocolException>(() => Decode(Report(Explicit(vmd, domain), MmsEncoding.Int(0x85, 1))));
        Reject<MmsProtocolException>(() => Decode(Report(Explicit(MmsEncoding.Tlv(0x83)), MmsEncoding.Int(0x85, 1))));
        var values = new byte[65][]; var names = new byte[65][];
        for (int i = 0; i < values.Length; i++) { values[i] = MmsEncoding.Int(0x85, i); names[i] = vmd; }
        Reject<MmsProtocolException>(() => Decode(Report(MmsEncoding.Tlv(0xa1, vmd), values)));
        Reject<MmsProtocolException>(() => Decode(Report(Explicit(names), values)));
        Array.Resize(ref values, 64); Array.Resize(ref names, 64);
        Check(Decode(Report(Explicit(names), values)).Count == 64);
        Check(Decode(Report(MmsEncoding.Tlv(0xa1, vmd), values)).Count == 64);
        var many = new byte[255][];
        for (int i = 0; i < many.Length; i++) many[i] = MmsEncoding.Int(0x85, i);
        Check(Decode(Report(MmsEncoding.Tlv(0xa1, vmd), MmsEncoding.Tlv(0xa2, many))).GetResult(0).Value.Count == 255);
        Reject<MmsProtocolException>(() => Decode(Report(MmsEncoding.Tlv(0xa1, vmd),
            MmsEncoding.Tlv(0xa2, many), MmsEncoding.Int(0x85, 1))));
    }
    private static void Ber()
    {
        foreach (int length in new[] { 0, 1, 127, 128, 255, 256, 65535, 65536, 16777215, 16777216 })
        {
            var header = new byte[6];
            int size = BerEncoding.WriteHeader(header, 0, 0x04, length);
            Check(size == (length < 128 ? 2 : length <= 255 ? 3 : length <= 65535 ? 4 : length <= 16777215 ? 5 : 6));
            Check(length < 128 ? header[1] == length : header[1] == (byte)(128 | (size - 2)));
            var buffer = new byte[size + length];
            Array.Copy(header, buffer, size);
            int position = 0;
            BerEncoding.Read(buffer, ref position, buffer.Length, out byte tag, out int offset, out int count);
            Check(tag == 0x04 && offset == size && count == length && position == buffer.Length);
            if (length > 0)
            {
                position = 0;
                Reject<MmsProtocolException>(() => BerEncoding.Read(buffer, ref position, buffer.Length - 1, out _, out _, out _));
            }
        }
        Reject<ArgumentOutOfRangeException>(() => BerEncoding.WriteHeader(new byte[6], 0, 0x04, -1));
        Reject<ArgumentOutOfRangeException>(() => BerEncoding.WriteHeader(new byte[6], 0, 0x00, 1));
        Reject<ArgumentOutOfRangeException>(() => BerEncoding.WriteHeader(new byte[6], 0, 0x1f, 1));
        foreach (byte[] invalid in new[] {
            new byte[] { 0x04, 0x80, 0x00, 0x00 },
            new byte[] { 0x04, 0x85, 0, 0, 0, 0, 1, 0 },
            new byte[] { 0x04, 0x82, 0x01 },
            new byte[] { 0x04, 0x02, 0x00 },
            new byte[] { 0x00, 0x00 },
            new byte[] { 0x1f, 0x01, 0x00 },
            new byte[] { 0x04 },
            new byte[0] })
        {
            int position = 0;
            Reject<MmsProtocolException>(() => BerEncoding.Read(invalid, ref position, invalid.Length, out _, out _, out _));
            Check(position == 0);
        }
        var indefinite = new byte[200];
        indefinite[0] = 0x04;
        indefinite[1] = 0x80;
        int start = 0;
        Reject<MmsProtocolException>(() => BerEncoding.Read(indefinite, ref start, indefinite.Length, out _, out _, out _));
        Check(start == 0);
        foreach (long value in new[] { 0L, 1, -1, 127, 128, -128, -129, 255, 256, 32767, 32768, int.MinValue, int.MaxValue,
            uint.MaxValue, long.MinValue, long.MaxValue })
        {
            var buffer = new byte[10];
            int size = BerEncoding.WriteInteger(buffer, 0, 0x85, value);
            var element = MmsEncoding.Single(buffer, 0, size, 0x85);
            Check(element.Integer() == value && element.Length <= 8);
            Check(element.Length == 1 || !(buffer[2] == 0 && (buffer[3] & 128) == 0 || buffer[2] == 255 && (buffer[3] & 128) != 0));
        }
        Reject<MmsProtocolException>(() => BerEncoding.ReadInteger(new byte[] { 0, 1 }, 0, 2));
        Reject<MmsProtocolException>(() => BerEncoding.ReadInteger(new byte[] { 255, 128 }, 0, 2));
        Reject<MmsProtocolException>(() => BerEncoding.ReadInteger(new byte[9], 0, 9));
        Reject<MmsProtocolException>(() => BerEncoding.ReadInteger(new byte[1], 0, 0));
        Check(BerEncoding.ReadInteger(new byte[] { 0, 128 }, 0, 2) == 128);
        Check(BerEncoding.ReadInteger(new byte[] { 255, 127 }, 0, 2) == -129);
        Reject<MmsProtocolException>(() => BerEncoding.ReadBoolean(new byte[2], 0, 2));
        Check(BerEncoding.ReadBoolean(new byte[] { 0xff }, 0, 1) && !BerEncoding.ReadBoolean(new byte[1], 0, 1));
    }
    private static void Fuzz()
    {
        var random = new Random(9506);
        byte[] vmd = MmsEncoding.Tlv(0x80, new byte[] { 86 });
        byte[] domain = MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier("Plant"), MmsEncoding.Identifier("Temperature"));
        byte[] structure = MmsEncoding.Tlv(0xa2, MmsEncoding.Int(0x85, -5), MmsEncoding.Tlv(0x83, new byte[] { 0 }),
            MmsEncoding.Tlv(0x8a, new byte[] { 65, 66 }), MmsEncoding.Tlv(0xa1, MmsEncoding.Int(0x86, 7)));
        byte[] seed = Report(Explicit(vmd, domain), MmsEncoding.Int(0x85, 42), structure);
        int decoded = 0, rejected = 0;
        for (int iteration = 0; iteration < 30000; iteration++)
        {
            byte[] data;
            if (iteration % 3 == 0)
            {
                data = new byte[random.Next(64)];
                random.NextBytes(data);
                if (data.Length > 0 && iteration % 2 == 0) data[0] = 0xa3;
            }
            else
            {
                data = (byte[])seed.Clone();
                int mutations = 1 + random.Next(4);
                for (int i = 0; i < mutations; i++)
                {
                    int position = random.Next(data.Length);
                    if (random.Next(2) == 0) data[position] = (byte)random.Next(256);
                    else data[position] ^= (byte)(1 << random.Next(8));
                }
                if (random.Next(4) == 0) Array.Resize(ref data, random.Next(data.Length + 1));
            }
            try
            {
                var report = Decode(data);
                for (int i = 0; i < report.Count; i++) { var result = report.GetResult(i); Check(result.Value != null || result.ErrorCode >= 0); }
                decoded++;
            }
            catch (MmsProtocolException) { rejected++; }
            try
            {
                if (data.Length > 0) { MmsValue.Decode(MmsEncoding.Single(data, 0, data.Length, data[0])); decoded++; }
            }
            catch (MmsProtocolException) { rejected++; }
        }
        Check(decoded > 0 && rejected > 0);

        int packets = 0;
        var decoder = new TpktDecoder(64, (packet, length) => { Check(length >= 7 && length <= 64 && packet[0] == 3); packets++; });
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            var chunk = new byte[random.Next(80)];
            random.NextBytes(chunk);
            if (iteration % 2 == 0 && chunk.Length >= 4)
            {
                chunk[0] = 3; chunk[1] = 0; chunk[2] = 0; chunk[3] = (byte)(7 + random.Next(chunk.Length - 3 < 58 ? chunk.Length - 3 : 58));
            }
            try
            {
                for (int offset = 0; offset < chunk.Length;)
                {
                    int count = 1 + random.Next(chunk.Length - offset);
                    decoder.Feed(chunk, offset, count);
                    offset += count;
                }
            }
            catch (MmsProtocolException) { Check(decoder.Faulted); decoder.Reset(); Check(decoder.BufferedBytes == 0); }
        }
        Check(packets > 0);
    }
    private static BerElement Element(byte[] encoded) { return MmsEncoding.Single(encoded, 0, encoded.Length, encoded[0]); }
    private static void SessionConstructors()
    {
        var channel = new ConstructorChannel();
        var variable = new MmsServerVariable("Plant", "Count", 5);
        var variables = new[] { variable };
        var list = new MmsServerNamedVariableList("Plant", "Counts", variables);
        var sessions = new[] {
            new MmsServerSession(channel, variables),
            new MmsServerSession(channel, variables, 1024),
            new MmsServerSession(channel, variables, 2048, 1234),
            new MmsServerSession(channel, variables, 3072, 2345, 1),
            new MmsServerSession(channel, variables, 4096, 3456, 16, null),
            new MmsServerSession(channel, variables, namedVariableLists: new[] { list }),
            new MmsServerSession(channel, variables, timeoutMilliseconds: 4567)
        };
        int[] timeouts = { 10000, 10000, 1234, 2345, 3456, 10000, 4567 };
        for (int i = 0; i < sessions.Length; i++)
        {
            Check(!sessions[i].Associated && !sessions[i].Faulted);
            Check(sessions[i].channel == channel && sessions[i].variables != variables &&
                sessions[i].variables[0] == variable);
            Reject<System.IO.EndOfStreamException>(() => sessions[i].Associate());
            Check(channel.LastTimeout == timeouts[i] && sessions[i].Faulted);
            sessions[i].Close();
        }
        Reject<ArgumentNullException>(() => new MmsServerSession(null, variables));
        Reject<ArgumentNullException>(() => new MmsServerSession(channel, null));
        Reject<ArgumentOutOfRangeException>(() => new MmsServerSession(channel, variables, 1023));
        Reject<ArgumentOutOfRangeException>(() => new MmsServerSession(channel, variables, 32769));
        Reject<ArgumentOutOfRangeException>(() => new MmsServerSession(channel, variables, 1024, 0));
        Reject<ArgumentOutOfRangeException>(() => new MmsServerSession(channel, variables, 1024, 1, 0));
        Reject<ArgumentOutOfRangeException>(() => new MmsServerSession(channel, variables, 1024, 1, 129));
        Reject<ArgumentException>(() => new MmsServerSession(channel, new[] { variable, variable }));
    }
    private sealed class ConstructorChannel : IMmsChannel
    {
        internal int LastTimeout;
        public long Milliseconds { get { return 0; } }
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            LastTimeout = timeoutMilliseconds;
            throw new System.IO.EndOfStreamException("Constructor timeout probe.");
        }
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            throw new InvalidOperationException("No request received.");
        }
    }
    private static void ServerVariables()
    {
        var flag = new MmsServerVariable("Plant", "Flag", false);
        var count = new MmsServerVariable("Plant", "Count", 5);
        var level = new MmsServerVariable("Plant", "Level", 0f);
        long version = flag.ChangeVersion;
        flag.UpdateBoolean(false); Check(flag.ChangeVersion == version);
        flag.UpdateBoolean(true); Check(flag.ChangeVersion == version + 1 && flag.EncodeValue()[2] == 1);
        version = count.ChangeVersion;
        count.UpdateInteger(5); Check(count.ChangeVersion == version);
        foreach (int value in new[] { 128, -129, int.MinValue, int.MaxValue })
        {
            count.UpdateInteger(value);
            Check(count.ChangeVersion == ++version && Element(count.EncodeValue()).Integer() == value);
            count.UpdateInteger(value); Check(count.ChangeVersion == version);
        }
        version = level.ChangeVersion;
        level.UpdateFloatingPoint(0f); Check(level.ChangeVersion == version);
        level.UpdateFloatingPoint(-0f); Check(level.ChangeVersion == ++version && level.EncodeValue()[3] == 0x80);
        level.UpdateFloatingPoint(1.5f); Check(level.ChangeVersion == ++version);
        level.UpdateFloatingPoint(1.5f); Check(level.ChangeVersion == version);
        level.UpdateFloatingPoint(float.NaN); Check(level.ChangeVersion == ++version);
        Reject<InvalidOperationException>(() => flag.UpdateInteger(1));
        Reject<InvalidOperationException>(() => count.UpdateFloatingPoint(1));
        Reject<InvalidOperationException>(() => level.UpdateBoolean(true));
        Reject<InvalidOperationException>(() => level.SetInteger(1));
        var composite = new MmsServerVariable("Plant", "Group", new[] { flag, count });
        Reject<InvalidOperationException>(() => composite.EnableWrite());
        long compositeVersion = composite.ChangeVersion;
        count.UpdateInteger(77); Check(composite.ChangeVersion == compositeVersion + 1);

        Check(!count.Writable && count.ApplyWrite(Element(MmsEncoding.Int(0x85, 1))) == 3);
        int handled = 0, handledValue = 0, handlerResult = -1, validatorResult = -1;
        count.EnableWrite(v => validatorResult, v => { handled++; handledValue = (int)v.Integer; return handlerResult; });
        Check(count.Writable);
        Check(count.ApplyWrite(Element(MmsEncoding.Tlv(0x83, new byte[] { 1 }))) == 7 && handled == 0);
        Check(count.ApplyWrite(Element(MmsEncoding.Int(0x85, (long)int.MaxValue + 1))) == 11 && handled == 0);
        Check(count.ApplyWrite(Element(MmsEncoding.Int(0x85, (long)int.MinValue - 1))) == 11 && handled == 0);
        validatorResult = 2;
        Check(count.ApplyWrite(Element(MmsEncoding.Int(0x85, 10))) == 2 && handled == 0);
        validatorResult = -1; handlerResult = 5; version = count.ChangeVersion;
        Check(count.ApplyWrite(Element(MmsEncoding.Int(0x85, 11))) == 5 && handled == 1 && handledValue == 11);
        Check(count.ChangeVersion == version && Element(count.EncodeValue()).Integer() == 77);
        handlerResult = -1;
        Check(count.ApplyWrite(Element(MmsEncoding.Int(0x85, 12))) == -1 && handled == 2 && handledValue == 12);
        Check(count.ChangeVersion == version + 1 && Element(count.EncodeValue()).Integer() == 12);
        handlerResult = 12;
        Reject<InvalidOperationException>(() => count.ApplyWrite(Element(MmsEncoding.Int(0x85, 13))));
        handlerResult = -2;
        Reject<InvalidOperationException>(() => count.ApplyWrite(Element(MmsEncoding.Int(0x85, 13))));
        validatorResult = 12;
        Reject<InvalidOperationException>(() => count.ApplyWrite(Element(MmsEncoding.Int(0x85, 13))));
        Check(Element(count.EncodeValue()).Integer() == 12);
        count.DisableWrite();
        Check(!count.Writable && count.ApplyWrite(Element(MmsEncoding.Int(0x85, 1))) == 3);

        level.EnableWrite();
        Check(level.ApplyWrite(Element(MmsEncoding.Tlv(0x87, new byte[] { 8, 0x3f, 0xc0, 0, 0 }))) == -1);
        Check(level.EncodeValue()[3] == 0x3f && level.EncodeValue()[4] == 0xc0);
        Check(level.ApplyWrite(Element(MmsEncoding.Tlv(0x87, new byte[] { 8, 0x3f, 0xc0, 0 }))) == 7);
        Check(level.ApplyWrite(Element(MmsEncoding.Tlv(0x87, new byte[] { 11, 0x3f, 0xc0, 0, 0 }))) == 7);
        Check(level.ApplyWrite(Element(MmsEncoding.Int(0x85, 1))) == 7);
        flag.EnableWrite(v => v.Boolean ? -1 : 4);
        Check(flag.ApplyWrite(Element(MmsEncoding.Tlv(0x83, new byte[] { 0 }))) == 4 && flag.EncodeValue()[2] == 1);
        Reject<MmsProtocolException>(() => flag.ApplyWrite(Element(MmsEncoding.Tlv(0x83, new byte[] { 0, 0 }))));
    }
    private static void Loop(bool handlerFailure, int iterations = 1)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var value = new MmsServerVariable("Plant", "Temperature", 42); value.EnableWrite();
        var reservedLookingName = new MmsServerVariable("Plant", "L$CO$Ordinary", true);
        var model = new[] { value, reservedLookingName };
        var set = new MmsServerNamedVariableList("Plant", "Measurements", model);
        Task server = Task.Run(() => {
            try
            {
                using (var socket = listener.AcceptSocket())
                {
                    socket.NoDelay = true;
                    var session = new MmsServerSession(new MmsSocketChannel(socket), model,
                        maximumNamesPerPage: 1, namedVariableLists: new[] { set });
                    try
                    {
                        session.Associate();
                        session.SendInformationReport(set);
                        if (handlerFailure)
                        {
                            Check(!session.ProcessRequest());
                            return;
                        }
                        while (session.ProcessRequest())
                            if (value.EncodeValue()[0] == 0x85 && value.ChangeVersion > 1)
                            { session.SendInformationReport(set); break; }
                        Check(session.ProcessRequest() == false);
                    }
                    finally { session.Close(); }
                }
            }
            finally { listener.Stop(); }
        });
        try
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.NoDelay = true;
                socket.Connect(IPAddress.Loopback, port);
                var client = new MmsClient(new MmsSocketChannel(socket), maximumTpduSize: 128);
                client.Associate();
                int received = 0;
                client.InformationReportReceived = report => {
                    received++;
                    Check(report.NamedVariableList.Domain == "Plant" && report.Count == 2);
                    Reject<InvalidOperationException>(() => client.Read("Plant", "Temperature"));
                    if (handlerFailure) throw new ApplicationException("Injected callback failure.");
                };
                if (handlerFailure)
                {
                    Reject<ApplicationException>(() => client.ReceiveInformationReport());
                    Check(client.Faulted && !client.Associated);
                    Reject<InvalidOperationException>(() => client.Read("Plant", "Temperature"));
                }
                else
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        long started = SoakRunner.StartExchange();
                        Check(client.Read("Plant", "Temperature").Integer == 42);
                        SoakRunner.EndExchange(started);
                    }
                    Check(client.Read("Plant", "Temperature").Integer == 42 && received == 1);
                    Check(client.GetNameList().Names[0] == "Plant");
                    var page = client.GetNameList("Plant", maximumNames: 1);
                    Check(page.MoreFollows && page.Names[0] == "L$CO$Ordinary");
                    Check(client.GetNameList("Plant", page.Names[0]).Names[0] == "Temperature");
                    Check(client.Read("Plant", "L$CO$Ordinary").Boolean);
                    var directory = client.GetNamedVariableListDirectory("Plant", "Measurements");
                    Check(directory.Count == 2 && !directory.Deletable);
                    Check(client.ReadNamedVariableList(directory)[0].Value.Integer == 42);
                    Check(client.ReadMultiple(new[] { new MmsVariableReference("Plant", "missing"),
                        new MmsVariableReference("Plant", "Temperature") })[0].ErrorCode == 10);
                    client.Write("Plant", "Temperature", 43);
                    var last = client.ReceiveInformationReport();
                    Check(last.GetResult(0).Value.Integer == 43 && received == 2);
                    client.Release(); Check(!client.Associated && !client.Faulted);
                }
            }
            Check(server.Wait(15000));
        }
        finally { listener.Stop(); }
    }
    private static void Native(string executable)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using (var process = Process.Start(new ProcessStartInfo(executable, "server " + port) {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            try
            {
                Check(process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == "READY");
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Loopback, port);
                    var client = new MmsClient(new MmsSocketChannel(socket)); client.Associate();
                    Check(client.Read("NFTESTLD0", "GGIO1$ST$Status$stVal").Boolean);
                    Check(client.Read("NFTESTLD0", "GGIO1$ST$Count$stVal").Integer == -123456);
                    client.Release();
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.StandardInput.WriteLine("quit");
                    if (!process.WaitForExit(10000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Native MMS peer shutdown."); }
                }
            }
            Check(process.ExitCode == 0);
        }
    }
}
