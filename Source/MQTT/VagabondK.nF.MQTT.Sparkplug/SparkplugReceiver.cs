using System;

namespace VagabondK.nF.MQTT.Sparkplug
{
    /// <summary><see cref="SparkplugReceiver.Apply(string, byte[], bool, long)"/>의 처리 결과입니다.</summary>
    /// <remarks>유효하지 않은 갱신으로 스냅샷의 일부만 바뀌지는 않습니다.</remarks>
    public enum SparkplugResult
    {
        /// <summary>유효한 DBIRTH/DDATA 값이나 빈 NDATA 하트비트를 반영했습니다.</summary>
        Applied,
        /// <summary>NBIRTH를 수락했습니다. 값을 받으려면 DBIRTH가 더 필요합니다.</summary>
        NodeBirth,
        /// <summary>DDEATH를 받아 DBIRTH 전까지 장치를 사용할 수 없게 했습니다.</summary>
        DeviceDeath,
        /// <summary>현재 세션의 bdSeq와 일치하는 NDEATH를 받아 노드와 장치를 무효화했습니다. Rebirth가 필요합니다.</summary>
        NodeDeath,
        /// <summary>설정한 노드·장치와 관계없는 토픽이라 무시했습니다.</summary>
        Ignored,
        /// <summary>보관(retained) 메시지 재전송이라 상태를 바꾸지 않고 거부했습니다.</summary>
        Retained,
        /// <summary>잘못되었거나 지원하지 않는 페이로드입니다. 장치를 사용 불가 상태로 표시하고 Rebirth를 요구합니다.</summary>
        Malformed,
        /// <summary>BIRTH를 받기 전이라 데이터를 받아들일 수 없습니다.</summary>
        NeedBirth,
        /// <summary>seq 누락·역순·중복이 감지되어 Rebirth가 필요합니다.</summary>
        SequenceGap,
        /// <summary>다른(이전) 세션의 NDEATH라 무시했습니다.</summary>
        StaleDeath,
        /// <summary>로컬 MQTT 연결이 끊긴 상태라 처리하지 않았습니다(<see cref="SparkplugReceiver.SetConnected(bool)"/>).</summary>
        Disconnected
    }

    /// <summary>설정한 스키마 순서대로 정리한 마지막 수신 값의 불변 스냅샷입니다.</summary>
    /// <remarks>장치를 사용할 수 없게 된 뒤에도 진단용으로 마지막 값을 유지합니다. 값의 유효성은 <see cref="SparkplugReceiver.IsAvailable(long)"/>로 확인하십시오.</remarks>
    public sealed class SparkplugSnapshot
    {
        private readonly SparkplugMetric[] metrics;
        /// <summary>이 스냅샷을 만든 마지막 DBIRTH/DDATA의 seq입니다.</summary>
        public int Sequence { get; private set; }
        /// <summary>스키마에 설정한 메트릭 수입니다.</summary>
        public int Count { get { return metrics.Length; } }
        /// <summary>스키마 인덱스 위치의 마지막 메트릭입니다.</summary>
        /// <remarks>DDATA는 일부 메트릭만 담을 수 있으므로 값은 각자 마지막으로 수신한 메시지의 것입니다.
        /// <see cref="SparkplugMetric.Integer"/>/<see cref="SparkplugMetric.Real"/>을 읽기 전에 설정한 형식을 확인하십시오.</remarks>
        /// <param name="index">0부터 <see cref="Count"/>-1까지의 스키마 인덱스입니다.</param>
        public SparkplugMetric this[int index] { get { return metrics[index]; } }
        internal SparkplugSnapshot(SparkplugMetric[] metrics, int sequence)
        { this.metrics = metrics; Sequence = sequence; }
    }

    /// <summary>원격 Edge Node의 장치 하나에서 오는 Sparkplug B 실시간 상태를 검증하며, 동시 접근을 지원하는 수신기입니다.</summary>
    /// <remarks>
    /// <para>설정한 스키마(이름·형식), BIRTH에서 선언된 별칭, bdSeq 세션, seq 연속성, 수신 값의 유효 시간을 검사합니다.
    /// 순서가 맞춰지기를 기다리지 않으므로 seq가 하나라도 어긋나면 Rebirth를 요구합니다.</para>
    /// <para>사용 흐름: MQTT 연결 후 <see cref="SetConnected(bool)"/>(true) → 노드/장치 토픽 구독 → 수신마다 <see cref="Apply"/> →
    /// <see cref="NeedsRebirth"/>이면 <see cref="CommandTopic"/>으로 <see cref="SparkplugPayload.RebirthRequest"/> 발행.</para>
    /// </remarks>
    public sealed class SparkplugReceiver
    {
        private readonly SparkplugSession identity;
        private readonly string device;
        private readonly string[] names;
        private readonly SparkplugDataType[] types;
        private readonly long maximumAgeMilliseconds;
        private readonly string nodeBirthTopic, nodeDataTopic, nodeDeathTopic, deviceBirthTopic, deviceDataTopic, deviceDeathTopic, commandTopic;
        /// <summary>수신할 노드·장치와 메트릭 스키마를 설정합니다. 배열은 복사됩니다.</summary>
        /// <param name="group">Sparkplug 그룹 ID입니다.</param>
        /// <param name="node">Edge Node ID입니다.</param>
        /// <param name="device">장치 ID입니다.</param>
        /// <param name="names">메트릭 이름 1..8개입니다. 중복될 수 없습니다.</param>
        /// <param name="types">이름별 형식입니다. Int32/Int64/Float/Double/Boolean만 지원합니다.</param>
        /// <param name="maximumAgeMilliseconds">마지막 값 수신 후 사용 가능으로 보는 최대 경과 시간(밀리초)입니다. 0보다 커야 합니다.</param>
        /// <exception cref="ArgumentException">토픽 구성 요소·이름·형식·경과 시간이 잘못되었거나 배열 길이가 다른 경우.</exception>
        public SparkplugReceiver(string group, string node, string device, string[] names, SparkplugDataType[] types, long maximumAgeMilliseconds)
        {
            identity = new SparkplugSession(group, node, 0);
            identity.Topic("DBIRTH", device);
            if (names == null || types == null || names.Length == 0 || names.Length > SparkplugPayload.MaximumMetrics ||
                names.Length != types.Length || maximumAgeMilliseconds <= 0) throw new ArgumentException("Receiver schema/age.");
            this.names = new string[names.Length]; this.types = new SparkplugDataType[types.Length];
            for (int i = 0; i < names.Length; i++)
            {
                SparkplugMetric.ValidateName(names[i]);
                if (types[i] != SparkplugDataType.Int32 && types[i] != SparkplugDataType.Int64 && types[i] != SparkplugDataType.Float && types[i] != SparkplugDataType.Double && types[i] != SparkplugDataType.Boolean)
                    throw new ArgumentException("Unsupported receiver datatype.");
                for (int j = 0; j < i; j++) if (names[j] == names[i]) throw new ArgumentException("Duplicate receiver name.");
                this.names[i] = names[i]; this.types[i] = types[i];
            }
            this.device = device; this.maximumAgeMilliseconds = maximumAgeMilliseconds;
            aliases = new ulong[names.Length]; hasAlias = new bool[names.Length];
            nodeBirthTopic = Topic("NBIRTH"); nodeDataTopic = Topic("NDATA"); nodeDeathTopic = Topic("NDEATH");
            deviceBirthTopic = Topic("DBIRTH"); deviceDataTopic = Topic("DDATA"); deviceDeathTopic = Topic("DDEATH");
            commandTopic = identity.Topic("NCMD");
        }
        /// <summary>설정한 원격 노드/장치의 Sparkplug 토픽을 만듭니다. 구독 토픽을 구성할 때 사용합니다.</summary>
        /// <param name="verb">NBIRTH, NDATA, NDEATH, NCMD 또는 DBIRTH, DDATA, DDEATH입니다. 장치 메시지에는 설정한 장치 ID가 붙습니다.</param>
        /// <returns><c>spBv1.0/{group}/{verb}/{node}[/{device}]</c> 형식의 토픽입니다.</returns>
        /// <exception cref="ArgumentException">지원하지 않는 메시지 종류인 경우(DCMD 포함).</exception>
        public string Topic(string verb)
        { return identity.Topic(verb, verb == "DBIRTH" || verb == "DDATA" || verb == "DDEATH" ? device : null); }
        /// <summary>상태 재선언(Rebirth) 요청을 보낼 NCMD 토픽입니다.</summary>
        public string CommandTopic { get { return commandTopic; } }
        private readonly object gate = new object();
        private bool connected, nodeBorn, deviceBorn, needRebirth;
        private int bdSequence, sequence;
        private long receivedAt;
        private readonly ulong[] aliases;
        private readonly bool[] hasAlias;
        private SparkplugSnapshot model;

        /// <summary>마지막 불변 스냅샷을 반환합니다.</summary>
        /// <remarks>값이 지금 유효한지는 <see cref="IsAvailable(long)"/>로 따로 확인해야 합니다.</remarks>
        /// <returns>마지막 스냅샷입니다. 첫 DBIRTH를 받기 전에는 <see langword="null"/>입니다.</returns>
        public SparkplugSnapshot Read() { lock (gate) return model; }
        /// <summary>연결된 상태에서 앱이 Rebirth 요청(NCMD)을 보내야 하는지 여부입니다.</summary>
        public bool NeedsRebirth { get { lock (gate) return connected && needRebirth; } }
        /// <summary>로컬 MQTT 연결 상태를 갱신합니다. 호출할 때마다 BIRTH 상태를 무효화하며, 진단용 마지막 값은 유지합니다.</summary>
        /// <param name="value">연결되었으면 <see langword="true"/>입니다. 연결 직후에는 Rebirth가 필요한 상태가 됩니다.</param>
        public void SetConnected(bool value)
        {
            lock (gate) { connected = value; nodeBorn = deviceBorn = false; needRebirth = value; }
        }
        /// <summary>BIRTH·세션이 유효하고 마지막 값 수신 후 설정한 최대 경과 시간이 지나지 않았는지 확인합니다.</summary>
        /// <param name="now"><see cref="Apply"/>에 넘긴 것과 같은 기준의 로컬 단조 시계 값(밀리초)입니다.</param>
        /// <returns>마지막 스냅샷이 수신 상태와 유효 시간 조건을 만족하면 <see langword="true"/>입니다.</returns>
        public bool IsAvailable(long now)
        { lock (gate) return connected && nodeBorn && deviceBorn && !needRebirth && now >= receivedAt && now - receivedAt < maximumAgeMilliseconds; }
        /// <summary>원격 페이로드 하나를 원자적으로 검증하고 상태에 반영합니다.</summary>
        /// <param name="topic">수신 토픽입니다.</param>
        /// <param name="bytes">수신 페이로드입니다.</param>
        /// <param name="retained">브로커의 보관 메시지 플래그입니다. 참이면 반영하지 않습니다.</param>
        /// <param name="now">로컬 단조 시계 값(밀리초, 0 이상)입니다. 수신 값의 유효 시간 계산에 쓰입니다.</param>
        /// <returns>처리 결과입니다.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="now"/>가 음수인 경우.</exception>
        public SparkplugResult Apply(string topic, byte[] bytes, bool retained, long now)
        {
            if (now < 0) throw new ArgumentOutOfRangeException("now");
            string verb = topic == nodeBirthTopic ? "NBIRTH" : topic == deviceBirthTopic ? "DBIRTH" :
                topic == deviceDataTopic ? "DDATA" : topic == nodeDataTopic ? "NDATA" :
                topic == deviceDeathTopic ? "DDEATH" : topic == nodeDeathTopic ? "NDEATH" : null;
            if (verb == null) return SparkplugResult.Ignored;
            if (retained) return SparkplugResult.Retained;
            lock (gate)
            {
                if (!connected) return SparkplugResult.Disconnected;
                SparkplugPayload payload;
                if (!SparkplugPayload.TryDecode(bytes, out payload)) return Invalid(SparkplugResult.Malformed);
                if (verb == "NDEATH")
                {
                    int death = BirthDeath(payload);
                    if (death < 0 || payload.Sequence >= 0 || payload.Metrics.Length != 1 || payload.Metrics[0].HasAlias)
                        return Invalid(SparkplugResult.Malformed);
                    if (!nodeBorn || death != bdSequence) return SparkplugResult.StaleDeath;
                    nodeBorn = deviceBorn = false; needRebirth = true;
                    return SparkplugResult.NodeDeath;
                }
                if (!payload.HasTimestamp || payload.Sequence < 0) return Invalid(SparkplugResult.Malformed);
                if (verb == "NBIRTH")
                {
                    int birth = BirthDeath(payload);
                    if (birth < 0 || payload.Sequence != 0 || payload.Metrics.Length != 2) return Invalid(SparkplugResult.Malformed);
                    SparkplugMetric control = payload.Metrics[0].Name == "bdSeq" ? payload.Metrics[1] : payload.Metrics[0];
                    if (control.Name != "Node Control/Rebirth" || control.DataType != SparkplugDataType.Boolean || control.ValueField != 14 || control.Integer != 0 ||
                        payload.Metrics[0].HasAlias || payload.Metrics[1].HasAlias) return Invalid(SparkplugResult.Malformed);
                    bdSequence = birth; sequence = 0; nodeBorn = true; deviceBorn = false; needRebirth = false;
                    return SparkplugResult.NodeBirth;
                }
                if (!nodeBorn || needRebirth) return SparkplugResult.NeedBirth;
                if (payload.Sequence != ((sequence + 1) & 255)) return Invalid(SparkplugResult.SequenceGap);
                if (verb == "DDEATH")
                {
                    if (payload.Metrics.Length != 0) return Invalid(SparkplugResult.Malformed);
                    sequence = payload.Sequence; deviceBorn = false;
                    return SparkplugResult.DeviceDeath;
                }
                if (verb == "NDATA")
                {
                    // 이 프로파일은 노드 메트릭 갱신을 지원하지 않지만 빈 하트비트도 seq를 소비합니다.
                    if (payload.Metrics.Length != 0) return Invalid(SparkplugResult.Malformed);
                    sequence = payload.Sequence;
                    return SparkplugResult.Applied;
                }
                bool birthMessage = verb == "DBIRTH";
                if (!birthMessage && !deviceBorn) return SparkplugResult.NeedBirth;
                if (birthMessage && payload.Metrics.Length != names.Length || !birthMessage && payload.Metrics.Length == 0)
                    return Invalid(SparkplugResult.Malformed);
                var newAliases = birthMessage ? new ulong[names.Length] : null;
                var newHasAlias = birthMessage ? new bool[names.Length] : null;
                var values = new SparkplugMetric[names.Length];
                if (model != null) for (int i = 0; i < values.Length; i++) values[i] = model[i];
                int seen = 0;
                foreach (SparkplugMetric metric in payload.Metrics)
                {
                    int index = -1;
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (metric.Name == names[i]) index = i;
                        if (!birthMessage && metric.Name == null && metric.HasAlias && hasAlias[i] && metric.Alias == aliases[i]) index = i;
                    }
                    if (index < 0 || (seen & (1 << index)) != 0 || birthMessage && metric.Name == null ||
                        metric.DataType != types[index] && (birthMessage || metric.DataType != SparkplugDataType.Unspecified) ||
                        !birthMessage && metric.HasAlias && (!hasAlias[index] || metric.Alias != aliases[index]))
                        return Invalid(SparkplugResult.Malformed);
                    if (birthMessage)
                    {
                        for (int i = 0; i < names.Length; i++)
                            if (newHasAlias[i] && metric.HasAlias && newAliases[i] == metric.Alias) return Invalid(SparkplugResult.Malformed);
                        newAliases[index] = metric.Alias; newHasAlias[index] = metric.HasAlias;
                    }
                    seen |= 1 << index;
                    int expectedField = types[index] == SparkplugDataType.Boolean ? 14 : types[index] == SparkplugDataType.Int32 ? 10 : types[index] == SparkplugDataType.Int64 ? 11 : types[index] == SparkplugDataType.Float ? 12 : 13;
                    if (metric.ValueField != expectedField) return Invalid(SparkplugResult.Malformed);
                    values[index] = metric;
                }
                if (birthMessage)
                { Array.Copy(newAliases, aliases, names.Length); Array.Copy(newHasAlias, hasAlias, names.Length); }
                model = new SparkplugSnapshot(values, payload.Sequence);
                sequence = payload.Sequence; deviceBorn = true; receivedAt = now;
                return SparkplugResult.Applied;
            }
        }
        private SparkplugResult Invalid(SparkplugResult result)
        { deviceBorn = false; needRebirth = true; return result; }
        private static int BirthDeath(SparkplugPayload payload)
        {
            int result = -1;
            foreach (SparkplugMetric metric in payload.Metrics)
                if (metric.Name == "bdSeq")
                {
                    if (result != -1 || (metric.DataType != SparkplugDataType.Int64 && metric.DataType != SparkplugDataType.UInt64) ||
                        metric.ValueField != 11 || metric.Integer > 255) return -1;
                    result = (int)metric.Integer;
                }
            return result;
        }
    }
}
