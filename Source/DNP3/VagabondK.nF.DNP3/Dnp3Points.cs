using System;

namespace VagabondK.nF.DNP3
{
    /// <summary>지원하는 DNP3 Point 종류입니다.</summary>
    public enum Dnp3PointType
    {
        /// <summary>1비트 Binary Input입니다.</summary>
        Binary,

        /// <summary>2비트 Binary Input입니다.</summary>
        DoubleBitBinary,

        /// <summary>누적 Counter입니다.</summary>
        Counter,

        /// <summary>동결된 Counter입니다.</summary>
        FrozenCounter,

        /// <summary>Analog Input입니다.</summary>
        Analog,

        /// <summary>Binary Output의 현재 상태입니다.</summary>
        BinaryOutputStatus,

        /// <summary>Analog Output의 현재 상태입니다.</summary>
        AnalogOutputStatus
    }

    /// <summary>Point 값, 품질 Flags 및 선택적 DNP3 Timestamp를 보관합니다.</summary>
    public struct Dnp3Value
    {
        /// <summary>Binary/Counter의 정수 값 또는 Analog Double 값의 비트 표현입니다.</summary>
        public ulong Raw { get; private set; }

        /// <summary>Point 종류에 따른 DNP3 품질 Flags입니다. 기본값 1은 Online입니다.</summary>
        public byte Flags { get; private set; }

        /// <summary>1970-01-01 UTC부터의 경과 밀리초입니다. 시각 없는 Variation의 디코딩 값은 0입니다.</summary>
        public long Timestamp { get; private set; }

        /// <summary>정수 또는 이미 인코딩된 비트 표현으로 Point 값을 생성합니다.</summary>
        /// <param name="raw">원시 값입니다. Point 종류별 범위는 Point 정의 또는 Database 갱신 시 검사합니다.</param>
        /// <param name="flags">DNP3 품질 Flags입니다.</param>
        /// <param name="timestamp">0~0xFFFFFFFFFFFF 범위의 UTC 경과 밀리초입니다.</param>
        /// <returns>지정된 값과 메타데이터입니다.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Timestamp가 부호 없는 48비트 정수 범위를 벗어납니다.</exception>
        public static Dnp3Value Bits(ulong raw, byte flags = 1, long timestamp = 0)
        {
            if (timestamp < 0 || timestamp > 0xffffffffffffL)
                throw new ArgumentOutOfRangeException("timestamp");
            return new Dnp3Value { Raw = raw, Flags = flags, Timestamp = timestamp };
        }

        /// <summary>유한한 Double 값으로 Analog Point 값을 생성합니다.</summary>
        /// <param name="value">NaN 또는 Infinity가 아닌 Analog 값입니다.</param>
        /// <param name="flags">DNP3 품질 Flags입니다.</param>
        /// <param name="timestamp">0~0xFFFFFFFFFFFF 범위의 UTC 경과 밀리초입니다.</param>
        /// <returns>Double 비트 표현으로 저장된 Point 값입니다.</returns>
        /// <exception cref="ArgumentException">값이 NaN 또는 Infinity입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Timestamp가 부호 없는 48비트 정수 범위를 벗어납니다.</exception>
        public static Dnp3Value Analog(double value, byte flags = 1, long timestamp = 0)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("Finite DNP3 analog value required.");
            return Bits(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)), flags, timestamp);
        }

        /// <summary>원시 비트를 Double로 해석합니다.</summary>
        /// <returns>Raw의 비트 패턴에 대응하는 Double 값입니다. 정수에서의 수치 변환은 수행하지 않습니다.</returns>
        public double AsAnalog()
        {
            return BitConverter.Int64BitsToDouble(unchecked((long)Raw));
        }
    }

    /// <summary>Outstation Point의 종류, 인덱스, 초기값 및 Event 생성 정책을 정의합니다.</summary>
    public sealed class Dnp3PointDefinition
    {
        /// <summary>Point 종류입니다.</summary>
        public Dnp3PointType Type { get; private set; }

        /// <summary>같은 종류 내에서 식별하는 16비트 Point 인덱스입니다.</summary>
        public ushort Index { get; private set; }

        /// <summary>Event Class입니다. 0이면 Event를 생성하지 않고 1~3이면 해당 Class를 사용합니다.</summary>
        public byte EventClass { get; private set; }

        /// <summary>Analog 값의 이벤트 발생 기준인 절대 변화량입니다. 값과 같은 단위를 사용하며, 실제 변화량이 이 기준을 초과하면 이벤트가 발생합니다.</summary>
        public double Deadband { get; private set; }

        /// <summary>Static 응답에 사용할 Object Variation입니다.</summary>
        public byte StaticVariation { get; private set; }

        /// <summary>Event 응답에 사용할 Object Variation입니다.</summary>
        public byte EventVariation { get; private set; }

        /// <summary>Database 생성 시 사용할 초기값입니다.</summary>
        public Dnp3Value Initial { get; private set; }

        /// <summary>지원하는 Variation과 값 범위를 검사하여 Point 정의를 생성합니다.</summary>
        /// <param name="type">지원하는 Point 종류입니다.</param>
        /// <param name="index">Point 인덱스입니다.</param>
        /// <param name="initial">초기값입니다. Binary는 0~1, DoubleBitBinary는 0~3,
        /// Counter는 UInt32 범위이며 Analog는 유한한 Double이어야 합니다.</param>
        /// <param name="eventClass">0~3 범위의 Event Class입니다. 0이면 Event 생성이 비활성화됩니다.</param>
        /// <param name="deadband">유한한 0 이상의 Analog Deadband입니다. 다른 종류에서는 0이어야 합니다.</param>
        /// <param name="staticVariation">Static Variation입니다. 0이면 종류별 기본 Variation을 사용합니다.</param>
        /// <param name="eventVariation">Event Variation입니다. 0이면 종류별 기본 Variation을 사용합니다.</param>
        /// <remarks>기본 Variation은 Binary 계열 2/2, Counter 계열 1/5, Analog 5/7,
        /// AnalogOutputStatus 3/7입니다(Static/Event 순서). 지원하는 Static/Event Variation은
        /// Binary 계열 2 및 1~2, Counter 계열 1~2 및 1,2,5,6,
        /// Analog 1,2,5,6 및 1~8, AnalogOutputStatus 1~4 및 1~8입니다.
        /// 전송 형식보다 큰 Analog 값은 전송 시 포화되며 품질 Over-range 비트가 설정될 수 있습니다.</remarks>
        /// <exception cref="ArgumentException">종류, Class, Deadband, Variation 또는 초기값이 유효하지 않습니다.</exception>
        public Dnp3PointDefinition(Dnp3PointType type, ushort index, Dnp3Value initial,
            byte eventClass = 1, double deadband = 0, byte staticVariation = 0, byte eventVariation = 0)
        {
            if (type < Dnp3PointType.Binary || type > Dnp3PointType.AnalogOutputStatus || eventClass > 3 ||
                double.IsNaN(deadband) || double.IsInfinity(deadband) || deadband < 0 ||
                deadband != 0 && type != Dnp3PointType.Analog && type != Dnp3PointType.AnalogOutputStatus)
                throw new ArgumentException("DNP3 point definition; deadband applies only to analog values.");
            Type = type;
            Index = index;
            Initial = initial;
            EventClass = eventClass;
            Deadband = deadband;
            StaticVariation = staticVariation == 0 ? Dnp3Objects.DefaultVariation(type, false) : staticVariation;
            EventVariation = eventVariation == 0 ? Dnp3Objects.DefaultVariation(type, true) : eventVariation;
            if (!Dnp3Objects.Supported(type, false, StaticVariation) || !Dnp3Objects.Supported(type, true, EventVariation))
                throw new ArgumentException("Unsupported DNP3 object variation.");
            Validate(initial);
        }

        internal void Validate(Dnp3Value value)
        {
            switch (Type)
            {
                case Dnp3PointType.Binary:
                case Dnp3PointType.BinaryOutputStatus:
                    if (value.Raw > 1) throw new ArgumentException("DNP3 point value range.");
                    break;
                case Dnp3PointType.DoubleBitBinary:
                    if (value.Raw > 3) throw new ArgumentException("DNP3 point value range.");
                    break;
                case Dnp3PointType.Counter:
                case Dnp3PointType.FrozenCounter:
                    if (value.Raw > uint.MaxValue) throw new ArgumentException("DNP3 point value range.");
                    break;
                case Dnp3PointType.Analog:
                case Dnp3PointType.AnalogOutputStatus:
                    double number = value.AsAnalog();
                    if (double.IsNaN(number) || double.IsInfinity(number))
                        throw new ArgumentException("Finite DNP3 analog value required.");
                    break;
            }
        }
    }

    internal struct Dnp3Record
    {
        internal Dnp3PointDefinition Point;
        internal Dnp3Value Value;
        internal long EventId;
        internal byte Variation;
    }
}
