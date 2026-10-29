using System;
using System.Text;

namespace VagabondK.nF.MQTT.Sparkplug
{
    /// <summary>실시간 상태 전달에 맞춰 범위를 제한한 Sparkplug B 전송 프로파일의 스칼라 메트릭 하나입니다.</summary>
    /// <remarks>
    /// <para>송신용 메트릭은 정적 팩터리 메서드로 만듭니다. 별칭 없는 오버로드는 이름과 형식을 모두 담아 BIRTH 정의나 이름 기반 데이터에 쓰고,
    /// 별칭 오버로드는 이름을 <see langword="null"/>로, includeType을 <see langword="false"/>로 주면 DDATA용 별칭 전용 메트릭이 됩니다.</para>
    /// <para>메트릭 이름은 1..64자의 출력 가능한 ASCII 문자여야 합니다. 팩터리 메서드는 이름이 잘못되었거나 이름과 별칭이 모두 없으면
    /// <see cref="ArgumentException"/>을, 부동소수점 값이 NaN/무한대이면 <see cref="ArgumentOutOfRangeException"/>을 던집니다.</para>
    /// </remarks>
    public sealed class SparkplugMetric
    {
        /// <summary>메트릭 이름입니다. 별칭만으로 식별하는 데이터면 <see langword="null"/>입니다.</summary>
        public string Name { get; internal set; }
        /// <summary>메트릭 별칭 값입니다. 0도 유효한 별칭이므로 사용 전에 <see cref="HasAlias"/>를 확인하십시오.</summary>
        public ulong Alias { get; internal set; }
        /// <summary>별칭 필드가 있었는지(별칭 0 포함) 여부입니다.</summary>
        public bool HasAlias { get; internal set; }
        /// <summary>페이로드 타임스탬프와 별도로 지정된 메트릭 타임스탬프(UTC 밀리초)입니다. <see cref="HasTimestamp"/>가 참일 때만 의미가 있습니다.</summary>
        public ulong Timestamp { get; internal set; }
        /// <summary>메트릭별 타임스탬프 필드가 있었는지 여부입니다.</summary>
        public bool HasTimestamp { get; internal set; }
        /// <summary>Sparkplug 데이터 형식입니다. 형식 필드가 없으면 <see cref="SparkplugDataType.Unspecified"/>입니다.</summary>
        public SparkplugDataType DataType { get; internal set; }
        /// <summary>값이 실린 protobuf 필드 번호입니다. 10=uint32, 11=uint64, 12=float, 13=double, 14=bool입니다.</summary>
        public int ValueField { get; internal set; }
        /// <summary>정수/불리언 값의 원시 비트입니다. 부호 있는 값은 2의 보수이므로 Int32는 <c>(int)(uint)</c>, Int64는 <c>(long)</c>로 변환해 읽습니다.</summary>
        public ulong Integer { get; internal set; }
        /// <summary>디코딩된 유한 부동소수점 값입니다(Float/Double 메트릭).</summary>
        public double Real { get; internal set; }
        /// <summary>별칭 없이 이름과 형식을 가진 Boolean 메트릭을 만듭니다.</summary>
        /// <param name="name">메트릭 이름입니다.</param>
        /// <param name="value">값입니다.</param>
        /// <returns>새 메트릭입니다.</returns>
        public static SparkplugMetric Boolean(string name, bool value) { return Create(name, 0, false, SparkplugDataType.Boolean, 14, value ? 1UL : 0UL, 0); }
        /// <summary>별칭이 있는 Boolean 메트릭을 만듭니다.</summary>
        /// <param name="name">메트릭 이름입니다. 별칭 전용 데이터면 <see langword="null"/>입니다.</param>
        /// <param name="value">값입니다.</param>
        /// <param name="alias">BIRTH에서 선언했거나 선언할 별칭입니다.</param>
        /// <param name="includeType"><see langword="false"/>이면 형식 필드를 생략합니다(BIRTH에는 사용할 수 없음).</param>
        /// <returns>새 메트릭입니다.</returns>
        public static SparkplugMetric Boolean(string name, bool value, ulong alias, bool includeType = true)
        { return Create(name, alias, true, includeType ? SparkplugDataType.Boolean : SparkplugDataType.Unspecified, 14, value ? 1UL : 0UL, 0); }
        /// <summary>별칭 없이 이름과 형식을 가진 Int32 메트릭을 만듭니다.</summary>
        /// <param name="name">메트릭 이름입니다.</param>
        /// <param name="value">값입니다. 와이어에는 2의 보수 uint32로 실립니다.</param>
        /// <returns>새 메트릭입니다.</returns>
        public static SparkplugMetric Int32(string name, int value) { return Create(name, 0, false, SparkplugDataType.Int32, 10, unchecked((uint)value), 0); }
        /// <summary>별칭이 있는 Int32 메트릭을 만듭니다. 와이어에는 2의 보수 uint32로 실립니다.</summary>
        /// <param name="name">메트릭 이름입니다. 별칭 전용 데이터면 <see langword="null"/>입니다.</param>
        /// <param name="value">값입니다.</param>
        /// <param name="alias">BIRTH에서 선언했거나 선언할 별칭입니다.</param>
        /// <param name="includeType"><see langword="false"/>이면 형식 필드를 생략합니다(BIRTH에는 사용할 수 없음).</param>
        /// <returns>새 메트릭입니다.</returns>
        public static SparkplugMetric Int32(string name, int value, ulong alias, bool includeType = true)
        { return Create(name, alias, true, includeType ? SparkplugDataType.Int32 : SparkplugDataType.Unspecified, 10, unchecked((uint)value), 0); }
        /// <summary>별칭 없이 이름과 형식을 가진 Int64 메트릭을 만듭니다.</summary>
        /// <param name="name">메트릭 이름입니다.</param>
        /// <param name="value">값입니다. 와이어에는 2의 보수 uint64로 실립니다.</param>
        /// <returns>새 메트릭입니다.</returns>
        public static SparkplugMetric Int64(string name, long value) { return Create(name, 0, false, SparkplugDataType.Int64, 11, unchecked((ulong)value), 0); }
        /// <summary>별칭이 있는 Int64 메트릭을 만듭니다. 와이어에는 2의 보수 uint64로 실립니다.</summary>
        /// <param name="name">메트릭 이름입니다. 별칭 전용 데이터면 <see langword="null"/>입니다.</param>
        /// <param name="value">값입니다.</param>
        /// <param name="alias">BIRTH에서 선언했거나 선언할 별칭입니다.</param>
        /// <param name="includeType"><see langword="false"/>이면 형식 필드를 생략합니다(BIRTH에는 사용할 수 없음).</param>
        /// <returns>새 메트릭입니다.</returns>
        public static SparkplugMetric Int64(string name, long value, ulong alias, bool includeType = true)
        { return Create(name, alias, true, includeType ? SparkplugDataType.Int64 : SparkplugDataType.Unspecified, 11, unchecked((ulong)value), 0); }
        /// <summary>별칭 없이 이름과 형식을 가진 Float 메트릭을 만듭니다.</summary>
        /// <param name="name">메트릭 이름입니다.</param>
        /// <param name="value">유한한 값입니다.</param>
        /// <returns>새 메트릭입니다.</returns>
        public static SparkplugMetric Float(string name, float value) { return Create(name, 0, false, SparkplugDataType.Float, 12, 0, value); }
        /// <summary>별칭이 있는 Float 메트릭을 만듭니다.</summary>
        /// <param name="name">메트릭 이름입니다. 별칭 전용 데이터면 <see langword="null"/>입니다.</param>
        /// <param name="value">유한한 값입니다.</param>
        /// <param name="alias">BIRTH에서 선언했거나 선언할 별칭입니다.</param>
        /// <param name="includeType"><see langword="false"/>이면 형식 필드를 생략합니다(BIRTH에는 사용할 수 없음).</param>
        /// <returns>새 메트릭입니다.</returns>
        public static SparkplugMetric Float(string name, float value, ulong alias, bool includeType = true)
        { return Create(name, alias, true, includeType ? SparkplugDataType.Float : SparkplugDataType.Unspecified, 12, 0, value); }
        /// <summary>별칭 없이 이름과 형식을 가진 Double 메트릭을 만듭니다.</summary>
        /// <param name="name">메트릭 이름입니다.</param>
        /// <param name="value">유한한 값입니다.</param>
        /// <returns>새 메트릭입니다.</returns>
        public static SparkplugMetric Double(string name, double value) { return Create(name, 0, false, SparkplugDataType.Double, 13, 0, value); }
        /// <summary>별칭이 있는 Double 메트릭을 만듭니다.</summary>
        /// <param name="name">메트릭 이름입니다. 별칭 전용 데이터면 <see langword="null"/>입니다.</param>
        /// <param name="value">유한한 값입니다.</param>
        /// <param name="alias">BIRTH에서 선언했거나 선언할 별칭입니다.</param>
        /// <param name="includeType"><see langword="false"/>이면 형식 필드를 생략합니다(BIRTH에는 사용할 수 없음).</param>
        /// <returns>새 메트릭입니다.</returns>
        public static SparkplugMetric Double(string name, double value, ulong alias, bool includeType = true)
        { return Create(name, alias, true, includeType ? SparkplugDataType.Double : SparkplugDataType.Unspecified, 13, 0, value); }
        private static SparkplugMetric Create(string name, ulong alias, bool hasAlias, SparkplugDataType type, int field, ulong integer, double real)
        {
            if (name == null && !hasAlias) throw new ArgumentException("Metric name or alias required.");
            if (name != null) ValidateName(name);
            if (double.IsNaN(real) || double.IsInfinity(real)) throw new ArgumentOutOfRangeException("value");
            return new SparkplugMetric { Name = name, HasAlias = hasAlias, Alias = alias,
                DataType = type, ValueField = field, Integer = integer, Real = real };
        }
        internal static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64) throw new ArgumentException("Metric ASCII name length.");
            foreach (char c in name) if (c < 32 || c > 126) throw new ArgumentException("Metric ASCII name.");
        }
    }

    /// <summary>크기를 제한한 Sparkplug B 스칼라 protobuf 페이로드 코덱입니다.</summary>
    /// <remarks>지원하지 않는 필드, 중복 필드, 이력/일시(transient)/null 값은 거부합니다. 인코딩·디코딩 모두
    /// <see cref="MaximumBytes"/>와 <see cref="MaximumMetrics"/> 한도를 적용합니다.
    /// Tahu 호환성을 위한 UInt64는 별칭 없는 bdSeq(0..255)에만 허용하며 일반 장치 매핑 형식은 아닙니다.</remarks>
    public sealed partial class SparkplugPayload
    {
        /// <summary>인코딩·디코딩할 수 있는 최대 페이로드 크기(바이트)입니다.</summary>
        public const int MaximumBytes = 512;
        /// <summary>페이로드 하나에 담을 수 있는 최대 메트릭 수입니다.</summary>
        public const int MaximumMetrics = 8;
        /// <summary>페이로드 타임스탬프(UTC 밀리초)입니다. <see cref="HasTimestamp"/>가 참일 때만 의미가 있습니다.</summary>
        public ulong Timestamp { get; private set; }
        /// <summary>타임스탬프 필드가 있었는지 여부입니다.</summary>
        public bool HasTimestamp { get; private set; }
        /// <summary>공유 순서 번호 seq(0..255)입니다. 필드가 없으면 -1입니다.</summary>
        public int Sequence { get; private set; }
        /// <summary>디코딩된 메트릭 배열입니다. 이 페이로드가 소유하며, <see cref="SparkplugReceiver"/>의 스냅샷은 이 배열을 노출하지 않습니다.</summary>
        public SparkplugMetric[] Metrics { get; private set; }

        /// <summary>지원 프로파일의 페이로드를 디코딩합니다. 잘못된 입력에도 예외를 던지지 않습니다.</summary>
        /// <param name="bytes">수신한 페이로드입니다. 비었거나 <see cref="MaximumBytes"/>를 넘으면 실패합니다.</param>
        /// <param name="payload">성공하면 디코딩 결과, 실패하면 <see langword="null"/>입니다.</param>
        /// <returns>디코딩에 성공하면 <see langword="true"/>입니다.</returns>
        public static bool TryDecode(byte[] bytes, out SparkplugPayload payload)
        {
            payload = null;
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) return false;
            var reader = new Reader(bytes, 0, bytes.Length);
            var metrics = new SparkplugMetric[MaximumMetrics];
            int count = 0, sequence = -1;
            ulong timestamp = 0;
            bool timeSeen = false;
            while (!reader.End)
            {
                ulong tag, value;
                if (!reader.Varint(out tag)) return false;
                if (tag == 8)
                {
                    if (timeSeen || !reader.Varint(out timestamp)) return false;
                    timeSeen = true;
                }
                else if (tag == 24)
                {
                    if (sequence >= 0 || !reader.Varint(out value) || value > 255) return false;
                    sequence = (int)value;
                }
                else if (tag == 18)
                {
                    Reader metricReader;
                    SparkplugMetric metric;
                    if (count == MaximumMetrics || !reader.Message(out metricReader) || !DecodeMetric(metricReader, out metric)) return false;
                    metrics[count++] = metric;
                }
                else return false;
            }
            var result = new SparkplugMetric[count];
            Array.Copy(metrics, result, count);
            payload = new SparkplugPayload { Timestamp = timestamp, HasTimestamp = timeSeen, Sequence = sequence, Metrics = result };
            return true;
        }

        private static bool DecodeMetric(Reader reader, out SparkplugMetric result)
        {
            result = null;
            var metric = new SparkplugMetric();
            int seen = 0;
            while (!reader.End)
            {
                ulong tag, value;
                if (!reader.Varint(out tag)) return false;
                if (tag == 10)
                {
                    if ((seen & 1) != 0 || !reader.Name(out string name)) return false;
                    metric.Name = name; seen |= 1;
                }
                else if (tag == 16)
                {
                    if ((seen & 2) != 0 || !reader.Varint(out value)) return false;
                    metric.Alias = value; metric.HasAlias = true; seen |= 2;
                }
                else if (tag == 24)
                {
                    if ((seen & 4) != 0 || !reader.Varint(out value)) return false;
                    metric.Timestamp = value; metric.HasTimestamp = true;
                    seen |= 4;
                }
                else if (tag == 32)
                {
                    if ((seen & 8) != 0 || !reader.Varint(out value) || value > 34 || value == 0) return false;
                    metric.DataType = (SparkplugDataType)value; seen |= 8;
                }
                else if (tag == 40 || tag == 48 || tag == 56)
                {
                    int bit = tag == 40 ? 16 : tag == 48 ? 32 : 64;
                    // 이 실시간 상태 프로파일은 이력·일시(transient)·null 값을 허용하지 않습니다.
                    if ((seen & bit) != 0 || !reader.Varint(out value) || value != 0) return false;
                    seen |= bit;
                }
                else if (tag == 80 || tag == 88 || tag == 112)
                {
                    if (metric.ValueField != 0 || !reader.Varint(out value) ||
                        (tag == 80 && value > uint.MaxValue) || (tag == 112 && value > 1)) return false;
                    metric.ValueField = (int)(tag >> 3); metric.Integer = value;
                }
                else if (tag == 101 || tag == 105)
                {
                    if (metric.ValueField != 0 || !reader.Real(tag == 101, out double real)) return false;
                    metric.ValueField = (int)(tag >> 3); metric.Real = real;
                }
                else return false;
            }
            if (metric.Name == null && !metric.HasAlias || metric.ValueField == 0 || !ValidType(metric)) return false;
            result = metric;
            return true;
        }
        internal static bool ValidType(SparkplugMetric metric)
        {
            if (metric.DataType == SparkplugDataType.Unspecified) return true;
            return metric.DataType == SparkplugDataType.Int32 && metric.ValueField == 10 || metric.DataType == SparkplugDataType.Int64 && metric.ValueField == 11 ||
                metric.DataType == SparkplugDataType.UInt64 && metric.Name == "bdSeq" && !metric.HasAlias &&
                    metric.ValueField == 11 && metric.Integer <= 255 ||
                metric.DataType == SparkplugDataType.Float && metric.ValueField == 12 || metric.DataType == SparkplugDataType.Double && metric.ValueField == 13 ||
                metric.DataType == SparkplugDataType.Boolean && metric.ValueField == 14;
        }

        /// <summary>Edge Node에 상태 재선언(Rebirth)을 요청하는 <c>Node Control/Rebirth=true</c> NCMD 페이로드를 인코딩합니다.</summary>
        /// <remarks>명령은 Edge의 공유 seq를 쓰지 않으므로 seq와 타임스탬프 필드를 생략합니다.</remarks>
        /// <returns>NCMD 토픽으로 발행할 페이로드입니다.</returns>
        public static byte[] RebirthRequest()
        {
            return Encode(0, -1, new[] { SparkplugMetric.Boolean("Node Control/Rebirth", true) }, false);
        }

        private sealed class Reader
        {
            private readonly byte[] bytes;
            private readonly int end;
            private int position;
            internal Reader(byte[] bytes, int start, int end) { this.bytes = bytes; position = start; this.end = end; }
            internal bool End { get { return position == end; } }
            internal bool Varint(out ulong value)
            {
                value = 0;
                for (int i = 0; i < 10; i++)
                {
                    if (position == end) return false;
                    byte next = bytes[position++];
                    if (i == 9 && next > 1) return false;
                    value |= (ulong)(next & 127) << (7 * i);
                    if ((next & 128) == 0) return true;
                }
                return false;
            }
            internal bool Message(out Reader reader)
            {
                reader = null;
                if (!Varint(out ulong length) || length > (ulong)(end - position)) return false;
                reader = new Reader(bytes, position, position + (int)length);
                position += (int)length; return true;
            }
            internal bool Name(out string name)
            {
                name = null;
                if (!Varint(out ulong length) || length < 1 || length > 64 || length > (ulong)(end - position)) return false;
                // 이 프로파일은 정확한 ASCII 식별자만 허용하며 잘못된 UTF-8을 대체 문자로 복원하지 않습니다.
                for (int i = 0; i < (int)length; i++) if (bytes[position + i] < 32 || bytes[position + i] > 126) return false;
                name = Encoding.UTF8.GetString(bytes, position, (int)length); position += (int)length;
                return true;
            }
            internal bool Real(bool single, out double value)
            {
                value = 0;
                int length = single ? 4 : 8;
                if (end - position < length) return false;
                // nanoFramework는 32비트 대상에서도 Double 비트 변환을 제공합니다.
                long bits = 0;
                for (int i = 0; i < length; i++) bits |= (long)bytes[position++] << (8 * i);
                if (single)
                {
                    value = VagabondK.nF.Internal.RealBits.Decode((uint)bits);
                }
                else value = BitConverter.Int64BitsToDouble(bits);
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
        }
    }
}
