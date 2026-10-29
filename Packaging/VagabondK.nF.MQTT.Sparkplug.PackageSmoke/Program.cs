using System;
using VagabondK.nF.MQTT.Sparkplug;

internal static class Program
{
    public static void Main()
    {
        var sender = new SparkplugSession("Factory", "Boiler", 1);
        var receiver = new SparkplugReceiver("Factory", "Boiler", "Unit", new[] { "Counter" }, new[] { SparkplugDataType.Int64 }, 5000);
        receiver.SetConnected(true);
        receiver.Apply(receiver.Topic("NBIRTH"), sender.NodeBirth(1), false, 0);
        var result = receiver.Apply(receiver.Topic("DBIRTH"),
            sender.Next(2, new[] { SparkplugMetric.Int64("Counter", -1, 0) }, true), false, 1);
        if (result != SparkplugResult.Applied || receiver.Read()[0].Integer != ulong.MaxValue)
            throw new InvalidOperationException("Public package API.");
    }
}
