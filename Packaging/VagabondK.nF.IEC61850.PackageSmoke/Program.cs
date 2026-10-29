using VagabondK.nF.MMS;
using System;
using VagabondK.nF.IEC61850;

internal static class Program
{
    private sealed class Channel : IMmsChannel
    {
        public long Milliseconds { get { return Environment.TickCount64; } }
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds) { throw new InvalidOperationException("No peer."); }
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds) { throw new InvalidOperationException("No peer."); }
    }
    public static void Main()
    {
        var channel = new Channel();
        var quality = new byte[2];
        Iec61850Data.WriteQuality(IecQuality.Good, quality, 0);
        if (MmsControlModel.DirectNormal != (MmsControlModel)1) throw new Exception("Invalid control model.");
#if CLIENT
        var client = new VagabondK.nF.IEC61850.Client.Iec61850Client(channel);
        if (client.Associated) throw new Exception("Unexpected association.");
#endif
#if SERVER
        var variable = VagabondK.nF.IEC61850.Server.Iec61850ServerData.CreateQuality("NFTESTLD0", "GGIO1$ST$Ind$q");
        var session = new VagabondK.nF.IEC61850.Server.Iec61850ServerSession(channel,
            new VagabondK.nF.MMS.Server.MmsServerVariable[] { variable },
            controls: new VagabondK.nF.IEC61850.Server.MmsServerControl[0],
            dataSets: new VagabondK.nF.IEC61850.Server.MmsServerDataSet[0],
            reports: new VagabondK.nF.IEC61850.Server.MmsServerReportControl[0]);
        session.Close();
#endif
#if TCP
        using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp))
        {
            var tcp = new VagabondK.nF.MMS.TCP.MmsSocketChannel(socket);
            if (tcp.Milliseconds < 0) throw new Exception("Invalid clock.");
        }
#endif
    }
}
