using VagabondK.nF.MMS;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>실제 모니터 샘플의 탐색·DataSet 읽기·보고·정상 종료 흐름을 로컬 및 외부 시험 peer로 검증합니다.</summary>
internal static class MonitorSampleTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("Monitor sample: " + message); }
    internal static void Run()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port, executions = 0;
        var variables = DataServiceTests.Model(); var set = DataServiceTests.Set(variables);
        var report = new MmsServerReportControl(DataChecks.Domain, DataChecks.RcbName, "NFREPORT", set);
        var control = new MmsServerControl(DataChecks.Domain, "GGIO1$CO$Direct", MmsControlModel.DirectNormal);
        control.SetHandlers(r => -1, r => { executions++; return -1; }); control.Enabled = true;
        var worker = Task.Run(() => {
            for (int connection = 0; connection < 3; connection++)
            using (var socket = listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult())
            {
                var session = new Iec61850ServerSession(new MmsSocketChannel(socket), variables, controls: new[] { control },
                    dataSets: new[] { set }, reports: new[] { report }, maximumNamesPerPage: 1);
                try
                {
                    session.Associate();
                    while (session.Associated)
                    {
                        if (socket.Poll(1000, SelectMode.SelectRead) && !session.ProcessRequest()) break;
                        session.ProcessReports();
                    }
                }
                finally { session.Close(); }
            }
        });
        try
        {
            var monitor = new MonitorSession(message => { });
            using (var socket = VirtualIedFaultTests.Connect(port))
                monitor.Run(new MmsSocketChannel(socket), DataChecks.Domain, DataChecks.SetName, DataChecks.RcbName, control.Item);
            Check(monitor.Reports >= 3 && monitor.ControlAttempted && executions == 0, "Test was not side-effect free");
            var operated = new MonitorSession(message => { });
            using (var socket = VirtualIedFaultTests.Connect(port))
            {
                var loss = new VirtualIedFaultTests.PacketChannel(socket) {
                    ArmOnOperate = VirtualIedFaultTests.FaultAction.Drop, Tag = 0xa1 };
                bool failed = false;
                try { operated.Run(loss, DataChecks.Domain, DataChecks.SetName, DataChecks.RcbName, control.Item, test: false); }
                catch (TimeoutException) { failed = true; }
                Check(failed && operated.ControlAttempted && loss.OperateRequests == 1, "lost response contract");
            }
            using (var socket = VirtualIedFaultTests.Connect(port))
                operated.Run(new MmsSocketChannel(socket), DataChecks.Domain, DataChecks.SetName, DataChecks.RcbName, control.Item, test: false);
            Check(executions == 1 && operated.Reports >= 6, "reconnect replayed control or lost reporting");
            worker.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { listener.Stop(); }
        Console.WriteLine("MONITOR SAMPLE: paged discovery/DataSet/GI/Test, lost Operate response/reconnect/no replay PASS.");
    }
    internal static void Interop(string executable)
    {
        int port = VirtualIedFaultTests.FreePort();
        var monitor = new MonitorSession(message => { });
        for (int restart = 0; restart < 2; restart++)
        using (var peer = new VirtualIedFaultTests.NativeIed(executable, "dataserver", port))
        using (var socket = VirtualIedFaultTests.Connect(port))
            monitor.Run(new MmsSocketChannel(socket), DataChecks.Domain, DataChecks.SetName, DataChecks.RcbName);
        Check(monitor.Reports >= 6 && !monitor.ControlAttempted, "native report/restart");
        Console.WriteLine("MONITOR ORIGINAL: shared MCU sample against original virtual IED, two process lifetimes PASS.");
    }
}
