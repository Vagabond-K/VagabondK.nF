using System;
using VagabondK.nF.MQTT.Mapping;

internal static class Program
{
    public static void Main()
    {
        SensorSnapshot decoded;
        if (!SnapshotJson.TryDecode(SnapshotJson.Encode(new SensorSnapshot(1, 125, true)), out decoded) ||
            decoded.Sequence != 1 || decoded.TemperatureDeciC != 125 || !decoded.Running)
            throw new InvalidOperationException("Generated JSON model contract.");
    }
}
