using VagabondK.nF.MMS;
using System;
using System.Collections.Generic;
using System.IO;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;

/// <summary>미리 기록한 통신 응답을 나누어 수신하는 채널로 협상·읽기·오류 응답과 Faulted 전이를 재현 가능하게 검증합니다.</summary>
internal static class MmsClientTests
{
    // 수정하지 않은 원본 libiec61850 v1.6.2 피어에서 관측한 통신 데이터이며 해당 라이브러리의 소스 코드는 아닙니다.
    private static readonly byte[] Confirm = Convert.FromHexString("0300001611D00001000100C0010DC2020001C1020001");
    private static readonly byte[] Accept = Convert.FromHexString(
        "0E8605061301001601021402000234020001C1743172A003800101A26B830400000001" +
        "A512300780010081025101300780010081025101614F304D020101A0486146A1070605" +
        "28CA220203A203020100A305A103020100BE2F282D020103A028A926800300FDE88101" +
        "0582010583010AA416800101810305F100820C03EE1C00000002000040ED18");
    private static int checks;

    static MmsClientTests()
    {
        // 관측된 피어의 한도 값을 한 번 맞춰 Ready()가 Run()의 선행 실행에 의존하지 않게 합니다.
        Confirm[13] = 10;
        int limits = Find(Accept, Convert.FromHexString("81010582010583010A"));
        Accept[limits + 2] = 1;
        Accept[limits + 5] = 1;
        Accept[limits + 8] = 8;
    }

    private static void Check(bool valid)
    {
        checks++;
        if (!valid) throw new Exception("MMS Client assertion " + checks);
    }
    private static void Reject(Action action, Type type)
    {
        try { action(); }
        catch (Exception error) { Check(error.GetType() == type); return; }
        throw new Exception("Expected " + type);
    }
    internal static void Run()
    {
        // 작은 TPDU를 사용해 Association 분할을 시험합니다.
        byte[] cc = (byte[])Confirm.Clone();
        cc[13] = 7;
        byte[] accept = (byte[])Accept.Clone();
        var channel = new ScriptedChannel();
        channel.Input.AddRange(cc);
        channel.Segments(accept, 50);
        var client = new Iec61850Client(channel, maximumTpduSize: 128);
        client.Associate();
        Check(client.Associated && !client.Faulted && client.NegotiatedPduSize == 4096);
        // Association 요청 자체도 여러 COTP DT로 분할되어야 합니다.
        Check(channel.SentSegments > 1);
        channel.Segments(Response(1, MmsEncoding.Tlv(0x83, new byte[] { 1 })), 5);
        Check(client.Read("NFTESTLD0", "GGIO1$ST$Status$stVal").Boolean);
        channel.Segments(Response(2, MmsEncoding.Int(0x80, 10)), 50);
        Reject(() => client.Read("NFTESTLD0", "missing"), typeof(MmsAccessException));
        Check(client.Associated && !client.Faulted);
        channel.Segments(Response(3, MmsEncoding.Int(0x85, long.MinValue)), 50);
        Check(client.Read("d", "i").Integer == long.MinValue);
        Reject(() => client.Read("", "i"), typeof(ArgumentOutOfRangeException));
        Check(!client.Faulted);
        channel.Segments(Response(4, MmsEncoding.Tlv(0x86, new byte[] { 0, 255, 255, 255, 255, 255, 255, 255, 255 })), 50);
        Check(client.Read("d", "i").Unsigned == ulong.MaxValue);
        channel.Segments(Response(5, MmsEncoding.Tlv(0x87, new byte[] { 8, 0x41, 0x48, 0, 0 })), 50);
        Check(client.Read("d", "i").FloatingPoint == 12.5);
        channel.Segments(Response(6, MmsEncoding.Tlv(0x87, new byte[] { 11, 0xc0, 0x29, 0, 0, 0, 0, 0, 0 })), 50);
        Check(client.Read("d", "i").FloatingPoint == -12.5);
        channel.Segments(Response(999, MmsEncoding.Int(0x85, 42)), 50);
        Reject(() => client.Read("d", "i"), typeof(MmsProtocolException));
        Check(client.Faulted && !client.Associated);
        Reject(() => client.Read("d", "i"), typeof(InvalidOperationException));
        Reject(() => client.Associate(), typeof(InvalidOperationException));

        for (int cut = 0; cut < Accept.Length; cut++)
        {
            var truncated = new ScriptedChannel();
            truncated.Input.AddRange(Confirm);
            byte[] prefix = new byte[cut];
            Array.Copy(Accept, prefix, cut);
            if (cut != 0) truncated.Segments(prefix, 100);
            var c = new Iec61850Client(truncated);
            try { c.Associate(); throw new Exception("Truncated association accepted"); }
            catch (MmsProtocolException) { }
            catch (IOException) { }
            Check(c.Faulted && !c.Associated);
        }
        // 형식이 잘못된 모든 CC는 Association을 보내기 전에 거부해야 합니다.
        foreach (int index in new int[] { 0, 1, 3, 4, 5, 6, 7, 10, 11, 12, 13, 14 })
        {
            var bad = new ScriptedChannel();
            byte[] bytes = (byte[])Confirm.Clone();
            bytes[index] ^= 0x40;
            bad.Input.AddRange(bytes);
            var c = new Iec61850Client(bad);
            try { c.Associate(); throw new Exception("Malformed CC accepted"); }
            catch (MmsProtocolException) { }
            catch (IOException) { }
            Check(c.Faulted);
        }
        var stalled = new ScriptedChannel { Advance = 2 };
        stalled.Input.AddRange(Confirm);
        var timed = new Iec61850Client(stalled, timeoutMilliseconds: 3);
        Reject(() => timed.Associate(), typeof(TimeoutException));
        Check(timed.Faulted);
        var zeroWrite = new ScriptedChannel { ZeroWrite = true };
        Reject(() => new Iec61850Client(zeroWrite).Associate(), typeof(IOException));
        var invalidRead = new ScriptedChannel { BadReadCount = true };
        Reject(() => new Iec61850Client(invalidRead).Associate(), typeof(IOException));
        Reject(() => new Iec61850Client(null), typeof(ArgumentNullException));
        Reject(() => new Iec61850Client(channel, maximumPduSize: 1023), typeof(ArgumentOutOfRangeException));
        Reject(() => new Iec61850Client(channel, maximumTpduSize: 129), typeof(ArgumentOutOfRangeException));
        Reject(() => new Iec61850Client(channel, timeoutMilliseconds: 0), typeof(ArgumentOutOfRangeException));
        Reject(() => new Iec61850Client(channel).Read("d", "i"), typeof(InvalidOperationException));

        byte[][] badValues = {
            new byte[] { 0x83, 0 }, new byte[] { 0x85, 0 }, new byte[] { 0x86, 1, 0xff },
            new byte[] { 0x86, 2, 0, 1 }, new byte[] { 0x87, 1, 8 }, new byte[] { 0xa2, 1, 0 }
        };
        foreach (byte[] encoded in badValues)
            Reject(() => MmsValue.Decode(MmsEncoding.Single(encoded, 0, encoded.Length, encoded[0])),
                typeof(MmsProtocolException));
        var boolValue = MmsValue.Decode(new BerElement(new byte[] { 1 }, 0, 1, 0x83));
        Reject(() => { long ignored = boolValue.Integer; }, typeof(InvalidOperationException));

        // 마지막이 아닌 작은 세그먼트가 계속 도착해도 전체 시간 제한을 지켜야 합니다.
        var endless = Ready();
        var endlessClient = new Iec61850Client(endless, timeoutMilliseconds: 1000);
        endlessClient.Associate();
        for (int i = 0; i < 100; i++) endless.Input.AddRange(new byte[] { 3, 0, 0, 8, 2, 240, 0, 1 });
        endless.Advance = 20;
        Reject(() => endlessClient.Read("d", "i"), typeof(TimeoutException));
        Check(endlessClient.Faulted);
        var overflow = Ready();
        var overflowClient = new Iec61850Client(overflow, maximumPduSize: 1024);
        overflowClient.Associate();
        overflow.Segments(new byte[3000], 100);
        Reject(() => overflowClient.Read("d", "i"), typeof(MmsProtocolException));
        Check(overflowClient.Faulted);
        var eof = Ready();
        var eofClient = new Iec61850Client(eof);
        eofClient.Associate();
        Reject(() => eofClient.Read("d", "i"), typeof(IOException));
        Check(eofClient.Faulted && !eofClient.Associated);
        foreach (byte[] pattern in new byte[][] {
            Convert.FromHexString("A203020100"), Convert.FromHexString("060528CA220203"),
            Convert.FromHexString("820C03EE"), Convert.FromHexString("810305F100")
        })
        {
            byte[] invalid = (byte[])Accept.Clone();
            int offset = Find(invalid, pattern);
            invalid[offset + pattern.Length - 1] ^= pattern[0] == 0x82 ? (byte)8 : (byte)1;
            var badAssociation = new ScriptedChannel();
            badAssociation.Input.AddRange(Confirm);
            badAssociation.Segments(invalid, 100);
            var badClient = new Iec61850Client(badAssociation);
            Reject(() => badClient.Associate(), typeof(MmsProtocolException));
            Check(badClient.Faulted && !badClient.Associated);
        }
        TryReadResults();
        Discovery();
        CompositeResponses();
        WriteResponses();
        Console.WriteLine("MMS Client state/segmentation/deadline: " + checks + " assertions passed.");
    }

    private static void TryReadResults()
    {
        var channel = Ready();
        var client = new Iec61850Client(channel);
        client.Associate();
        MmsValue value = MmsValue.Decode(new BerElement(new byte[] { 1 }, 0, 1, 0x83));
        int error = 99;
        int invoke = 0;
        for (int code = 0; code <= 11; code++)
        {
            channel.Segments(Response(++invoke, MmsEncoding.Int(0x80, code)), 5);
            Check(!client.TryRead("d", "i", out value, out error));
            Check(value == null && error == code && client.Associated && !client.Faulted);
            channel.Segments(Response(++invoke, MmsEncoding.Tlv(0x83, new byte[] { 1 })), 5);
            Check(client.TryRead("d", "i", out value, out error));
            Check(value.Boolean && error == -1 && client.Associated && !client.Faulted);
        }
        Reject(() => client.TryRead("", "i", out value, out error), typeof(ArgumentOutOfRangeException));
        Check(value == null && error == -1 && client.Associated && !client.Faulted);
        channel.Segments(Response(++invoke, MmsEncoding.Int(0x85, long.MaxValue)), 5);
        Check(client.TryRead("d", "i", out value, out error) && value.Integer == long.MaxValue && error == -1);
        channel.Segments(Response(++invoke, MmsEncoding.Int(0x80, 0)), 5);
        try { client.Read("d", "i"); throw new Exception("Expected legacy access exception"); }
        catch (MmsAccessException access) { Check(access.ErrorCode == 0 && !client.Faulted && client.Associated); }
        channel.Segments(Response(++invoke, MmsEncoding.Tlv(0x87, new byte[] { 8, 0x41, 0x48, 0, 0 })), 5);
        Check(client.TryRead("d", "i", out value, out error) && value.FloatingPoint == 12.5 && error == -1);

        foreach (byte[] result in new byte[][] {
            MmsEncoding.Int(0x80, -1), MmsEncoding.Int(0x80, 12),
            MmsEncoding.Tlv(0x80, new byte[0]), MmsEncoding.Tlv(0xa2, new byte[] { 0 })
        })
        {
            var badChannel = Ready();
            var bad = new Iec61850Client(badChannel);
            bad.Associate();
            badChannel.Segments(Response(1, result), 5);
            Reject(() => bad.TryRead("d", "i", out value, out error), typeof(MmsProtocolException));
            Check(value == null && error == -1 && bad.Faulted && !bad.Associated);
            Reject(() => bad.TryRead("d", "i", out value, out error), typeof(InvalidOperationException));
        }
        var eofChannel = Ready();
        var eof = new Iec61850Client(eofChannel);
        eof.Associate();
        Reject(() => eof.TryRead("d", "i", out value, out error), typeof(IOException));
        Check(value == null && error == -1 && eof.Faulted && !eof.Associated);
        var timedChannel = Ready();
        var timed = new Iec61850Client(timedChannel, timeoutMilliseconds: 10);
        timed.Associate();
        timedChannel.Advance = 20;
        Reject(() => timed.TryRead("d", "i", out value, out error), typeof(TimeoutException));
        Check(value == null && error == -1 && timed.Faulted && !timed.Associated);
        var mismatchChannel = Ready();
        var mismatch = new Iec61850Client(mismatchChannel);
        mismatch.Associate();
        mismatchChannel.Segments(Response(2, MmsEncoding.Int(0x80, 10)), 5);
        Reject(() => mismatch.TryRead("d", "i", out value, out error), typeof(MmsProtocolException));
        Check(value == null && error == -1 && mismatch.Faulted && !mismatch.Associated);
    }
    private static void WriteResponses()
    {
        ControlResponses();
        var readOnlyChannel = new ScriptedChannel();
        readOnlyChannel.Input.AddRange(Confirm);
        var readOnlyAccept = (byte[])Accept.Clone();
        int serviceBits = Find(readOnlyAccept, Convert.FromHexString("820C03EE"));
        readOnlyAccept[serviceBits + 3] &= 0xfb;
        readOnlyChannel.Segments(readOnlyAccept, 100);
        var readOnly = new Iec61850Client(readOnlyChannel);
        readOnly.Associate();
        Check(!readOnly.SupportsWrite);
        int deniedCode = 99, sent = readOnlyChannel.SentSegments;
        Reject(() => readOnly.TryWrite("d", "i", true, out deniedCode), typeof(InvalidOperationException));
        Check(deniedCode == -1 && readOnlyChannel.SentSegments == sent && readOnly.Associated && !readOnly.Faulted);
        var channel = Ready();
        var client = new Iec61850Client(channel);
        client.Associate();
        int code = 99, invoke = 0;
        Check(client.SupportsWrite);
        for (int error = 0; error <= 11; error++)
        {
            channel.Segments(ServiceResponse(++invoke, MmsEncoding.Tlv(0xa5, MmsEncoding.Int(0x80, error))), 3);
            Check(!client.TryWrite("d", "i", 42, out code) && code == error && client.Associated && !client.Faulted);
            channel.Segments(ServiceResponse(++invoke, MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0x81))), 3);
            Check(client.TryWrite("d", "i", true, out code) && code == -1);
        }
        channel.Segments(ServiceResponse(++invoke, MmsEncoding.Tlv(0xa5, MmsEncoding.Int(0x80, 3))), 3);
        try { client.Write("d", "i", 1.25f); throw new Exception("Write access error not thrown"); }
        catch (MmsAccessException error) { Check(error.ErrorCode == 3 && !client.Faulted); }
        Reject(() => client.TryWrite("", "i", true, out code), typeof(ArgumentOutOfRangeException));
        Check(code == -1 && !client.Faulted);
        channel.Segments(ErrorResponse(++invoke), 3);
        Reject(() => client.TryWrite("d", "i", true, out code), typeof(MmsServiceException));
        Check(code == -1 && client.Associated && !client.Faulted);
        foreach (byte[] bad in new byte[][] {
            MmsEncoding.Tlv(0x81, new byte[] { 0 }), MmsEncoding.Int(0x80, -1), MmsEncoding.Int(0x80, 12),
            MmsEncoding.Tlv(0x80), MmsEncoding.Tlv(0x82),
            MmsEncoding.Join(MmsEncoding.Tlv(0x81), MmsEncoding.Tlv(0x81))
        })
        {
            var malformed = Ready();
            var failed = new Iec61850Client(malformed);
            failed.Associate();
            malformed.Segments(ServiceResponse(1, MmsEncoding.Tlv(0xa5, bad)), 3);
            Reject(() => failed.TryWrite("d", "i", 1, out code), typeof(MmsProtocolException));
            Check(code == -1 && failed.Faulted && !failed.Associated);
        }
        for (int mode = 0; mode < 3; mode++)
        {
            var stalled = Ready();
            var failed = new Iec61850Client(stalled, timeoutMilliseconds: 10);
            failed.Associate();
            int before = stalled.SentSegments;
            if (mode == 1) stalled.Advance = 20;
            if (mode == 2) stalled.Segments(ServiceResponse(2, MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0x81))), 3);
            Reject(() => failed.TryWrite("d", "i", false, out code),
                mode == 0 ? typeof(IOException) : mode == 1 ? typeof(TimeoutException) : typeof(MmsProtocolException));
            Check(code == -1 && failed.Faulted && !failed.Associated);
            if (mode == 0) Check(stalled.SentSegments == before + 1);
        }
    }
    private static byte[] ControlReport(string reference = "d/i", int number = 1, string name = "LastApplError", int category = 3)
    {
        return MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, MmsEncoding.Tlv(0xa3, MmsEncoding.Tlv(0xa0,
            MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0x80, System.Text.Encoding.ASCII.GetBytes(name))))),
            MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa2,
                MmsEncoding.Tlv(0x8a, System.Text.Encoding.ASCII.GetBytes(reference)), MmsEncoding.Int(0x85, 0),
                MmsEncoding.Tlv(0xa2, MmsEncoding.Int(0x85, category), MmsEncoding.Tlv(0x89, new byte[] { 78, 70 })),
                MmsEncoding.Int(0x86, number), MmsEncoding.Int(0x85, 18)))))));
    }
    private static void ControlResponses()
    {
        for (int mode = 0; mode < 8; mode++)
        {
            var channel = Ready();
            var client = new Iec61850Client(channel);
            client.Associate();
            byte[] report = mode == 1 ? ControlReport(reference: "d/other") : mode == 2 ? ControlReport(number: 2) :
                mode == 3 ? ControlReport(name: "UnknownReport") : mode == 4 ? ControlReport(category: 4) : ControlReport();
            channel.Segments(report, 3);
            if (mode == 5) channel.Segments(report, 3);
            if (mode != 7) channel.Segments(ServiceResponse(1, MmsEncoding.Tlv(0xa5,
                mode == 6 ? MmsEncoding.Tlv(0x81) : MmsEncoding.Int(0x80, 3))), 3);
            int code;
            int before = channel.SentSegments;
            if (mode == 0)
            {
                Check(!client.TryWriteControl("d", "i", ControlTests.Command(true).Buffer, out code) && code == 3);
                Check(client.ControlError.AdditionalCause == 18 && client.Associated && !client.Faulted);
            }
            else
            {
                Reject(() => client.TryWriteControl("d", "i", ControlTests.Command(true).Buffer, out code),
                    mode == 7 ? typeof(IOException) : typeof(MmsProtocolException));
                Check(client.Faulted && !client.Associated);
            }
            Check(channel.SentSegments == before + 1);
        }
    }
    private static void CompositeResponses()
    {
        byte[] nested = MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0xa1,
            MmsEncoding.Int(0x85, 1), MmsEncoding.Int(0x85, 2)));
        var channel = Ready();
        var client = new Iec61850Client(channel);
        client.Associate();
        channel.Segments(Response(1, nested), 3);
        MmsValue value;
        int code;
        Check(client.TryRead("d", "i", out value, out code) && code == -1 &&
            value.GetElement(0).GetElement(1).Integer == 2 && !client.Faulted);
        channel.Segments(Response(2, MmsEncoding.Tlv(0x83, new byte[] { 1 })), 3);
        Check(client.Read("d", "i").Boolean && value.GetElement(0).GetElement(0).Integer == 1);
        for (int i = 0; i < 9; i++) nested = MmsEncoding.Tlv(0xa2, nested);
        channel.Segments(Response(3, nested), 3);
        Reject(() => client.TryRead("d", "i", out value, out code), typeof(MmsProtocolException));
        Check(value == null && code == -1 && client.Faulted && !client.Associated);
    }
    private static void Discovery()
    {
        byte[] one = MmsEncoding.Tlv(0xa0, MmsEncoding.Identifier("a"));
        byte[] two = MmsEncoding.Tlv(0xa0, MmsEncoding.Identifier("a"), MmsEncoding.Identifier("b"));
        byte[] final = MmsEncoding.Tlv(0x81, new byte[] { 0 });
        var channel = Ready();
        var client = new Iec61850Client(channel);
        client.Associate();
        Check(client.SupportsNameList && client.SupportsVariableAttributes);
        channel.Segments(ServiceResponse(1, MmsEncoding.Tlv(0xa1, one)), 3);
        var first = client.GetNameList();
        Check(first.MoreFollows && first.Names.Length == 1 && first.ContinueAfter == "a");
        first.Names[0] = "mutated";
        Check(first.ContinueAfter == "a");
        channel.Segments(ServiceResponse(2, MmsEncoding.Tlv(0xa1,
            MmsEncoding.Tlv(0xa0, MmsEncoding.Identifier("b")), final)), 3);
        var second = client.GetNameList(continueAfter: first.ContinueAfter);
        Check(!second.MoreFollows && second.ContinueAfter == null && second.Names[0] == "b");
        channel.Segments(ServiceResponse(3, MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0), final)), 3);
        Check(client.GetNameList("d", "b").Names.Length == 0);
        Reject(() => client.GetNameList(maximumNames: 0), typeof(ArgumentOutOfRangeException));
        Reject(() => client.GetNameList(maximumNames: 129), typeof(ArgumentOutOfRangeException));
        Reject(() => client.GetNameList(""), typeof(ArgumentOutOfRangeException));
        Reject(() => client.GetNameList(continueAfter: "a b"), typeof(ArgumentException));
        Reject(() => client.GetVariableAccessAttributes("d", ""), typeof(ArgumentOutOfRangeException));
        Check(client.Associated && !client.Faulted);

        foreach (byte[] bad in new byte[][] {
            MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0)),
            MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, MmsEncoding.Identifier("a"), MmsEncoding.Identifier("a")), final),
            MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, MmsEncoding.Identifier("b"), MmsEncoding.Identifier("a")), final),
            MmsEncoding.Tlv(0xa1, one, MmsEncoding.Tlv(0x81)),
            MmsEncoding.Tlv(0xa1, one, final, final),
            MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0x1a)), final),
            MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0x1a, new byte[65])), final),
            MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0x1a, new byte[] { 32 })), final)
        }) DiscoveryFault(bad, false);
        DiscoveryFault(MmsEncoding.Tlv(0xa1, two, final), false, maximumNames: 1);
        DiscoveryFault(MmsEncoding.Tlv(0xa1, one, final), false, continuation: "a");
        DiscoveryFault(MmsEncoding.Tlv(0xa1, one, final), false, invoke: 2);

        byte[][] types = {
            MmsEncoding.Tlv(0x83), MmsEncoding.Int(0x85, 64), MmsEncoding.Int(0x86, 32),
            MmsEncoding.Tlv(0xa7, MmsEncoding.Int(2, 64), MmsEncoding.Int(2, 11))
        };
        int[] widths = { 1, 64, 32, 64 };
        int[] exponents = { 0, 0, 0, 11 };
        MmsValueKind[] kinds = { MmsValueKind.Boolean, MmsValueKind.Integer, MmsValueKind.Unsigned, MmsValueKind.FloatingPoint };
        for (int i = 0; i < types.Length; i++)
        {
            channel.Segments(ServiceResponse(4 + i, Attributes(types[i])), 4);
            var attributes = client.GetVariableAccessAttributes("d", "i");
            Check(attributes.Kind == kinds[i] && attributes.BitWidth == widths[i] &&
                attributes.ExponentWidth == exponents[i] && !attributes.Deletable);
        }
        foreach (byte[] bad in new byte[][] {
            MmsEncoding.Tlv(0x83, new byte[] { 0 }), MmsEncoding.Int(0x85, 0), MmsEncoding.Int(0x86, 65),
            MmsEncoding.Tlv(0xa7, MmsEncoding.Int(2, 32), MmsEncoding.Int(2, 11)),
            MmsEncoding.Tlv(0xa7, MmsEncoding.Int(2, 64)), MmsEncoding.Tlv(0xa2),
            MmsEncoding.Join(MmsEncoding.Tlv(0x83), MmsEncoding.Tlv(0x83))
        }) DiscoveryFault(Attributes(bad), true);
        DiscoveryFault(MmsEncoding.Tlv(0xa6, MmsEncoding.Tlv(0x80), MmsEncoding.Tlv(0xa2, types[0])), true);
        DiscoveryFault(Attributes(types[0]), true, invoke: 99);

        // 정상적인 ServiceError는 연결 손상이 아니며 선택적 진단 정보도 허용합니다.
        channel.Segments(ErrorResponse(8), 4);
        try { client.GetNameList("missing"); throw new Exception("Missing ServiceError"); }
        catch (MmsServiceException error) { Check(error.ErrorClass == 7 && error.ErrorCode == 2); }
        channel.Segments(ErrorResponse(9, optional: true), 4);
        try { client.GetVariableAccessAttributes("d", "missing"); throw new Exception("Missing ServiceError"); }
        catch (MmsServiceException error) { Check(error.ErrorClass == 7 && error.ErrorCode == 2); }
        channel.Segments(Response(10, MmsEncoding.Tlv(0x83, new byte[] { 1 })), 4);
        Check(client.Read("d", "i").Boolean && client.Associated && !client.Faulted);
        var mismatchChannel = Ready();
        var mismatch = new Iec61850Client(mismatchChannel);
        mismatch.Associate();
        mismatchChannel.Segments(ErrorResponse(99), 4);
        Reject(() => mismatch.GetNameList(), typeof(MmsProtocolException));
        Check(mismatch.Faulted && !mismatch.Associated);
        for (int mode = 0; mode < 2; mode++)
        {
            var failedChannel = Ready();
            var failed = new Iec61850Client(failedChannel, timeoutMilliseconds: 10);
            failed.Associate();
            if (mode == 1) failedChannel.Advance = 20;
            Reject(() => failed.GetNameList(), mode == 0 ? typeof(IOException) : typeof(TimeoutException));
            Check(failed.Faulted && !failed.Associated);
        }
        byte[] readOnly = (byte[])Accept.Clone();
        int supported = Find(readOnly, Convert.FromHexString("820C03EE"));
        readOnly[supported + 3] &= 0xbd;
        var readOnlyChannel = new ScriptedChannel();
        readOnlyChannel.Input.AddRange(Confirm);
        readOnlyChannel.Segments(readOnly, 50);
        var readOnlyClient = new Iec61850Client(readOnlyChannel);
        readOnlyClient.Associate();
        Check(!readOnlyClient.SupportsNameList && !readOnlyClient.SupportsVariableAttributes);
        Reject(() => readOnlyClient.GetNameList(), typeof(InvalidOperationException));
        Reject(() => readOnlyClient.GetVariableAccessAttributes("d", "i"), typeof(InvalidOperationException));
        Check(readOnlyClient.Associated && !readOnlyClient.Faulted);
    }
    internal static byte[] ServiceResponse(int invoke, byte[] service)
    {
        return MmsEncoding.Join(new byte[] { 1, 0, 1, 0 },
            MmsEncoding.Pdv(3, MmsEncoding.Tlv(0xa1, MmsEncoding.Int(2, invoke), service)));
    }
    private static byte[] Attributes(byte[] type)
    {
        return MmsEncoding.Tlv(0xa6, MmsEncoding.Tlv(0x80, new byte[] { 0 }), MmsEncoding.Tlv(0xa2, type));
    }
    private static byte[] ErrorResponse(int invoke, bool optional = false)
    {
        return MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3,
            MmsEncoding.Tlv(0xa2, MmsEncoding.Int(0x80, invoke),
                optional ? MmsEncoding.Int(0x81, 0) : new byte[0],
                MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x87, 2)),
                    optional ? MmsEncoding.Int(0x81, -1) : new byte[0],
                    optional ? MmsEncoding.Tlv(0x82, new byte[] { 78, 111 }) : new byte[0]))));
    }
    private static void DiscoveryFault(byte[] service, bool attributes, int maximumNames = 128, string continuation = null, int invoke = 1)
    {
        var channel = Ready();
        var client = new Iec61850Client(channel);
        client.Associate();
        channel.Segments(ServiceResponse(invoke, service), 3);
        Reject(() => {
            if (attributes) client.GetVariableAccessAttributes("d", "i");
            else client.GetNameList("d", continuation, maximumNames);
        }, typeof(MmsProtocolException));
        Check(client.Faulted && !client.Associated);
        Reject(() => client.GetNameList(), typeof(InvalidOperationException));
    }
    internal static ScriptedChannel Ready()
    {
        var channel = new ScriptedChannel();
        channel.Input.AddRange(Confirm);
        channel.Segments(Accept, 100);
        return channel;
    }
    private static int Find(byte[] buffer, byte[] pattern)
    {
        for (int p = 0; p <= buffer.Length - pattern.Length; p++)
        {
            int i = 0;
            while (i < pattern.Length && buffer[p + i] == pattern[i]) i++;
            if (i == pattern.Length) return p;
        }
        throw new Exception("Transcript pattern missing");
    }
    private static byte[] Response(int invoke, byte[] value)
    {
        return MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3,
            MmsEncoding.Tlv(0xa1, MmsEncoding.Int(2, invoke),
                MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, value)))));
    }
    internal sealed class ScriptedChannel : IMmsChannel
    {
        internal readonly List<byte> Input = new List<byte>();
        private readonly List<byte> output = new List<byte>();
        private int position;
        internal int Advance;
        internal bool ZeroWrite, BadReadCount;
        public long Milliseconds { get; private set; }
        internal int SentSegments
        {
            get
            {
                int p = 0, count = 0;
                while (p < output.Count)
                {
                    int n = (output[p + 2] << 8) | output[p + 3];
                    if (output[p + 5] == 0xf0) count++;
                    p += n;
                }
                return count;
            }
        }
        public int Read(byte[] b, int p, int n, int timeout)
        {
            Milliseconds += Advance;
            if (BadReadCount) return n + 1;
            n = Math.Min(Math.Min(n, 3), Input.Count - position);
            for (int i = 0; i < n; i++) b[p + i] = Input[position++];
            return n;
        }
        public int Write(byte[] b, int p, int n, int timeout)
        {
            Milliseconds += Advance;
            if (ZeroWrite) return 0;
            n = Math.Min(n, 5);
            for (int i = 0; i < n; i++) output.Add(b[p + i]);
            return n;
        }
        internal void Segments(byte[] bytes, int payload)
        {
            for (int p = 0; p < bytes.Length;)
            {
                int count = Math.Min(payload, bytes.Length - p);
                var packet = new byte[count + 7];
                CotpData.Write(packet, 0, bytes, p, count, p + count == bytes.Length);
                Input.AddRange(packet);
                p += count;
            }
        }
    }
}
