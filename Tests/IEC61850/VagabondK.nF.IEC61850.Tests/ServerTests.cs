using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>통신 기록을 나누어 수신하는 채널과 로컬·외부 클라이언트로 서버의 협상·응답 인코딩·서비스 오류 및 상태 전이를 검증합니다.</summary>
internal static class ServerTests
{
    private static int checks;
    private static void Check(bool valid)
    {
        checks++;
        if (!valid) throw new Exception("MMS Server assertion " + checks);
    }
    private static void Reject(Action action, Type type)
    {
        try { action(); }
        catch (Exception error) { Check(error.GetType() == type); return; }
        throw new Exception("Expected " + type);
    }
    internal static MmsServerVariable[] Model(int sequence)
    {
        return new MmsServerVariable[] {
            new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Status$stVal", sequence % 2 != 0),
            new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Count$stVal", -123456 + sequence),
            new MmsServerVariable("NFTESTLD0", "GGIO1$MX$Analog$mag", 12.5f + sequence),
            CompositeTests.Model()[0]
        };
    }
    internal static void Run()
    {
        var model = Model(1);
        Reject(() => new MmsServerVariable("", "i", true), typeof(ArgumentOutOfRangeException));
        Reject(() => model[0].SetInteger(1), typeof(InvalidOperationException));
        Reject(() => model[1].SetFloatingPoint(1), typeof(InvalidOperationException));
        Reject(() => model[2].SetBoolean(true), typeof(InvalidOperationException));
        var empty = new Transcript();
        Reject(() => new Iec61850ServerSession(null, model), typeof(ArgumentNullException));
        Reject(() => new Iec61850ServerSession(empty, null), typeof(ArgumentNullException));
        Reject(() => new Iec61850ServerSession(empty, new MmsServerVariable[0]), typeof(ArgumentOutOfRangeException));
        Reject(() => new Iec61850ServerSession(empty, new MmsServerVariable[] { model[0], model[0] }), typeof(ArgumentException));
        Reject(() => new Iec61850ServerSession(empty, new MmsServerVariable[] { null }), typeof(ArgumentException));
        Reject(() => new Iec61850ServerSession(empty, model).ProcessRead(), typeof(InvalidOperationException));
        byte[] association = MmsEncoding.Association(4096);
        for (int cut = 0; cut < association.Length; cut++)
        {
            var channel = ReadyTranscript(association, cut);
            var session = new Iec61850ServerSession(channel, model);
            try { session.Associate(); throw new Exception("Truncated server association accepted"); }
            catch (IOException) { }
            catch (MmsProtocolException) { }
            Check(session.Faulted && !session.Associated);
        }
        var valid = ReadyTranscript(association, association.Length);
        var server = new Iec61850ServerSession(valid, model);
        server.Associate();
        Check(server.Associated && server.NegotiatedPduSize == 4096);
        valid.AppendRead(MmsEncoding.ReadRequest(1, MmsEncoding.Identifier("NFTESTLD0"), MmsEncoding.Identifier("GGIO1$ST$Status$stVal")));
        Check(server.ProcessRead());
        model[0].SetBoolean(false); model[1].SetInteger(int.MinValue); model[2].SetFloatingPoint(-12.5f);
        valid.AppendRead(MmsEncoding.ReadRequest(2, MmsEncoding.Identifier("NFTESTLD0"), MmsEncoding.Identifier("missing")));
        Check(server.ProcessRead());
        Check(!server.ProcessRead() && !server.Associated && !server.Faulted);
        Reject(() => server.ProcessRead(), typeof(InvalidOperationException));
        Reject(() => server.Associate(), typeof(InvalidOperationException));
        var stalled = ReadyTranscript(association, association.Length);
        var timed = new Iec61850ServerSession(stalled, model, timeoutMilliseconds: 10);
        timed.Associate();
        stalled.Advance = 20;
        Reject(() => timed.ProcessRead(), typeof(TimeoutException));
        Check(timed.Faulted && !timed.Associated);
        foreach (byte[] request in new byte[][] {
            MmsEncoding.ReadRequest(-1, MmsEncoding.Identifier("d"), MmsEncoding.Identifier("i")),
            MmsEncoding.ReadRequest(1, MmsEncoding.Identifier("d"), MmsEncoding.Tlv(0x1a, new byte[] { 0 })),
            MmsEncoding.ReadRequest(1, MmsEncoding.Identifier("d"), MmsEncoding.Identifier("i"))
        })
        {
            var bad = ReadyTranscript(association, association.Length);
            var badServer = new Iec61850ServerSession(bad, model);
            badServer.Associate();
            bad.AppendRead(request);
            // 마지막 사례는 완전한 요청이 아닌 부분 프레임입니다.
            if (request[request.Length - 1] == (byte)'i') bad.Input.RemoveAt(bad.Input.Count - 1);
            try { badServer.ProcessRead(); throw new Exception("Malformed request accepted"); }
            catch (MmsProtocolException) { }
            catch (IOException) { }
            Check(badServer.Faulted && !badServer.Associated);
        }
        Loopback();
        Boundaries(model);
        ResponseEncoding();
        Discovery();
        WriteRequests();
        Console.WriteLine("MMS Server: " + checks + " assertions, scalar updates, EOF, malformed requests and deadline PASS.");
    }
    private static void ResponseEncoding()
    {
        var model = Model(1);
        var input = ReadyTranscript(MmsEncoding.Association(4096), MmsEncoding.Association(4096).Length);
        var server = new Iec61850ServerSession(input, model);
        server.Associate();
        foreach (long invoke in new long[] { 0, 127, 128, 255, 256, 32767, 32768, int.MaxValue, uint.MaxValue })
        {
            foreach (int value in new int[] { int.MinValue, -32769, -32768, -129, -128, -1, 0, 127, 128, 32767, 32768, int.MaxValue })
            {
                model[1].SetInteger(value);
                VerifyResponse(input, server, invoke, model[1].Item, MmsEncoding.Int(0x85, value));
            }
            model[0].SetBoolean(true);
            VerifyResponse(input, server, invoke, model[0].Item, new byte[] { 0x83, 1, 1 });
            model[0].SetBoolean(false);
            VerifyResponse(input, server, invoke, model[0].Item, new byte[] { 0x83, 1, 0 });
            VerifyResponse(input, server, invoke, "missing", new byte[] { 0x80, 1, 10 });
            foreach (float value in new float[] { 0, -0.0f, 12.5f, -12.5f, float.Epsilon,
                float.MaxValue, float.MinValue, float.PositiveInfinity, float.NegativeInfinity })
            {
                model[2].SetFloatingPoint(value);
                uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
                VerifyResponse(input, server, invoke, model[2].Item, new byte[] { 0x87, 5, 8,
                    (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits });
            }
        }
        model[2].SetFloatingPoint(float.NaN);
        VerifyResponse(input, server, 1, model[2].Item, new byte[] { 0x87, 5, 8, 0xff, 0xc0, 0, 0 });
    }
    private static void VerifyResponse(Transcript input, Iec61850ServerSession server, long invoke, string item, byte[] expectedValue)
    {
        byte[] ordinary = MmsEncoding.ReadRequest(1, MmsEncoding.Identifier("NFTESTLD0"), MmsEncoding.Identifier(item));
        var fields = MmsEncoding.Single(ordinary, 0, ordinary.Length, 0xa0).Children();
        fields.Read(2);
        byte[] service = fields.Read(0xa4).Copy();
        input.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, invoke), MmsEncoding.Tlv(0xa4, service)));
        input.Output.Clear();
        Check(server.ProcessRead());
        byte[] expected = MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3,
            MmsEncoding.Tlv(0xa1, MmsEncoding.Int(2, invoke),
                MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, expectedValue)))));
        byte[] actual = input.LastTsdu();
        Check(actual.Length == expected.Length);
        for (int i = 0; i < actual.Length; i++) Check(actual[i] == expected[i]);
    }
    private static void Boundaries(MmsServerVariable[] model)
    {
        byte[] association = MmsEncoding.Association(4096);
        foreach (byte[] pattern in new byte[][] {
            Convert.FromHexString("130100"), Convert.FromHexString("14020002"),
            Convert.FromHexString("33020001"), Convert.FromHexString("810400000001"),
            Convert.FromHexString("060452010001"), Convert.FromHexString("060528CA220201"),
            Convert.FromHexString("060528CA220203"), Convert.FromHexString("810305F100"),
            Convert.FromHexString("820C034E00"), Convert.FromHexString("830108")
        })
        {
            byte[] changed = (byte[])association.Clone();
            int p = Find(changed, pattern);
            if (pattern[0] == 0x82 && pattern[1] == 0x0c) changed[p + 3] = 0;
            else changed[p + pattern.Length - 1] = 0xff;
            var input = ReadyTranscript(changed, changed.Length);
            var session = new Iec61850ServerSession(input, model);
            Reject(session.Associate, typeof(MmsProtocolException));
            Check(session.Faulted && !session.Associated);
        }
        foreach (int index in new int[] { 0, 1, 3, 4, 5, 6, 7, 10, 11, 12, 13, 14 })
        {
            var input = ReadyTranscript(association, association.Length);
            input.Input[index] ^= 0x40;
            var session = new Iec61850ServerSession(input, model);
            try { session.Associate(); throw new Exception("Bad CR accepted"); }
            catch (MmsProtocolException) { }
            catch (IOException) { }
            Check(session.Faulted);
        }
        foreach (bool writeFailure in new bool[] { true, false })
        {
            var input = ReadyTranscript(association, association.Length);
            input.ZeroWrite = writeFailure;
            input.BadReadCount = !writeFailure;
            var session = new Iec61850ServerSession(input, model);
            Reject(session.Associate, typeof(IOException));
            Check(session.Faulted);
        }
        var limitedInput = ReadyTranscript(association, association.Length);
        var limited = new Iec61850ServerSession(limitedInput, model, maximumPduSize: 1024);
        limited.Associate();
        limitedInput.Segments(new byte[3000]);
        Reject(() => limited.ProcessRead(), typeof(MmsProtocolException));
        Check(limited.Faulted);
        var slowInput = ReadyTranscript(association, association.Length);
        var slow = new Iec61850ServerSession(slowInput, model, timeoutMilliseconds: 100);
        slow.Associate();
        slowInput.AppendRead(MmsEncoding.ReadRequest(1, MmsEncoding.Identifier("d"), MmsEncoding.Identifier("i")));
        slowInput.Advance = 20;
        Reject(() => slow.ProcessRead(), typeof(TimeoutException));
        Check(slow.Faulted);
        var valid = ReadyTranscript(association, association.Length);
        var server = new Iec61850ServerSession(valid, model);
        server.Associate();
        valid.Output.Clear();
        byte[] request = MmsEncoding.ReadRequest(1, MmsEncoding.Identifier("unknown"), MmsEncoding.Identifier("i"));
        var requestFields = MmsEncoding.Single(request, 0, request.Length, 0xa0).Children();
        requestFields.Read(2);
        BerElement service = requestFields.Read(0xa4);
        valid.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, uint.MaxValue),
            MmsEncoding.Tlv(0xa4, service.Copy())));
        Check(server.ProcessRead());
        byte[] response = valid.LastTsdu();
        BerElement pdv = MmsEncoding.UnwrapPdv(MmsEncoding.Single(response, 4, response.Length - 4, 0x61), 3);
        var confirmed = MmsEncoding.Single(pdv.Buffer, pdv.Offset, pdv.Length, 0xa1).Children();
        Check(confirmed.Read(2).Integer() == uint.MaxValue);
        var read = confirmed.Read(0xa4).Children();
        confirmed.End();
        var results = read.Read(0xa1).Children(); read.End();
        Check(results.Read(0x80).Integer() == 10); results.End();
        byte[] fullRead = MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3,
            MmsEncoding.ReadRequest(1, MmsEncoding.Identifier("d"), MmsEncoding.Identifier("i"))));
        for (int cut = 1; cut < fullRead.Length; cut++)
        {
            var input = ReadyTranscript(association, association.Length);
            var session = new Iec61850ServerSession(input, model);
            session.Associate();
            var prefix = new byte[cut];
            Array.Copy(fullRead, prefix, cut); input.Segments(prefix);
            try { session.ProcessRead(); throw new Exception("Truncated Read accepted"); }
            catch (MmsProtocolException) { }
            catch (IOException) { }
            Check(session.Faulted);
        }
        byte[] smallAssociation = MmsEncoding.Association(64);
        smallAssociation[Find(smallAssociation, Convert.FromHexString("830108")) + 2] = 1;
        var smallInput = ReadyTranscript(smallAssociation, smallAssociation.Length);
        var small = new Iec61850ServerSession(smallInput, model);
        small.Associate();
        Check(small.Associated && small.NegotiatedPduSize == 64);
        byte[] accept = smallInput.LastTsdu();
        Check(Find(accept, Convert.FromHexString("800140810101820101830101")) >= 0);
        byte[] ordinary = MmsEncoding.ReadRequest(1, MmsEncoding.Identifier("d"), MmsEncoding.Identifier("i"));
        var fields = MmsEncoding.Single(ordinary, 0, ordinary.Length, 0xa0).Children();
        fields.Read(2);
        byte[] body = fields.Read(0xa4).Copy();
        smallInput.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1),
            MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0x80, new byte[] { 0 }), body)));
        Check(small.ProcessRead());
        smallInput.Output.Clear();
        smallInput.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 2),
            MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0x80, new byte[] { 1 }), body)));
        Check(small.ProcessRead());
        Check(!small.Faulted && small.Associated);
        byte[] responseBytes = smallInput.LastTsdu();
        var responsePdv = MmsEncoding.UnwrapPdv(MmsEncoding.Single(responseBytes, 4, responseBytes.Length - 4, 0x61), 3);
        var withResult = MmsEncoding.Single(responsePdv.Buffer, responsePdv.Offset, responsePdv.Length, 0xa1).Children();
        Check(withResult.Read(2).Integer() == 2);
        var serviceResult = withResult.Read(0xa4).Children(); withResult.End();
        var returnedSpec = serviceResult.Read(0xa0).Children();
        Check(returnedSpec.Read(0xa0).Length > 0); returnedSpec.End();
        var accessResult = serviceResult.Read(0xa1).Children(); serviceResult.End();
        Check(accessResult.Read(0x80).Integer() == 10); accessResult.End();
    }
    private static int Find(byte[] bytes, byte[] pattern)
    {
        for (int p = 0; p <= bytes.Length - pattern.Length; p++)
        {
            int i = 0;
            while (i < pattern.Length && bytes[p + i] == pattern[i]) i++;
            if (i == pattern.Length) return p;
        }
        throw new Exception("Server test pattern not found");
    }
    private static void Loopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var model = Model(1);
            Task worker = Task.Run(() => {
                using (var socket = listener.AcceptSocket())
                {
                    var server = new Iec61850ServerSession(new MmsSocketChannel(socket), model);
                    server.Associate();
                    int count = 0;
                    while (server.ProcessRead())
                    {
                        if (++count == 4)
                        {
                            model[0].SetBoolean(false);
                            model[1].SetInteger(int.MinValue);
                            model[2].SetFloatingPoint(-12.5f);
                        }
                    }
                    Check(count == 7 && !server.Faulted);
                }
            });
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Connect(IPAddress.Loopback, port);
                var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                client.Associate();
                Check(client.Read("NFTESTLD0", "GGIO1$ST$Status$stVal").Boolean);
                Check(client.Read("NFTESTLD0", "GGIO1$ST$Count$stVal").Integer == -123455);
                Check(client.Read("NFTESTLD0", "GGIO1$MX$Analog$mag").FloatingPoint == 13.5);
                MmsValue value;
                int code;
                Check(!client.TryRead("NFTESTLD0", "missing", out value, out code) && code == 10 && value == null);
                Check(!client.Read("NFTESTLD0", "GGIO1$ST$Status$stVal").Boolean);
                Check(client.Read("NFTESTLD0", "GGIO1$ST$Count$stVal").Integer == int.MinValue);
                Check(client.Read("NFTESTLD0", "GGIO1$MX$Analog$mag").FloatingPoint == -12.5);
            }
            worker.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        finally { listener.Stop(); }
    }
    internal static void Interop(string executable)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task worker = Task.Run(() => {
                for (int sequence = 1; sequence <= 2; sequence++)
                using (var socket = listener.AcceptSocket())
                {
                    var model = Model(sequence);
                    WriteTests.Enable(model);
                    var server = new Iec61850ServerSession(new MmsSocketChannel(socket), model, maximumNamesPerPage: 1);
                    server.Associate();
                    int count = 0;
                    while (server.ProcessRead()) count++;
                    if (count != 69 || server.Faulted) throw new Exception("Native client server request totals: " + count);
                }
            });
            for (int sequence = 1; sequence <= 2; sequence++)
            {
                var start = new ProcessStartInfo(executable, "verify " + port + " 127.0.0.1 " + sequence) {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var native = Process.Start(start))
                {
                    if (!native.WaitForExit(20000)) { native.Kill(true); throw new Exception("Native verify timeout"); }
                    if (native.ExitCode != 0) throw new Exception("Native verify failed: " + native.StandardError.ReadToEnd());
                    Console.Write(native.StandardOutput.ReadToEnd());
                }
            }
            worker.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        finally { listener.Stop(); }
        Console.WriteLine("Original libiec61850 Client -> managed MMS Server: 60 scalar, 20 errors, paged discovery, scalar attributes, service errors and reconnect PASS.");
    }
    private static void WriteRequests()
    {
        byte[] association = MmsEncoding.Association(4096);
        byte[] name = MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0,
            MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier("D"), MmsEncoding.Identifier("i"))));
        byte[] access = MmsEncoding.Tlv(0xa0, name);
        byte[] data = MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x85, 5));
        foreach (byte[] bad in new byte[][] {
            MmsEncoding.Tlv(0xa5), MmsEncoding.Tlv(0xa5, access),
            MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0xa0, name, name), data),
            MmsEncoding.Tlv(0xa5, access, MmsEncoding.Tlv(0xa0)),
            MmsEncoding.Tlv(0xa5, access, MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x85, 5), MmsEncoding.Int(0x85, 6))),
            MmsEncoding.Tlv(0xa5, access, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0x85))),
            MmsEncoding.Tlv(0xa5, access, data, data)
        })
        {
            var channel = ReadyTranscript(association, association.Length);
            var variable = new MmsServerVariable("D", "i", 1);
            variable.EnableWrite();
            var server = new Iec61850ServerSession(channel, new[] { variable });
            server.Associate();
            channel.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1), bad));
            Reject(() => server.ProcessRequest(), typeof(MmsProtocolException));
            Check(server.Faulted && !server.Associated);
            byte[] value = variable.EncodeValue();
            Check(MmsValue.Decode(MmsEncoding.Single(value, 0, value.Length, 0x85)).Integer == 1);
        }
        // 콜백의 버그를 정상 쓰기나 일반적인 거절로 바꾸어 숨기지 않습니다.
        var throwingChannel = ReadyTranscript(association, association.Length);
        var throwing = new MmsServerVariable("D", "i", 1);
        throwing.EnableWrite(v => { throw new InvalidOperationException("Validator failure"); });
        var failed = new Iec61850ServerSession(throwingChannel, new[] { throwing });
        failed.Associate();
        throwingChannel.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1), MmsEncoding.Tlv(0xa5, access, data)));
        Reject(() => failed.ProcessRequest(), typeof(InvalidOperationException));
        Check(failed.Faulted && !failed.Associated);
    }
    private static void Discovery()
    {
        var model = new MmsServerVariable[] {
            new MmsServerVariable("Z", "z", true), new MmsServerVariable("A", "c", 1),
            new MmsServerVariable("A", "a", 1.0f), new MmsServerVariable("A", "b", false)
        };
        byte[] association = MmsEncoding.Association(4096);
        var channel = ReadyTranscript(association, association.Length);
        Reject(() => new Iec61850ServerSession(channel, model, maximumNamesPerPage: 0), typeof(ArgumentOutOfRangeException));
        Reject(() => new Iec61850ServerSession(channel, model, maximumNamesPerPage: 129), typeof(ArgumentOutOfRangeException));
        var server = new Iec61850ServerSession(channel, model, maximumNamesPerPage: 1);
        server.Associate();
        int invoke = 0;
        BerCursor Request(byte[] service, byte tag = 0xa1)
        {
            channel.Output.Clear();
            channel.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, ++invoke), service));
            Check(server.ProcessRequest() && server.Associated && !server.Faulted);
            byte[] response = channel.LastTsdu();
            var pdv = MmsEncoding.UnwrapPdv(MmsEncoding.Single(response, 4, response.Length - 4, 0x61), 3);
            Check(pdv.Length <= server.NegotiatedPduSize);
            var fields = MmsEncoding.Single(pdv.Buffer, pdv.Offset, pdv.Length, tag).Children();
            Check(fields.Read(tag == 0xa1 ? (byte)2 : (byte)0x80).Integer() == invoke);
            return fields;
        }
        void Page(string domain, string after, string expected, bool more)
        {
            var fields = Request(NameRequest(domain, after));
            var response = fields.Read(0xa1).Children(); fields.End();
            var names = response.Read(0xa0).Children();
            if (expected != null)
            {
                var name = names.Read(0x1a);
                Check(System.Text.Encoding.ASCII.GetString(name.Buffer, name.Offset, name.Length) == expected);
            }
            names.End();
            var flag = response.Read(0x81); response.End();
            Check(BerEncoding.ReadBoolean(flag.Buffer, flag.Offset, flag.Length) == more);
        }
        void Error(byte[] request, int expectedClass = 7, int expectedCode = 2)
        {
            var fields = Request(request, 0xa2);
            var error = fields.Read(0xa2).Children(); fields.End();
            var code = error.Read(0xa0).Children(); error.End();
            Check(code.Read((byte)(0x80 + expectedClass)).Integer() == expectedCode); code.End();
        }
        Page(null, null, "A", true); Page(null, "A", "Z", false); Page(null, "Z", null, false);
        Page("A", null, "a", true); Page("A", "a", "b", true); Page("A", "b", "c", false);
        Page("A", "c", null, false);
        Error(NameRequest("missing", null)); Error(NameRequest("A", "missing"));
        Error(MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x80, 2)),
            MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0x80))), 4, 0);
        foreach (var variable in model)
        {
            var fields = Request(AttributeRequest(variable.Domain, variable.Item));
            var attributes = fields.Read(0xa6).Children(); fields.End();
            var deletable = attributes.Read(0x80);
            Check(!BerEncoding.ReadBoolean(deletable.Buffer, deletable.Offset, deletable.Length));
            var specification = attributes.Read(0xa2).Children(); attributes.End();
            Check(specification.Read().Tag == (variable.Item == "a" ? 0xa7 : variable.Item == "c" ? 0x85 : 0x83));
            specification.End();
        }
        Error(AttributeRequest("A", "missing"));
        Error(AttributeRequest("missing", "a"));
        Page("Z", null, "z", false);
        Check(model[0].Domain == "Z" && model[1].Item == "c"); // 생성자는 호출자의 배열 순서를 바꾸지 않아야 합니다.

        // 항목 개수뿐 아니라 식별자와 페이지의 크기도 협상한 PDU 한도에 맞아야 합니다.
        var longModel = new MmsServerVariable[32];
        for (int i = 0; i < longModel.Length; i++)
            longModel[i] = new MmsServerVariable("D", i.ToString("D2") + new string('x', 62), i);
        association = MmsEncoding.Association(1024);
        channel = ReadyTranscript(association, association.Length);
        server = new Iec61850ServerSession(channel, longModel, maximumNamesPerPage: 128);
        server.Associate();
        string continuation = null;
        int total = 0, pages = 0;
        do
        {
            var fields = Request(NameRequest("D", continuation));
            var response = fields.Read(0xa1).Children(); fields.End();
            var names = response.Read(0xa0).Children();
            int count = 0;
            while (names.More)
            {
                var name = names.Read(0x1a);
                continuation = System.Text.Encoding.ASCII.GetString(name.Buffer, name.Offset, name.Length);
                Check(continuation == longModel[total++].Item); count++;
            }
            Check(count > 0);
            var flag = response.Read(0x81); response.End(); pages++;
            if (!BerEncoding.ReadBoolean(flag.Buffer, flag.Offset, flag.Length)) break;
            Check(total < 32 && pages < 32);
        } while (true);
        Check(total == 32 && pages == 3);

        association = MmsEncoding.Association(64);
        channel = ReadyTranscript(association, association.Length);
        server = new Iec61850ServerSession(channel, longModel);
        server.Associate();
        Error(NameRequest("D", null), 3, 0);
        var compoundChildren = new MmsServerVariable[80];
        for (int i = 0; i < compoundChildren.Length; i++)
            compoundChildren[i] = new MmsServerVariable("D", "c" + i, i);
        var compound = new MmsServerVariable("D", "large", compoundChildren);
        foreach (bool attributes in new[] { false, true })
        {
            var tooSmall = ReadyTranscript(association, association.Length);
            var limitedServer = new Iec61850ServerSession(tooSmall, new[] { compound });
            limitedServer.Associate();
            tooSmall.AppendRead(attributes ? MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1), AttributeRequest("D", "large")) :
                MmsEncoding.ReadRequest(1, MmsEncoding.Identifier("D"), MmsEncoding.Identifier("large")));
            Reject(() => limitedServer.ProcessRequest(), typeof(MmsProtocolException));
            Check(limitedServer.Faulted && !limitedServer.Associated);
        }
        foreach (byte[] bad in new byte[][] {
            MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x80, 0))),
            NameRequest("", null),
            MmsEncoding.Tlv(0xa6, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa1,
                MmsEncoding.Identifier("D"), MmsEncoding.Tlv(0x1a)))),
            MmsEncoding.Join(AttributeRequest("D", "x"), new byte[] { 0 })
        })
        {
            association = MmsEncoding.Association(4096);
            var malformed = ReadyTranscript(association, association.Length);
            var badServer = new Iec61850ServerSession(malformed, model);
            badServer.Associate();
            malformed.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1), bad));
            Reject(() => badServer.ProcessRequest(), typeof(MmsProtocolException));
            Check(badServer.Faulted && !badServer.Associated);
        }
        Console.WriteLine("MMS discovery: sorted multi-domain pages, continuation/errors, 64-byte names and exact PDU-limited pagination PASS.");
    }
    private static byte[] NameRequest(string domain, string continuation)
    {
        byte[] scope = domain == null ? MmsEncoding.Tlv(0x80) :
            domain.Length == 0 ? MmsEncoding.Tlv(0x81) : MmsEncoding.Identifier(domain);
        if (domain != null) scope[0] = 0x81;
        byte[] after = continuation == null ? new byte[0] : MmsEncoding.Identifier(continuation);
        if (after.Length != 0) after[0] = 0x82;
        return MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x80, domain == null ? 9 : 0)),
            MmsEncoding.Tlv(0xa1, scope), after);
    }
    private static byte[] AttributeRequest(string domain, string item)
    {
        return MmsEncoding.Tlv(0xa6, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa1,
            MmsEncoding.Identifier(domain), MmsEncoding.Identifier(item))));
    }
    internal static Transcript ReadyTranscript(byte[] association, int count)
    {
        var result = new Transcript();
        result.Input.AddRange(Convert.FromHexString("0300001611E00000000100C00107C2020001C1020001"));
        if (count != 0)
        {
            var prefix = new byte[count];
            Array.Copy(association, prefix, count);
            result.Segments(prefix);
        }
        return result;
    }
    internal sealed class Transcript : IMmsChannel
    {
        internal readonly System.Collections.Generic.List<byte> Input = new System.Collections.Generic.List<byte>();
        internal readonly System.Collections.Generic.List<byte> Output = new System.Collections.Generic.List<byte>();
        private int p;
        internal int Advance;
        internal bool ZeroWrite, BadReadCount;
        internal int WriteBudget = -1;
        internal int WriteAdvance;
        public long Milliseconds { get; private set; }
        internal void Elapse(int milliseconds) { Milliseconds += milliseconds; }
        public int Read(byte[] bytes, int offset, int count, int timeout)
        {
            Milliseconds += Advance;
            if (BadReadCount) return count + 1;
            int n = Math.Min(Math.Min(count, 3), Input.Count - p);
            for (int i = 0; i < n; i++) bytes[offset + i] = Input[p++];
            return n;
        }
        public int Write(byte[] bytes, int offset, int count, int timeout)
        {
            Milliseconds += WriteAdvance;
            if (ZeroWrite || WriteBudget == 0) return 0;
            int n = Math.Min(count, 5);
            if (WriteBudget > 0) { n = Math.Min(n, WriteBudget); WriteBudget -= n; }
            for (int i = 0; i < n; i++) Output.Add(bytes[offset + i]);
            return n;
        }
        internal byte[] LastTsdu()
        {
            var bytes = new System.Collections.Generic.List<byte>();
            for (int p = 0; p < Output.Count;)
            {
                int length = (Output[p + 2] << 8) | Output[p + 3];
                if (Output[p + 5] == 0xf0)
                    for (int i = 7; i < length; i++) bytes.Add(Output[p + i]);
                p += length;
            }
            return bytes.ToArray();
        }
        internal void AppendRead(byte[] request)
        {
            Segments(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, request)));
        }
        internal void Segments(byte[] bytes)
        {
            for (int p = 0; p < bytes.Length;)
            {
                int n = Math.Min(50, bytes.Length - p);
                var frame = new byte[n + 7];
                CotpData.Write(frame, 0, bytes, p, n, p + n == bytes.Length);
                Input.AddRange(frame);
                p += n;
            }
        }
    }
}
