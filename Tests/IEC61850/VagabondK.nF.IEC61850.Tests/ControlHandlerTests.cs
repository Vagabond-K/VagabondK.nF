using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.IO;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;

/// <summary>허용 검사와 실행 처리기의 호출 순서, Test 요청에 부작용이 없는지 여부 및 처리기 실패가 세션에 미치는 영향을 검증합니다.</summary>
internal static class ControlHandlerTests
{
    private static void Check(bool ok) { if (!ok) throw new Exception("Control handler assertion"); }
    private static void Reject(Action action, Type expected)
    {
        try { action(); }
        catch (Exception error) { if (error.GetType() == expected) return; throw; }
        throw new Exception("Expected control callback failure: " + expected.Name);
    }
    internal static void Run()
    {
        Model();
        Managed();
        Faults();
        Console.WriteLine("CONTROL handlers: snapshot/default-deny/validation/execution/Test/SBO/fault/response-loss/no-retry PASS.");
    }
    private static void Model()
    {
        var control = new MmsServerControl("D", "LN$CO$Object", MmsControlModel.SboNormal);
        var owner = new object();
        int checks = 0, executions = 0, validation = -1, execution = -1;
        MmsControlRequest saved = null;
        MmsControlValidator validator = request => {
            checks++;
            Check(request.Domain == "D" && request.Item == "LN$CO$Object" && request.Value);
            Check(request.OriginCategory == 3 && request.ControlNumber == 1);
            Check(request.GetOrigin()[0] == 78 && request.GetOrigin()[1] == 70);
            Check(request.GetUtcTime().Length == 8 && request.GetUtcTime()[0] == 0);
            var origin = request.GetOrigin(); origin[0] = 0;
            var time = request.GetUtcTime(); time[0] = 1;
            Check(request.GetOrigin()[0] == 78 && request.GetUtcTime()[0] == 0);
            saved = request;
            return validation;
        };
        MmsControlHandler handler = request => {
            executions++;
            Check(object.ReferenceEquals(saved, request) && !request.Test);
            return execution;
        };
        Reject(() => control.SetHandlers(null, handler), typeof(ArgumentNullException));
        Reject(() => control.SetHandlers(validator, null), typeof(ArgumentNullException));
        Check(!control.Enabled);
        control.Enabled = true;
        control.Select(owner, 0);
        control.SetHandlers(validator, handler);
        Check(!control.Enabled && control.Apply(owner, 0, ControlTests.Command(true), true) == 3 && checks == 0);
        control.Enabled = true;
        Check(control.Apply(owner, 0, ControlTests.Command(true), true) == 3 && checks == 0);
        control.Select(owner, 0);
        Check(control.Apply(owner, 0, Element(MmsEncoding.Int(0x85, 1)), true) == 7 && checks == 0);
        Reject(() => control.Apply(owner, 0, Element(MmsEncoding.Tlv(0xa2)), true), typeof(MmsProtocolException));
        var invalid = ControlTests.Command(true);
        var fields = invalid.Children();
        fields.Read(); fields.Read();
        var number = fields.Read(0x86);
        number.Buffer[number.Offset] = 255;
        Check(control.Apply(owner, 0, invalid, true) == 11 && checks == 0);
        Check(control.Apply(new object(), 1, ControlTests.Command(true), true) == 3 && checks == 0);
        Check(control.Apply(owner, 1000, ControlTests.Command(true), true) == 3 && checks == 0);
        control.Select(owner, 1001);
        Check(control.Apply(owner, 1002, ControlTests.Command(true, cancel: true), false) == -1 && checks == 0);
        control.Select(owner, 1003);
        Check(control.Apply(owner, 1004, ControlTests.Command(true, check: true), true) == 3 && checks == 0);
        Check(control.Apply(owner, 1004, ControlTests.Command(true, test: true), true) == -1);
        Check(checks == 1 && executions == 0 && saved.Test && !control.Value);
        Check(control.Apply(owner, 1004, ControlTests.Command(true), true) == 3);
        for (int code = 0; code <= 11; code++)
        {
            validation = code;
            control.Select(owner, 1005);
            Check(control.Apply(owner, 1006, ControlTests.Command(true), true) == code && !control.Value);
            Check(control.Apply(owner, 1007, ControlTests.Command(true), true) == 3);
        }
        Check(checks == 13 && executions == 0);
        validation = -1;
        for (int code = 0; code <= 11; code++)
        {
            execution = code;
            control.Select(owner, 1008);
            Check(control.Apply(owner, 1009, ControlTests.Command(true), true) == code && !control.Value);
            Check(control.Apply(owner, 1010, ControlTests.Command(true), true) == 3);
        }
        Check(checks == 25 && executions == 12);
        execution = -1;
        control.Select(owner, 1011);
        var command = ControlTests.Command(true);
        Check(control.Apply(owner, 1012, command, true) == -1 && control.Value && executions == 13);
        Array.Clear(command.Buffer, 0, command.Buffer.Length);
        Check(saved.Value && saved.GetOrigin()[0] == 78 && saved.GetUtcTime()[0] == 0);
        control.Select(owner, 1013);
        control.SetHandlers(validator, handler);
        control.Enabled = true;
        Check(control.Apply(owner, 1014, ControlTests.Command(true), true) == 3);
        Check(MmsValue.Decode(Element(control.Variables[4].EncodeValue())).Boolean);
        control.SetHandlers(request => {
            Reject(() => control.Enabled = false, typeof(InvalidOperationException));
            Reject(() => control.ReportControlErrors = true, typeof(InvalidOperationException));
            Reject(() => control.SetHandlers(validator, handler), typeof(InvalidOperationException));
            Reject(() => control.Select(owner, 0), typeof(InvalidOperationException));
            Reject(() => control.Apply(owner, 0, ControlTests.Command(false), true), typeof(InvalidOperationException));
            return -1;
        }, handler);
        control.Enabled = true;
        control.Select(owner, 1015);
        Check(control.Apply(owner, 1016, ControlTests.Command(true, test: true), true) == -1 && control.Enabled);
    }
    private static void Managed()
    {
        var controls = ControlTests.Model();
        int validations = 0, executions = 0;
        int validation = -1, execution = -1;
        MmsControlRequest saved = null;
        foreach (var control in controls)
        {
            control.SetHandlers(request => { saved = request; validations++; return validation; },
                request => { executions++; return execution; });
            control.Enabled = true;
        }
        using (var connection = new ControlTests.Connection(controls))
        {
            var client = connection.Client;
            var direct = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$Direct", new byte[] { 65 });
            var sbo = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$Selected", new byte[] { 65 });
            int code;
            validation = 3;
            Check(!direct.Operate(true, 0, out code) && code == 3 && executions == 0);
            Check(!direct.Operate(true, 0, out code, test: true) && code == 3 && executions == 0);
            validation = -1; execution = 0;
            Check(!direct.Operate(true, 0, out code) && code == 0 && executions == 1);
            Check(!client.Read("NFTESTLD0", "GGIO1$ST$Direct$stVal").Boolean);
            execution = -1;
            Check(direct.Operate(true, 1234, out code) && code == -1 && executions == 2);
            var applied = saved;
            Check(applied.Domain == "NFTESTLD0" && applied.Item == "GGIO1$CO$Direct" &&
                applied.ControlNumber == 4 && applied.OriginCategory == 3 && applied.GetOrigin()[0] == 65);
            Check(applied.GetUtcTime()[3] == 1 && applied.GetUtcTime()[7] == 0x3f);
            Check(client.Read("NFTESTLD0", "GGIO1$ST$Direct$stVal").Boolean);
            Check(direct.Operate(false, 0, out code, test: true) && executions == 2);
            Check(client.Read("NFTESTLD0", "GGIO1$ST$Direct$stVal").Boolean);
            Check(sbo.Select() && sbo.Cancel(0, out code) && executions == 2);
            validation = 3;
            Check(sbo.Select() && !sbo.Operate(true, 0, out code) && code == 3);
            Check(!client.TryWriteControl("NFTESTLD0", "GGIO1$CO$Selected$Oper",
                ControlTests.Command(true).Buffer, out code) && code == 3);
            validation = -1;
            Check(sbo.Select() && sbo.Operate(true, 0, out code, test: true) && executions == 2);
            Check(!client.Read("NFTESTLD0", "GGIO1$ST$Selected$stVal").Boolean);
            Check(sbo.Select() && sbo.Operate(true, 0, out code) && executions == 3);
            Check(client.Read("NFTESTLD0", "GGIO1$ST$Selected$stVal").Boolean);
            Check(validations == 8 && client.Associated && !client.Faulted);
            Check(applied.Value && !applied.Test && applied.GetOrigin()[0] == 65 && applied.GetUtcTime()[3] == 1);
        }
    }
    private static BerElement Element(byte[] bytes) { return MmsEncoding.Single(bytes, 0, bytes.Length, bytes[0]); }
    private static void Faults()
    {
        for (int mode = 0; mode < 8; mode++)
        {
            int scenario = mode, validations = 0, executions = 0;
            var marker = new InvalidOperationException("Application control failure");
            var control = new MmsServerControl("D", "LN$CO$Object", MmsControlModel.SboNormal);
            control.SetHandlers(request => {
                validations++;
                if (scenario == 0) throw marker;
                return scenario == 1 ? -2 : scenario == 2 ? 12 : -1;
            }, request => {
                executions++;
                if (scenario == 3) throw marker;
                return scenario == 4 ? -2 : scenario == 5 ? 12 : -1;
            });
            control.Enabled = true;
            byte[] association = MmsEncoding.Association(4096);
            var channel = ServerTests.ReadyTranscript(association, association.Length);
            var server = new Iec61850ServerSession(channel, new MmsServerVariable[0], controls: new[] { control });
            server.Associate();
            control.Select(server, channel.Milliseconds);
            byte[] name = MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0,
                MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier("D"), MmsEncoding.Identifier("LN$CO$Object$Oper"))));
            byte[] command = ControlTests.Command(true).Buffer;
            channel.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1),
                MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0xa0, name), MmsEncoding.Tlv(0xa0, command))));
            channel.Output.Clear();
            if (scenario == 6) channel.ZeroWrite = true;
            if (scenario == 7)
            {
                channel.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 2),
                    MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0xa0, name), MmsEncoding.Tlv(0xa0, command))));
                Check(server.ProcessRequest() && server.ProcessRequest());
                Check(validations == 1 && executions == 1 && control.Value && !server.Faulted);
                server.Close();
                continue;
            }
            Exception observed = null;
            try { server.ProcessRequest(); } catch (Exception error) { observed = error; }
            Check(observed != null && observed.GetType() == (scenario == 6 ? typeof(IOException) : typeof(InvalidOperationException)));
            if (scenario == 0 || scenario == 3) Check(object.ReferenceEquals(observed, marker));
            Check(server.Faulted && !server.Associated && channel.Output.Count == 0);
            Check(validations == 1 && executions == (scenario < 3 ? 0 : 1) && control.Value == (scenario == 6));
            Check(MmsValue.Decode(Element(control.Variables[4].EncodeValue())).Boolean == control.Value);
            Reject(() => server.ProcessRequest(), typeof(InvalidOperationException));
            Check(validations == 1 && executions == (scenario < 3 ? 0 : 1));
            var selection = MmsValue.Decode(Element(control.Select(new object(), channel.Milliseconds)));
            Check(selection.VisibleString.Length > 0);
            server.Close();
        }
    }
}
