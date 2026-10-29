using System;
using System.IO;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.MMS;

/// <summary>Try API의 정상 접근 거절과 시간 초과 결과를 구분하고 EOF·프로토콜 오류를 시간 초과로 숨기지 않는지 검증합니다.</summary>
internal static class TimeoutResultTests
{
    private static void Check(bool valid)
    { if (!valid) throw new Exception("Timeout result assertion."); }
    private static void Reject(Action action, Type type)
    {
        try { action(); }
        catch (Exception error) { Check(error.GetType() == type); return; }
        throw new Exception("Expected " + type.Name);
    }

    internal static void Run()
    {
        for (int scenario = 0; scenario < 7; scenario++)
        {
            var channel = new Channel();
            var client = new Iec61850Client(channel, timeoutMilliseconds: 100);
            client.Associate();
            channel.OnlyTry = true;
            if (scenario == 0) channel.ReadTimeout = true;
            if (scenario == 1) { channel.Script.Input.AddRange(new byte[] { 3, 0 }); channel.TimeoutAfter = 1; }
            if (scenario == 2) { channel.Script.Input.AddRange(new byte[] { 3, 0, 0, 20, 2, 0xf0 }); channel.TimeoutAfter = 3; }
            if (scenario == 3) channel.WriteTimeout = true;
            if (scenario == 4) channel.Script.Advance = 60;
            if (scenario == 5)
            {
                for (int i = 0; i < 10; i++) channel.Script.Input.AddRange(new byte[] { 3, 0, 0, 8, 2, 0xf0, 0, 1 });
                channel.Script.Advance = 1; channel.ReadAdvance = 10;
            }
            if (scenario == 6) channel.ReadAdvance = 100;
            MmsValue value;
            int error;
            bool timedOut;
            Check(!client.TryRead("d", "i", out value, out error, out timedOut) &&
                timedOut && value == null && error == -1 && client.Faulted && !client.Associated);
            int calls = channel.Writes;
            Reject(() => client.TryRead("d", "i", out value, out error, out timedOut), typeof(InvalidOperationException));
            Check(channel.Writes == calls);
        }
        {
            var channel = new Channel();
            var client = new Iec61850Client(channel);
            client.Associate(); channel.OnlyTry = true;
            channel.Script.Segments(ReadResponse(1, MmsEncoding.Int(0x85, 42)), 5);
            MmsValue value; int error; bool timedOut;
            Check(client.TryRead("d", "i", out value, out error, out timedOut) &&
                value.Integer == 42 && error == -1 && !timedOut);
            channel.Script.Segments(ReadResponse(2, MmsEncoding.Int(0x80, 10)), 5);
            Check(!client.TryRead("d", "i", out value, out error, out timedOut) &&
                value == null && error == 10 && !timedOut && client.Associated && !client.Faulted);
            channel.OnlyTry = false;
            channel.Script.Segments(ReadResponse(3, MmsEncoding.Int(0x80, 10)), 5);
            Reject(() => client.Read("d", "i"), typeof(MmsAccessException));
            Check(client.Associated && !client.Faulted);
        }
        for (int scenario = 0; scenario < 3; scenario++)
        {
            var channel = new Channel();
            var client = new Iec61850Client(channel);
            client.Associate(); channel.OnlyTry = true;
            if (scenario == 1) channel.Script.Segments(ReadResponse(999, MmsEncoding.Int(0x85, 42)), 50);
            if (scenario == 2) channel.Script.Segments(ReadResponse(1, new byte[] { 0x85, 0 }), 50);
            MmsValue value; int error; bool timedOut = true;
            Reject(() => client.TryRead("d", "i", out value, out error, out timedOut),
                scenario == 0 ? typeof(IOException) : typeof(MmsProtocolException));
            Check(!timedOut && client.Faulted && !client.Associated);
        }
        {
            var script = MmsClientTests.Ready();
            var client = new Iec61850Client(script); client.Associate();
            MmsValue value; int error; bool timedOut;
            Reject(() => client.TryRead("d", "i", out value, out error, out timedOut), typeof(NotSupportedException));
            Check(!client.Faulted && client.Associated);
        }
        {
            var channel = new Channel();
            var client = new Iec61850Client(channel, timeoutMilliseconds: 100);
            client.Associate(); channel.OnlyTry = true;
            int reports = 0;
            client.InformationReportReceived = report => { reports++; channel.Advance(100); };
            byte[] pdu = MmsEncoding.Tlv(0xa3, MmsEncoding.Tlv(0xa0,
                MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0x80, new byte[] { 82 })),
                MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x85, 42))));
            channel.Script.Segments(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, pdu)), 50);
            MmsValue value; int error; bool timedOut;
            Check(!client.TryRead("d", "i", out value, out error, out timedOut) &&
                timedOut && reports == 1 && client.Faulted && !client.Associated);
        }
        Control();
        Console.WriteLine("TIMEOUT RESULT: partial I/O, whole deadline, access errors, faults, normal control/no retry PASS.");
    }

    private static void Control()
    {
        for (int scenario = 0; scenario < 4; scenario++)
        {
            var channel = new Channel();
            var client = new Iec61850Client(channel, timeoutMilliseconds: 100);
            client.Associate();
            channel.Script.Segments(ReadResponse(1, MmsEncoding.Int(0x85, 1)), 50);
            var control = new MmsControlClient(client, "d", "LN$CO$Direct", new byte[] { 70 });
            channel.OnlyTry = true;
            if (scenario == 0) channel.ReadTimeout = true;
            if (scenario == 1) channel.WriteTimeout = true;
            if (scenario >= 2)
                channel.Script.Segments(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3,
                    MmsEncoding.Tlv(0xa1, MmsEncoding.Int(2, 2), MmsEncoding.Tlv(0xa5,
                        scenario == 2 ? MmsEncoding.Tlv(0x81) : MmsEncoding.Int(0x80, 3))))), 5);
            int error; bool timedOut;
            bool accepted = control.TryOperate(true, 0, out error, out timedOut);
            Check(accepted == (scenario == 2) && timedOut == (scenario < 2) &&
                error == (scenario == 3 ? 3 : -1));
            Check(client.Faulted == timedOut && client.Associated != timedOut);
            if (timedOut)
            {
                int writes = channel.Writes;
                Reject(() => control.TryOperate(false, 0, out error, out timedOut), typeof(InvalidOperationException));
                Check(channel.Writes == writes);
            }
        }
    }
    private static byte[] ReadResponse(int invoke, byte[] result)
    {
        return MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3,
            MmsEncoding.Tlv(0xa1, MmsEncoding.Int(2, invoke), MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, result)))));
    }
    private sealed class Channel : IMmsTimeoutChannel
    {
        internal readonly MmsClientTests.ScriptedChannel Script = MmsClientTests.Ready();
        internal bool OnlyTry, ReadTimeout, WriteTimeout;
        internal int TimeoutAfter = -1, Reads, Writes, ReadAdvance;
        private long elapsed;
        internal void Advance(int milliseconds) { elapsed += milliseconds; }
        public long Milliseconds { get { return Script.Milliseconds + elapsed; } }
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            Check(!OnlyTry);
            return Script.Read(buffer, offset, count, timeoutMilliseconds);
        }
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            Check(!OnlyTry);
            return Script.Write(buffer, offset, count, timeoutMilliseconds);
        }
        public bool TryRead(byte[] buffer, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            if (ReadTimeout || (TimeoutAfter >= 0 && Reads++ >= TimeoutAfter))
            { elapsed += timeoutMilliseconds; transferred = 0; return false; }
            // 읽기 준비가 되었더라도 전체 기한에 도달해 완료됐다면 예외 없이 실패를 반환해야 합니다.
            if (ReadAdvance >= timeoutMilliseconds)
            { elapsed += ReadAdvance; transferred = 1; buffer[offset] = 3; return true; }
            elapsed += ReadAdvance;
            transferred = Script.Read(buffer, offset, count, timeoutMilliseconds);
            return true;
        }
        public bool TryWrite(byte[] buffer, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            Writes++;
            if (WriteTimeout) { elapsed += timeoutMilliseconds; transferred = 0; return false; }
            transferred = Script.Write(buffer, offset, count, timeoutMilliseconds);
            return true;
        }
    }
}
