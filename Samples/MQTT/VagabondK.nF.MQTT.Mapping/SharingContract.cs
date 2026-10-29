using System;
using VagabondK.nF.MQTT.Mapping;

namespace VagabondK.nF.MQTT.Mapping
{
    /// <summary>장치가 발행하는 센서 스냅샷입니다. 순번은 1 이상, 온도는 -50.0~150.0℃(0.1℃ 단위)입니다.</summary>
    [MqttJsonMap("vknf/sample/device-b/sensor", MaximumBytes = 160)]
    public sealed partial class SensorSnapshot
    {
        [MqttJsonMember(0, "sequence", Minimum = 1)]
        public int Sequence { get; private set; }
        [MqttJsonMember(1, "temperatureDeciC", Minimum = -500, Maximum = 1500)]
        public int TemperatureDeciC { get; private set; }
        [MqttJsonMember(2, "running")]
        public bool Running { get; private set; }

        public SensorSnapshot(int sequence, int temperatureDeciC, bool running)
        {
            if (sequence < 1 || temperatureDeciC < -500 || temperatureDeciC > 1500)
                throw new ArgumentOutOfRangeException("snapshot");
            Sequence = sequence; TemperatureDeciC = temperatureDeciC; Running = running;
        }
    }

    /// <summary>생성한 <see cref="SensorSnapshot.MqttBinding"/> 코덱을 기존 샘플 API에 연결합니다.</summary>
    /// <remarks>필드 3개가 정확히 한 번씩 있어야 하며 최대 <see cref="MaximumBytes"/>바이트입니다. 범용 JSON이 아니며 ASCII 필드 이름만 허용합니다.</remarks>
    public static class SnapshotJson
    {
        public const int MaximumBytes = SensorSnapshot.MqttBinding.MaximumBytes;
        public static byte[] Encode(SensorSnapshot snapshot)
        {
            return SensorSnapshot.MqttBinding.Encode(snapshot);
        }

        /// <summary>재사용할 버퍼에 기록하고 유효한 바이트 수를 반환합니다.</summary>
        /// <remarks>offset부터 반환 길이만 유효합니다. 배열 전체를 보내는 MQTT API에는 정확한 길이를 할당하는 오버로드를 사용하십시오.</remarks>
        public static int Encode(SensorSnapshot snapshot, byte[] buffer, int offset = 0)
        {
            return SensorSnapshot.MqttBinding.Encode(snapshot, buffer, offset);
        }

        /// <summary>길이가 다른 값도 같은 버퍼에 기록할 수 있는지 통신 전에 확인합니다.</summary>
        public static void DemonstrateBufferEncoding()
        {
            const int offset = 3;
            var buffer = new byte[MaximumBytes + offset];
            var snapshots = new[] {
                new SensorSnapshot(1, -500, false),
                new SensorSnapshot(int.MaxValue, 1500, true),
                new SensorSnapshot(2, 0, true)
            };
            foreach (var snapshot in snapshots)
            {
                int length = Encode(snapshot, buffer, offset);
                byte[] expected = Encode(snapshot);
                if (length != expected.Length) throw new InvalidOperationException("JSON buffer length.");
                for (int n = 0; n < length; n++)
                    if (buffer[offset + n] != expected[n]) throw new InvalidOperationException("JSON buffer content.");
            }
        }

        public static bool TryDecode(byte[] bytes, out SensorSnapshot snapshot)
        {
            snapshot = null;
            SensorSnapshot.MqttBinding.Snapshot decoded;
            if (!SensorSnapshot.MqttBinding.TryDecode(bytes, out decoded)) return false;
            snapshot = new SensorSnapshot(decoded.Sequence, decoded.TemperatureDeciC, decoded.Running);
            return true;
        }
    }

    /// <summary><see cref="RemoteSensorMapping.Apply"/>의 처리 결과입니다. 적용되지 않은 메시지는 이전 값을 바꾸지 않습니다.</summary>
    public enum ApplyResult { Applied, WrongTopic, Retained, Malformed, OldSequence, Disconnected }

    /// <summary>수신 측 애플리케이션이 쓰는 원격 센서 값입니다.</summary>
    /// <remarks>수신 측 이름은 발행 측 필드 이름과 같을 필요가 없습니다.</remarks>
    public sealed class ControllerView
    {
        public int SourceSequence { get; private set; }
        public int ExternalTemperatureDeciC { get; private set; }
        public bool SourceRunning { get; private set; }
        internal ControllerView(SensorSnapshot snapshot)
        {
            SourceSequence = snapshot.Sequence;
            ExternalTemperatureDeciC = snapshot.TemperatureDeciC;
            SourceRunning = snapshot.Running;
        }
    }

    /// <summary>한 토픽의 JSON 스냅샷을 검증해 <see cref="ControllerView"/>로 보관하는 스레드 안전 매핑입니다.</summary>
    /// <remarks>보관(retained) 메시지, 잘못된 형식, 같거나 오래된 순번은 거부하고 기존 뷰를 보존합니다.
    /// 값의 신선도는 연결 상태와 로컬 수신 후 경과 시간으로 판단하며 재연결만으로 회복되지 않습니다.
    /// 송신자 재부팅에 따른 순번 초기화는 자동 수락하지 않으므로 별도 세션 정책이 필요합니다.</remarks>
    public sealed class RemoteSensorMapping
    {
        private readonly object gate = new object();
        private readonly string topic;
        private readonly int maximumAgeMilliseconds;
        private ControllerView view;
        private bool connected, current;
        private long receivedAt;

        public RemoteSensorMapping(string topic, int maximumAgeMilliseconds)
        {
            if (string.IsNullOrEmpty(topic) || topic.IndexOf('+') >= 0 || topic.IndexOf('#') >= 0 ||
                maximumAgeMilliseconds < 1) throw new ArgumentException("Exact topic and positive age required.");
            this.topic = topic; this.maximumAgeMilliseconds = maximumAgeMilliseconds;
        }
        public ControllerView Read() { lock (gate) return view; }
        public void SetConnected(bool value)
        {
            lock (gate)
            {
                connected = value;
                // 재연결만으로 이전 값이 최신이 되지는 않습니다. 새 메시지를 받아야 다시 유효해집니다.
                if (!value) current = false;
            }
        }
        public bool IsAvailable(long now)
        {
            lock (gate) return connected && current && now >= receivedAt && now - receivedAt < maximumAgeMilliseconds;
        }
        public ApplyResult Apply(string receivedTopic, byte[] payload, bool retained, long now)
        {
            if (now < 0) throw new ArgumentOutOfRangeException("now");
            if (receivedTopic != topic) return ApplyResult.WrongTopic;
            if (retained) return ApplyResult.Retained;
            SensorSnapshot snapshot;
            if (!SnapshotJson.TryDecode(payload, out snapshot)) return ApplyResult.Malformed;
            lock (gate)
            {
                if (!connected) return ApplyResult.Disconnected;
                if (view != null && snapshot.Sequence <= view.SourceSequence) return ApplyResult.OldSequence;
                view = new ControllerView(snapshot);
                receivedAt = now; current = true;
                return ApplyResult.Applied;
            }
        }
    }
}
