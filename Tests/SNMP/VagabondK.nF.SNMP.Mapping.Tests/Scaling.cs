using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using VagabondK.nF.SNMP.Tests;

/// <summary>자체 BER 시험기로 Mapping.Hardware 펌웨어(UDP 1161)에 GET/GETBULK/SET을 반복해 지연 시간과 보드 측정값을 JSON으로 저장합니다. SharpSnmpLib 외부 피어 시험과는 별개입니다.</summary>
internal static class ScalingPeer
{
    internal static void Run(string address, int count, int rounds, string output)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = 3000;
        socket.Connect(new IPEndPoint(IPAddress.Parse(address), 1161));
        string model = "1.3.6.1.4.1.32473.30";
        string metric = "1.3.6.1.4.1.32473.31";
        int id = 10000;
        int datagrams = 0;
        var timings = new Dictionary<string, List<double>> {
            ["GET"] = new(), ["GETBULK"] = new(), ["SET"] = new()
        };
        Wire.Reply Ask(int pdu, int repetitions, byte[][] binds, bool record = false)
        {
            int requestId = ++id;
            byte[] message = Wire.Message(1, pdu == 0xa3 ? "private" : "public", pdu, requestId, 0, repetitions, binds);
            long start = Stopwatch.GetTimestamp();
            if (socket.Send(message) != message.Length) throw new IOException("Partial UDP send.");
            var buffer = new byte[4096];
            int length = socket.Receive(buffer);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Wire.Reply reply = Wire.Parse(buffer.Take(length).ToArray());
            Program.Check(reply.Id == requestId && reply.Version == 1 && reply.Pdu == 0xa2 && reply.Status == 0, "Scaling response identity/status.");
            datagrams++;
            if (record) timings[pdu == 0xa0 ? "GET" : pdu == 0xa5 ? "GETBULK" : "SET"].Add(ms);
            return reply;
        }
        uint[] Metrics()
        {
            Wire.Reply reply = Ask(0xa0, 0, Enumerable.Range(1, 14).Select(i => Wire.Bind(metric + "." + i + ".0")).ToArray());
            Program.Check(reply.Bindings.Count == 14 && reply.Bindings.All(b => b.Tag == 0x42), "Metrics format.");
            uint[] values = reply.Bindings.Select(b => checked((uint)b.Unsigned)).ToArray();
            Program.Check(values[0] == count && values[9] == 0 && values[13] == 0x600D, "Exact scale, no debugger, complete benchmark.");
            return values;
        }
        uint[] Sample()
        {
            uint before = Metrics()[11];
            Ask(0xa3, 0, new[] { Wire.Bind(metric + ".13.0", Wire.UInt(1, 0x42)) });
            uint[] values = Metrics();
            Program.Check(values[11] == before + 1, "GC sample completed after packet helper returned.");
            return values;
        }
        DateTime deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            try { Ask(0xa0, 0, new[] { Wire.Bind("1.3.6.1.2.1.1.1.0") }); break; }
            catch (SocketException error) when ((error.SocketErrorCode == SocketError.TimedOut || error.SocketErrorCode == SocketError.ConnectionReset) &&
                DateTime.UtcNow < deadline)
            {
                Console.WriteLine("Waiting for scaling MCU: " + error.SocketErrorCode);
                Thread.Sleep(1000);
            }
        }
        uint[] initial = Metrics();
        void Cycle(int round, bool record)
        {
            byte[] payload = Enumerable.Repeat((byte)round, 512).ToArray();
            for (int first = 1; first <= count; first += 8)
            {
                int length = Math.Min(8, count - first + 1);
                int[] indices = Enumerable.Range(first, length).ToArray();
                byte[][] writes = indices.Select(i => Wire.Bind(model + "." + i + ".0",
                    i == count ? Wire.Tlv(4, payload) : Wire.Int(round * 1000 + i))).ToArray();
                Wire.Reply set = Ask(0xa3, 0, writes, record);
                Program.Check(set.Bindings.Count == length && set.Index == 0);
                Wire.Reply get = Ask(0xa0, 0, indices.Select(i => Wire.Bind(model + "." + i + ".0")).ToArray(), record);
                string startOid = first == 1 ? model : model + "." + (first - 1) + ".0";
                Wire.Reply bulk = Ask(0xa5, length, new[] { Wire.Bind(startOid) }, record);
                foreach (Wire.Reply reply in new[] { get, bulk })
                {
                    Program.Check(reply.Bindings.Count == length, "Exact GET/BULK batch length.");
                    for (int n = 0; n < length; n++)
                    {
                        var binding = reply.Bindings[n];
                        int point = first + n;
                        Program.Check(binding.Oid == model + "." + point + ".0", "Numeric OID walk.");
                        Program.Check(point == count ? binding.Tag == 4 && binding.Content.SequenceEqual(payload) :
                            binding.Tag == 2 && binding.Signed == round * 1000 + point, "SET→GET/BULK data.");
                    }
                }
            }
        }
        for (int round = 0; round < 3; round++) Cycle(round, false);
        // 진단 요청과 GC 샘플링 자체도 예열 단계에 포함합니다.
        Sample();
        uint[] warm = Sample();
        var samples = new List<uint> { warm[10] };
        for (int round = 0; round < rounds; round++)
        {
            Cycle(round + 3, true);
            if ((round + 1) % 10 == 0 || round + 1 == rounds) samples.Add(Sample()[10]);
        }
        uint[] final = Metrics();
        long growth = Math.Max(0L, (long)samples[0] - samples.Min());
        var result = new {
            Points = count, NumericPoints = count - 1, OctetBytes = 512, Rounds = rounds, Datagrams = datagrams,
            HeapBeforeModel = initial[1], HeapAfterModelStore = initial[2], HeapAfterAgentAndMetrics = initial[3],
            HeapAfterBenchmark = initial[4], ModelStoreBytes = (long)initial[1] - initial[2],
            AgentAndMetricsBytes = (long)initial[2] - initial[3],
            DirectNumericSweeps = initial[7], DirectNumericPairs = initial[7] * (count - 1),
            DirectNumericMs = initial[5], OctetValidationCalls = initial[8], OctetValidationMs = initial[6],
            WarmHeap = samples[0], GcSamples = samples, MaximumRetainedGrowthBytes = growth,
            FinalGcSampleCounter = final[11],
            NetworkMs = timings.ToDictionary(p => p.Key, p => new {
                Requests = p.Value.Count, Min = p.Value.Min(), Median = p.Value.OrderBy(v => v).ElementAt(p.Value.Count / 2),
                P95 = p.Value.OrderBy(v => v).ElementAt((int)Math.Ceiling(p.Value.Count * 0.95) - 1), Max = p.Value.Max()
            })
        };
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
        Program.Check(growth == 0, "Post-warmup retained heap growth must be zero; raw results saved.");
    }
}
