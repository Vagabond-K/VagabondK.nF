using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using VagabondK.nF.DNP3.TCP;
using VagabondK.nF.DNP3.Serial;
using static VagabondK.nF.DNP3.Interop.Program;

namespace VagabondK.nF.DNP3.Interop
{
    // 독립 네이티브 피어를 별도 프로세스로 실행하여 Master/Outstation 양쪽의 값·품질·시각과 제어를 비교합니다.
    // Serial 경로도 가상 연결로 검사하므로 실제 COM 배선 검증과 구분합니다.
    internal static class NativeDnpCases
    {
        private sealed class Tool : IDisposable
        {
            private readonly Process _process;
            private readonly BlockingCollection<string> _lines = new BlockingCollection<string>();
            private readonly Thread _reader;
            private readonly System.Threading.Tasks.Task<string> _errors;

            internal Tool(string executable, int port, bool full = false)
            {
                _process = Process.Start(new ProcessStartInfo(executable, port + (full ? " full" : ""))
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                _errors = _process.StandardError.ReadToEndAsync();
                _reader = new Thread(() =>
                {
                    string line;
                    while ((line = _process.StandardOutput.ReadLine()) != null)
                        _lines.Add(line);
                    _lines.CompleteAdding();
                });
                _reader.Start();
                Until("READY");
            }

            internal void Send(string command)
            {
                _process.StandardInput.WriteLine(command);
                _process.StandardInput.Flush();
            }

            internal List<string> Until(string prefix)
            {
                var result = new List<string>();
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 15000)
                {
                    if (!_lines.TryTake(out var line, 500))
                        continue;
                    result.Add(line);
                    if (line.StartsWith("LOG ", StringComparison.Ordinal))
                        Console.WriteLine("OpenDNP3 native: " + line);
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                        return result;
                }
                throw new TimeoutException("External DNP3 tool: expected " + prefix + "; output=" + string.Join("; ", result));
            }

            internal List<string> Scan(int group, int variation, string range = "")
            {
                Send(group + " " + variation + range);
                var lines = Until("DONE ");
                Check(lines[lines.Count - 1] == "DONE SUCCESS", "Independent scan task failed: " + string.Join("; ", lines));
                return lines;
            }

            public void Dispose()
            {
                bool clean = true;
                if (!_process.HasExited)
                {
                    Send("quit");
                    clean = _process.WaitForExit(5000);
                }
                if (!clean)
                {
                    _process.Kill(true);
                    _process.WaitForExit();
                }
                Check(_reader.Join(5000), "External DNP3 stdout reader did not stop.");
                int exit = _process.ExitCode;
                string error = _errors.GetAwaiter().GetResult();
                _process.Dispose();
                _lines.Dispose();
                Check(clean && exit == 0, "External DNP3 tool failed: " + error);
            }
        }

        private sealed class Handler : IDnp3ControlHandler, IDnp3TimeHandler, IDnp3MasterHandler
        {
            internal int Operations, Batches;
            internal long Utc;
            internal Dnp3Measurement[] Values;
            internal readonly List<Dnp3Measurement> UnsolicitedValues = new List<Dnp3Measurement>();
            internal bool Unsolicited;
            private readonly List<Dnp3Measurement> _solicited = new List<Dnp3Measurement>(), _unsolicited = new List<Dnp3Measurement>();

            public Dnp3CommandStatus Validate(Dnp3Command command)
            {
                return Dnp3CommandStatus.Success;
            }

            public Dnp3CommandStatus Operate(Dnp3Command command, bool selected)
            {
                Interlocked.Increment(ref Operations);
                return Dnp3CommandStatus.Success;
            }

            public void SetUtcMilliseconds(long value)
            {
                Interlocked.Exchange(ref Utc, value);
            }

            public void OnMeasurements(Dnp3Measurement[] measurements, Dnp3ResponseInfo response)
            {
                var all = response.Unsolicited ? _unsolicited : _solicited;
                if (response.First)
                    all.Clear();
                all.AddRange(measurements);
                Values = all.ToArray();
                Unsolicited = response.Unsolicited;
                if (measurements.Length > 0)
                    Batches++;
                if (response.Unsolicited)
                    UnsolicitedValues.AddRange(measurements);
            }
        }

        // 여기서 TCP는 가상 바이트 전달 수단일 뿐이며 실제 UART를 검증하는 시험은 아닙니다.
        private sealed class SocketBytes : IDnp3ByteChannel
        {
            private readonly Socket _socket;
            private bool _noise = true, _dropped;
            internal int ConfirmedFrames;

            internal SocketBytes(Socket socket)
            {
                _socket = socket;
            }

            public long Milliseconds
            {
                get
                {
                    return Environment.TickCount64;
                }
            }

            public int Read(byte[] buffer, int offset, int count, int timeout)
            {
                if (_noise)
                {
                    _noise = false;
                    buffer[offset] = 0xff;
                    return 1;
                }
                if (!_socket.Poll(timeout * 1000, SelectMode.SelectRead))
                    return 0;
                int read = _socket.Receive(buffer, offset, Math.Min(count, 7), SocketFlags.None);
                if (read == 0)
                    throw new IOException("Virtual serial peer closed.");
                return read;
            }

            public void Write(byte[] buffer, int offset, int count, int timeout)
            {
                if ((buffer[offset + 3] & 0x4f) == 0x43)
                {
                    ConfirmedFrames++;
                    if (!_dropped)
                    {
                        _dropped = true;
                        return;
                    }
                }
                _socket.SendTimeout = timeout;
                for (int sent = 0; sent < count;)
                {
                    int n = _socket.Send(buffer, offset + sent, count - sent, SocketFlags.None);
                    if (n == 0)
                        throw new IOException("Virtual serial send stalled.");
                    sent += n;
                }
            }
        }

        private sealed class Session
        {
            private readonly Dnp3TcpSession _tcp;
            private readonly Dnp3SerialSession _serial;
            private readonly SocketBytes _bytes;

            internal Session(Socket socket, IDnp3Endpoint endpoint, bool serial)
            {
                if (serial)
                {
                    _bytes = new SocketBytes(socket);
                    _serial = new Dnp3SerialSession(_bytes, endpoint, ackTimeoutMilliseconds: 500);
                }
                else
                    _tcp = new Dnp3TcpSession(socket, endpoint);
            }

            internal void Send(byte[] bytes)
            {
                if (_serial != null)
                    _serial.Send(bytes);
                else
                    _tcp.Send(bytes);
            }

            internal void RunOnce()
            {
                if (_serial != null)
                    _serial.RunOnce(1);
                else
                    _tcp.RunOnce();
            }

            internal void Verify()
            {
                if (_serial != null)
                    Check(_bytes.ConfirmedFrames > 1 && _serial.Retransmissions == 1 && _serial.Decoder.DiscardedBytes >= 1,
                        "Independent confirmed link/reset/FCB retry and noise recovery.");
            }
        }

        private sealed class Server : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Thread _worker;
            private volatile bool _stop;
            private Socket _connection;
            private Exception _error;

            internal int Port { get; private set; }

            internal Server(Dnp3Station station, bool serial = false)
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _worker = new Thread(() =>
                {
                    try
                    {
                        while (!_stop)
                        {
                            using Socket socket = _listener.AcceptSocket();
                            _connection = socket;
                            station.ResetConnection();
                            var session = new Session(socket, station, serial);
                            try
                            {
                                while (!_stop)
                                    session.RunOnce();
                            }
                            catch (IOException) { }
                            catch (SocketException) when (_stop || socket.Poll(0, SelectMode.SelectRead)) { }
                            finally
                            {
                                _connection = null;
                                session.Verify();
                            }
                        }
                    }
                    catch (SocketException) when (_stop) { }
                    catch (ObjectDisposedException) when (_stop) { }
                    catch (Exception error)
                    {
                        _error = error;
                    }
                });
                _worker.Start();
            }

            public void Dispose()
            {
                _stop = true;
                _listener.Stop();
                _connection?.Dispose();
                Check(_worker.Join(5000), "Native station server thread did not stop.");
                if (_error != null)
                    throw new InvalidOperationException("Native station server failed.", _error);
            }
        }

        private static Dnp3Value Value(int type, int index, int generation)
        {
            return type == 4 || type == 6 ? Dnp3Value.Analog(index + generation + 0.25, 1, 1700000000000 + generation) :
                Dnp3Value.Bits(type == 0 || type == 5 ? (ulong)((index + generation) & 1) :
                    type == 1 ? (ulong)(1 + ((index + generation) & 1)) : (ulong)(1000 + index + generation),
                    1, 1700000000000 + generation);
        }
        private static readonly string[] Names = { "B", "D", "C", "F", "A", "O", "Q" };

        private static List<string[]> Values(List<string> lines)
        {
            var result = new List<string[]>();
            foreach (string line in lines)
            {
                var parts = line.Split(' ');
                if (parts.Length == 5 && Array.IndexOf(Names, parts[0]) >= 0)
                    result.Add(parts);
            }
            return result;
        }

        private static void Compare(List<string> lines, int expected, bool timed, int generation, int type = -1)
        {
            var values = Values(lines);
            Check(values.Count == expected, "Independent all-type count: expected " + expected + ", actual " + values.Count +
                "; " + string.Join("; ", lines));
            var seen = new HashSet<string>();
            foreach (var parts in values)
            {
                int t = Array.IndexOf(Names, parts[0]), index = int.Parse(parts[1], CultureInfo.InvariantCulture);
                Check((type == -1 || t == type) && seen.Add(parts[0] + parts[1]), "Independent type / unique index.");
                var value = Value(t, index, generation);
                double number = t == 4 || t == 6 ? value.AsAnalog() : value.Raw;
                byte flags = (byte)(1 | (t == 0 || t == 5 ? (int)value.Raw << 7 : t == 1 ? (int)value.Raw << 6 : 0));
                Check(double.Parse(parts[2], CultureInfo.InvariantCulture) == number &&
                    byte.Parse(parts[3], CultureInfo.InvariantCulture) == flags &&
                    long.Parse(parts[4], CultureInfo.InvariantCulture) == (timed ? value.Timestamp : 0),
                    "Independent value / quality / time mismatch: " + string.Join(" ", parts));
            }
        }

        internal static void Run(string masterExecutable)
        {
            StationAgainstExternalMaster(masterExecutable);
            MasterAgainstExternalStation(Path.Combine(Path.GetDirectoryName(masterExecutable), "VagabondKDnp3Outstation.exe"));
            StationAgainstExternalMaster(masterExecutable, true);
            MasterAgainstExternalStation(Path.Combine(Path.GetDirectoryName(masterExecutable), "VagabondKDnp3Outstation.exe"), true);
            Console.WriteLine("DNP3 SERIAL BYTE-CARRIER PASS: both native roles against independent OpenDNP3, confirmed outbound links/reset/FCB/retry, dropped frame, noise and 7-byte reads; TCP carrier only, no UART/RS485 electrical validation. OpenDNP3 sends unconfirmed return links.");
        }

        private static void StationAgainstExternalMaster(string executable, bool serial = false)
        {
            var points = new Dnp3PointDefinition[490];
            int p = 0;
            for (int type = 0; type < 7; type++)
                for (int i = 0; i < 70; i++)
                    points[p++] = new Dnp3PointDefinition((Dnp3PointType)type, (ushort)i, Value(type, i, 0), (byte)(type % 3 + 1));
            var db = new Dnp3Database(points, 1024);
            var handler = new Handler();
            var station = new Dnp3Station(db, 1, 2, handler, handler, 1024);
            using var server = new Server(station, serial);
            using var master = new Tool(executable, server.Port, true);
            Compare(master.Scan(60, 1), 490, false, 0);
            Check(SpinWait.SpinUntil(() => Interlocked.Read(ref handler.Utc) > 1700000000000, 7000), "External master time synchronization.");
            int variations = 0, generation = 0;
            for (int type = 0; type < 7; type++)
                for (int v = 1; v <= 8; v++)
                    if (Dnp3Objects.Supported((Dnp3PointType)type, false, v))
                    {
                        // 정수 표현은 규정된 방식에 따라 설정된 아날로그 값의 소수부를 버립니다.
                        var lines = master.Scan(Dnp3Objects.Group((Dnp3PointType)type, false), v);
                        var values = Values(lines);
                        Check(values.Count == 70, "Independent static variation count.");
                        foreach (var parts in values)
                        {
                            int index = int.Parse(parts[1], CultureInfo.InvariantCulture);
                            double expected = type == 4 || type == 6 ? index + 0.25 : Value(type, index, 0).Raw;
                            if ((type == 4 && v <= 2) || (type == 6 && v <= 2))
                                expected = (int)expected;
                            Check(double.Parse(parts[2], CultureInfo.InvariantCulture) == expected, "Independent static variation value.");
                        }
                        variations++;
                    }
            for (int type = 0; type < 7; type++)
                for (int v = 1; v <= 8; v++)
                    if (Dnp3Objects.Supported((Dnp3PointType)type, true, v))
                    {
                        generation++;
                        for (int i = 0; i < 70; i++)
                            Check(db.Update((Dnp3PointType)type, (ushort)i, Value(type, i, generation)), "Event generation.");
                        var lines = master.Scan(Dnp3Objects.Group((Dnp3PointType)type, true), v);
                        var values = Values(lines);
                        Check(values.Count == 70, "Independent event variation count.");
                        foreach (var parts in values)
                        {
                            int index = int.Parse(parts[1], CultureInfo.InvariantCulture);
                            double expected = type == 4 || type == 6 ? index + generation + 0.25 : Value(type, index, generation).Raw;
                            if ((type == 4 || type == 6) && v <= 4)
                                expected = (int)expected;
                            bool timed = type == 4 || type == 6 ? v == 3 || v == 4 || v == 7 || v == 8 :
                                type == 2 || type == 3 ? v >= 5 : v == 2;
                            Check(double.Parse(parts[2], CultureInfo.InvariantCulture) == expected &&
                                long.Parse(parts[4], CultureInfo.InvariantCulture) == (timed ? 1700000000000 + generation : 0), "Independent event value/time.");
                        }
                        Check(SpinWait.SpinUntil(() => db.EventCount == 0, 3000), "Independent event confirm drained database.");
                        variations++;
                    }
            master.Send("enable");
            master.Until("DONE SUCCESS");
            db.Update(Dnp3PointType.Counter, 0, Value(2, 0, ++generation));
            var unsolicited = master.Until("C 0 ");
            Compare(unsolicited, 1, true, generation, 2);
            Check(SpinWait.SpinUntil(() => db.EventCount == 0, 3000), "Independent unsolicited confirm.");
            master.Send("disable");
            master.Until("DONE SUCCESS");
            foreach (string command in new[] { "relay 1 3 1", "relay 2 4 0", "ao 1 3 -123 1", "ao 2 4 -123 0", "ao 3 5 1.25 1", "ao 4 6 1.25 0" })
            {
                master.Send(command);
                var result = master.Until("CMD ");
                Check(result.Contains("CMD SUCCESS") && result.Contains("STATUS SUCCESS"), "Independent CROB/analog command: " + string.Join("; ", result));
            }
            Check(handler.Operations == 6, "Independent operations execute exactly once.");
            if (PatchedIeee)
            {
                int samples = 0;
                foreach (var sample in NumericSamples())
                {
                    db.Update(Dnp3PointType.Analog, 0, Dnp3Value.Analog(sample.Value));
                    var decoded = Values(master.Scan(30, sample.Width == 32 ? 5 : 6, " 0 0"));
                    Check(decoded.Count == 1 && BitConverter.DoubleToInt64Bits(double.Parse(decoded[0][2], CultureInfo.InvariantCulture)) ==
                        BitConverter.DoubleToInt64Bits(sample.Value), "Patched external master boundary decode: " + sample.Width + "/" + sample.Bits.ToString("X16"));
                    samples++;
                }
                Console.WriteLine("PATCHED NUMERIC " + (serial ? "SERIAL BYTE-CARRIER" : "TCP") + ": VagabondK.nF Station -> patched external Master; " + samples + " float32/64 boundary/random exact values including signed zeros/subnormals.");
            }
            Console.WriteLine("DNP3 NATIVE STATION (" + (serial ? "virtual serial" : "TCP") + "): " + (PatchedIeee ? "test-only IEEE-patched" : "independent unmodified") + " OpenDNP3 master, 490 points, " + variations +
                " static/event variations, fragment confirms, time sync, unsolicited, CROB and four analog control formats.");
        }

        private static void Complete(Session session, Dnp3Master master)
        {
            long deadline = Environment.TickCount64 + 10000;
            while (master.Busy)
            {
                Check(Environment.TickCount64 < deadline, "Native master independent task deadline.");
                session.RunOnce();
            }
            Check(master.Result == Dnp3MasterResult.Success, "Native master independent task result: " + master.Result + "/" + master.Iin2);
        }

        private static void ValidateMasterValues(Handler handler, int generation, bool events)
        {
            Check(handler.Values.Length == 490, "Native master all seven types / 70 points count: " + handler.Values.Length);
            var seen = new HashSet<string>();
            foreach (var m in handler.Values)
            {
                int t = (int)m.Type, i = m.Index;
                double expected = t == 4 ? i + generation + 0.25 : t == 6 ? i + generation + 0.5 :
                    t == 0 || t == 5 ? (i + generation) % 2 : t == 1 ? (i + generation) % 2 != 0 ? 2 : 1 : 1000 + i + generation;
                Check(seen.Add(t + ":" + i) && m.IsEvent == events && m.Value.Flags == 1 &&
                    (t == 4 || t == 6 ? m.Value.AsAnalog() : m.Value.Raw) == expected, "Native master decoded external values.");
                if (events && t != 3)
                    Check(m.Value.Timestamp == 1700000000000 + generation, "Native master external absolute event time.");
            }
        }

        private static void MasterAgainstExternalStation(string executable, bool serial = false)
        {
            Check(File.Exists(executable), "Build independent outstation tool first.");
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            using var station = new Tool(executable, port);
            station.Send("update 0");
            station.Until("UPDATED 0");
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(IPAddress.Loopback, port);
            var handler = new Handler();
            var master = new Dnp3Master(2, 1, handler);
            var session = new Session(socket, master, serial);
            session.Send(master.ClearRestart(Environment.TickCount64));
            Complete(session, master);
            session.Send(master.SetUnsolicited(14, false, Environment.TickCount64));
            Complete(session, master);
            session.Send(master.ReadClasses(1, Environment.TickCount64));
            Complete(session, master);
            ValidateMasterValues(handler, 0, false);
            station.Send("update 1");
            station.Until("UPDATED 1");
            session.Send(master.ReadClasses(14, Environment.TickCount64));
            Complete(session, master);
            ValidateMasterValues(handler, 1, true);
            int generation = 1, variations = 0;
            for (int t = 0; t < 7; t++)
                for (int e = 0; e < 2; e++)
                    for (int v = 1; v <= 8; v++)
                    {
                        if (!Dnp3Objects.Supported((Dnp3PointType)t, e == 1, v))
                            continue;
                        if (e == 1)
                        {
                            station.Send("update " + ++generation);
                            station.Until("UPDATED ");
                        }
                        session.Send(master.ReadGroup(Dnp3Objects.Group((Dnp3PointType)t, e == 1), (byte)v, Environment.TickCount64));
                        Complete(session, master);
                        Check(handler.Values.Length == 70, "Native master external variation count.");
                        foreach (var m in handler.Values)
                        {
                            int i = m.Index;
                            double expected = t == 4 ? i + generation + 0.25 : t == 6 ? i + generation + 0.5 :
                                t == 0 || t == 5 ? (i + generation) % 2 : t == 1 ? (i + generation) % 2 != 0 ? 2 : 1 : 1000 + i + generation;
                            if ((t == 4 || t == 6) && v <= (e == 1 ? 4 : 2))
                                expected = (int)expected;
                            Check((int)m.Type == t && m.IsEvent == (e == 1) && m.Value.Flags == 1 &&
                                (t == 4 || t == 6 ? m.Value.AsAnalog() : m.Value.Raw) == expected, "Native master independent variation value.");
                        }
                        if (e == 1)
                        {
                            session.Send(master.ReadClasses(14, Environment.TickCount64));
                            Complete(session, master);
                        }
                        variations++;
                    }
            session.Send(master.SynchronizeTime(1700000001234, Environment.TickCount64));
            Complete(session, master);
            Check(station.Until("TIME ").Exists(x => x.StartsWith("TIME 170000000", StringComparison.Ordinal)), "Independent outstation accepted time sync.");
            foreach (var command in new[] { Dnp3Command.Relay(1, 3), Dnp3Command.Analog(2, -123, 1), Dnp3Command.Analog(3, -123, 2),
                Dnp3Command.Analog(4, 1.25, 3), Dnp3Command.Analog(5, 1.25, 4) })
            {
                session.Send(master.Command(command, true, Environment.TickCount64));
                Complete(session, master);
                station.Until("OPERATE ");
            }
            session.Send(master.SetUnsolicited(14, true, Environment.TickCount64));
            Complete(session, master);
            station.Send("update " + ++generation);
            station.Until("UPDATED ");
            long deadline = Environment.TickCount64 + 10000;
            while (handler.UnsolicitedValues.Count < 490)
            {
                Check(Environment.TickCount64 < deadline, "Native master unsolicited deadline.");
                session.RunOnce();
            }
            Check(handler.Unsolicited, "Native master unsolicited callback.");
            handler.Values = handler.UnsolicitedValues.ToArray();
            ValidateMasterValues(handler, generation, true);
            if (PatchedIeee)
            {
                session.Send(master.SetUnsolicited(14, false, Environment.TickCount64));
                Complete(session, master);
                int samples = 0;
                foreach (var sample in NumericSamples())
                {
                    station.Send("number " + sample.Width + " " + sample.Bits.ToString("X16", CultureInfo.InvariantCulture));
                    station.Until("NUMBER UPDATED");
                    session.Send(master.ReadGroup(30, sample.Width == 32 ? (byte)5 : (byte)6, Environment.TickCount64, 0, 0));
                    Complete(session, master);
                    Check(handler.Values.Length == 1 && handler.Values[0].Type == Dnp3PointType.Analog &&
                        handler.Values[0].Value.Raw == unchecked((ulong)BitConverter.DoubleToInt64Bits(sample.Value)),
                        "Native master patched external encode boundary: " + sample.Width + "/" + sample.Bits.ToString("X16"));
                    samples++;
                }
                Console.WriteLine("PATCHED NUMERIC " + (serial ? "SERIAL BYTE-CARRIER" : "TCP") + ": patched external Station -> VagabondK.nF Master; " + samples + " float32/64 boundary/random exact values including signed zeros/subnormals.");
            }
            session.Verify();
            Console.WriteLine("DNP3 NATIVE MASTER (" + (serial ? "virtual serial" : "TCP") + "): " + (PatchedIeee ? "test-only IEEE-patched" : "independent unmodified") + " OpenDNP3 outstation, 490 points, " + variations +
                " static/event variations, fragmented reads, unsolicited, time sync and select/operate controls.");
        }

        private struct NumericSample
        {
            internal int Width;
            internal ulong Bits;
            internal double Value;
        }

        private static IEnumerable<NumericSample> NumericSamples()
        {
            uint random = 20261023;
            for (int i = 0; i < 142; i++)
            {
                uint bits = Fixtures.NextFiniteBits(ref random, i);
                yield return new NumericSample { Width = 32, Bits = bits, Value = BitConverter.UInt32BitsToSingle(bits) };
                bits = (bits & 0x807fffff) | 1;
                yield return new NumericSample { Width = 32, Bits = bits, Value = BitConverter.UInt32BitsToSingle(bits) };
            }
            foreach (ulong bits in new[] { 0UL, 0x8000000000000000UL, 1UL, 0x8000000000000001UL,
                0xfffffffffffffUL, 0x800fffffffffffffUL, 0x10000000000000UL, 0x8010000000000000UL,
                0x7fefffffffffffffUL, 0xffefffffffffffffUL, 0x3ff0000000000000UL, 0xbff0000000000000UL })
                yield return new NumericSample { Width = 64, Bits = bits, Value = BitConverter.Int64BitsToDouble(unchecked((long)bits)) };
            ulong state = 20261023;
            for (int i = 0; i < 128; i++)
            {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                ulong bits = state;
                if ((bits & 0x7ff0000000000000UL) == 0x7ff0000000000000UL)
                    bits ^= 0x10000000000000UL;
                yield return new NumericSample { Width = 64, Bits = bits, Value = BitConverter.Int64BitsToDouble(unchecked((long)bits)) };
                bits = (bits & 0x800fffffffffffffUL) | 1;
                yield return new NumericSample { Width = 64, Bits = bits, Value = BitConverter.Int64BitsToDouble(unchecked((long)bits)) };
            }
        }
    }
}
