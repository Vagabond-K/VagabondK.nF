using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>DPC/APC의 네 가지 제어 모델과 enhanced 완료 대기를 제공하는 보드/루프백 공용 메모리 모델입니다.</summary>
internal sealed class TypedControlModel
{
    private readonly MmsServerControl[] controls = new MmsServerControl[8];
    private readonly MmsControlRequest[] pending = new MmsControlRequest[8];
    internal int Reports, Requests;
    internal TypedControlModel()
    {
        for (int kind = 1; kind <= 2; kind++)
        for (int mode = 1; mode <= 4; mode++)
        {
            int index = (kind - 1) * 4 + mode - 1;
            var control = new MmsServerControl("NFTESTLD0", "GGIO1$CO$" + (kind == 1 ? "D" : "A") + mode,
                (MmsControlModel)mode, valueKind: (MmsControlValueKind)kind);
            controls[index] = control;
            int token = index;
            control.SetHandlers(request => request.ValueKind != MmsControlValueKind.AnalogFloat32 ||
                (request.AnalogValue >= -100 && request.AnalogValue <= 100) ? -1 : 11,
                request => { pending[token] = request; return -1; });
            control.Enabled = true;
        }
    }
    internal void Serve(Socket socket)
    {
        var session = new Iec61850ServerSession(new MmsSocketChannel(socket), new MmsServerVariable[0],
            controls: controls, timeoutMilliseconds: 5000,
            dataSets: new MmsServerDataSet[0], reports: new MmsServerReportControl[0]);
        try
        {
            session.Associate();
            long deadline = Environment.TickCount64 + 120000;
            while (Environment.TickCount64 < deadline)
            {
                if (socket.Poll(1000, SelectMode.SelectRead))
                {
                    if (!session.ProcessRequest()) return;
                    Requests++;
                }
                for (int i = 0; i < pending.Length; i++)
                {
                    if (pending[i] != null && controls[i].HasPendingOperation)
                        controls[i].Complete(pending[i], true);
                    pending[i] = null;
                }
                if (session.ProcessControlCompletion()) Reports++;
                Thread.Sleep(1);
            }
            throw new TimeoutException("Typed control connection exceeded 120 seconds.");
        }
        finally { session.Close(); }
    }
}
