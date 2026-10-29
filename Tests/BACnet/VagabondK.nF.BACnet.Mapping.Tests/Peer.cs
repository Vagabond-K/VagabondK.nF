using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using VagabondK.nF.BACnet;
using VagabondK.nF.BACnet.IP;
using VagabondK.nF.BACnet.Mapping.Sample;
using static Program;

/// <summary>독립 BACnet 클라이언트(System.IO.BACnet)로 생성 저장소 기반 장치를 검사하는 상대 역할입니다.</summary>
/// <remarks><see cref="Loopback"/>은 PC 안에서, <see cref="Sample"/>과 <see cref="Scaling"/>은 UDP 47808의 실제 보드를 대상으로 실행합니다.</remarks>
internal static class Peer
{
    private static uint Finite(ref uint state, int i)
    {
        uint[] edges = { 0, 0x80000000, 1, 0x80000001, 0x007fffff, 0x807fffff, 0x00800000, 0x80800000,
            0x7f7fffff, 0xff7fffff, 0x3f800000, 0xbf800000, 0x3eaaaaab, 0x4b800000 };
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        uint bits = i < edges.Length ? edges[i] : state;
        return (bits & 0x7f800000) == 0x7f800000 ? bits ^ 0x00800000 : bits;
    }

    private sealed class Manager : IDisposable
    {
        internal readonly BacnetClient Client;
        internal readonly BacnetAddress Address;
        internal readonly BacnetObjectId Device = new(BacnetObjectTypes.OBJECT_DEVICE, 12345);
        private readonly ManualResetEventSlim iam = new();
        private int errorClass = -1, errorCode = -1;
        internal readonly List<double> Reads = new(), Writes = new();
        internal Manager(string host, int port)
        {
            string local;
            using (var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                route.Connect(new IPEndPoint(IPAddress.Parse(host), port));
                local = ((IPEndPoint)route.LocalEndPoint).Address.ToString();
            }
            Client = new BacnetClient(new BacnetIpUdpProtocolTransport(0, true, false, 1472, local), 2000, 0);
            Address = new BacnetAddress(BacnetAddressTypes.IP, host + ":" + port);
            Client.OnIam += (sender, peer, instance, apdu, segmentation, vendor) =>
            {
                if (instance == 12345 && apdu == 480 && segmentation == BacnetSegmentations.SEGMENTATION_NONE && vendor == 0)
                    iam.Set();
            };
            Client.OnError += (sender, peer, pdu, service, invoke, cls, code, bytes, offset, length) =>
            { errorClass = (int)cls; errorCode = (int)code; };
            Client.Start();
        }
        internal void Discover()
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            do { Client.WhoIs(12345, 12345, Address); } while (!iam.Wait(1000) && DateTime.UtcNow < deadline);
            Check(iam.IsSet, "Independent Who-Is/I-Am.");
        }
        internal IList<BacnetValue> Read(BacnetObjectId obj, BacnetPropertyIds property = BacnetPropertyIds.PROP_PRESENT_VALUE,
            uint array = uint.MaxValue, bool record = false)
        {
            long start = Stopwatch.GetTimestamp();
            bool ok = Client.ReadPropertyRequest(Address, obj, property, out IList<BacnetValue> values, arrayIndex: array);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Check(ok, "Independent ReadProperty: " + obj + "/" + property + " array=" + array +
                " elapsedMs=" + elapsed + " lastError=" + errorClass + "/" + errorCode);
            if (record) Reads.Add(elapsed);
            return values;
        }
        internal void Write(BacnetObjectId obj, BacnetValue value, bool record = false)
        {
            long start = Stopwatch.GetTimestamp();
            bool ok = Client.WritePropertyRequest(Address, obj, BacnetPropertyIds.PROP_PRESENT_VALUE, new[] { value });
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Check(ok, "Independent WriteProperty: " + obj);
            if (record) Writes.Add(elapsed);
        }
        internal void RejectWrite(BacnetObjectId obj, BacnetValue value, int code, byte? priority = null)
        {
            errorClass = errorCode = -1;
            using var result = (BacnetAsyncResult)Client.BeginWritePropertyRequest(Address, obj,
                BacnetPropertyIds.PROP_PRESENT_VALUE, new[] { value }, true, priority: priority);
            Check(result.WaitForDone(4000), "Expected rejection timeout.");
            Client.EndWritePropertyRequest(result, out Exception error);
            Check(error != null && errorClass == 2 && errorCode == code, "Write error mismatch: " + errorClass + "/" + errorCode);
        }
        internal void RejectRead(BacnetObjectId obj, BacnetPropertyIds property, int cls, int code)
        {
            errorClass = errorCode = -1;
            using var result = (BacnetAsyncResult)Client.BeginReadPropertyRequest(Address, obj, property, true);
            Check(result.WaitForDone(4000), "Expected read rejection timeout.");
            Client.EndReadPropertyRequest(result, out _, out Exception error);
            Check(error != null && errorClass == cls && errorCode == code, "Read error mismatch.");
        }
        public void Dispose() { Client.Dispose(); iam.Dispose(); }
    }
    internal static void Sample(string host, int port, int iterations)
    {
        using var manager = new Manager(host, port);
        manager.Discover();
        var av = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 100);
        var input = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 0);
        var bv = new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, 0);
        var quality = new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, 1);
        var objects = manager.Read(manager.Device, BacnetPropertyIds.PROP_OBJECT_LIST);
        Check(objects.Count == 5 && objects.Select(v => (BacnetObjectId)v.Value).Distinct().Count() == 5, "Exact Object_List including Device.");
        Check((uint)manager.Read(manager.Device, BacnetPropertyIds.PROP_OBJECT_LIST, 0)[0].Value == 5);
        var expectedObjects = new[] { manager.Device, input, av, bv, quality };
        for (int i = 0; i < expectedObjects.Length; i++)
        {
            Check(((BacnetObjectId)objects[i].Value).Equals(expectedObjects[i]), "Device-first numeric Object_List.");
            Check(((BacnetObjectId)manager.Read(manager.Device, BacnetPropertyIds.PROP_OBJECT_LIST, (uint)i + 1)[0].Value)
                .Equals(expectedObjects[i]), "One-based Object_List indexed access.");
        }
        Check((float)manager.Read(input)[0].Value == 250f);
        foreach (bool good in new[] { false, true })
        {
            manager.Write(quality, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, good ? 1U : 0U));
            var flags = (BacnetBitString)manager.Read(input, BacnetPropertyIds.PROP_STATUS_FLAGS)[0].Value;
            Check(flags.bits_used == 4 && flags.GetBit(1) == !good, "Mapped quality→Fault.");
            Check(Convert.ToUInt32(manager.Read(input, BacnetPropertyIds.PROP_RELIABILITY)[0].Value) == (good ? 0U : 7U));
        }
        manager.RejectWrite(input, new BacnetValue(1f), 40);
        manager.RejectWrite(av, new BacnetValue(float.NaN), 37);
        manager.RejectWrite(av, new BacnetValue(float.PositiveInfinity), 37);
        manager.RejectWrite(av, new BacnetValue(true), 9);
        manager.RejectWrite(av, new BacnetValue(1f), 40, 8);
        manager.RejectWrite(bv, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 2U), 37);
        manager.RejectRead(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 999),
            BacnetPropertyIds.PROP_PRESENT_VALUE, 1, 31);
        uint random = 20261025;
        for (int i = 0; i < iterations; i++)
        {
            uint bits = Finite(ref random, i);
            manager.Write(av, new BacnetValue(BitConverter.UInt32BitsToSingle(bits)));
            Check(BitConverter.SingleToUInt32Bits((float)manager.Read(av)[0].Value) == bits, "Exact finite float bits #" + i);
            manager.Write(bv, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, (uint)i & 1));
            Check(Convert.ToUInt32(manager.Read(bv)[0].Value) == ((uint)i & 1));
        }
        using var malformed = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        foreach (byte[] frame in new[] { new byte[0], new byte[] { 0x81, 0x0a, 0, 4 }, new byte[513], new byte[1472] })
        {
            malformed.SendTo(frame, new IPEndPoint(IPAddress.Parse(host), port));
            Check(manager.Read(manager.Device, BacnetPropertyIds.PROP_OBJECT_NAME).Count == 1, "Malformed recovery.");
        }
        Console.WriteLine("BACnet 4.0.0 mapping peer PASS: " + iterations + " AV/BV bit-exact cycles, quality, discovery, rejection and malformed recovery.");
    }

    internal static void Loopback()
    {
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var session = new BacnetIpSession(udp, new BacnetDevice(new DeviceData.BacnetStore(new DeviceData(), new object()),
            12345, 0, "Mapping test"));
        using var stop = new CancellationTokenSource();
        Exception failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    try { session.ProcessNext(20); }
                    catch (BacnetProtocolException) { }
            }
            catch (Exception error) { failure = error; }
        });
        worker.Start();
        try { Sample("127.0.0.1", ((IPEndPoint)udp.LocalEndPoint).Port, 128); }
        finally
        {
            stop.Cancel();
            if (!worker.Join(3000)) throw new InvalidOperationException("Loopback worker did not stop.");
        }
        if (failure != null) throw new InvalidOperationException("Loopback worker failed.", failure);
    }

    internal static void Scaling(string host, int count, int rounds, string output)
    {
        using var manager = new Manager(host, 47808);
        manager.Discover();
        using var diagnostic = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        diagnostic.ReceiveTimeout = 3000;
        diagnostic.Connect(new IPEndPoint(IPAddress.Parse(host), 47809));
        uint[] Metrics(byte command)
        {
            diagnostic.Send(new byte[] { 0x42, 0x4e, 0x53, 0x31, command });
            byte[] response = new byte[64];
            int length = diagnostic.Receive(response);
            Check(length == 60 && response[0] == 0x42 && response[1] == 0x4e && response[2] == 0x53 && response[3] == 0x31, "Diagnostics frame.");
            uint[] values = Enumerable.Range(0, 14).Select(i =>
                ((uint)response[4 + i * 4] << 24) | ((uint)response[5 + i * 4] << 16) |
                ((uint)response[6 + i * 4] << 8) | response[7 + i * 4]).ToArray();
            Check(values[0] == count && values[9] == 0 && values[13] == 0x600D, "Exact scale, debugger absent, benchmark complete.");
            return values;
        }
        uint[] SampleGc()
        {
            uint before = Metrics(0)[11];
            Metrics(1);
            uint[] values = Metrics(0);
            Check(values[11] == before + 1, "GC after request locals leave scope.");
            return values;
        }
        uint[] initial = Metrics(0);
        // 객체 64개에서 비분할 APDU 한도를 넘지 않도록 Object_List는 인덱스별로 읽습니다.
        Check(Convert.ToUInt32(manager.Read(manager.Device, BacnetPropertyIds.PROP_OBJECT_LIST, 0)[0].Value) == count + 1);
        Check(((BacnetObjectId)manager.Read(manager.Device, BacnetPropertyIds.PROP_OBJECT_LIST, 1)[0].Value).Equals(manager.Device));
        for (int i = 1; i <= count; i++)
        {
            var id = (BacnetObjectId)manager.Read(manager.Device, BacnetPropertyIds.PROP_OBJECT_LIST, (uint)i + 1)[0].Value;
            var expected = new BacnetObjectId(i <= count / 2 ? BacnetObjectTypes.OBJECT_ANALOG_VALUE : BacnetObjectTypes.OBJECT_BINARY_VALUE,
                (uint)((i - 1) % (count / 2)));
            Check(id.Equals(expected), "Sorted Object_List index.");
        }
        uint random = 20261025;
        void Cycle(int round, bool record)
        {
            for (int i = 0; i < count / 2; i++)
            {
                uint bits = Finite(ref random, round * (count / 2) + i);
                var av = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, (uint)i);
                var bv = new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, (uint)i);
                manager.Write(av, new BacnetValue(BitConverter.UInt32BitsToSingle(bits)), record);
                Check(BitConverter.SingleToUInt32Bits((float)manager.Read(av, record: record)[0].Value) == bits);
                manager.Write(bv, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, (uint)round & 1), record);
                Check(Convert.ToUInt32(manager.Read(bv, record: record)[0].Value) == ((uint)round & 1));
            }
        }
        for (int i = 0; i < 3; i++) Cycle(i, false);
        var savedAnalog = new uint[count / 2];
        var savedBinary = new uint[count / 2];
        for (int i = 0; i < count / 2; i++)
        {
            savedAnalog[i] = BitConverter.SingleToUInt32Bits((float)manager.Read(
                new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, (uint)i))[0].Value);
            savedBinary[i] = Convert.ToUInt32(manager.Read(
                new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, (uint)i))[0].Value);
        }
        int[] slots = { 0, count / 4, count / 2 - 1 };
        for (int slot = 0; slot < slots.Length; slot++)
        {
            int changed = slots[slot];
            Metrics((byte)(slot + 2));
            savedAnalog[changed] = BitConverter.SingleToUInt32Bits(-(700f + changed));
            savedBinary[changed] = (uint)changed & 1;
            var changedId = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, (uint)changed);
            manager.RejectWrite(changedId, new BacnetValue(true), 9);
            manager.RejectWrite(changedId, new BacnetValue(float.PositiveInfinity), 37);
            for (int i = 0; i < count / 2; i++)
            {
                Check(BitConverter.SingleToUInt32Bits((float)manager.Read(
                    new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, (uint)i))[0].Value) == savedAnalog[i],
                    "Local analog change and rejected write must preserve every other point.");
                Check(Convert.ToUInt32(manager.Read(
                    new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, (uint)i))[0].Value) == savedBinary[i],
                    "Local binary change isolated by object instance.");
            }
        }
        using (var reconnected = new Manager(host, 47808))
        {
            Check(BitConverter.SingleToUInt32Bits((float)reconnected.Read(
                new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, (uint)slots[2]))[0].Value) == savedAnalog[slots[2]],
                "New peer/invoke namespace observes last local value.");
        }
        Console.WriteLine("BACNET ROBUST PASS first/middle/last local-model isolation, rejection preservation, new peer.");
        SampleGc();
        var samples = new List<uint> { SampleGc()[10] };
        for (int round = 0; round < rounds; round++)
        {
            Cycle(round + 3, true);
            if ((round + 1) % 10 == 0 || round + 1 == rounds) samples.Add(SampleGc()[10]);
        }
        long growth = Math.Max(0L, (long)samples[0] - samples.Min());
        object Summary(List<double> values) => new {
            Requests = values.Count, Min = values.Min(), Median = values.OrderBy(v => v).ElementAt(values.Count / 2),
            P95 = values.OrderBy(v => v).ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1), Max = values.Max()
        };
        var result = new {
            Points = count, AnalogPoints = count / 2, BinaryPoints = count / 2, Rounds = rounds,
            HeapBeforeModel = initial[1], HeapAfterModelStore = initial[2], HeapAfterDevice = initial[3], HeapAfterBenchmark = initial[4],
            ModelStoreBytes = (long)initial[1] - initial[2], DeviceBytes = (long)initial[2] - initial[3],
            DirectAnalogMs = initial[5], DirectBinaryMs = initial[6], DirectSweeps = initial[7], DirectPairsPerType = initial[7] * count / 2,
            WarmHeap = samples[0], GcSamples = samples, MaximumRetainedGrowthBytes = growth,
            NetworkMs = new { Read = Summary(manager.Reads), Write = Summary(manager.Writes) }
        };
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
        Check(growth == 0, "No retained heap growth after warmup; raw samples saved.");
    }
}
