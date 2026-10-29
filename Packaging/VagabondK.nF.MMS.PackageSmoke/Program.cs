using System;
using VagabondK.nF.MMS;

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
        var reference = new MmsVariableReference("Plant", "Temperature");
        if (reference.Item != "Temperature") throw new Exception("Invalid reference.");
#if CLIENT
        var client = new VagabondK.nF.MMS.Client.MmsClient(channel);
        client.InformationReportReceived = report => {
            if (report.Count < 1) throw new InvalidOperationException("Empty report.");
        };
        if (client.Associated) throw new Exception("Unexpected association.");
#endif
#if SERVER
        var variable = new VagabondK.nF.MMS.Server.MmsServerVariable("Plant", "Temperature", 42);
        variable.EnableWrite();
        var list = new VagabondK.nF.MMS.Server.MmsServerNamedVariableList("Plant", "Measurements", new[] { variable });
        var session = new VagabondK.nF.MMS.Server.MmsServerSession(channel, new[] { variable }, namedVariableLists: new[] { list });
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
