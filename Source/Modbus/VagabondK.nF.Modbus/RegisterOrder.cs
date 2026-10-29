namespace VagabondK.nF.Modbus
{
    /// <summary>각 16비트 레지스터 내부의 논리적인 바이트 순서입니다.</summary>
    /// <remarks>Modbus 프레임의 바이트 순서를 바꾸지 않으며, 이미 수신한 ushort 값의 해석에만 적용합니다.</remarks>
    public enum RegisterByteOrder
    {
        /// <summary>상위 바이트를 먼저 해석합니다. 값 0x4142는 ASCII "AB"입니다.</summary>
        HighByteFirst,

        /// <summary>하위 바이트를 먼저 해석합니다. 값 0x4241은 ASCII "AB"입니다.</summary>
        LowByteFirst
    }

    /// <summary>여러 레지스터로 표현하는 숫자의 16비트 워드 순서입니다.</summary>
    public enum RegisterWordOrder
    {
        /// <summary>가장 높은 자리의 워드가 낮은 배열 인덱스에 위치합니다.</summary>
        HighWordFirst,

        /// <summary>가장 낮은 자리의 워드가 낮은 배열 인덱스에 위치합니다. 64비트에서는 네 워드 전체의 순서를 뒤집습니다.</summary>
        LowWordFirst
    }
}
