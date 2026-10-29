using VagabondK.nF.MMS;
using System;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;

/// <summary>외부 시험 서버가 제공하는 도메인·변수 목록과 스칼라 형식 정보가 약속된 모델과 일치하는지 확인합니다.</summary>
internal static class DiscoveryTests
{
    internal static void NativeServer(Iec61850Client client)
    {
        MmsNameListPage domains = client.GetNameList();
        if (domains.Names.Length != 1 || domains.Names[0] != "NFTESTLD0" || domains.MoreFollows ||
            domains.ContinueAfter != null) throw new Exception("Native domain list mismatch");
        MmsNameListPage names = client.GetNameList("NFTESTLD0");
        string[] items = { "GGIO1$ST$Status$stVal", "GGIO1$ST$Count$stVal", "GGIO1$MX$Analog$mag" };
        MmsValueKind[] kinds = { MmsValueKind.Boolean, MmsValueKind.Integer, MmsValueKind.FloatingPoint };
        int[] bits = { 1, 32, 32 };
        for (int i = 0; i < items.Length; i++)
        {
            if (Array.IndexOf(names.Names, items[i]) < 0) throw new Exception("Native name missing");
            var type = client.GetVariableAccessAttributes("NFTESTLD0", items[i]);
            if (type.Kind != kinds[i] || type.BitWidth != bits[i] || type.Deletable ||
                type.ExponentWidth != (i == 2 ? 8 : 0)) throw new Exception("Native scalar attributes mismatch");
        }
        MmsNameListPage after = client.GetNameList("NFTESTLD0", names.Names[0]);
        if (after.Names.Length != names.Names.Length - 1) throw new Exception("Native continuation mismatch");
        try { client.GetVariableAccessAttributes("NFTESTLD0", "missing"); throw new Exception("Expected service error"); }
        catch (MmsServiceException error)
        {
            if (error.ErrorClass != 7 || error.ErrorCode != 2 || client.Faulted || !client.Associated)
                throw new Exception("Native service error state mismatch");
        }
        if (!client.Read("NFTESTLD0", items[0]).Boolean) throw new Exception("Read after service error failed");
        Console.WriteLine("Native server discovery: domain/names/continueAfter, three scalar types and recoverable ConfirmedError PASS.");
    }
}
