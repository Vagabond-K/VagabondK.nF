using VagabondK.nF.MMS;
using System;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;

internal delegate void MonitorLog(string message);

/// <summary>탐색→DataSet 읽기→URCB 예약·GI 수신→선택적 시험 제어→정상 종료를 직렬 수행합니다.</summary>
internal sealed class MonitorSession
{
    private readonly MonitorLog log;
    internal bool ControlAttempted { get; private set; }
    internal int Reports { get; private set; }
    internal MonitorSession(MonitorLog log)
    {
        if (log == null) throw new ArgumentNullException("log");
        this.log = log;
    }
    // 채널은 호출자가 해제합니다. 서버의 고정 DataSet/URCB 구성이 일치해야 하며 제어는 기본 Test 요청입니다.
    internal void Run(IMmsChannel channel, string domain, string dataSet, string reportItem,
        string controlItem = null, bool test = true)
    {
        var client = new Iec61850Client(channel, timeoutMilliseconds: 5000);
        client.Associate();
        Discover(client, null, false);
        Discover(client, domain, true);
        var set = client.GetDataSetDirectory(domain, dataSet);
        var values = client.ReadDataSet(set);
        for (int i = 0; i < values.Length; i++)
            if (values[i].ErrorCode != -1) throw new InvalidOperationException("DataSet access error: " + values[i].ErrorCode);
        log("DataSet members=" + set.Count + ", values=" + values.Length);
        var reports = new MmsReportClient(client, domain, reportItem, set, delegate(MmsReport report) {
            Reports++;
            log("Report sequence=" + report.SequenceNumber + ", members=" + report.Count);
        });
        reports.Reserve(true); reports.Enable();
        for (int i = 0; i < 3; i++)
        {
            int before = Reports;
            reports.GeneralInterrogation();
            if (Reports == before) client.ReceiveReport();
        }
        reports.Disable(); reports.Reserve(false);
        if (controlItem != null && !ControlAttempted)
        {
            // 응답이 유실되어도 재접속 후 명령을 재실행하지 않도록 전송 전에 시도 여부를 기록합니다.
            ControlAttempted = true;
            var control = new MmsControlClient(client, domain, controlItem, new byte[] { 78, 70, 83 });
            int error;
            bool accepted, completed;
            if (control.Model == MmsControlModel.SboNormal && !control.Select())
                throw new InvalidOperationException("SBO selection denied.");
            if (control.Model == MmsControlModel.SboEnhanced && !control.SelectWithValue(true, 0, out error, test))
                throw new InvalidOperationException("SBOw selection denied: " + error);
            if (control.Model == MmsControlModel.DirectEnhanced || control.Model == MmsControlModel.SboEnhanced)
                completed = control.OperateAndWait(true, 0, out accepted, out error, test);
            else { accepted = control.Operate(true, 0, out error, test); completed = false; }
            log("Control test=" + test + ", accepted=" + accepted + ", completed=" + completed + ", error=" + error);
            if (!accepted || ((int)control.Model >= 3 && !completed))
                throw new InvalidOperationException("Control rejected or failed; inspect the recorded result.");
        }
        client.Release();
    }
    private void Discover(Iec61850Client client, string domain, bool dataSets)
    {
        string continuation = null;
        for (int page = 0; page < 16; page++)
        {
            var names = client.GetNameList(domain, continuation, maximumNames: 16, dataSets: dataSets);
            for (int i = 0; i < names.Names.Length; i++) log("Discovered: " + names.Names[i]);
            if (!names.MoreFollows) return;
            continuation = names.ContinueAfter;
        }
        throw new InvalidOperationException("Discovery exceeded the sample's 16-page bound.");
    }
}
