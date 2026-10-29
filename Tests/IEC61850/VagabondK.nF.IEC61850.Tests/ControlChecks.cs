using VagabondK.nF.MMS;
using System;
using System.Threading;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;

/// <summary>이미 연결된 메모리 시험 peer에서 Boolean Direct/SBO 선택·실행과 상태 조회 계약을 확인합니다.</summary>
internal static class ControlChecks
{
    private static void Check(bool ok)
    { if (!ok) throw new Exception("Boolean Direct/SBO memory control mismatch."); }
    internal static void Run(Iec61850Client client)
    {
        const string domain = "NFTESTLD0";
        int code;
        var specification = client.GetVariableAccessAttributes(domain, "GGIO1$CO$Selected");
        Check(specification.Kind == MmsValueKind.Structure && specification.ComponentCount == 3);
        var oper = specification.GetComponent(1);
        Check(oper.ComponentCount == 6 && oper.GetComponent(0).Kind == MmsValueKind.Boolean &&
            oper.GetComponent(1).GetComponent(1).Kind == MmsValueKind.OctetString &&
            oper.GetComponent(3).Kind == MmsValueKind.UtcTime &&
            oper.GetComponent(5).Kind == MmsValueKind.BitString);
        var direct = new MmsControlClient(client, domain, "GGIO1$CO$Direct", new byte[] { 78, 70 });
        Check(direct.Model == MmsControlModel.DirectNormal);
        Check(direct.Operate(true, 1700000000123, out code) && code == -1);
        Check(client.Read(domain, "GGIO1$ST$Direct$stVal").Boolean);
        Check(direct.Operate(false, 1700000000123, out code, true));
        Check(client.Read(domain, "GGIO1$ST$Direct$stVal").Boolean);
        Check(direct.Operate(false, 1700000000123, out code));
        Check(!client.Read(domain, "GGIO1$ST$Direct$stVal").Boolean);
        var sbo = new MmsControlClient(client, domain, "GGIO1$CO$Selected", new byte[] { 78, 70 });
        Check(sbo.Model == MmsControlModel.SboNormal);
        Check(sbo.Select() && sbo.Operate(true, 1700000000123, out code));
        Check(client.Read(domain, "GGIO1$ST$Selected$stVal").Boolean);
        Check(sbo.Select() && sbo.Cancel(1700000000123, out code));
        Check(sbo.Select() && sbo.Operate(false, 1700000000123, out code));
        Check(!client.Read(domain, "GGIO1$ST$Selected$stVal").Boolean && client.Associated && !client.Faulted);
        var expired = new MmsControlClient(client, domain, "GGIO1$CO$Selected", new byte[0]);
        Check(expired.Select());
        Thread.Sleep(1200);
        Check(!expired.Operate(true, 0, out code) && code == 3 && client.Associated && !client.Faulted);
        Check(expired.LastError != null && expired.LastError.AdditionalCause == 18 &&
            expired.LastError.ObjectReference == domain + "/GGIO1$CO$Selected$Oper");
        Check(!client.Read(domain, "GGIO1$ST$Selected$stVal").Boolean);
    }
}
