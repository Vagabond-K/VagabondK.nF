using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.MasterMapping.Sample;
using VagabondK.nF.DNP3.TCP;

// 보드 Master 매핑의 수신 동작과 규모별 힙 사용량을 검사하고 성공/실패 지표를 UDP 20002로 제공합니다.
// 네트워크 설정과 전용 PC Outstation:20000이 필요하며 운영 장치에 제어 시나리오를 보내면 안 됩니다.
internal static class MasterHardware
{
    private static string stage = "startup";
    internal static void Run()
    {
        try { Execute(); }
        catch (Exception error)
        {
            string failure = stage + ": " + error;
            Debug.WriteLine("DNP3 MASTER MAPPING FAILED: " + failure);
            Serve(null, failure);
        }
    }
    private static void Execute()
    {
        var metrics = new uint[16];
#if DNP3_MASTER_MAPPING_SCALING
        stage = "model";
        metrics[0] = ScalingModel.Count;
        metrics[1] = nanoFramework.Runtime.Native.GC.Run(true);
        var data = new ScalingModel();
        metrics[2] = nanoFramework.Runtime.Native.GC.Run(true);
        var binding = new ScalingModel.Dnp3MasterBinding(data, new object());
        metrics[3] = nanoFramework.Runtime.Native.GC.Run(true);
        data.Captured = new Dnp3Measurement[32][];
        data.Responses = new Dnp3ResponseInfo[32];
        metrics[11] = nanoFramework.Runtime.Native.GC.Run(true);
        var master = new Dnp3Master(2, 1, binding, responseTimeout: 10000, maximumMeasurements: 512);
        stage = "independent receive";
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            socket.Connect(new IPEndPoint(IPAddress.Parse("192.168.0.210"), 20000));
            var session = new Dnp3TcpSession(socket, master, 3000, 3000);
            MasterValidation.Request(master, session, master.SetUnsolicited(14, false, Environment.TickCount64));
            MasterValidation.Request(master, session, master.ReadClasses(1, Environment.TickCount64));
            if (data.Records != 490 || data.Fragments < 2)
                throw new InvalidOperationException("Independent scaling record/Fragment count.");
            if (!data.Verify()) throw new InvalidOperationException("Independent scaling model values.");
        }
        stage = "replay";
        Profile(data, binding, metrics);
#else
        stage = "sample";
        var validation = new MasterValidation();
        validation.Run("192.168.0.210", 20000, 8);
        metrics[0] = 7;
        metrics[4] = (uint)validation.Checks;
        metrics[5] = (uint)validation.Events;
        metrics[6] = (uint)validation.Unsolicited;
        metrics[7] = (uint)validation.Received;
#endif
        metrics[14] = Debugger.IsAttached ? 1U : 0U;
        metrics[15] = 0x600D;
        Serve(metrics, null);
    }
    private static void Serve(uint[] metrics, string failure)
    {
        var request = new byte[5];
        byte[] error = failure == null ? null : Encoding.UTF8.GetBytes(failure);
        var response = new byte[error == null ? 68 : 4 + error.Length];
        if (error != null) Array.Copy(error, 0, response, 4, error.Length);
        using (var diagnostic = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            diagnostic.Bind(new IPEndPoint(IPAddress.Any, 20002));
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int length = diagnostic.ReceiveFrom(request, ref remote);
                if (length != 4 || request[0] != 0x44 || request[1] != 0x4d || request[2] != 0x4d || request[3] != 0x31)
                {
                    Debug.WriteLine("Rejected Master mapping diagnostic.");
                    continue;
                }
                Array.Copy(request, response, 4);
                if (error != null) response[3] = 0x45;
                for (int i = 0; error == null && i < metrics.Length; i++)
                {
                    uint value = metrics[i];
                    int p = 4 + i * 4;
                    response[p] = (byte)(value >> 24); response[p + 1] = (byte)(value >> 16);
                    response[p + 2] = (byte)(value >> 8); response[p + 3] = (byte)value;
                }
                if (diagnostic.SendTo(response, remote) != response.Length) throw new InvalidOperationException("Diagnostic send.");
            }
        }
    }
#if DNP3_MASTER_MAPPING_SCALING
    private static void Replay(ScalingModel data, ScalingModel.Dnp3MasterBinding binding)
    {
        for (int i = 0; i < data.Fragments; i++) binding.OnMeasurements(data.Captured[i], data.Responses[i]);
    }
    private static void Profile(ScalingModel data, ScalingModel.Dnp3MasterBinding binding, uint[] metrics)
    {
        data.Replay = true;
        Replay(data, binding);
        data.Records = 0;
        metrics[4] = nanoFramework.Runtime.Native.GC.Run(true);
        long start = Environment.TickCount64;
        const int rounds = 20;
        for (int i = 0; i < rounds; i++) Replay(data, binding);
        metrics[5] = (uint)(Environment.TickCount64 - start);
        metrics[6] = nanoFramework.Runtime.Native.GC.Run(true);
        metrics[7] = rounds;
        metrics[8] = (uint)data.Records;
        metrics[9] = (uint)data.Fragments;
        if (!data.Verify() || data.Records != rounds * 490 || metrics[6] < metrics[4])
            throw new InvalidOperationException("Mapping replay state/retained heap.");
        metrics[10] = nanoFramework.Runtime.Native.GC.Run(true);
    }
#endif
}
