using VagabondK.nF.MQTT.Sparkplug;
using VagabondK.nF.MQTT.Sparkplug.Mapping;

namespace VagabondK.nF.MQTT.Sparkplug.Mapping
{
    /// <summary>원격 장치(edge-a/sensor-a)의 마지막 스냅샷을 애플리케이션의 멤버 이름에 맞춰 제공하는 모델입니다.</summary>
    [SparkplugMap]
    public sealed partial class RemoteProcess
    {
        internal RemoteProcess() { }
        internal RemoteProcess(SparkplugSnapshot snapshot) { ApplySnapshot(snapshot); }
        internal RemoteProcess(bool running, int count, float temperature, double energy)
        { SourceRunning = running; SourceCount = count; ExternalTemperature = temperature; ImportedEnergy = energy; }
        [SparkplugMetric("Sensor/Running", Alias = 10)]
        public bool SourceRunning { get; private set; }
        [SparkplugMetric("Sensor/Count", Alias = 11)]
        public int SourceCount { get; private set; }
        [SparkplugMetric("Sensor/Temperature", Alias = 12)]
        public float ExternalTemperature { get; private set; }
        [SparkplugMetric("Sensor/Energy", Alias = 13)]
        public double ImportedEnergy { get; private set; }
        public int Sequence { get { return SparkplugSequence; } }
    }
    /// <summary>원격 장치 edge-a/sensor-a의 스키마를 고정한 <see cref="SparkplugReceiver"/> 래퍼입니다.</summary>
    /// <remarks>마지막 장치 값 수신 후 3000ms가 지나거나 단절·Death·잘못된 입력이 있으면 사용할 수 없습니다.
    /// Read는 마지막 진단 값도 반환하므로 IsAvailable을 함께 확인하십시오. 별칭·부분 갱신·순번 검사는 생성 바인딩의 수신기에 위임합니다.</remarks>
    public sealed class SparkplugMapping
    {
        public const string Prefix = "spBv1.0/VKNF", Node = "edge-a", Device = "sensor-a";
        public const string CommandTopic = Prefix + "/NCMD/" + Node;
        private readonly RemoteProcess.SparkplugBinding binding = new RemoteProcess.SparkplugBinding("VKNF", Node, Device, 3000);
        private SparkplugReceiver receiver { get { return binding.Receiver; } }
        public static string Topic(string verb)
        { return new SparkplugSession("VKNF", Node, 0).Topic(verb, verb == "DBIRTH" || verb == "DDATA" || verb == "DDEATH" ? Device : null); }
        public RemoteProcess Read() { var value = new RemoteProcess(); return binding.ReadInto(value) ? value : null; }
        public bool NeedsRebirth { get { return receiver.NeedsRebirth; } }
        public void SetConnected(bool value) { receiver.SetConnected(value); }
        public bool IsAvailable(long now) { return receiver.IsAvailable(now); }
        public SparkplugResult Apply(string topic, byte[] bytes, bool retained, long now)
        { return receiver.Apply(topic, bytes, retained, now); }
    }
}
