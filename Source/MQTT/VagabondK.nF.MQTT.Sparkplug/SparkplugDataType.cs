namespace VagabondK.nF.MQTT.Sparkplug
{
    /// <summary>이 제한된 Sparkplug B 프로파일이 지원하는 데이터 형식 번호입니다. 값은 Sparkplug B 사양의 DataType 번호와 같습니다.</summary>
    public enum SparkplugDataType
    {
        /// <summary>형식 필드를 생략한 상태입니다. 수신 측은 BIRTH에서 받은 스키마로 형식을 결정합니다.</summary>
        Unspecified = 0,
        /// <summary>부호 있는 32비트 정수입니다. 와이어에는 2의 보수 uint32로 실립니다.</summary>
        Int32 = 3,
        /// <summary>부호 있는 64비트 정수입니다. 와이어에는 2의 보수 uint64로 실립니다.</summary>
        Int64 = 4,
        /// <summary>Tahu 상호운용을 위한 bdSeq 수신 형식입니다. 장치 매핑 값 형식으로는 지원하지 않습니다.</summary>
        UInt64 = 8,
        /// <summary>유한한 단정밀도 부동소수점 값입니다(NaN/무한대 불가).</summary>
        Float = 9,
        /// <summary>유한한 배정밀도 부동소수점 값입니다(NaN/무한대 불가).</summary>
        Double = 10,
        /// <summary>불리언 값입니다.</summary>
        Boolean = 11
    }
}
