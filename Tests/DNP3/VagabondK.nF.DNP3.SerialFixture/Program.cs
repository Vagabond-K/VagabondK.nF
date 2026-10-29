using System;
using System.IO.Ports;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.Serial;
using VagabondK.nF.DNP3.SerialHost;

// PC의 COM6 9600 8E1을 사용하는 보드 검증 피어입니다. 역할 인수에 따라 Master/Outstation 또는 독립 도구를 실행합니다.
// OutstationFaults는 ACK 유실과 CRC 손상을 주입합니다. RTS/DTR 및 배선을 확인하고 운영 버스와 분리합니다.
internal static class Program
{
    private sealed class Channel : IDnp3ByteChannel
    {
        private readonly SerialPort _port;
        internal long BytesRead, BytesWritten;
        internal SerialValidation Validation;
        internal bool InjectFaults, DroppedAck, CorruptedFrame;
        internal readonly System.Collections.Generic.Queue<string> Trace = new System.Collections.Generic.Queue<string>();

        internal Channel(SerialPort port)
        {
            _port = port;
        }

        public long Milliseconds
        {
            get
            {
                return Environment.TickCount64;
            }
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            _port.ReadTimeout = timeoutMilliseconds;
            try
            {
                int read = _port.Read(buffer, offset, count);
                BytesRead += read;
                Record("RX " + BitConverter.ToString(buffer, offset, read));
                return read;
            }
            catch (TimeoutException)
            {
                return 0;
            }
        }

        public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            if (InjectFaults && Validation.Operations > 0 && !DroppedAck &&
                count == 10 && (buffer[offset + 3] & 0x7f) == 0)
            {
                DroppedAck = true;
                Record("INJECT dropped control link ACK");
                return;
            }
            if (InjectFaults && Validation.Operations >= 3 && !CorruptedFrame &&
                count > 10 && (buffer[offset + 3] & 0x5f) == 0x53)
            {
                var damaged = new byte[count];
                Array.Copy(buffer, offset, damaged, 0, count);
                damaged[count - 1] ^= 1;
                buffer = damaged;
                offset = 0;
                CorruptedFrame = true;
                Record("INJECT corrupted response CRC");
            }
            _port.WriteTimeout = timeoutMilliseconds;
            _port.Write(buffer, offset, count);
            BytesWritten += count;
            Record("TX " + BitConverter.ToString(buffer, offset, count));
        }

        private void Record(string line)
        {
            Trace.Enqueue(line);
            if (Trace.Count > 30)
                Trace.Dequeue();
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[1] == "IndependentMaster" &&
            (args[0] == "COM6" || System.Net.IPAddress.TryParse(args[0], out _)))
            return IndependentMaster.Run(args[0]);
        if (args.Length == 2 && args[1] == "IndependentOutstation" &&
            (args[0] == "COM6" || System.Net.IPAddress.TryParse(args[0], out _)))
            return IndependentOutstation.Run(args[0]);
        if (args.Length != 2 || args[0] != "COM6" ||
            (args[1] != "Master" && args[1] != "Outstation" && args[1] != "OutstationFaults" && args[1] != "Probe"))
        {
            Console.Error.WriteLine("Usage: SerialFixture COM6 Master|Outstation|OutstationFaults|Probe; " +
                "or SerialFixture COM6|IPv4 IndependentMaster|IndependentOutstation; isolated test devices only.");
            return 2;
        }
        Channel channel = null;
        try
        {
            using var port = new SerialPort(args[0], 9600, Parity.Even, 8, StopBits.One);
            port.Handshake = Handshake.None;
            port.DtrEnable = true;
            port.RtsEnable = true;
            port.ParityReplace = 0;
            port.Open();
            channel = new Channel(port);
            var validation = new SerialValidation();
            channel.Validation = validation;
            channel.InjectFaults = args[1] == "OutstationFaults";
            bool masterRole = args[1] == "Master" || args[1] == "Probe";
            IDnp3Endpoint endpoint = masterRole ? new Dnp3Master(2, 1, validation, responseTimeout: 30000, transportTimeout: 30000) :
                new Dnp3Station(validation.Database, 1, 2, validation, validation,
                    fragmentSize: 256, confirmationTimeout: 30000, transportTimeout: 30000);
            var session = new Dnp3SerialSession(channel, endpoint, ackTimeoutMilliseconds: 3000, frameTimeoutMilliseconds: 3000);
            long started = channel.Milliseconds;
            Console.WriteLine("READY: COM6 " + args[1] + " 9600 8E1 RTS/DTR=true; no physical output handlers.");
            if (args[1] == "Probe")
            {
                var master = (Dnp3Master)endpoint;
                long startup = channel.Milliseconds + 3000;
                while (channel.Milliseconds < startup)
                    session.RunOnce(10);
                foreach (byte variation in new byte[] { 1, 2, 3, 4 })
                    foreach (double value in new[] { 123.0, -123.0, 0.0 })
                    {
                        session.Send(master.Command(Dnp3Command.Analog(0, value, variation), false, channel.Milliseconds));
                        long deadline = channel.Milliseconds + 60000;
                        while (master.Busy || !session.TransmitIdle)
                        {
                            if (channel.Milliseconds >= deadline)
                                throw new TimeoutException("Probe deadline.");
                            session.RunOnce(10);
                        }
                        Console.WriteLine("PROBE v=" + variation + " value=" + value + " result=" + master.Result + " IIN2=" + master.Iin2);
                    }
                return 0;
            }
            if (masterRole)
                validation.RunMaster(channel, (Dnp3Master)endpoint, session, 100);
            else
            {
                long deadline = started + 600000;
                while (validation.Database.Read(Dnp3PointType.AnalogOutputStatus, 0).AsAnalog() != 24589 || !session.TransmitIdle)
                {
                    if (channel.Milliseconds >= deadline)
                        throw new TimeoutException("MCU master fixture deadline.");
                    session.RunOnce(10);
                }
                // 완료 값 재조회와 남은 모든 확인 응답의 전송이 끝나도록 기다립니다.
                long settle = channel.Milliseconds + 3000;
                while (channel.Milliseconds < settle)
                    session.RunOnce(10);
                if (validation.Operations != SerialValidation.ExpectedOperations || validation.Utc < 1700000001234 || validation.Utc > 1700000031234)
                    throw new InvalidOperationException("MCU validation command/time-sync totals mismatch: " + validation.Operations + "/" + validation.Utc);
                if (channel.InjectFaults && (!channel.DroppedAck || !channel.CorruptedFrame || session.Retransmissions == 0))
                    throw new InvalidOperationException("Physical ACK/CRC fault injection was not exercised.");
            }
            if (session.Faulted || channel.BytesRead == 0 || channel.BytesWritten == 0)
                throw new InvalidOperationException("No complete physical exchange.");
            Console.WriteLine("PHYSICAL DNP3 SERIAL PASS: PC " + args[1] + "; 45 variations; 7 types x 8 points; " +
                "100 repeated reads; control/readback/time/unsolicited; checks=" + validation.Checks +
                "; RX=" + channel.BytesRead + "; TX=" + channel.BytesWritten + "; retries=" + session.Retransmissions +
                "; CRCerrors=" + session.Decoder.CrcErrors + "; frameTimeouts=" + session.Decoder.FrameTimeouts +
                "; droppedControlAck=" + channel.DroppedAck + "; corruptedResponse=" + channel.CorruptedFrame +
                "; operations=" + validation.Operations + "; elapsedMs=" + (channel.Milliseconds - started) + ".");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("PHYSICAL DNP3 SERIAL FAIL: " + error);
            if (channel != null)
                foreach (string line in channel.Trace)
                    Console.Error.WriteLine(line);
            return 1;
        }
    }
}
