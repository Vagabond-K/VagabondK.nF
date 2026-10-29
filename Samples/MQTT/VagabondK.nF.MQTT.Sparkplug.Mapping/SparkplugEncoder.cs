using VagabondK.nF.MQTT.Sparkplug;

namespace VagabondK.nF.MQTT.Sparkplug.Mapping
{
    /// <summary>이 장치(edge-b/sensor-b)가 발행하는 Sparkplug 토픽과 메트릭 구성을 모은 도우미입니다.</summary>
    /// <remarks>BIRTH에서는 이름·형식·별칭 10~13을 함께 보내고, DATA에서는 별칭만 보내 페이로드를 줄입니다.</remarks>
    public static class SparkplugEncoder
    {
        public const string Node = "edge-b", Device = "sensor-b";
        public const string CommandTopic = SparkplugMapping.Prefix + "/NCMD/" + Node;
        public static string Topic(string verb)
        { return new SparkplugSession("VKNF", Node, 0).Topic(verb, verb == "DBIRTH" || verb == "DDATA" || verb == "DDEATH" ? Device : null); }
        public static byte[] NodeDeath(byte bdSequence)
        { return new SparkplugSession("VKNF", Node, bdSequence).NodeDeath(); }
        public static byte[] NodeBirth(ulong timestamp, byte bdSequence)
        { return new SparkplugSession("VKNF", Node, bdSequence).NodeBirth(timestamp); }
        public static byte[] DeviceDeath(ulong timestamp, byte sequence)
        { return SparkplugPayload.Encode(timestamp, sequence, new SparkplugMetric[0]); }
        public static SparkplugMetric[] Metrics(bool birth, bool running, int count, float temperature, double energy)
        {
            return new RemoteProcess(running, count, temperature, energy).Metrics(birth);
        }
        public static byte[] Values(bool birth, ulong timestamp, byte sequence, bool running, int count, float temperature, double energy)
        { return SparkplugPayload.Encode(timestamp, sequence, Metrics(birth, running, count, temperature, energy)); }
    }
}
