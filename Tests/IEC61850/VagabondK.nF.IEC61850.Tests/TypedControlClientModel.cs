using VagabondK.nF.MMS;
using System.Net;
using System.Net.Sockets;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.MMS.TCP;

/// <summary>보드 클라이언트의 형식별 제어 반복 시험에서 지정된 데스크톱 peer로 연결하는 메모리 시험 모델입니다.</summary>
internal sealed class TypedControlClientModel
{
    internal IPAddress Peer { get; set; }
    internal int Reports, Requests;
    internal void Serve()
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            socket.Connect(new IPEndPoint(Peer, 18105));
            var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
            client.Associate();
            TypedControlChecks.Run(client, false);
            client.Release();
            Requests += TypedControlChecks.Checks;
            Reports += TypedControlChecks.Completions;
        }
    }
}
