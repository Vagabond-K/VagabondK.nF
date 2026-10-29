using VagabondK.nF.MMS;
using System;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;

/// <summary>DPC/APC 값 프로파일을 normal/enhanced 및 Direct/SBO 모델에 교차 적용하여 수락·완료·범위 검사를 확인합니다.</summary>
internal static class TypedControlChecks
{
    internal static int Checks, Completions;
    private static void Check(bool ok) { Checks++; if (!ok) throw new Exception("Typed control assertion."); }
    internal static void Run(Iec61850Client client, bool rangeChecks = true)
    {
        Checks = Completions = 0;
        int error;
        for (int kind = 1; kind <= 2; kind++)
        for (int mode = 1; mode <= 4; mode++)
        {
            bool analog = kind == 2, enhanced = mode >= 3;
            var control = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$" + (analog ? "A" : "D") + mode,
                new byte[] { 78, 70 }, valueKind: (MmsControlValueKind)kind);
            Check((int)control.Model == mode);
            for (int action = 0; action < 3; action++)
            {
                bool test = action == 2, boolean = action != 1;
                float number = action == 1 ? -6.25f : 12.5f;
                if (mode == 2) Check(control.Select());
                if (mode == 4) Check(analog ? control.SelectWithValue(number, 0, out error, test) : control.SelectWithValue(boolean, 0, out error, test));
                bool accepted = false;
                if (enhanced)
                {
                    Check(analog ? control.OperateAndWait(number, 0, out accepted, out error, test) :
                        control.OperateAndWait(boolean, 0, out accepted, out error, test));
                    Check(accepted && error == -1);
                    Completions++;
                }
                else Check(analog ? control.Operate(number, 0, out error, test) : control.Operate(boolean, 0, out error, test));
                var state = client.Read("NFTESTLD0", "GGIO1$" + (analog ? "MX$A" : "ST$D") + mode + (analog ? "$mxVal$f" : "$stVal"));
                Check(analog ? state.FloatingPoint == (action == 0 ? 12.5 : -6.25) :
                    Iec61850Data.ReadDoublePoint(state) == (action == 0 ? IecDoublePoint.On : IecDoublePoint.Off));
            }
            if (mode == 2) { Check(control.Select()); Check(control.Cancel(0, out error)); }
            if (mode == 4)
            {
                Check(analog ? control.SelectWithValue(1f, 0, out error) : control.SelectWithValue(true, 0, out error));
                Check(control.Cancel(0, out error));
            }
            if (analog && rangeChecks)
            {
                bool accepted;
                if (mode == 2) Check(control.Select());
                if (mode == 4) Check(!control.SelectWithValue(128f, 0, out error) && error == 11);
                else if (enhanced) Check(!control.OperateAndWait(128f, 0, out accepted, out error) && !accepted && error == 11);
                else Check(!control.Operate(128f, 0, out error) && error == 11);
                Check(!client.Faulted && client.Read("NFTESTLD0", "GGIO1$MX$A" + mode + "$mxVal$f").FloatingPoint == -6.25);
            }
            bool rejected = false;
            try
            {
                if (analog) control.Operate(float.NaN, 0, out error);
                else control.Operate(1f, 0, out error);
            }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !client.Faulted);
        }
    }
}
