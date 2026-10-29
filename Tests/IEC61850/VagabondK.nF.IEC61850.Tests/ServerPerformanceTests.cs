using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.IO;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Server;

/// <summary>미리 준비한 요청과 워밍업으로 서버 응답·제어·모델 갱신의 할당량과 소요 시간을 측정하는 선택적 시험입니다.</summary>
internal static class ServerPerformanceTests
{
    internal static void Run()
    {
        var model = new MmsServerVariable[] {
            new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Status$stVal", true),
            new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Count$stVal", -123456),
            new MmsServerVariable("NFTESTLD0", "GGIO1$MX$Analog$mag", 12.5f)
        };
        var channel = new Replay();
        var server = new Iec61850ServerSession(channel, model);
        server.Associate();
        for (int i = 0; i < 1000; i++) server.ProcessRead();
        const int count = 10000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
            if (!server.ProcessRead()) throw new Exception("Performance replay EOF");
        long ticks = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        for (int i = 0; i < 1000; i++) Update(model, i);
        before = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++) Update(model, i);
        long updateTicks = Stopwatch.GetTimestamp() - started;
        long updateAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated > count * 1000L || updateAllocated != 0)
            throw new Exception("Server allocation regression: reads=" + allocated + ", setters=" + updateAllocated);
        Console.WriteLine("SERVER PERF reads=" + count + " allocated=" + allocated +
            " bytes/read=" + ((double)allocated / count) +
            " us/read=" + (ticks * 1000000.0 / Stopwatch.Frequency / count) +
            " setterTriples=" + count + " setterAllocated=" + updateAllocated +
            " us/setterTriple=" + (updateTicks * 1000000.0 / Stopwatch.Frequency / count));
        Controls();
        Enhanced(false);
        Enhanced(true);
    }
    private static void Enhanced(bool sbo)
    {
        byte[] command = MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1), MmsEncoding.Tlv(0xa5,
            MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0,
                MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier("D"), MmsEncoding.Identifier("L$CO$O$Oper"))))),
            MmsEncoding.Tlv(0xa0, ControlTests.Command(true).Buffer)));
        var service = MmsEncoding.Single(command, 0, command.Length, 0xa0).Children();
        service.Read();
        var write = service.Read(0xa5);
        byte[] encodedService = MmsEncoding.Tlv(0xa5, write.Copy());
        byte[] selection = SboEnhancedTests.Write("SBOw", 1);
        var selectionFields = MmsEncoding.Single(selection, 0, selection.Length, 0xa0).Children();
        selectionFields.Read();
        byte[] selectionService = MmsEncoding.Tlv(0xa5, selectionFields.Read(0xa5).Copy());
        foreach (bool success in new[] { true, false })
        {
            MmsControlRequest pending = null;
            var control = new MmsServerControl("D", "L$CO$O", sbo ? MmsControlModel.SboEnhanced : MmsControlModel.DirectEnhanced);
            control.SetHandlers(r => -1, r => { pending = r; return -1; }); control.Enabled = true;
            var server = new Iec61850ServerSession(new Replay(sbo ? new[] { selectionService, encodedService } :
                new[] { encodedService }), new MmsServerVariable[0], controls: new[] { control });
            server.Associate();
            for (int i = 0; i < 1000; i++)
            {
                if (sbo) server.ProcessRequest();
                server.ProcessRequest(); control.Complete(pending, success, success ? 0 : 9);
                if (!server.ProcessControlCompletion()) throw new Exception("Missing enhanced warmup completion");
            }
            const int count = 10000;
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            {
                if (sbo && !server.ProcessRequest()) throw new Exception("SBOw replay EOF");
                if (!server.ProcessRequest()) throw new Exception("Enhanced replay EOF");
                control.Complete(pending, success, success ? 0 : 9);
                if (!server.ProcessControlCompletion() || server.HasPendingControl || control.HasPendingOperation)
                    throw new Exception("Enhanced completion retained");
            }
            long ticks = Stopwatch.GetTimestamp() - start;
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocation > count * (sbo ? success ? 10700L : 11700L : success ? 8000L : 9000L) || control.Value != success)
                throw new Exception("Enhanced allocation/state regression");
            Console.WriteLine((sbo ? "SBO ENHANCED" : "ENHANCED") + " PERF success=" + success + " operations=" + count +
                " bytes/operation=" + ((double)allocation / count) + " us/operation=" +
                (ticks * 1000000.0 / Stopwatch.Frequency / count));
            server.Close();
        }
    }
    private static void Controls()
    {
        const string domain = "NFTESTLD0", item = "GGIO1$CO$Direct";
        byte[] name = MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0,
            MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier(domain), MmsEncoding.Identifier(item + "$Oper"))));
        byte[] service = MmsEncoding.Tlv(0xa5, MmsEncoding.Tlv(0xa0, name),
            MmsEncoding.Tlv(0xa0, ControlTests.Command(true).Buffer));
        long memoryAllocation = 0, handlerAllocation = 0;
        int[] budgets = { 2800, 3000, 6800, 3000 };
        for (int mode = 0; mode < 4; mode++)
        {
            var control = new MmsServerControl(domain, item, MmsControlModel.DirectNormal);
            if (mode != 0) control.SetHandlers(request => -1, request => -1);
            control.Enabled = mode != 2;
            control.ReportControlErrors = mode >= 2;
            var server = new Iec61850ServerSession(new Replay(new[] { service }), new MmsServerVariable[0], controls: new[] { control });
            server.Associate();
            for (int i = 0; i < 1000; i++) server.ProcessRequest();
            const int count = 10000;
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
                if (!server.ProcessRequest()) throw new Exception("Control performance replay EOF");
            long ticks = Stopwatch.GetTimestamp() - start;
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
            if (mode == 0) memoryAllocation = allocation;
            if (mode == 1) handlerAllocation = allocation;
            if (allocation > count * (long)budgets[mode] || (mode == 1 && allocation - memoryAllocation > count * 256L) ||
                (mode == 3 && allocation != handlerAllocation))
                throw new Exception("Control allocation regression: mode=" + mode + " allocated=" + allocation);
            if (control.Value != (mode != 2) || server.Faulted) throw new Exception("Control performance state mismatch");
            Console.WriteLine("CONTROL PERF mode=" + (mode == 0 ? "memory" : mode == 1 ? "handler" :
                mode == 2 ? "diagnostic-failure" : "diagnostic-success") +
                " requests=" + count + " bytes/request=" + ((double)allocation / count) +
                " us/request=" + (ticks * 1000000.0 / Stopwatch.Frequency / count));
            server.Close();
        }
    }
    private static void Update(MmsServerVariable[] model, int i)
    {
        model[0].SetBoolean((i & 1) != 0);
        model[1].SetInteger(i - 5000);
        model[2].SetFloatingPoint(i + 0.5f);
    }
    private sealed class Replay : IMmsChannel
    {
        private readonly byte[] association;
        private readonly byte[][] reads;
        private int position, next;
        private bool associated;
        public long Milliseconds { get { return 0; } }
        internal Replay(byte[][] services = null)
        {
            association = MmsEncoding.Join(Convert.FromHexString("0300001611E00000000100C0010AC2020001C1020001"),
                Frame(MmsEncoding.Association(4096)));
            string[] items = { "GGIO1$ST$Status$stVal", "GGIO1$ST$Count$stVal", "GGIO1$MX$Analog$mag", "missing" };
            reads = new byte[services == null ? items.Length : services.Length][];
            for (int i = 0; i < reads.Length; i++)
                reads[i] = Frame(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3,
                    services == null ? MmsEncoding.ReadRequest(i + 1, MmsEncoding.Identifier("NFTESTLD0"), MmsEncoding.Identifier(items[i])) :
                    MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, i + 1), services[i]))));
        }
        private static byte[] Frame(byte[] payload)
        {
            var bytes = new byte[payload.Length + 7];
            CotpData.Write(bytes, 0, payload, 0, payload.Length, true);
            return bytes;
        }
        public int Read(byte[] buffer, int offset, int count, int timeout)
        {
            byte[] input = associated ? reads[next] : association;
            int n = Math.Min(count, input.Length - position);
            if (n == 0) throw new IOException("Replay read boundary");
            Array.Copy(input, position, buffer, offset, n);
            position += n;
            if (position == input.Length)
            {
                position = 0;
                if (!associated) associated = true;
                else next = (next + 1) % reads.Length;
            }
            return n;
        }
        public int Write(byte[] buffer, int offset, int count, int timeout) { return count; }
    }
}
