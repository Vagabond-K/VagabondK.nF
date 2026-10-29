using System;
using VagabondK.nF.MQTT.Sparkplug;
using VagabondK.nF.MQTT.Sparkplug.Mapping;

[SparkplugMap]
internal sealed partial class Scalars
{
    [SparkplugMetric("Enabled", SparkplugDataType.Boolean, 0)]
    public bool Enabled { get; private set; }
    [SparkplugMetric("Count", SparkplugDataType.Int32, 1)]
    public int Count { get; private set; }
    [SparkplugMetric("Lifetime", SparkplugDataType.Int64, ulong.MaxValue)]
    public long Lifetime { get; private set; }
    [SparkplugMetric("Ratio", SparkplugDataType.Float, 3)]
    public float Ratio { get; private set; }
    [SparkplugMetric("Total", SparkplugDataType.Double, 4)]
    public double Total { get; private set; }
    internal void Set(bool enabled, int count, long lifetime, float ratio, double total)
    { Enabled = enabled; Count = count; Lifetime = lifetime; Ratio = ratio; Total = total; }
}

[SparkplugMap]
internal partial class Named
{
    [SparkplugMetric("value", SparkplugDataType.Int32)]
    private int value;
    internal int Value { get { return value; } }
}

[SparkplugMap]
internal partial class Throwing
{
    internal bool Reject;
    private int value;
    [SparkplugMetric("value", SparkplugDataType.Int32)]
    internal int Value { get { return value; } set { if (Reject) throw new InvalidOperationException("Application setter."); this.value = value; } }
}
