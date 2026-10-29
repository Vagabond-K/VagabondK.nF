using VagabondK.nF.MMS;
using System;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;

/// <summary>Direct enhanced에서 요청 수락과 정상/부정 완료를 구분하고 Test 요청이 실행 없이 완료되는지 확인합니다.</summary>
internal static class EnhancedChecks
{
    internal static void Run(Iec61850Client client)
    {
        for (int mode = 0; mode < 4; mode++)
        {
            var control = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$Enhanced", new byte[] { 78, 70 },
                mode == 1 ? 8 : mode == 2 ? 7 : 3);
            bool accepted; int code;
            bool completed = control.OperateAndWait(mode == 0, 1234, out accepted, out code, test: mode == 3);
            if (!accepted || code != -1 || completed != (mode == 0 || mode == 3))
                throw new InvalidOperationException("Enhanced acceptance/completion mismatch.");
            if (completed ? control.LastError != null : control.LastError == null ||
                control.LastError.AdditionalCause != (mode == 1 ? 9 : 16))
                throw new InvalidOperationException("Enhanced diagnostic mismatch.");
            if (!client.Read("NFTESTLD0", "GGIO1$ST$Enhanced$stVal").Boolean || client.Faulted)
                throw new InvalidOperationException("Enhanced readback/Test mutation.");
        }
    }
}
