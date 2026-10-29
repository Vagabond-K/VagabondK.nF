using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>enhanced 요청의 수락 후 완료 통지·기한·응답 형식과 연결 유실 시 실행 불확실성을 검증합니다.</summary>
internal static class EnhancedControlTests
{
    private static void Check(bool ok) { if (!ok) throw new Exception("Enhanced control assertion"); }
    private static void Reject(Action action, Type type)
    {
        try { action(); } catch (Exception error) { Check(error.GetType() == type); return; }
        throw new Exception("Expected enhanced control error: " + type);
    }
    internal static void Run()
    {
        var control = new MmsServerControl("D", "L$CO$O", MmsControlModel.DirectEnhanced, completionTimeoutMilliseconds: 100);
        var owner = new object();
        var other = new object();
        MmsControlRequest request = null;
        int executions = 0;
        control.Enabled = true;
        Check(control.Apply(owner, 0, ControlTests.Command(true), true) == 3);
        control.SetHandlers(r => -1, r => { request = r; executions++; return -1; });
        control.Enabled = true;
        Check(control.Apply(owner, 0, ControlTests.Command(true), true) == -1 && !control.Value && control.HasPendingOperation);
        Check(control.CompletionReport(owner, 1) == null);
        Check(control.Apply(owner, 1, ControlTests.Command(false), true) == 3);
        Check(control.Apply(other, 1, ControlTests.Command(false), true) == 3 && executions == 1);
        Reject(() => control.Enabled = false, typeof(InvalidOperationException));
        Reject(() => control.SetHandlers(r => -1, r => -1), typeof(InvalidOperationException));
        Reject(() => control.Complete(null, true), typeof(InvalidOperationException));
        Reject(() => control.Complete(request, false, 25), typeof(ArgumentOutOfRangeException));
        Reject(() => control.Complete(request, true, 9), typeof(ArgumentOutOfRangeException));
        control.Complete(request, true);
        Reject(() => control.Complete(request, true), typeof(InvalidOperationException));
        Check(control.CompletionReport(other, 1) == null);
        Check(control.CompletionReport(owner, 1) != null && control.Value);
        control.Release(owner);
        Reject(() => control.Complete(request, true), typeof(InvalidOperationException));
        var previous = request;
        Check(control.Apply(owner, 2, ControlTests.Command(false), true) == -1);
        Reject(() => control.Complete(previous, true), typeof(InvalidOperationException));
        control.Complete(request, false, 9);
        Check(control.CompletionReport(owner, 3) != null && control.Value);
        control.Release(owner);
        Check(control.Apply(owner, 4, ControlTests.Command(false), true) == -1);
        control.Complete(request, true);
        Check(control.CompletionReport(owner, 104) != null && control.Value);
        control.Release(owner);
        Check(control.Apply(owner, 105, ControlTests.Command(false, test: true), true) == -1);
        Check(control.CompletionReport(owner, 106) != null && control.Value && executions == 3);
        control.Release(owner);
        Check(control.Apply(owner, 107, ControlTests.Command(false), true, connectionBusy: true) == 3 && executions == 3);
        SessionFailures();
        ClientResponses();
        Console.WriteLine("ENHANCED model: delayed completion/token/busy/deadline/Test/disconnect/send-failure PASS.");
    }
    private static void ClientResponses()
    {
        foreach (bool sbo in new[] { false, true })
        for (int mode = 0; mode < 14; mode++)
        {
            var channel = MmsClientTests.Ready();
            var client = new Iec61850Client(channel, timeoutMilliseconds: mode == 9 ? 7000 : 20000);
            client.Associate();
            channel.Segments(MmsClientTests.ServiceResponse(1,
                MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, MmsEncoding.Int(0x85, sbo ? 4 : 3)))), 100);
            var c = new MmsControlClient(client, "D", "L$CO$O", new byte[] { 78, 70 });
            int code;
            bool accepted = false;
            Reject(() => c.Operate(true, 0, out code), typeof(InvalidOperationException));
            Check(!client.Faulted);
            if (sbo)
            {
                channel.Segments(MmsClientTests.ServiceResponse(2, MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0x81))), 100);
                Check(c.SelectWithValue(true, 0, out code));
            }
            int invoke = sbo ? 3 : 2;
            if (mode == 13)
            {
                var disabled = new MmsServerControl("D", "L$CO$O", MmsControlModel.DirectEnhanced);
                var denied = ControlTests.Command(true);
                Check(disabled.Apply(new object(), 0, denied, true) == 3);
                channel.Segments(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 },
                    MmsEncoding.Pdv(3, disabled.ErrorReport(denied, true))), 50);
                channel.Segments(MmsClientTests.ServiceResponse(invoke,
                    MmsEncoding.Tlv(0xa5, MmsEncoding.Int(0x80, 3))), 100);
                Check(!c.OperateAndWait(true, 0, out accepted, out code) && !accepted && code == 3 &&
                    !client.Faulted && c.LastError.AdditionalCause == 8);
                continue;
            }
            byte[] command = ControlTests.Command(true).Buffer;
            // MmsControlClient는 clockNotSynchronized를 사용하지만 재사용하는 원시 명령의 시간 품질은 0입니다.
            var fields = MmsEncoding.Single(command, 0, command.Length, 0xa2).Children();
            fields.Read(); fields.Read(); fields.Read();
            var time = fields.Read(0x91); time.Buffer[time.Offset + 7] = 0x3f;
            var control = new MmsServerControl("D", "L$CO$O", sbo ? MmsControlModel.SboEnhanced : MmsControlModel.DirectEnhanced);
            MmsControlRequest request = null;
            control.SetHandlers(r => -1, r => { request = r; return -1; }); control.Enabled = true;
            var owner = new object();
            if (sbo) Check(control.Apply(owner, 0, MmsEncoding.Single(command, 0, command.Length, 0xa2),
                false, selectWithValue: true) == -1);
            Check(control.Apply(owner, 0, MmsEncoding.Single(command, 0, command.Length, 0xa2), true) == -1);
            bool negative = mode == 1 || mode == 12;
            control.Complete(request, !negative, negative ? 9 : 0);
            byte[] report = control.CompletionReport(owner, 1);
            if (mode == 12)
            {
                var detail = MmsEncoding.Single(report, 0, report.Length, 0xa3).Children().Read(0xa0).Children();
                detail.Read();
                var elements = detail.Read(0xa0).Children().Read(0xa2).Children();
                elements.Read(); var error = elements.Read(); elements.Read(); elements.Read(); var cause = elements.Read();
                error.Buffer[error.Offset] = 0; cause.Buffer[cause.Offset] = 0;
            }
            if (mode == 2) report[0] = 0xa1;
            if (mode == 3 || mode == 4 || mode == 5 || mode == 6)
            {
                var info = MmsEncoding.Single(report, 0, report.Length, 0xa3).Children().Read(0xa0).Children();
                var names = info.Read(0xa0).Children().Read(0x30).Children().Read(0xa0).Children().Read(0xa1).Children();
                var domain = names.Read(0x1a);
                var item = names.Read(0x1a);
                if (mode == 3) domain.Buffer[domain.Offset] = 69;
                if (mode == 4) item.Buffer[item.Offset] = 66;
                var values = info.Read(0xa0).Children().Read(0xa2).Children();
                var value = values.Read(0x83); var origin = values.Read(0xa2).Children();
                origin.Read(0x85); var identifier = origin.Read(0x89); var number = values.Read(0x86);
                if (mode == 5) number.Buffer[number.Offset] = 2;
                if (mode == 6) identifier.Buffer[identifier.Offset] ^= 1;
                Check(value.Length == 1);
            }
            if (mode == 7) report = MmsEncoding.Tlv(0xa3, MmsEncoding.Tlv(0xa0));
            byte[] response = MmsClientTests.ServiceResponse(invoke, MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0x81)));
            if (mode == 10) channel.Segments(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, report)), 100);
            channel.Segments(response, 100);
            if (mode != 8)
                channel.Segments(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, report)), 100);
            if (mode == 9) channel.Advance = 150;
            if (mode == 11)
                channel.Segments(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, report)), 100);
            if (mode <= 1 || mode == 11)
            {
                Check(c.OperateAndWait(true, 0, out accepted, out code) == (mode != 1));
                Check(accepted && code == -1 && !client.Faulted);
                if (mode == 1) Check(c.LastError.AdditionalCause == 9);
                else Check(c.LastError == null);
                if (mode == 11)
                {
                    Reject(() => client.Read("D", "L$ST$O$stVal"), typeof(MmsProtocolException));
                    Check(client.Faulted);
                }
            }
            else
            {
                Reject(() => c.OperateAndWait(true, 0, out accepted, out code),
                    mode == 8 ? typeof(System.IO.IOException) : mode == 9 ? typeof(TimeoutException) : typeof(MmsProtocolException));
                Check(client.Faulted && !client.Associated && accepted == (mode != 10));
                int sent = channel.SentSegments;
                Reject(() => c.OperateAndWait(true, 0, out accepted, out code), typeof(InvalidOperationException));
                Check(channel.SentSegments == sent);
            }
        }
    }
    private static byte[] Write(string item, int invoke)
    {
        return MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, invoke), MmsEncoding.Tlv(0xa5,
            MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0,
                MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier("D"), MmsEncoding.Identifier(item))))),
            MmsEncoding.Tlv(0xa0, ControlTests.Command(true).Buffer)));
    }
    private static void SessionFailures()
    {
        for (int mode = 0; mode < 8; mode++)
        {
            MmsControlRequest request = null;
            int calls = 0;
            var control = new MmsServerControl("D", "L$CO$O", MmsControlModel.DirectEnhanced);
            control.SetHandlers(r => -1, r => { calls++; request = r; return -1; });
            control.Enabled = true;
            var second = new MmsServerControl("D", "L$CO$B", MmsControlModel.DirectEnhanced);
            second.SetHandlers(r => -1, r => { calls++; return -1; }); second.Enabled = true;
            byte[] association = MmsEncoding.Association(4096);
            var channel = ServerTests.ReadyTranscript(association, association.Length);
            var server = new Iec61850ServerSession(channel, new MmsServerVariable[0], controls: new[] { control, second });
            server.Associate();
            Check(!server.ProcessControlCompletion());
            channel.AppendRead(Write("L$CO$O$Oper", 1));
            if (mode == 6)
            {
                channel.ZeroWrite = true;
                Reject(() => server.ProcessRequest(), typeof(System.IO.IOException));
                Check(server.Faulted && !server.HasPendingControl && !control.HasPendingOperation && !control.Value && calls == 1);
                Reject(() => control.Complete(request, true), typeof(InvalidOperationException));
                server.Close();
                continue;
            }
            Check(server.ProcessRequest() && server.HasPendingControl && control.HasPendingOperation && !control.Value);
            Check(!server.ProcessControlCompletion());
            channel.AppendRead(Write("L$CO$B$Oper", 2));
            Check(server.ProcessRequest() && calls == 1 && !second.HasPendingOperation);
            if (mode == 7)
            {
                channel.AppendRead(MmsEncoding.Tlv(0xa0));
                Reject(() => server.ProcessRequest(), typeof(MmsProtocolException));
                Check(server.Faulted && !server.HasPendingControl && !control.HasPendingOperation);
                Reject(() => control.Complete(request, true), typeof(InvalidOperationException));
                server.Close();
                continue;
            }
            if (mode == 0) server.Close();
            else if (mode == 1) Check(!server.ProcessRequest());
            else
            {
                control.Complete(request, true);
                if (mode == 2) channel.ZeroWrite = true;
                if (mode == 3) channel.Elapse(5000);
                if (mode == 4) channel.WriteAdvance = 10001;
                if (mode == 2 || mode == 4)
                    Reject(() => server.ProcessControlCompletion(), mode == 2 ? typeof(System.IO.IOException) : typeof(TimeoutException));
                else Check(server.ProcessControlCompletion() && !server.HasPendingControl && !control.HasPendingOperation);
                Check(control.Value == (mode != 3));
            }
            Check(!control.HasPendingOperation && !server.HasPendingControl && calls == 1);
            Reject(() => control.Complete(request, true), typeof(InvalidOperationException));
            server.Close();
        }
    }
    internal static void Interop(string executable, bool sbo = false)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using (var peer = Start(executable, (sbo ? "sboenhancedserver " : "enhancedserver ") + port))
        {
            try
            {
                Check(peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == "READY");
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Loopback, port);
                    var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                    client.Associate();
                    if (sbo) SboEnhancedChecks.Run(client);
                    else EnhancedChecks.Run(client);
                }
            }
            finally
            {
                if (!peer.HasExited) { peer.StandardInput.WriteLine("quit"); peer.StandardInput.Flush(); }
                if (!peer.WaitForExit(10000)) { peer.Kill(true); throw new Exception("Enhanced peer shutdown timeout"); }
                if (peer.ExitCode != 0) throw new Exception(peer.StandardError.ReadToEnd());
            }
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int executions = 0;
        int validations = 0, requests = 0, terminations = 0;
        var worker = Task.Run(() => {
            using (var socket = listener.AcceptSocket())
            {
                MmsControlRequest pending = null;
                var control = new MmsServerControl("NFTESTLD0", sbo ? "GGIO1$CO$SboEnhanced" : "GGIO1$CO$Enhanced",
                    sbo ? MmsControlModel.SboEnhanced : MmsControlModel.DirectEnhanced,
                    completionTimeoutMilliseconds: 150);
                control.SetHandlers(r => { validations++; return -1; }, r => { pending = r; executions++; return -1; }); control.Enabled = true;
                var server = new Iec61850ServerSession(new MmsSocketChannel(socket), new MmsServerVariable[0], controls: new[] { control });
                try
                {
                    server.Associate();
                    while (true)
                    {
                        if (pending != null)
                        {
                            Thread.Sleep(30);
                            if (pending.OriginCategory != 7) control.Complete(pending, pending.OriginCategory != 8,
                                pending.OriginCategory == 8 ? 9 : 0);
                            pending = null;
                        }
                        if (server.ProcessControlCompletion()) terminations++;
                        if (socket.Poll(1000, SelectMode.SelectRead))
                        {
                            if (!server.ProcessRequest()) break;
                            requests++;
                        }
                    }
                    Check(!server.Faulted && !control.HasPendingOperation && control.Value);
                }
                finally { server.Close(); }
            }
        });
        try
        {
            using (var peer = Start(executable, (sbo ? "sboenhancedclient " : "enhancedclient ") + port + " 127.0.0.1"))
            {
                if (!peer.WaitForExit(15000)) { peer.Kill(true); throw new Exception("Native enhanced timeout"); }
                if (peer.ExitCode != 0) throw new Exception("Native enhanced failed: " + peer.StandardError.ReadToEnd());
                Check(peer.StandardOutput.ReadToEnd().Contains(sbo ? "NATIVE SBO ENHANCED PASS" : "NATIVE ENHANCED PASS"));
            }
            worker.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Check(executions == 3 && validations == (sbo ? 10 : 4) && requests == (sbo ? 18 : 10) && terminations == 4);
        }
        finally { listener.Stop(); }
        Console.WriteLine((sbo ? "SBO ENHANCED" : "ENHANCED") +
            " original peer both directions: accepted/delayed-success/negative/timeout/readback PASS.");
    }
    private static Process Start(string executable, string args)
    { return Process.Start(new ProcessStartInfo(executable, args) {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true }); }
}
