using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.DNP3.TCP;

namespace VagabondK.nF.DNP3.Tests
{
    // 실제 루프백 소켓의 Dnp3TcpSession에서 TCP 분할 수신 시의 프레임 처리, 디코더 복구 및 오류 상태 유지를 검증합니다.
    // 실제 로컬 소켓으로 분할·느린 입력·부분 프레임 만료·피어 종료를 주입하고 디코더 복구와 Faulted 상태 유지를 확인합니다.
    internal static class Dnp3TcpCases
    {
        private static void Check(bool condition, string reason)
        {
            if (!condition)
                throw new Exception("DNP3 TCP: " + reason);
            Cases.Check(true);
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                Cases.Check(true);
                return;
            }
            throw new Exception("DNP3 TCP: expected " + typeof(T).Name);
        }

        internal static void Run()
        {
            Guards();
            SessionRoundTrip();
            ByteTrickle();
            DecoderRecovery();
            HalfFrameTimeout();
            PeerClose();
            EndpointFailure();
        }

        internal static void SoakNormal() { SessionRoundTrip(50); }
        internal static void SoakMalformed() { DecoderRecovery(); HalfFrameTimeout(); }
        internal static void SoakLifecycle() { PeerClose(); EndpointFailure(); SessionRoundTrip(); }

        private static void Pair(Action<Socket, Socket> body)
        {
            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            using (var local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                local.Connect(listener.LocalEndPoint);
                using (Socket remote = listener.Accept())
                {
                    local.NoDelay = true;
                    remote.NoDelay = true;
                    body(remote, local);
                }
            }
        }

        private static Dnp3Station Station()
        {
            return new Dnp3Station(new Dnp3Database(new[] {
                new Dnp3PointDefinition(Dnp3PointType.Analog, 0, Dnp3Value.Analog(-12.5)),
                new Dnp3PointDefinition(Dnp3PointType.Counter, 0, Dnp3Value.Bits(0xFFFFFFFF))
            }, 16), 1, 2);
        }

        // 요청을 만들고 나중에 소켓에서 읽은 원시 응답 프레임을 검증하는 Master입니다.
        private static Dnp3Master Requester(out byte[] request)
        {
            var master = new Dnp3Master(2, 1, new Handler());
            request = master.ReadClasses(1, Environment.TickCount64);
            return master;
        }

        private static bool Answered(Socket remote, Dnp3Master master)
        {
            var clock = Stopwatch.StartNew();
            while (master.Result != Dnp3MasterResult.Success && clock.ElapsedMilliseconds < 2000)
                master.Process(ReceiveFrame(remote, 2000), Environment.TickCount64);
            return master.Result == Dnp3MasterResult.Success;
        }

        private static byte[] ReceiveFrame(Socket remote, int timeoutMilliseconds)
        {
            var header = new byte[3];
            ReadExactly(remote, header, 0, 3, timeoutMilliseconds);
            var frame = new byte[Dnp3Link.FrameLength(header)];
            Array.Copy(header, frame, 3);
            ReadExactly(remote, frame, 3, frame.Length - 3, timeoutMilliseconds);
            return frame;
        }

        private static void ReadExactly(Socket socket, byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            socket.ReceiveTimeout = timeoutMilliseconds;
            while (count > 0)
            {
                int read = socket.Receive(buffer, offset, count, SocketFlags.None);
                Check(read > 0, "peer response before EOF");
                offset += read;
                count -= read;
            }
        }

        private static void Guards()
        {
            Pair((remote, local) =>
            {
                var station = Station();
                Throws<ArgumentNullException>(() => new Dnp3TcpSession(null, station));
                Throws<ArgumentNullException>(() => new Dnp3TcpSession(local, null));
                Throws<ArgumentOutOfRangeException>(() => new Dnp3TcpSession(local, station, 1000, 0));
                Check(!new Dnp3TcpSession(local, station, 1000, 1).Faulted, "1 ms write timeout is accepted");
                var session = new Dnp3TcpSession(local, station);
                Throws<ArgumentOutOfRangeException>(() => session.RunOnce(-1));
                Throws<ArgumentOutOfRangeException>(() => session.RunOnce(1001));
                session.RunOnce(1000);
                Check(!session.Faulted, "argument errors do not fault the session");
                session.Send(null);
                session.RunOnce(0);
                Check(!session.Faulted, "null send and empty poll are no-ops");
            });
        }

        private static void SessionRoundTrip(int iterations = 1)
        {
            Pair((remote, local) =>
            {
                var handler = new Handler();
                var master = new Dnp3Master(2, 1, handler);
                var stationSession = new Dnp3TcpSession(remote, Station());
                var masterSession = new Dnp3TcpSession(local, master);
                for (int i = 0; i < iterations; i++)
                {
                    long started = SoakRunner.StartExchange();
                    masterSession.Send(master.ReadClasses(1, Environment.TickCount64));
                    var clock = Stopwatch.StartNew();
                    while (!(master.Result == Dnp3MasterResult.Success && handler.Count == 2) && clock.ElapsedMilliseconds < 3000)
                    {
                        stationSession.RunOnce(5);
                        masterSession.RunOnce(5);
                    }
                    Check(master.Result == Dnp3MasterResult.Success && handler.Count == 2, "integrity poll over two TCP sessions (result=" + master.Result + ", count=" + handler.Count + ", faults=" + stationSession.Faulted + "/" + masterSession.Faulted + ")");
                    Check(handler.Analog == -12.5 && handler.Counter == 0xFFFFFFFF, "values survive the TCP sessions");
                    SoakRunner.EndExchange(started);
                }
                Check(!stationSession.Faulted && !masterSession.Faulted, "healthy sessions");
            });
        }

        private static void ByteTrickle()
        {
            Pair((remote, local) =>
            {
                var session = new Dnp3TcpSession(local, Station());
                byte[] request;
                var master = Requester(out request);
                for (int i = 0; i < request.Length; i++)
                {
                    remote.Send(request, i, 1, SocketFlags.None);
                    Thread.Sleep(1);
                    session.RunOnce(20);
                }
                for (int i = 0; i < 20; i++)
                    session.RunOnce(5);
                Check(Answered(remote, master) && !session.Faulted, "frame reassembled from one-byte TCP segments");
            });
        }

        private static void DecoderRecovery()
        {
            Pair((remote, local) =>
            {
                var session = new Dnp3TcpSession(local, Station());
                byte[] request;
                var master = Requester(out request);
                byte[] corrupt = (byte[])request.Clone();
                corrupt[corrupt.Length - 1] ^= 0xFF;
                remote.Send(new byte[] { 1, 2, 3, 0x64, 5 });
                remote.Send(corrupt);
                for (int i = 0; i < 10; i++)
                    session.RunOnce(10);
                Check(session.Decoder.DiscardedBytes > 0 && session.Decoder.CrcErrors > 0 && !session.Faulted,
                    "noise and CRC errors are counted without faulting");
                remote.Send(request);
                for (int i = 0; i < 10; i++)
                    session.RunOnce(10);
                Check(Answered(remote, master), "valid frame after noise is answered");
            });
        }

        private static void HalfFrameTimeout()
        {
            Pair((remote, local) =>
            {
                var session = new Dnp3TcpSession(local, Station(), 100);
                                byte[] request;
                                var master = Requester(out request);
                remote.Send(request, 0, 5, SocketFlags.None);
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 300)
                    session.RunOnce(10);
                Check(session.Decoder.FrameTimeouts == 1 && !session.Faulted, "stalled partial frame is dropped after the frame timeout");
                remote.Send(request);
                for (int i = 0; i < 10; i++)
                    session.RunOnce(10);
                Check(Answered(remote, master), "session recovers after a frame timeout");
            });
        }

        private static void PeerClose()
        {
            Pair((remote, local) =>
            {
                var session = new Dnp3TcpSession(local, Station());
                byte[] request;
                Requester(out request);
                remote.Send(request, 0, 7, SocketFlags.None);
                remote.Shutdown(SocketShutdown.Send);
                Throws<IOException>(() =>
                {
                    for (int i = 0; i < 50; i++)
                        session.RunOnce(20);
                });
                Check(session.Faulted, "peer close mid-frame latches the fault");
                Throws<InvalidOperationException>(() => session.RunOnce(0));
                Throws<InvalidOperationException>(() => session.Send(request));
            });
        }

        private static void EndpointFailure()
        {
            Pair((remote, local) =>
            {
                var session = new Dnp3TcpSession(local, new FailingEndpoint());
                byte[] request;
                Requester(out request);
                remote.Send(request);
                Throws<InvalidOperationException>(() =>
                {
                    for (int i = 0; i < 50; i++)
                        session.RunOnce(20);
                });
                Check(session.Faulted, "endpoint failure latches the fault");
                Throws<InvalidOperationException>(() => session.RunOnce(0));
            });
        }

        private sealed class FailingEndpoint : IDnp3Endpoint
        {
            public ushort LocalAddress { get { return 1; } }

            public ushort RemoteAddress { get { return 2; } }

            public bool IsMaster { get { return false; } }

            public void ResetConnection()
            {
            }

            public byte[] Process(byte[] frame, long now)
            {
                throw new InvalidOperationException("Endpoint failure.");
            }

            public byte[] Poll(long now)
            {
                return null;
            }
        }

        private sealed class Handler : IDnp3MasterHandler
        {
            internal int Count;
            internal double Analog;
            internal ulong Counter;

            public void OnMeasurements(Dnp3Measurement[] values, Dnp3ResponseInfo response)
            {
                if (response.Unsolicited)
                    return;
                if (response.First)
                    Count = 0;
                foreach (var value in values)
                {
                    Count++;
                    if (value.Type == Dnp3PointType.Analog)
                        Analog = value.Value.AsAnalog();
                    else if (value.Type == Dnp3PointType.Counter)
                        Counter = value.Value.Raw;
                }
            }
        }
    }
}
