using VagabondK.nF.MMS;
using System;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;

/// <summary>관리 코드와 외부 피어 시험에서 공유하는 품질·UTC 및 고정 DataSet/URCB 기대값을 확인합니다.</summary>
internal static class DataChecks
{
    internal const string Domain = "NFTESTLD0", SetName = "GGIO1$Measurements", RcbName = "GGIO1$RP$Events";
    private static void Check(bool ok) { if (!ok) throw new Exception("Data/report shared check failed."); }
    internal static void Quality(Iec61850Client client)
    {
        Check(Iec61850Data.ReadQuality(client.Read(Domain, "GGIO1$ST$Status$q")) ==
            (IecQuality.Invalid | IecQuality.Test | IecQuality.OldData));
        var utc = client.Read(Domain, "GGIO1$ST$Status$t");
        Check(Iec61850Data.ReadTimestampMilliseconds(utc) == 1700000000125 && Iec61850Data.ReadTimeQuality(utc) == 0x3f);
    }
    internal static MmsReportClient Run(Iec61850Client client)
    {
        Check(client.SupportsDataSetDirectory);
        var names = client.GetNameList(Domain, dataSets: true);
        Check(names.Names.Length == 1 && names.Names[0] == SetName && !names.MoreFollows);
        var set = client.GetDataSetDirectory(Domain, SetName);
        Check(set.Count == 3 && !set.Deletable);
        var refs = new MmsVariableReference[5];
        for (int i = 0; i < 3; i++) refs[i] = set.GetMember(i);
        refs[3] = new MmsVariableReference(Domain, "missing"); refs[4] = refs[0];
        var multiple = client.ReadMultiple(refs);
        Check(multiple.Length == 5 && multiple[0].Value.Boolean && multiple[1].Value.Integer == -123456 &&
            multiple[2].Value.FloatingPoint == 12.5 && multiple[3].ErrorCode == 10 && multiple[3].Value == null &&
            multiple[4].Value.Boolean);
        var snapshot = client.ReadDataSet(set);
        Check(snapshot.Length == 3 && snapshot[0].Value.Boolean && snapshot[1].Value.Integer == -123456 &&
            snapshot[2].Value.FloatingPoint == 12.5);
        int received = 0;
        var report = new MmsReportClient(client, Domain, RcbName, set, r => {
            Check(r.ReportId == "NFREPORT" && r.ConfigurationRevision == 1 && r.SequenceNumber == 0 &&
                r.Count == 3 && r.GetValue(0).Boolean && r.GetValue(1).Integer == -123456 &&
                r.GetValue(2).FloatingPoint == 12.5 && r.GetReason(0) == 4 && r.GetReason(1) == 4 && r.GetReason(2) == 4);
            received++;
        });
        report.Enable(); report.GeneralInterrogation();
        if (received == 0) client.ReceiveReport();
        report.Disable();
        Check(received == 1 && !client.Faulted);
        return report;
    }
}
