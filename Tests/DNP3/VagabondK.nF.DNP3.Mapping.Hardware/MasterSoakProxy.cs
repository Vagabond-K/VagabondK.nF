using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

// 데스크톱 전용 오류 주입기이며 MCU 이미지에는 포함하지 않습니다.
// 보드와 독립 피어 사이의 TCP를 중계하다 연결 양쪽을 RST로 닫아 복구를 유도하는 PC 전용 도구입니다.
// 지정한 주소에 Listener를 열고 기존 연결을 강제로 끊으므로 운영 트래픽에 사용하지 않습니다.
public sealed class MasterSoakProxy : IDisposable
{
    private readonly TcpListener listener;
    private readonly IPEndPoint target;
    private readonly object gate = new object();
    private readonly List<Socket> connections = new List<Socket>();
    private readonly List<Task> workers = new List<Task>();
    private readonly HashSet<Socket> injected = new HashSet<Socket>();
    private readonly Task accepting;
    private bool stopping;
    private Exception error;
    public int ObservedTransportClosures;
    public MasterSoakProxy(string address, int port, int targetPort)
    {
        target = new IPEndPoint(IPAddress.Parse(address), targetPort);
        listener = new TcpListener(IPAddress.Parse(address), port);
        listener.Start();
        accepting = Accept();
    }
    public int ActiveConnections { get { lock (gate) return connections.Count / 2; } }
    public void Healthy()
    {
        lock (gate) if (error != null) throw new IOException("Soak proxy failed.", error);
    }
    private async Task Accept()
    {
        try
        {
            while (true)
            {
                Socket client = await listener.AcceptSocketAsync().ConfigureAwait(false);
                lock (gate)
                {
                    if (stopping) { client.Dispose(); return; }
                    workers.RemoveAll(t => t.IsCompleted);
                    workers.Add(Bridge(client));
                }
            }
        }
        catch (SocketException ex) { lock (gate) if (!stopping) error = ex; }
        catch (ObjectDisposedException ex) { lock (gate) if (!stopping) error = ex; }
    }
    private async Task Bridge(Socket client)
    {
        using (client)
        using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            bool registered = false;
            try
            {
                await server.ConnectAsync(target).ConfigureAwait(false);
                lock (gate)
                {
                    connections.Add(client); connections.Add(server); registered = true;
                }
                Task outbound = Copy(client, server), inbound = Copy(server, client);
                Task first = await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
                lock (gate)
                    if (first.IsFaulted && !injected.Contains(client) && !stopping) error = first.Exception;
                client.Close();
                server.Close();
                await Task.WhenAll(outbound, inbound).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                // 어느 한쪽 연결이 닫히면 반대쪽도 닫습니다. 연결 실패는 예상한 주입 오류가 아닙니다.
                lock (gate)
                {
                    ObservedTransportClosures++;
                    if (!registered && !stopping) error = ex;
                }
            }
            catch (ObjectDisposedException ex)
            {
                lock (gate)
                {
                    ObservedTransportClosures++;
                    if (!registered && !stopping) error = ex;
                }
            }
            catch (Exception ex) { lock (gate) error = ex; }
            finally
            {
                lock (gate)
                {
                    connections.Remove(client); connections.Remove(server);
                    injected.Remove(client); injected.Remove(server);
                }
            }
        }
    }
    private static async Task Copy(Socket source, Socket destination)
    {
        var bytes = new byte[4096];
        while (true)
        {
            int length = await source.ReceiveAsync(bytes, SocketFlags.None).ConfigureAwait(false);
            if (length == 0) return;
            for (int p = 0; p < length;)
            {
                int written = await destination.SendAsync(new ArraySegment<byte>(bytes, p, length - p), SocketFlags.None).ConfigureAwait(false);
                if (written == 0) throw new IOException("Proxy send made no progress.");
                p += written;
            }
        }
    }
    public int ResetConnections()
    {
        lock (gate)
        {
            int count = connections.Count / 2;
            foreach (Socket socket in connections)
            {
                injected.Add(socket);
                socket.LingerState = new LingerOption(true, 0);
                socket.Close();
            }
            return count;
        }
    }
    public void Dispose()
    {
        lock (gate) stopping = true;
        listener.Stop();
        ResetConnections();
        if (!accepting.Wait(5000)) throw new TimeoutException("Proxy accept shutdown.");
        Task[] pending;
        lock (gate) pending = workers.ToArray();
        if (!Task.WaitAll(pending, 5000)) throw new TimeoutException("Proxy bridge shutdown.");
        Healthy();
    }
}
