using System;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.Serial;
using VagabondK.nF.DNP3.MasterMapping.Sample;

// 같은 매핑 검증을 Serial 채널에서 수행합니다. Link ACK 재시도 소진과 Application 응답 시간 초과를 구분합니다.
internal sealed partial class MasterValidation
{
    private IDnp3ByteChannel serial;
    private Dnp3SerialSession serialSession;
    internal long SerialRetries, SerialFrameTimeouts;
    internal IDnp3ByteChannel SerialChannel { get { return serial; } }

    internal void InitializeSerial(IDnp3ByteChannel channel)
    {
        Check(data == null, "serial initialization once");
        serial = channel;
        data = new ReceivedData { Observer = this };
        master = new Dnp3Master(2, 1, new ReceivedData.Dnp3MasterBinding(data, new object()),
            responseTimeout: 10000, retries: 0, maximumMeasurements: 64, transportTimeout: 10000);
        serialSession = NewSerial(true);
    }
    private Dnp3SerialSession NewSerial(bool confirmed)
    {
        return new Dnp3SerialSession(serial, master, confirmed, ackTimeoutMilliseconds: 1000, retries: 2,
            frameTimeoutMilliseconds: 1000, writeTimeoutMilliseconds: 3000);
    }
    private void SerialRequest(byte[] frames)
    {
        serialSession.Send(frames);
        long deadline = serial.Milliseconds + 30000;
        while (master.Busy || !serialSession.TransmitIdle)
        {
            if (serial.Milliseconds > deadline) throw new TimeoutException("Serial mapping request deadline.");
            serialSession.RunOnce(10);
        }
        Check(master.Result == Dnp3MasterResult.Success, "Serial request success");
    }
    private void SerialHealthy()
    {
        Check(!serialSession.Faulted && serialSession.Decoder.CrcErrors == 0, "Serial healthy, zero CRC errors");
        SerialRetries = serialSession.Retransmissions;
        SerialFrameTimeouts = serialSession.Decoder.FrameTimeouts;
    }
    internal void RunSerial(int rounds)
    {
        Check(rounds >= 2 && rounds <= 10 && rounds % 2 == 0, "Serial round count");
        int initialEvents = data.Events, initialUnsolicited = Unsolicited;
        serialSession = NewSerial(true);
        serialSession.Reset();
        SerialRequest(master.SetUnsolicited(14, false, serial.Milliseconds));
        Begin(Generation);
        SerialRequest(master.ReadClasses(1, serial.Milliseconds));
        Count(8);
        CheckModel(data, false);
        for (int round = 0; round < rounds; round++)
        {
            int first = Generation + 1;
            for (int n = 0; n < 2; n++)
            {
                SerialRequest(master.Command(Dnp3Command.Relay(65500, 3), n != 0, serial.Milliseconds));
                Generation++;
            }
            Begin(Generation, first, Generation);
            SerialRequest(master.ReadClasses(2, serial.Milliseconds));
            Count(16);
            CheckModel(data, true);
            Begin(Generation);
            SerialRequest(master.ReadClasses(2, serial.Milliseconds));
            Count(0);
            Begin(Generation, Generation + 1, Generation + 1);
            int before = Events;
            SerialRequest(master.SetUnsolicited(2, true, serial.Milliseconds));
            SerialRequest(master.Command(Dnp3Command.Relay(65500, 3), false, serial.Milliseconds));
            Generation++;
            long deadline = serial.Milliseconds + 30000;
            while (Events - before < 56 || !serialSession.TransmitIdle)
            {
                if (serial.Milliseconds > deadline) throw new TimeoutException("Serial unsolicited deadline.");
                serialSession.RunOnce(10);
            }
            Count(8);
            CheckModel(data, true);
            SerialRequest(master.SetUnsolicited(14, false, serial.Milliseconds));
        }
        Check(data.Events - initialEvents == rounds * 168 && Unsolicited - initialUnsolicited == rounds * 56,
            "Serial all duplicate events preserved");
        Check(data.Unmapped > 0, "Serial unmapped records delivered");
        SerialHealthy();
    }
    internal void PrepareSerialFault()
    {
        SerialRequest(master.SetUnsolicited(14, false, serial.Milliseconds));
        Begin(Generation);
        SerialRequest(master.ReadClasses(1, serial.Milliseconds));
        Count(8);
        CheckModel(data, false);
        SerialHealthy();
    }
    internal int AwaitSerialTimeout(bool confirmed)
    {
        System.Threading.Thread.Sleep(500);
        serialSession = NewSerial(confirmed);
        serialSession.Reset();
        serialSession.Send(master.ReadClasses(1, serial.Milliseconds));
        long deadline = serial.Milliseconds + 20000;
        bool timedOut = false;
        try
        {
            while (serial.Milliseconds < deadline) serialSession.RunOnce(10);
        }
        catch (TimeoutException)
        {
            timedOut = true;
            Check(serialSession.Faulted, "Serial timeout faults session");
        }
        Check(timedOut, "Serial offline timeout observed");
        if (confirmed)
            Check(serialSession.Retransmissions == 2 && master.Result != Dnp3MasterResult.TimedOut,
                "Serial Link ACK retries exhausted before application timeout");
        else
            Check(serialSession.Retransmissions == 0 && master.Result == Dnp3MasterResult.TimedOut,
                "Serial application response timeout without Link ACK policy");
        SerialHealthyAfterFault();
        serialSession.Reset();
        Check(!serialSession.Faulted && !master.Busy && serialSession.TransmitIdle, "Serial Reset restores clean session");
        return confirmed ? 4 : 5;
    }
    private void SerialHealthyAfterFault()
    {
        Check(serialSession.Decoder.CrcErrors == 0, "Serial fault path zero CRC errors");
        SerialRetries = serialSession.Retransmissions;
        SerialFrameTimeouts = serialSession.Decoder.FrameTimeouts;
    }
    // 세 바이트만 도착한 프레임은 수신 기한이 한 번 만료된 뒤 폐기되어야 합니다. 모델에는 전달되지 않고 세션은 정상 상태여야 합니다.
    internal void AwaitSerialPartial()
    {
        serialSession = NewSerial(true);
        serialSession.Reset();
        int before = Received;
        long deadline = serial.Milliseconds + 6000;
        while (serial.Milliseconds < deadline) serialSession.RunOnce(10);
        Check(serialSession.Decoder.FrameTimeouts == 1 && serialSession.Decoder.DiscardedBytes == 3,
            "Actual UART three-byte prefix expired exactly once");
        Check(Received == before && !serialSession.Faulted && serialSession.TransmitIdle,
            "Partial frame never delivered or mistaken for session failure");
        SerialHealthy();
        serialSession.Reset();
    }
}
