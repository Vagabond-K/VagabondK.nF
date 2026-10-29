using System;
using System.Threading;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Client;
using VagabondK.nF.MMS.Server;

namespace VagabondK.nF.Virtual.Tests
{
    /// <summary>스레드 간 메모리 채널에서 MMS 협상·읽기·쓰기와 손상된 연결의 재사용 차단을 검증합니다.</summary>
    internal static class MmsVirtual
    {
        private static MmsServerSession session;
        private static bool serverDone;
        private static string serverError;

        internal static void Run()
        {
            for (int overload = 0; overload < 4; overload++)
            {
                Recovery(overload);
                RoundTrip(overload);
            }
        }

        private static void Recovery(int kind)
        {
            int delivered = 0;
            var good = new byte[] { 3, 0, 0, 7, 2, 0xf0, 0x80 };
            var decoder = new TpktDecoder(64, (packet, length) =>
            {
                Program.Check(length == good.Length && packet[5] == 0xf0, "MMS recovered TPKT content");
                delivered++;
            });
            byte[] bad = Program.Copy(good, 4);
            if (kind == 0) bad[0] = 2;
            else if (kind == 1) bad[1] = 1;
            else if (kind == 2) bad[3] = 6;
            if (kind == 3)
            {
                decoder.Feed(bad, 0, bad.Length);
                Program.Rejects(() => decoder.CompleteInput(), typeof(MmsProtocolException),
                    "MMS partial TPKT EOF rejected");
            }
            else
                Program.Rejects(() => decoder.Feed(bad, 0, bad.Length), typeof(MmsProtocolException),
                    "MMS malformed TPKT rejected " + kind);
            Program.Check(decoder.Faulted && delivered == 0, "MMS invalid TPKT never delivered " + kind);
            Program.Rejects(() => decoder.Feed(good, 0, good.Length), typeof(InvalidOperationException),
                "MMS faulted decoder cannot reuse failed connection " + kind);
            // Faulted 디코더는 기존 연결에서 계속 쓰지 않으며 명시적 Reset 후 새 입력만 허용합니다.
            decoder.Reset();
            Program.Check(!decoder.Faulted && decoder.BufferedBytes == 0, "MMS decoder reset " + kind);
            for (int i = 0; i < good.Length; i++) decoder.Feed(good, i, 1);
            decoder.CompleteInput();
            Program.Check(delivered == 1, "MMS normal TPKT after reset " + kind);

            var input = new Pipe();
            var output = new Pipe();
            byte[] invalid = kind == 3 ? good : bad;
            input.Write(invalid, 0, invalid.Length);
            var failed = new MmsServerSession(new Channel(input, output),
                new[] { new MmsServerVariable("Plant", "Count", -5) });
            try
            {
                Program.Rejects(() => failed.Associate(), typeof(MmsProtocolException),
                    "MMS server rejects invalid TPKT/COTP association " + kind);
                Program.Check(failed.Faulted && !failed.Associated, "MMS failed association state " + kind);
                Program.Rejects(() => failed.Associate(), typeof(InvalidOperationException),
                    "MMS failed session cannot reassociate " + kind);
            }
            finally
            {
                failed.Close();
                input.Close();
                output.Close();
            }
        }

        private static void RoundTrip(int overload)
        {
            serverDone = false;
            serverError = null;
            var toServer = new Pipe();
            var toClient = new Pipe();
            var count = new MmsServerVariable("Plant", "Count", -5);
            var level = new MmsServerVariable("Plant", "Level", -2.5f);
            var alarm = new MmsServerVariable("Plant", "Alarm", true);
            count.EnableWrite();
            level.EnableWrite();
            alarm.EnableWrite();
            var channel = new Channel(toServer, toClient);
            var variables = new[] { count, level, alarm };
            // 생성자 오버로드별 PDU/페이지 제한이 실제 세션 협상과 탐색에 전달되는지 확인합니다.
            switch (overload)
            {
                case 0: session = new MmsServerSession(channel, variables); break;
                case 1: session = new MmsServerSession(channel, variables, 1024); break;
                case 2: session = new MmsServerSession(channel, variables, 2048, 3000); break;
                default: session = new MmsServerSession(channel, variables, 3072, 3000, 1); break;
            }
            var server = new Thread(Serve);
            server.Start();

            var client = new MmsClient(new Channel(toClient, toServer), 4096, 5000);
            client.Associate();
            Program.Check(client.Associated && session.Associated, "MMS association over threaded pipe");
            Program.Check(session.NegotiatedPduSize == (overload == 0 ? 4096 : overload * 1024),
                "MMS constructor forwards PDU size");
            Program.Check(client.Read("Plant", "Count").Integer == -5, "MMS negative integer read");
            Program.Check(client.Read("Plant", "Level").FloatingPoint == -2.5, "MMS negative float read");
            Program.Check(client.Read("Plant", "Alarm").Boolean, "MMS boolean read");
            client.Write("Plant", "Count", int.MinValue);
            client.Write("Plant", "Level", 1.5e-3f);
            client.Write("Plant", "Alarm", false);
            Program.Check(client.Read("Plant", "Count").Integer == int.MinValue, "MMS Int32.MinValue write round trip");
            Program.Check(client.Read("Plant", "Level").FloatingPoint == 1.5e-3f, "MMS float write round trip");
            Program.Check(!client.Read("Plant", "Alarm").Boolean, "MMS boolean write round trip");
            MmsNameListPage names = client.GetNameList("Plant");
            if (overload == 3)
            {
                Program.Check(names.Names.Length == 1 && names.MoreFollows, "MMS constructor forwards page size");
                names = client.GetNameList("Plant", names.Names[0]);
                Program.Check(names.Names.Length == 1 && names.MoreFollows, "MMS name list second page");
                names = client.GetNameList("Plant", names.Names[0]);
                Program.Check(names.Names.Length == 1 && !names.MoreFollows, "MMS name list final page");
            }
            else
                Program.Check(names.Names.Length == 3 && !names.MoreFollows, "MMS default name list page size");
            int error;
            MmsValue missing;
            Program.Check(!client.TryRead("Plant", "Missing", out missing, out error) && error > 0, "MMS missing variable access error");
            client.Release();
            Program.Check(!client.Associated, "MMS release");
            toServer.Close();
            for (int i = 0; i < 500 && !serverDone; i++)
                Thread.Sleep(10);
            Program.Check(serverDone && serverError == null, "MMS server session ended cleanly " + serverError);
        }

        private static void Serve()
        {
            try
            {
                session.Associate();
                while (session.ProcessRequest())
                {
                }
            }
            catch (Exception ex)
            {
                serverError = ex.GetType().Name + " " + ex.Message;
            }
            finally
            {
                session.Close();
                serverDone = true;
            }
        }

        private sealed class Pipe
        {
            private readonly object _gate = new object();
            private readonly byte[] _buffer = new byte[65536];
            private int _head, _count;
            private bool _closed;

            internal void Write(byte[] data, int offset, int count)
            {
                lock (_gate)
                {
                    if (_count + count > _buffer.Length)
                        throw new Exception("Pipe overflow.");
                    for (int i = 0; i < count; i++)
                        _buffer[(_head + _count + i) % _buffer.Length] = data[offset + i];
                    _count += count;
                }
            }

            internal int Read(byte[] data, int offset, int count, int timeout)
            {
                long deadline = Environment.TickCount64 + timeout;
                while (true)
                {
                    lock (_gate)
                    {
                        if (_count > 0)
                        {
                            int n = count < _count ? count : _count;
                            for (int i = 0; i < n; i++)
                                data[offset + i] = _buffer[(_head + i) % _buffer.Length];
                            _head = (_head + n) % _buffer.Length;
                            _count -= n;
                            return n;
                        }
                        if (_closed)
                            return 0;
                    }
                    if (Environment.TickCount64 >= deadline)
                        throw new Exception("Pipe timeout.");
                    Thread.Sleep(1);
                }
            }

            internal void Close()
            {
                lock (_gate)
                    _closed = true;
            }
        }

        private sealed class Channel : IMmsChannel
        {
            private readonly Pipe _input, _output;

            internal Channel(Pipe input, Pipe output)
            {
                _input = input;
                _output = output;
            }

            public long Milliseconds { get { return Environment.TickCount64; } }

            public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
            {
                return _input.Read(buffer, offset, count, timeoutMilliseconds);
            }

            public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
            {
                _output.Write(buffer, offset, count);
                return count;
            }
        }
    }
}
