using VagabondK.nF.MMS;
using System;
using System.Threading;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;

/// <summary>SBOw에서 선택한 값·Test가 후속 실행과 일치해야 하며 수락과 완료가 구분되는지 공통 peer로 확인합니다.</summary>
internal static class SboEnhancedChecks
{
    internal static void Run(Iec61850Client client)
    {
        for (int mode = 0; mode < 4; mode++)
        {
            var control = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$SboEnhanced",
                new byte[] { 78, 70 }, mode == 1 ? 8 : mode == 2 ? 7 : 3);
            bool accepted; int code;
            if (control.Model != MmsControlModel.SboEnhanced ||
                !control.SelectWithValue(mode == 0, 1234, out code, test: mode == 3) || code != -1)
                throw new InvalidOperationException("SBOw selection mismatch.");
            bool completed = control.OperateAndWait(mode == 0, 2345, out accepted, out code, test: mode == 3);
            if (!accepted || code != -1 || completed != (mode == 0 || mode == 3))
                throw new InvalidOperationException("SBO enhanced acceptance/completion mismatch.");
            if (completed ? control.LastError != null : control.LastError == null ||
                control.LastError.AdditionalCause != (mode == 1 ? 9 : 16))
                throw new InvalidOperationException("SBO enhanced diagnostic mismatch.");
            if (!client.Read("NFTESTLD0", "GGIO1$ST$SboEnhanced$stVal").Boolean || client.Faulted)
                throw new InvalidOperationException("SBO enhanced readback/Test mutation.");
        }
        var selected = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$SboEnhanced", new byte[] { 78, 70 });
        int result;
        if (!selected.SelectWithValue(false, 1234, out result) || !selected.Cancel(2345, out result) ||
            !selected.SelectWithValue(false, 3456, out result))
            throw new InvalidOperationException("SBO enhanced cancel/reselect.");
        Thread.Sleep(1200);
        bool received;
        if (selected.OperateAndWait(false, 4567, out received, out result) || received || result != 3 ||
            selected.LastError == null || selected.LastError.AdditionalCause != 18 || client.Faulted ||
            !client.Read("NFTESTLD0", "GGIO1$ST$SboEnhanced$stVal").Boolean)
            throw new InvalidOperationException("SBO enhanced selection expiry.");
    }
}
