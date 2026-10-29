using System;

namespace VagabondK.nF.MQTT.Sparkplug
{
    /// <summary>Edge Node 쪽 MQTT 연결(CONNECT) 하나에 대한 Sparkplug 토픽과 공유 seq 상태입니다.</summary>
    /// <remarks>동시 접근을 지원하지 않으므로 호출자가 차례로 호출해야 합니다. 전송 계층의 수명은 관리하지 않습니다.
    /// 일반 흐름: <see cref="NodeDeath"/>를 Will로 CONNECT → <see cref="NodeBirth"/> 발행 → <c>Next(..., birth: true)</c>로 DBIRTH → <see cref="Next"/>로 DDATA.</remarks>
    public sealed class SparkplugSession
    {
        private readonly string prefix;
        private byte sequence;
        private bool born;
        /// <summary>이 CONNECT 세션의 bdSeq입니다. NBIRTH와 Will(NDEATH)에 같은 값이 들어갑니다.</summary>
        public byte BirthDeathSequence { get; private set; }
        /// <summary>그룹·노드 ID를 검증해 세션 식별자를 만듭니다.</summary>
        /// <remarks>bdSeq는 CONNECT마다 호출자가 증가시키고 필요하면 영구 저장해야 합니다.</remarks>
        /// <param name="group">그룹 ID입니다. 1..64자의 출력 가능한 ASCII이며 <c>/</c>, <c>+</c>, <c>#</c>은 쓸 수 없습니다.</param>
        /// <param name="node">Edge Node ID입니다. 규칙은 <paramref name="group"/>과 같습니다.</param>
        /// <param name="birthDeathSequence">이 CONNECT에 사용할 bdSeq(0..255)입니다.</param>
        /// <exception cref="ArgumentException">그룹 또는 노드 ID가 잘못된 경우.</exception>
        public SparkplugSession(string group, string node, byte birthDeathSequence)
        {
            Component(group); Component(node);
            prefix = "spBv1.0/" + group + "/";
            Node = node; BirthDeathSequence = birthDeathSequence;
        }
        /// <summary>토픽에 쓰이는 Edge Node ID입니다.</summary>
        public string Node { get; private set; }
        /// <summary>지원하는 Sparkplug 토픽을 만듭니다.</summary>
        /// <param name="verb">NBIRTH, NDATA, NDEATH, NCMD 또는 DBIRTH, DDATA, DDEATH, DCMD입니다.</param>
        /// <param name="device">장치 메시지(D*)에는 필수인 장치 ID이고, 노드 메시지(N*)에는 <see langword="null"/>이어야 합니다.</param>
        /// <returns><c>spBv1.0/{group}/{verb}/{node}[/{device}]</c> 형식의 토픽(최대 128자)입니다.</returns>
        /// <exception cref="ArgumentException">메시지 종류·장치 ID가 잘못되었거나 토픽이 128자를 넘는 경우.</exception>
        public string Topic(string verb, string device = null)
        {
            bool deviceMessage = verb == "DBIRTH" || verb == "DDATA" || verb == "DDEATH" || verb == "DCMD";
            if (!deviceMessage && verb != "NBIRTH" && verb != "NDATA" && verb != "NDEATH" && verb != "NCMD")
                throw new ArgumentException("Sparkplug message type.");
            if (deviceMessage) Component(device);
            else if (device != null) throw new ArgumentException("Node message cannot have device.");
            string result = prefix + verb + "/" + Node + (deviceMessage ? "/" + device : "");
            if (result.Length > 128) throw new ArgumentException("MQTT topic bound.");
            return result;
        }
        /// <summary>bdSeq와 <c>Node Control/Rebirth=false</c>를 담은 NBIRTH(seq 0)를 인코딩하고 공유 seq를 0으로 초기화합니다.</summary>
        /// <remarks>Rebirth 요청을 받았을 때도 이 메서드로 다시 시작한 뒤 DBIRTH를 보내야 합니다.</remarks>
        /// <param name="timestamp">페이로드 타임스탬프(UTC 밀리초)입니다.</param>
        /// <returns>NBIRTH 토픽으로 발행할 페이로드입니다.</returns>
        public byte[] NodeBirth(ulong timestamp)
        {
            byte[] result = SparkplugPayload.Encode(timestamp, 0, new[] {
                SparkplugMetric.Int64("bdSeq", BirthDeathSequence),
                SparkplugMetric.Boolean("Node Control/Rebirth", false) });
            sequence = 0; born = true;
            return result;
        }
        /// <summary>bdSeq만 담은 NDEATH 페이로드를 인코딩합니다. 주로 CONNECT의 Will 페이로드로 사용합니다.</summary>
        /// <returns>seq와 타임스탬프가 없는 NDEATH 페이로드입니다.</returns>
        public byte[] NodeDeath()
        { return SparkplugPayload.Encode(0, -1, new[] { SparkplugMetric.Int64("bdSeq", BirthDeathSequence) }, false); }
        /// <summary>다음 노드·장치 메시지(DBIRTH, DDATA, NDATA, DDEATH 등)를 인코딩합니다. 성공했을 때만 seq를 1 증가시키며 255 다음에는 0으로 돌아갑니다.</summary>
        /// <param name="timestamp">페이로드 타임스탬프(UTC 밀리초)입니다.</param>
        /// <param name="metrics">담을 메트릭입니다. DDEATH나 하트비트는 빈 배열입니다.</param>
        /// <param name="birth">DBIRTH이면 <see langword="true"/>입니다. 모든 메트릭에 이름과 형식이 있고 이름·별칭이 중복되지 않아야 합니다.</param>
        /// <returns>발행할 페이로드입니다.</returns>
        /// <exception cref="InvalidOperationException"><see cref="NodeBirth"/>를 먼저 호출하지 않은 경우.</exception>
        /// <exception cref="ArgumentException">BIRTH 정의가 잘못되었거나 인코딩 한도를 넘는 경우.</exception>
        public byte[] Next(ulong timestamp, SparkplugMetric[] metrics, bool birth = false)
        {
            if (!born) throw new InvalidOperationException("NBIRTH required.");
            if (birth) ValidateBirth(metrics);
            byte next = (byte)((sequence + 1) & 255);
            byte[] result = SparkplugPayload.Encode(timestamp, next, metrics);
            sequence = next;
            return result;
        }
        internal static void ValidateBirth(SparkplugMetric[] metrics)
        {
            if (metrics == null || metrics.Length == 0) throw new ArgumentException("Birth metrics required.");
            for (int i = 0; i < metrics.Length; i++)
            {
                var metric = metrics[i];
                if (metric == null || metric.Name == null || metric.DataType == SparkplugDataType.Unspecified) throw new ArgumentException("Birth definitions required.");
                for (int j = 0; j < i; j++)
                    if (metrics[j].Name == metric.Name || metrics[j].HasAlias && metric.HasAlias && metrics[j].Alias == metric.Alias)
                        throw new ArgumentException("Duplicate birth name/alias.");
            }
        }
        private static void Component(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) throw new ArgumentException("Sparkplug topic component.");
            foreach (char c in value) if (c < 32 || c > 126 || c == '/' || c == '+' || c == '#')
                throw new ArgumentException("Sparkplug topic component.");
        }
    }
}
