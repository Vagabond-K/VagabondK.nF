using System;
using System.Net;
using System.Net.Sockets;

internal static class BoardHeap
{
    // MQS1 명령과 별도인 GCM1로 GC 후 여유 관리 힙만 조회합니다.
    internal static uint Sample(UdpClient udp)
    {
        byte[] request = { 71, 67, 77, 49 };
        long deadline = Environment.TickCount64 + 10000;
        while (Environment.TickCount64 < deadline)
        {
            udp.Send(request, request.Length);
            IPEndPoint source = new IPEndPoint(IPAddress.Any, 0);
            byte[] reply;
            try { reply = udp.Receive(ref source); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut) { continue; }
            if (reply.Length != 8 || reply[0] != 71 || reply[1] != 67 || reply[2] != 77 || reply[3] != 49)
                throw new InvalidOperationException("Board GC diagnostic response.");
            return (uint)reply[4] << 24 | (uint)reply[5] << 16 | (uint)reply[6] << 8 | reply[7];
        }
        throw new TimeoutException("Board GC diagnostic.");
    }
}
