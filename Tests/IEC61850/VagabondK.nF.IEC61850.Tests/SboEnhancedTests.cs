using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.IO;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;

/// <summary>SBO enhanced의 소유 세션·선택 기한·동일 명령 일치와 취소/완료 상태 전이를 검증합니다.</summary>
internal static class SboEnhancedTests
{
    private static void Check(bool ok) { if (!ok) throw new Exception("SBO enhanced assertion"); }
    private static void Reject(Action action, Type type)
    {
        try { action(); } catch (Exception error) { Check(error.GetType() == type); return; }
        throw new Exception("Expected SBO enhanced error: " + type);
    }
    private static MmsServerControl Control()
    { return new MmsServerControl("D", "L$CO$O", MmsControlModel.SboEnhanced, 100, 100); }
    internal static void Run()
    {
        var owner = new object(); var other = new object();
        var c = Control();
        int validations = 0, executions = 0, policy = -1;
        MmsControlRequest request = null;
        Check(Select(c, owner, 0) == 3);
        c.Enabled = true;
        Check(Select(c, owner, 0) == 3 && !c.HasPendingOperation);
        c.SetHandlers(r => { validations++; return policy; }, r => { executions++; request = r; return -1; });
        Check(Select(c, owner, 0) == 3 && validations == 0);
        c.Enabled = true;
        Check(c.Apply(owner, 0, Command(), true) == 3);
        Check(Select(c, owner, 0) == -1 && validations == 1 && executions == 0 && !c.HasPendingOperation);
        Check(c.Apply(owner, 1, Command(), false, selectWithValue: true) == 3 &&
            Cause(c.ErrorReport(Command(), false, true)) == 19);
        Check(Select(c, owner, 1) == 3 && Select(c, other, 1) == 3 && validations == 1);
        Check(c.Apply(other, 1, Command(), true) == 3);
        for (int field = 0; field < 6; field++)
        {
            var mismatch = Command(field);
            Check(c.Apply(owner, 2, mismatch, true) == 3 && validations == 1 && executions == 0);
            Check(Cause(c.ErrorReport(mismatch, true)) == (field == 5 ? 1 : 26));
        }
        Check(c.Apply(owner, 3, Command(time: 1), true) == -1 && executions == 1 && validations == 2);
        Check(Select(c, other, 4) == 3 && !c.Value);
        c.Complete(request, true);
        Check(c.CompletionReport(owner, 4) != null && c.Value);
        c.Release(owner);
        Check(c.Apply(owner, 5, Command(), true) == 3);
        Check(Select(c, other, 5) == -1);
        c.Release(owner);
        Check(Select(c, owner, 6) == 3);
        Check(c.Apply(other, 105, Command(), true) == 3);
        Check(Select(c, owner, 105) == -1);
        Check(c.Apply(owner, 205, Command(), true) == 3);
        Check(Select(c, owner, 206) == -1);
        var badCancel = Command(0, cancel: true);
        Check(c.Apply(owner, 207, badCancel, false) == 3 && Cause(c.ErrorReport(badCancel, false)) == 26);
        Check(c.Apply(owner, 208, Command(cancel: true, time: 9), false) == -1);
        Check(c.Apply(owner, 209, Command(), true) == 3);
        Check(Select(c, owner, 210) == -1);
        c.Enabled = false; c.Enabled = true;
        Check(c.Apply(owner, 211, Command(), true) == 3);
        policy = 3;
        Check(Select(c, owner, 212) == 3 && !c.HasPendingOperation);
        policy = -1;
        Check(Select(c, owner, 213) == -1);
        policy = 3;
        Check(c.Apply(owner, 214, Command(), true) == 3);
        policy = -1;
        Check(c.Apply(owner, 215, Command(), true) == 3);
        Check(Select(c, owner, 216, true) == -1);
        Check(c.Apply(owner, 217, Command(3), true) == -1 && executions == 1);
        Check(c.CompletionReport(owner, 218) != null && c.Value);
        c.Release(owner);
        Check(Select(c, owner, 219) == -1);
        c.SetHandlers(r => -1, r => -1); c.Enabled = true;
        Check(c.Apply(owner, 220, Command(), true) == 3);
        Check(c.Apply(owner, 221, Command(), false, selectWithValue: true, connectionBusy: true) == 3);
        Reject(() => c.Apply(owner, 0, MmsEncoding.Single(new byte[] { 0xa2, 0 }, 0, 2, 0xa2),
            false, selectWithValue: true), typeof(MmsProtocolException));
        SessionCleanup();
        ClientGuards();
        Console.WriteLine("SBO ENHANCED: ownership/expiry/value-origin-number-Test/Check/cancel/revalidation/cleanup/local guards PASS.");
    }
    private static int Select(MmsServerControl c, object owner, long now, bool test = false)
    { return c.Apply(owner, now, Command(test ? 3 : -1), false, selectWithValue: true); }
    internal static BerElement Command(int change = -1, bool cancel = false, byte time = 0)
    {
        byte[] bytes = MmsEncoding.Tlv(0xa2,
            MmsEncoding.Tlv(0x83, new byte[] { change == 0 ? (byte)0 : (byte)1 }),
            MmsEncoding.Tlv(0xa2, MmsEncoding.Int(0x85, change == 1 ? 8 : 3),
                MmsEncoding.Tlv(0x89, new byte[] { 78, change == 2 ? (byte)88 : (byte)70 })),
            MmsEncoding.Int(0x86, change == 4 ? 2 : 1),
            MmsEncoding.Tlv(0x91, new byte[] { time, 0, 0, 0, 0, 0, 0, 0 }),
            MmsEncoding.Tlv(0x83, new byte[] { change == 3 ? (byte)1 : (byte)0 }),
            cancel ? new byte[0] : MmsEncoding.Tlv(0x84, new byte[] { 6, change == 5 ? (byte)128 : (byte)0 }));
        return MmsEncoding.Single(bytes, 0, bytes.Length, 0xa2);
    }
    private static int Cause(byte[] report)
    {
        var info = MmsEncoding.Single(report, 0, report.Length, 0xa3).Children().Read(0xa0).Children();
        info.Read();
        var detail = info.Read(0xa0).Children().Read(0xa2).Children();
        detail.Read(); detail.Read(); detail.Read(); detail.Read();
        return (int)detail.Read().Integer();
    }
    internal static byte[] Write(string suffix, int invoke, BerElement command = null)
    {
        return MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, invoke), MmsEncoding.Tlv(0xa5,
            MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0,
                MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier("D"), MmsEncoding.Identifier("L$CO$O$" + suffix))))),
            MmsEncoding.Tlv(0xa0, (command ?? Command()).Buffer)));
    }
    private static void SessionCleanup()
    {
        for (int mode = 0; mode < 7; mode++)
        {
            var c = Control();
            MmsControlRequest pending = null;
            c.SetHandlers(r => -1, r => { pending = r; return -1; }); c.Enabled = true;
            byte[] association = MmsEncoding.Association(4096);
            var channel = ServerTests.ReadyTranscript(association, association.Length);
            var session = new Iec61850ServerSession(channel, new MmsServerVariable[0], controls: new[] { c });
            session.Associate();
            channel.AppendRead(Write("SBOw", 1));
            if (mode == 0)
            {
                channel.ZeroWrite = true;
                Reject(() => session.ProcessRequest(), typeof(IOException));
            }
            else
            {
                Check(session.ProcessRequest() && !session.HasPendingControl);
                if (mode >= 4)
                {
                    channel.AppendRead(Write("Oper", 2));
                    Check(session.ProcessRequest() && session.HasPendingControl);
                    if (mode == 4) Check(!session.ProcessRequest());
                    else
                    {
                        c.Complete(pending, mode == 6, mode == 6 ? 0 : 9);
                        Check(session.ProcessControlCompletion() && c.Value == (mode == 6));
                    }
                    Reject(() => c.Complete(pending, true), typeof(InvalidOperationException));
                }
                else if (mode == 1) session.Close();
                else if (mode == 2) Check(!session.ProcessRequest());
                else
                {
                    channel.AppendRead(MmsEncoding.Tlv(0xa0));
                    Reject(() => session.ProcessRequest(), typeof(MmsProtocolException));
                }
            }
            Check(!c.HasPendingOperation && !session.HasPendingControl);
            Check(c.Apply(new object(), 0, Command(), false, selectWithValue: true) == -1);
            session.Close();
        }
    }
    private static void ClientGuards()
    {
        for (int mode = 0; mode < 4; mode++)
        {
            var channel = MmsClientTests.Ready();
            var client = new Iec61850Client(channel); client.Associate();
            channel.Segments(MmsClientTests.ServiceResponse(1,
                MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, MmsEncoding.Int(0x85, 4)))), 100);
            var c = new MmsControlClient(client, "D", "L$CO$O", new byte[] { 78, 70 });
            bool accepted; int code;
            int sent = channel.SentSegments;
            Reject(() => c.Select(), typeof(InvalidOperationException));
            Reject(() => c.Operate(true, 0, out code), typeof(InvalidOperationException));
            Reject(() => c.OperateAndWait(true, 0, out accepted, out code), typeof(InvalidOperationException));
            Check(channel.SentSegments == sent && !client.Faulted);
            if (mode == 0)
            {
                Reject(() => c.SelectWithValue(true, 0, out code), typeof(IOException));
                Check(client.Faulted);
                continue;
            }
            if (mode == 1)
            {
                var disabled = Control();
                var command = Command();
                Check(disabled.Apply(new object(), 0, command, false, selectWithValue: true) == 3);
                channel.Segments(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 },
                    MmsEncoding.Pdv(3, disabled.ErrorReport(command, false, true))), 100);
                channel.Segments(MmsClientTests.ServiceResponse(2, MmsEncoding.Tlv(0xa5, MmsEncoding.Int(0x80, 3))), 100);
                Check(!c.SelectWithValue(true, 0, out code) && code == 3 && c.LastError.AdditionalCause == 8 && !client.Faulted);
                continue;
            }
            channel.Segments(MmsClientTests.ServiceResponse(2, MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0x81))), 100);
            Check(c.SelectWithValue(true, 0, out code, test: mode == 3));
            sent = channel.SentSegments;
            Reject(() => c.SelectWithValue(true, 0, out code), typeof(InvalidOperationException));
            Reject(() => c.OperateAndWait(false, 0, out accepted, out code, mode == 3), typeof(InvalidOperationException));
            Reject(() => c.OperateAndWait(true, 0, out accepted, out code, mode != 3), typeof(InvalidOperationException));
            Check(channel.SentSegments == sent && !client.Faulted);
            channel.Segments(MmsClientTests.ServiceResponse(3, MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0x81))), 100);
            Check(c.Cancel(1000, out code));
            Reject(() => c.Cancel(1000, out code), typeof(InvalidOperationException));
        }
        var c2 = Control();
        MmsControlRequest selection = null, operation = null;
        c2.SetHandlers(r => { if (selection == null) selection = r; return -1; },
            r => { operation = r; return -1; });
        c2.Enabled = true;
        var owner = new object();
        Check(Select(c2, owner, 0) == -1 && c2.Apply(owner, 1, Command(), true) == -1);
        Reject(() => c2.Complete(selection, true), typeof(InvalidOperationException));
        c2.Complete(operation, true);
        c2.Release(owner);
    }
}
