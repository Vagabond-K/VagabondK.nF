using System;
using VagabondK.nF.MQTT.Sparkplug;
using VagabondK.nF.MQTT.Sparkplug.Mapping;

internal static class Program
{
    public static void Main()
    {
        var metrics = SparkplugEncoder.Metrics(true, true, -100, 25.25f, 987654321.125);
        if (metrics.Length != 4 || metrics[0].DataType != SparkplugDataType.Boolean ||
            unchecked((int)(uint)metrics[1].Integer) != -100)
            throw new InvalidOperationException("Generated Sparkplug model contract.");
        var receiver = new SparkplugMapping();
        receiver.SetConnected(true);
        if (receiver.Read() != null) throw new InvalidOperationException("Expected uninitialized model.");
    }
}
