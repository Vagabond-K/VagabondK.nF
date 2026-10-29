using System;
using System.Linq;
using Google.Protobuf;
using Sparkplug.Reference;
using VagabondK.nF.MQTT.Sparkplug;

/// <summary>샘플 식별자와 무관한 임의 그룹/노드/스키마로 공개 Sparkplug 라이브러리 DLL을 직접 검사합니다.</summary>
/// <remarks>세션→수신기 왕복, 스키마 복사, 부호 있는 정수 비트, 최대 uint64 별칭, 신선도 경계, 중복 거부, 인수 검증을 확인합니다.</remarks>
internal static class SparkplugLibraryChecks
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    internal static void Run()
    {
        Check(typeof(SparkplugPayload).Assembly != typeof(SparkplugLibraryChecks).Assembly &&
            typeof(SparkplugPayload).Assembly.GetName().Name == "VagabondK.nF.MQTT.Sparkplug", "Tests consume the public library DLL, not copied sample protocol sources.");
        var sender = new SparkplugSession("Factory", "Boiler", 255);
        string[] names = { "Enabled", "Pressure", "Lifetime", "Ratio", "Total" };
        SparkplugDataType[] types = { SparkplugDataType.Boolean, SparkplugDataType.Int32, SparkplugDataType.Int64, SparkplugDataType.Float, SparkplugDataType.Double };
        var receiver = new SparkplugReceiver("Factory", "Boiler", "Unit7", names, types, 500);
        names[0] = "Changed"; types[0] = SparkplugDataType.Int64;
        var definitions = new[] {
            SparkplugMetric.Boolean("Enabled", false, 0),
            SparkplugMetric.Int32("Pressure", int.MinValue, 100),
            SparkplugMetric.Int64("Lifetime", long.MinValue, ulong.MaxValue),
            SparkplugMetric.Float("Ratio", -0.5f, 8),
            SparkplugMetric.Double("Total", double.MaxValue, 9) };
        Throws<InvalidOperationException>(() => sender.Next(1, definitions, true));
        receiver.SetConnected(true);
        Check(receiver.Apply(receiver.Topic("NBIRTH"), sender.NodeBirth(123), false, 0) == SparkplugResult.NodeBirth, "Arbitrary group/node NBIRTH.");
        Check(receiver.Apply(receiver.Topic("DBIRTH"), sender.Next(124, definitions, true), false, 10) == SparkplugResult.Applied, "Independent five-metric schema with Int64.");
        var old = receiver.Read();
        Check(old.Count == 5 && old[0].Name == "Enabled" && old[1].Integer == 2147483648UL &&
            old[2].Integer == 9223372036854775808UL && old[2].Alias == ulong.MaxValue && old[3].Real == -0.5 &&
            old[4].Real == double.MaxValue, "Schema copied, signed integer bits and full uint64 aliases.");
        Check(receiver.IsAvailable(509) && !receiver.IsAvailable(510), "Caller-configured receive age.");
        byte[] partial = sender.Next(125, new[] { SparkplugMetric.Int64(null, -1, ulong.MaxValue, false) });
        Check(receiver.Apply(receiver.Topic("DDATA"), partial, false, 20) == SparkplugResult.Applied &&
            receiver.Read()[2].Integer == ulong.MaxValue && receiver.Read()[1] == old[1] &&
            old[2].Integer == 9223372036854775808UL, "Partial alias update leaves previous immutable snapshot intact.");
        Check(receiver.Apply(receiver.Topic("DDATA"), partial, false, 21) == SparkplugResult.SequenceGap &&
            receiver.NeedsRebirth && !receiver.IsAvailable(21), "Duplicate never silently reapplied.");
        Check(receiver.Apply(receiver.Topic("NDEATH"), sender.NodeDeath(), false, 22) == SparkplugResult.NodeDeath, "Matching bdSeq invalidation.");
        Check(receiver.CommandTopic == "spBv1.0/Factory/NCMD/Boiler" &&
            sender.Topic("DDATA", "Unit7") == "spBv1.0/Factory/DDATA/Boiler/Unit7", "No sample identity in library.");
        Throws<ArgumentException>(() => sender.Topic("DDATA"));
        Throws<ArgumentException>(() => sender.Topic("NBIRTH", "x"));
        Throws<ArgumentException>(() => sender.Topic("BAD"));
        Throws<ArgumentException>(() => new SparkplugSession("a/invalid", "node", 0));
        Throws<ArgumentException>(() => new SparkplugReceiver("a", "b", "c", new[] { "x", "x" }, new[] { SparkplugDataType.Int32, SparkplugDataType.Int32 }, 1));
        Throws<ArgumentException>(() => new SparkplugReceiver("a", "b", "c", new[] { "x" }, new[] { (SparkplugDataType)7 }, 1));
        Throws<ArgumentException>(() => new SparkplugReceiver("a", "b", "c", new[] { "x" }, new[] { SparkplugDataType.Unspecified }, 1));
        Throws<ArgumentException>(() => new SparkplugReceiver("a", "b", "c", new[] { "x" }, new[] { SparkplugDataType.UInt64 }, 1));
        Throws<ArgumentException>(() => new SparkplugReceiver("a", "b", "c", new[] { "x" }, new[] { SparkplugDataType.Int32 }, 0));
        Throws<ArgumentException>(() => SparkplugMetric.Boolean(null, true));
        Throws<ArgumentException>(() => SparkplugMetric.Boolean("", true));
        Throws<ArgumentOutOfRangeException>(() => SparkplugMetric.Float("x", float.NaN));
        Throws<ArgumentOutOfRangeException>(() => SparkplugMetric.Double("x", double.PositiveInfinity));
        sender.NodeBirth(1);
        Throws<ArgumentException>(() => sender.Next(2, new[] { definitions[0], definitions[0] }, true));
        Throws<ArgumentException>(() => sender.Next(2, new[] { SparkplugMetric.Int32(null, 1, 0, false) }, true));
        var oversized = Enumerable.Range(0, 8).Select(i => SparkplugMetric.Int64(new string((char)('a' + i), 64), long.MinValue, (ulong)i)).ToArray();
        Throws<ArgumentException>(() => sender.Next(2, oversized, true));
        var next = Payload.Parser.ParseFrom(sender.Next(3, definitions, true));
        Check(next.Seq == 1, "Failed encodings do not consume shared sequence.");
        for (int i = 2; i <= 256; i++)
            Check(Payload.Parser.ParseFrom(sender.Next((ulong)i, new SparkplugMetric[0])).Seq == (ulong)(i & 255), "Shared sequence wrap.");
        for (int i = 1; i <= 8; i++)
        {
            var metrics = Enumerable.Range(0, i).Select(n => SparkplugMetric.Int32("Point" + n, n, (ulong)n)).ToArray();
            byte[] bytes = SparkplugPayload.Encode(0, 0, metrics);
            SparkplugPayload decoded;
            Check(SparkplugPayload.TryDecode(bytes, out decoded) && decoded.Metrics.Length == i, "Variable schema sizes.");
        }
        Throws<ArgumentOutOfRangeException>(() => SparkplugPayload.Encode(0, 0, new SparkplugMetric[9]));
        Throws<ArgumentException>(() => SparkplugPayload.Encode(0, -1, new SparkplugMetric[0], false));
        Throws<ArgumentOutOfRangeException>(() => SparkplugPayload.Encode(0, 256, new SparkplugMetric[0]));
        var maximumSchema = Enumerable.Range(0, 8).Select(i => "Point" + i).ToArray();
        var maximumReceiver = new SparkplugReceiver("Factory", "Boiler", "Max", maximumSchema, Enumerable.Repeat(SparkplugDataType.Int32, 8).ToArray(), 1000);
        maximumReceiver.SetConnected(true);
        maximumReceiver.Apply(maximumReceiver.Topic("NBIRTH"), sender.NodeBirth(1), false, 0);
        var maximumBirth = Enumerable.Range(0, 8).Select(i => SparkplugMetric.Int32(maximumSchema[i], i, (ulong)i)).ToArray();
        Check(maximumReceiver.Apply(maximumReceiver.Topic("DBIRTH"), sender.Next(2, maximumBirth, true), false, 0) == SparkplugResult.Applied &&
            maximumReceiver.Read().Count == 8 && maximumReceiver.Read()[7].Integer == 7, "Receiver supports its maximum schema, not just sample size.");
        var duplicate = new Payload { Timestamp = 2, Seq = 2 };
        duplicate.Metrics.Add(new Payload.Types.Metric { Alias = 0, IntValue = 99 });
        duplicate.Metrics.Add(new Payload.Types.Metric { Alias = 0, IntValue = 100 });
        Check(maximumReceiver.Apply(maximumReceiver.Topic("DDATA"), duplicate.ToByteArray(), false, 1) == SparkplugResult.Malformed &&
            maximumReceiver.Read()[0].Integer == 0, "Maximum schema still rejects duplicate assignments atomically.");
        var reference = new Payload();
        reference.Metrics.Add(new Payload.Types.Metric { Name = "Time", Datatype = 4, LongValue = 1, Timestamp = 123456 });
        SparkplugPayload parsed;
        Check(SparkplugPayload.TryDecode(reference.ToByteArray(), out parsed), "Per-metric timestamp decode.");
        var reencoded = Payload.Parser.ParseFrom(SparkplugPayload.Encode(0, -1, parsed.Metrics, false));
        Check(reencoded.Metrics[0].HasTimestamp && reencoded.Metrics[0].Timestamp == 123456, "Public codec preserves per-metric timestamps.");
        Console.WriteLine("SPARKPLUG LIBRARY PASS checks=" + checks);
    }
}
