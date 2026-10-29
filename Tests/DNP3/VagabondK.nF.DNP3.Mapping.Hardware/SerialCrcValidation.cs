using System;
using System.Net.Sockets;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.Serial;
using VagabondK.nF.DNP3.TCP;

// 헤더/데이터 CRC 손상 뒤의 정상 프레임만 Endpoint에 도달하는지 Serial과 TCP에서 비교하는 검증기입니다.
// 제어 콜백은 메모리의 실행 횟수만 증가시킵니다. 폐기·CRC·시간 초과 진단 수치와 중복 실행 방지를 함께 확인합니다.
public sealed class SerialCrcValidation : IDnp3MasterHandler, IDnp3ControlHandler, IDnp3Endpoint
{
    private readonly IDnp3Endpoint endpoint;
    private int delivered, measurements, operations;
    public long CrcErrors, DiscardedBytes;
    public long FrameTimeouts;

    public SerialCrcValidation(bool master)
    {
        endpoint = master ? (IDnp3Endpoint)new Dnp3Master(2, 1, this, responseTimeout: 10000, retries: 0) :
            new Dnp3Station(Database(), 1, 2, this, confirmationTimeout: 30000);
    }

    private static Dnp3Database Database()
    {
        return new Dnp3Database(new[] {
            new Dnp3PointDefinition(Dnp3PointType.Counter, 0, Dnp3Value.Bits(123))
        }, 8);
    }

    private static void Require(bool valid)
    {
        if (!valid) throw new InvalidOperationException("Serial CRC rejection/recovery contract.");
    }

    public ushort LocalAddress { get { return endpoint.LocalAddress; } }
    public ushort RemoteAddress { get { return endpoint.RemoteAddress; } }
    public bool IsMaster { get { return endpoint.IsMaster; } }
    public void ResetConnection() { endpoint.ResetConnection(); }
    public byte[] Poll(long now) { return endpoint.Poll(now); }
    public byte[] Process(byte[] frame, long now)
    {
        delivered++;
        return endpoint.Process(frame, now);
    }
    public void OnMeasurements(Dnp3Measurement[] values, Dnp3ResponseInfo info)
    {
        Require(!info.Unsolicited && values.Length == 1 && values[0].Type == Dnp3PointType.Counter &&
            values[0].Index == 0 && values[0].Value.Raw == 123 && values[0].Value.Flags == 1);
        measurements += values.Length;
    }
    public Dnp3CommandStatus Validate(Dnp3Command command)
    {
        Require(command.Index == 0 && command.ControlCode == 3);
        return Dnp3CommandStatus.Success;
    }
    public Dnp3CommandStatus Operate(Dnp3Command command, bool selected)
    {
        Require(!selected && command.Index == 0 && command.ControlCode == 3);
        operations++;
        return Dnp3CommandStatus.Success;
    }

    public void Run(IDnp3ByteChannel channel, int kind)
    {
        Require(kind >= 0 && kind <= 2);
        var session = new Dnp3SerialSession(channel, this, false, frameTimeoutMilliseconds: 1000);
        session.Reset();
        if (IsMaster) session.Send(((Dnp3Master)endpoint).ReadClasses(1, channel.Milliseconds));
        long deadline = channel.Milliseconds + 4000;
        while (channel.Milliseconds < deadline) session.RunOnce(10);
        int errors = kind == 2 ? 6 : 4;
        CrcErrors = session.Decoder.CrcErrors;
        DiscardedBytes = session.Decoder.DiscardedBytes;
        Require(CrcErrors == errors && DiscardedBytes == CorruptLength(IsMaster) * errors &&
            session.Decoder.FrameTimeouts == 0 && !session.Faulted && session.TransmitIdle);
        Require(IsMaster ? delivered == 1 && measurements == 1 && operations == 0 &&
            ((Dnp3Master)endpoint).Result == Dnp3MasterResult.Success :
            delivered == 2 && measurements == 0 && operations == 1);
    }

    public void RunTcp(Socket socket, int kind)
    {
        Require(kind >= 0 && kind <= 3);
        ResetConnection();
        var session = new Dnp3TcpSession(socket, this);
        if (IsMaster) session.Send(((Dnp3Master)endpoint).ReadClasses(1, Environment.TickCount64));
        long deadline = Environment.TickCount64 + 4000;
        while (Environment.TickCount64 < deadline) session.RunOnce(10);
        int errors = kind == 3 ? 0 : kind == 2 ? 6 : 4;
        CrcErrors = session.Decoder.CrcErrors;
        DiscardedBytes = session.Decoder.DiscardedBytes;
        FrameTimeouts = session.Decoder.FrameTimeouts;
        Require(CrcErrors == errors && DiscardedBytes == (kind == 3 ? 3 : CorruptLength(IsMaster) * errors) &&
            FrameTimeouts == (kind == 3 ? 1 : 0) && !session.Faulted);
        Require(IsMaster ? delivered == 1 && measurements == 1 && operations == 0 &&
            ((Dnp3Master)endpoint).Result == Dnp3MasterResult.Success :
            delivered == 2 && measurements == 0 && operations == 1);
    }

    private static byte[] Frame(bool receivingMaster)
    {
        var handler = new SerialCrcValidation(true);
        var master = new Dnp3Master(2, 1, handler);
        if (!receivingMaster) return master.Command(Dnp3Command.Relay(0, 3), false, 0);
        var station = new Dnp3Station(Database(), 1, 2);
        return station.Process(master.ReadClasses(1, 0), 0);
    }
    private static int CorruptLength(bool receivingMaster) { return Frame(receivingMaster).Length; }

    public static byte[] Input(bool receivingMaster, int kind)
    {
        Require(kind >= 0 && kind <= 3);
        byte[] frame = Frame(receivingMaster);
        int errors = kind == 3 ? 0 : kind == 2 ? 6 : 4;
        byte[] read = receivingMaster ? new byte[0] :
            new Dnp3Channel(2, 1, true).Encode(new byte[] { 0xc1, 1, 60, 1, 6 });
        var result = new byte[frame.Length * (errors + 1) + read.Length];
        for (int i = 0; i <= errors; i++)
        {
            Array.Copy(frame, 0, result, i * frame.Length, frame.Length);
            if (i < errors)
            {
                bool header = kind == 0 || kind == 2 && i % 2 == 0;
                result[i * frame.Length + (header ? 8 : frame.Length - 1)] ^= 1;
            }
        }
        Array.Copy(read, 0, result, frame.Length * (errors + 1), read.Length);
        return result;
    }

    public static void CheckOutput(bool receivingMaster, byte[] output)
    {
        var decoder = new Dnp3StreamDecoder();
        var channel = new Dnp3Channel(receivingMaster ? (ushort)1 : (ushort)2,
            receivingMaster ? (ushort)2 : (ushort)1, !receivingMaster);
        int reads = 0, controls = 0;
        for (int i = 0; i < output.Length; i++)
        {
            byte[] frame = decoder.Push(output[i], 0);
            if (frame == null) continue;
            byte[] reply;
            byte[] app = channel.Receive(frame, 0, out reply);
            if (app == null) continue;
            if (receivingMaster)
            {
                Require(app.Length == 5 && app[1] == 1 && app[2] == 60 && app[3] == 1 && app[4] == 6);
                reads++;
            }
            else if (app[1] == 0x81)
            {
                Require((app[2] & 0x7f) == 0 && app[3] == 0);
                if ((app[0] & 15) == 0)
                {
                    // CROB 응답은 명령을 그대로 담고 성공 상태를 덧붙입니다.
                    Require(app.Length == 22 && app[4] == 12 && app[5] == 1 && app[21] == 0);
                    controls++;
                }
                else
                {
                    Require((app[0] & 15) == 1 && app.Length == 16 && app[4] == 20 && app[5] == 1 &&
                        app[6] == 1 && app[7] == 0 && app[8] == 0 && app[9] == 0 && app[10] == 0 &&
                        app[11] == 1 && app[12] == 123 && app[13] == 0 && app[14] == 0 && app[15] == 0);
                    reads++;
                }
            }
            else Require(app[1] == 0x82);
        }
        Require(decoder.CrcErrors == 0 && decoder.DiscardedBytes == 0 &&
            reads == 1 && controls == (receivingMaster ? 0 : 1));
    }
}
