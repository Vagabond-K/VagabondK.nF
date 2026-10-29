using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>장시간 시험에서 값·품질·UTC를 갱신하며 동일한 고정 DataSet/URCB를 여러 세션에 제공하는 메모리 모델입니다.</summary>
internal sealed class ReportSoakModel
{
    internal const string Domain = "SOAKLD0", SetName = "GGIO1$Values", ReportName = "GGIO1$RP$Events";
    private readonly MmsServerVariable count = new MmsServerVariable(Domain, "GGIO1$ST$Count$stVal", 0);
    private readonly MmsServerVariable quality = Iec61850ServerData.CreateQuality(Domain, "GGIO1$ST$Count$q");
    private readonly MmsServerVariable time = Iec61850ServerData.CreateTimestamp(Domain, "GGIO1$ST$Count$t", 1700000000000L);
    private readonly MmsServerVariable[] variables;
    private readonly MmsServerDataSet dataSet;
    private readonly MmsServerReportControl report;
    private int value;
    private long nextUpdate;
    internal int Reports, Requests;
    internal ReportSoakModel()
    {
        variables = new MmsServerVariable[] { count, quality, time };
        dataSet = new MmsServerDataSet(Domain, SetName, variables);
        report = new MmsServerReportControl(Domain, ReportName, "SOAKREPORT", dataSet,
            integrityMilliseconds: 1000, minimumIntervalMilliseconds: 100);
    }
    internal void Serve(Socket socket)
    {
        var session = new Iec61850ServerSession(new MmsSocketChannel(socket), variables,
            timeoutMilliseconds: 5000, controls: new MmsServerControl[0], dataSets: new MmsServerDataSet[] { dataSet },
            reports: new MmsServerReportControl[] { report });
        try
        {
            session.Associate();
            long deadline = Environment.TickCount64 + 120000;
            while (Environment.TickCount64 < deadline)
            {
                long now = Environment.TickCount64;
                if (now >= nextUpdate)
                {
                    value++;
                    count.SetInteger(value);
                    quality.SetQuality(value % 2 == 0 ? IecQuality.Good : IecQuality.Test);
                    time.SetTimestamp(1700000000000L + value * 250L);
                    // 값이 유지되는 구간과 바뀌는 구간을 번갈아 두어 integrity 기한을 초기화하지 않고 주기 보고를 시험합니다.
                    nextUpdate = now + (value % 8 == 0 ? 1500 : 250);
                }
                if (socket.Poll(1000, SelectMode.SelectRead))
                {
                    if (!session.ProcessRequest()) return;
                    Requests++;
                }
                Reports += session.ProcessReports();
                Thread.Sleep(1);
            }
            throw new TimeoutException("Report soak connection exceeded 120 seconds.");
        }
        finally { session.Close(); }
    }
}
