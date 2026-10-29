using System;
using VagabondK.nF.MQTT.Mapping;

[MqttJsonMap("plant/sensor", MaximumBytes = 160)]
internal partial class Model
{
    internal int Writes, Reads;
    internal bool ThrowWrite, ThrowRead;
    private int number;
    [MqttJsonMember(9, "enabled")]
    internal bool Enabled { get; private set; }
    [MqttJsonMember(0, "number")]
    internal int Number
    {
        get { Reads++; if (ThrowRead) throw new InvalidOperationException("getter"); return number; }
        private set { Writes++; if (ThrowWrite) throw new InvalidOperationException("setter"); number = value; }
    }
    [MqttJsonMember(4, "other")]
    internal int Other;
}

[MqttJsonMap("range", MaximumBytes = 32)]
internal partial class Ranged
{
    [MqttJsonMember(0, "value", Minimum = -500, Maximum = 1500)]
    internal int Value;
}

[MqttJsonMap("names")]
internal partial class Names
{
    [MqttJsonMember(0, "a")]
    internal int v0;
    [MqttJsonMember(1, "b")]
    internal new int ToString;
}
