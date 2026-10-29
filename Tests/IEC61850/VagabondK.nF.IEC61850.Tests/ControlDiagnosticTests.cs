using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;

/// <summary>제어 거절 진단이 요청 참조·출처·제어 번호와 일치할 때만 LastError로 전달되는지 검증합니다.</summary>
internal static class ControlDiagnosticTests
{
    private static void Check(bool ok) { if (!ok) throw new Exception("Control diagnostic assertion"); }
    internal static void Run()
    {
        var controls = ControlTests.Model();
        int validation = -1, execution = -1, calls = 0;
        foreach (var control in controls)
        {
            Check(!control.ReportControlErrors);
            control.ReportControlErrors = true;
            control.SetHandlers(request => validation, request => { calls++; return execution; });
            control.Enabled = true;
        }
        using (var connection = new ControlTests.Connection(controls))
        {
            var client = connection.Client;
            var direct = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$Direct", new byte[] { 0, 255 });
            var sbo = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$Selected", new byte[64]);
            int code;
            controls[0].Enabled = false;
            Check(!direct.Operate(true, 0, out code) && code == 3);
            CheckError(direct.LastError, 8, 0, "NFTESTLD0/GGIO1$CO$Direct$Oper");
            controls[0].Enabled = true;
            validation = 3;
            Check(!direct.Operate(true, 0, out code) && code == 3 && calls == 0);
            CheckError(direct.LastError, 0, 1, "NFTESTLD0/GGIO1$CO$Direct$Oper");
            validation = -1; execution = 0;
            Check(!direct.Operate(true, 0, out code) && code == 0 && calls == 1);
            CheckError(direct.LastError, 0, 1, "NFTESTLD0/GGIO1$CO$Direct$Oper");
            execution = -1;
            Check(direct.Operate(true, 0, out code) && direct.LastError == null && calls == 2);
            Check(client.Read("NFTESTLD0", "GGIO1$ST$Direct$stVal").Boolean);
            validation = 11;
            Check(!direct.Operate(false, 0, out code, test: true) && code == 11 && calls == 2);
            CheckError(direct.LastError, 0, 1, "NFTESTLD0/GGIO1$CO$Direct$Oper");
            validation = -1;
            Check(direct.Operate(false, 0, out code, test: true) && direct.LastError == null && controls[0].Value);
            Check(!client.TryWriteControl("NFTESTLD0", "GGIO1$CO$Selected$Oper",
                ControlTests.Command(true).Buffer, out code) && code == 3);
            CheckError(client.ControlError, 18, 0, "NFTESTLD0/GGIO1$CO$Selected$Oper");
            Check(sbo.Select());
            using (var other = new ControlTests.Connection(controls))
            {
                Check(!other.Client.TryWriteControl("NFTESTLD0", "GGIO1$CO$Selected$Oper",
                    ControlTests.Command(true).Buffer, out code) && code == 3);
                CheckError(other.Client.ControlError, 27, 0, "NFTESTLD0/GGIO1$CO$Selected$Oper");
            }
            Check(sbo.Operate(true, 0, out code) && sbo.LastError == null && calls == 3);
            Check(sbo.Select());
            controls[1].Enabled = false;
            Check(!sbo.Cancel(0, out code) && code == 3);
            CheckError(sbo.LastError, 8, 0, "NFTESTLD0/GGIO1$CO$Selected$Cancel");
            controls[1].Enabled = true;
            Check(sbo.Select() && sbo.Cancel(0, out code) && sbo.LastError == null && calls == 3);
            Check(!client.TryWriteControl("NFTESTLD0", "GGIO1$CO$Direct$Oper",
                ControlTests.Command(true, check: true).Buffer, out code) && code == 3);
            CheckError(client.ControlError, 1, 0, "NFTESTLD0/GGIO1$CO$Direct$Oper");
            Check(!client.TryWriteControl("NFTESTLD0", "GGIO1$CO$Direct$Cancel",
                ControlTests.Command(true, cancel: true).Buffer, out code) && code == 3);
            CheckError(client.ControlError, 1, 0, "NFTESTLD0/GGIO1$CO$Direct$Cancel");
            Check(client.Associated && !client.Faulted && calls == 3);
        }
        Boundaries();
        TransmissionFailures();
        Console.WriteLine("CONTROL Server LastApplError: opt-in/correlation/disabled/selection/Check/callback/Cancel/reset/bounds PASS.");
    }
    private static void CheckError(MmsControlError error, int cause, int code, string reference)
    {
        Check(error != null && error.AdditionalCause == cause && error.Error == code &&
            error.ObjectReference == reference && error.ControlNumber >= 0 && error.ControlNumber <= 255);
    }
    private static void Boundaries()
    {
        string domain = new string('D', 64), item = "L$CO$" + new string('O', 48);
        var control = new MmsServerControl(domain, item, MmsControlModel.DirectNormal) { ReportControlErrors = true };
        using (var connection = new ControlTests.Connection(new[] { control }))
        {
            var client = connection.Client;
            var c = new MmsControlClient(client, domain, item, new byte[64], 8);
            int code;
            for (int i = 1; i <= 256; i++)
            {
                Check(!c.Operate(true, 0, out code) && code == 3);
                CheckError(c.LastError, 8, 0, domain + "/" + item + "$Oper");
                Check(c.LastError.ControlNumber == (byte)i);
            }
            Check(!client.Faulted);
        }
        var command = ControlTests.Command(true);
        Check(control.Apply(new object(), 0, command, true) == 3 && control.ErrorReport(command, true) != null);
        byte[] invalidBytes = MmsEncoding.Tlv(0xa2);
        var invalid = MmsEncoding.Single(invalidBytes, 0, invalidBytes.Length, 0xa2);
        try { control.Apply(new object(), 0, invalid, true); throw new Exception("Malformed accepted"); }
        catch (MmsProtocolException) { }
        Check(control.ErrorReport(command, true) == null);
    }
    private static void TransmissionFailures()
    {
        foreach (int stage in new[] { 0, 1, 2 })
        {
            int validations = 0;
            var control = new MmsServerControl("D", "L$CO$O", MmsControlModel.SboNormal);
            control.SetHandlers(request => { validations++; return 3; },
                request => { throw new Exception("Denied request reached execution"); });
            control.Enabled = true; control.ReportControlErrors = true;
            byte[] association = MmsEncoding.Association(4096);
            var channel = ServerTests.ReadyTranscript(association, association.Length);
            var server = new Iec61850ServerSession(channel, new MmsServerVariable[0], controls: new[] { control });
            server.Associate();
            var command = ControlTests.Command(true);
            control.Select(server, 0);
            Check(control.Apply(server, 0, command, true) == 3);
            byte[] report = control.ErrorReport(command, true);
            byte[] payload = MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, report));
            int wireSize = payload.Length + 7 * ((payload.Length + 124) / 125);
            validations = 0;
            control.Select(server, 0);
            byte[] name = MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0,
                MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier("D"), MmsEncoding.Identifier("L$CO$O$Oper"))));
            channel.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1),
                MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0xa0, name), MmsEncoding.Tlv(0xa0, command.Buffer))));
            channel.Output.Clear();
            int budget = stage == 0 ? 0 : stage == 1 ? wireSize / 2 : wireSize;
            channel.WriteBudget = budget;
            try { server.ProcessRequest(); throw new Exception("Diagnostic send failure accepted"); }
            catch (System.IO.IOException) { }
            Check(server.Faulted && !server.Associated && !control.Value && validations == 1);
            Check(channel.Output.Count == budget);
            if (stage == 2)
            {
                byte[] actual = channel.LastTsdu();
                var pdu = MmsEncoding.UnwrapPdv(MmsEncoding.Single(actual, 4, actual.Length - 4, 0x61), 3);
                MmsEncoding.Single(pdu.Buffer, pdu.Offset, pdu.Length, 0xa3);
            }
            try { server.ProcessRequest(); throw new Exception("Faulted session retried"); }
            catch (InvalidOperationException) { }
            Check(validations == 1 && channel.Output.Count == budget);
            Check(control.Select(new object(), 0).Length > 2);
            server.Close();
        }
    }
}
