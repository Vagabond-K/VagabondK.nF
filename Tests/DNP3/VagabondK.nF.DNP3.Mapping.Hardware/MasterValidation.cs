using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.MasterMapping.Sample;
using VagabondK.nF.DNP3.TCP;

// 독립 피어의 세대별 값을 기준으로 원본 Fragment와 생성 모델의 값·품질·시각을 별도로 비교하는 검증기입니다.
internal sealed partial class MasterValidation : IDnp3MasterHandler
{
    internal int Checks, Received, Events, Unsolicited, Fragments, Generation;
    internal Dnp3Measurement[] Captured;
    private ReceivedData data;
    private Dnp3Master master;
    private Socket faultSocket;
    private Dnp3TcpSession faultSession;
    private int expectedStatic;
    private int minimumEvent, maximumEvent;
    private int eventGeneration;
    private readonly int[] seen = new int[7];
    internal void Check(bool valid, string reason)
    {
        if (!valid) throw new InvalidOperationException("Master mapping: " + reason);
        Checks++;
    }
    public void OnMeasurements(Dnp3Measurement[] measurements, Dnp3ResponseInfo response)
    {
        Fragments++;
        if (response.Unsolicited) Unsolicited += measurements.Length;
        Check((response.Iin2 & 7) == 0, "remote IIN errors");
        for (int i = 0; i < measurements.Length; i++)
        {
            var item = measurements[i];
            int type = (int)item.Type, index = item.Index;
            Check(type >= 0 && type < 7 && index < 8, "independent record identity");
            int generation = item.IsEvent ? (int)(item.Value.Timestamp - 1700000000000L) : expectedStatic;
            if (item.IsEvent)
            {
                Check(generation >= minimumEvent && generation <= maximumEvent, "event generation and timestamp");
                Check(generation >= eventGeneration, "event order within each type");
                eventGeneration = generation;
                Events++;
            }
            else Check(item.Value.Timestamp == 0, "timeless Static is not fabricated UTC");
            int ordinal = seen[type]++;
            Check(index == ordinal % 8, "exact index and event order");
            if (index == 7) eventGeneration = minimumEvent;
            double analog = (index % 2 == 0 ? -1 : 1) * (index + generation + 0.25);
            ulong raw = type == 0 || type == 5 ? (ulong)((index + generation) & 1) :
                type == 1 ? (ulong)(1 + ((index + generation) & 1)) : (ulong)(1000 + index + generation);
            Check(type == 4 || type == 6 ? item.Value.AsAnalog() == analog : item.Value.Raw == raw, "independent exact value");
            int mask = type == 0 || type == 5 ? 127 : type == 1 ? 63 : 255;
            Check((item.Value.Flags & mask) == 1, "independent quality");
            Received++;
        }
        if (measurements.Length > 0) Captured = measurements;
    }
    private void Begin(int staticGeneration, int firstEvent = 0, int lastEvent = 0)
    {
        expectedStatic = staticGeneration;
        minimumEvent = eventGeneration = firstEvent;
        maximumEvent = lastEvent;
        for (int i = 0; i < seen.Length; i++) seen[i] = 0;
    }
    private void Count(int perType)
    {
        for (int i = 0; i < seen.Length; i++) Check(seen[i] == perType, "per-type count");
    }
    internal static void Request(Dnp3Master master, Dnp3TcpSession session, byte[] frames)
    {
        session.Send(frames);
        long deadline = Environment.TickCount64 + 30000;
        while (master.Busy)
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Master fixture request.");
            session.RunOnce(10);
        }
        if (master.Result != Dnp3MasterResult.Success) throw new InvalidOperationException("Master fixture result: " + master.Result);
    }
    internal void Run(string address, int port, int rounds)
    {
        Check(rounds >= 2 && rounds <= 10 && rounds % 2 == 0, "round count");
        if (data == null)
        {
            data = new ReceivedData { Observer = this };
            var binding = new ReceivedData.Dnp3MasterBinding(data, new object());
            master = new Dnp3Master(2, 1, binding, responseTimeout: 10000, maximumMeasurements: 64);
        }
        int initialEvents = data.Events, initialUnsolicited = Unsolicited;
        for (int connection = 0; connection < 2; connection++)
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Connect(new IPEndPoint(IPAddress.Parse(address), port));
                master.ResetConnection();
                var session = new Dnp3TcpSession(socket, master, 3000, 3000);
                Request(master, session, master.SetUnsolicited(14, false, Environment.TickCount64));
                Begin(Generation);
                Request(master, session, master.ReadClasses(1, Environment.TickCount64));
                Count(8);
                CheckModel(data, false);
                for (int round = 0; round < rounds / 2; round++)
                {
                    int first = Generation + 1;
                    for (int n = 0; n < 2; n++)
                    {
                        Request(master, session, master.Command(Dnp3Command.Relay(65500, 3), n != 0, Environment.TickCount64));
                        Generation++;
                    }
                    Begin(Generation, first, Generation);
                    Request(master, session, master.ReadClasses(2, Environment.TickCount64));
                    Count(16);
                    CheckModel(data, true);
                    Begin(Generation);
                    Request(master, session, master.ReadClasses(2, Environment.TickCount64));
                    Count(0);
                    Begin(Generation, Generation + 1, Generation + 1);
                    int before = Events;
                    Request(master, session, master.SetUnsolicited(2, true, Environment.TickCount64));
                    Request(master, session, master.Command(Dnp3Command.Relay(65500, 3), false, Environment.TickCount64));
                    Generation++;
                    long deadline = Environment.TickCount64 + 30000;
                    while (Events - before < 56)
                    {
                        if (Environment.TickCount64 > deadline) throw new TimeoutException("Unsolicited mapping.");
                        session.RunOnce(10);
                    }
                    Count(8);
                    CheckModel(data, true);
                    Request(master, session, master.SetUnsolicited(14, false, Environment.TickCount64));
                }
                Check(session.Decoder.CrcErrors == 0 && !session.Faulted, "healthy TCP decoder");
            }
        }
        Check(data.Events - initialEvents == rounds * 168 && Unsolicited - initialUnsolicited == rounds * 56, "all events preserved across reconnect");
        Check(data.Unmapped > 0, "unmapped records explicitly delivered");
    }
    internal void PeerRestarted()
    {
        Generation = 0;
    }
    internal void OpenFault(string address, int port)
    {
        Check(master != null && faultSocket == null, "fault probe lifecycle");
        faultSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            faultSocket.Connect(new IPEndPoint(IPAddress.Parse(address), port));
            master.ResetConnection();
            faultSession = new Dnp3TcpSession(faultSocket, master, 3000, 3000);
            Request(master, faultSession, master.SetUnsolicited(14, false, Environment.TickCount64));
            Begin(Generation);
            Request(master, faultSession, master.ReadClasses(1, Environment.TickCount64));
            Count(8);
            CheckModel(data, false);
        }
        catch
        {
            faultSocket.Close();
            faultSocket = null;
            faultSession = null;
            throw;
        }
    }
    internal int AwaitFault()
    {
        Check(faultSession != null, "armed fault session");
        try
        {
            // 이 명시적 요청을 보내기 전에 호스트가 장애를 주입할 시간을 줍니다.
            System.Threading.Thread.Sleep(500);
            try
            {
                faultSession.Send(master.ReadClasses(1, Environment.TickCount64));
                long deadline = Environment.TickCount64 + 15000;
                while (Environment.TickCount64 < deadline) faultSession.RunOnce(10);
            }
            catch (IOException) { Check(faultSession.Faulted, "closed peer faults session"); return 1; }
            catch (SocketException) { Check(faultSession.Faulted, "reset peer faults session"); return 2; }
            catch (TimeoutException) { Check(faultSession.Faulted, "timeout faults session"); return 3; }
            throw new InvalidOperationException("Injected TCP fault was not detected.");
        }
        finally
        {
            faultSocket.Close();
            faultSocket = null;
            faultSession = null;
        }
    }
    private void CheckModel(ReceivedData data, bool timed)
    {
        Check(data.Contact == ((Generation & 1) != 0) && data.Enabled == data.Contact, "mapped Binary values");
        Check(data.State == 1 + (Generation & 1) && data.Total == 1000 + Generation && data.Frozen == data.Total, "mapped counters/DBB");
        Check(data.Temperature == -Generation - 0.25 && data.Setpoint == data.Temperature, "mapped Analog values");
        long timestamp = timed ? 1700000000000L + Generation : 0;
        Check(data.ContactTime == timestamp && data.StateTime == timestamp && data.TotalTime == timestamp &&
            data.FrozenTime == timestamp && data.TemperatureTime == timestamp && data.EnabledTime == timestamp && data.SetpointTime == timestamp,
            "mapped individual timestamps");
        Check((data.ContactFlags & 127) == 1 && (data.StateFlags & 63) == 1 && data.TotalFlags == 1 &&
            data.FrozenFlags == 1 && data.TemperatureFlags == 1 && (data.EnabledFlags & 127) == 1 && data.SetpointFlags == 1,
            "mapped individual quality");
    }
}
