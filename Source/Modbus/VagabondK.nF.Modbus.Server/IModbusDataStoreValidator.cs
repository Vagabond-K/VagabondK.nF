namespace VagabondK.nF.Modbus.Server
{
    /// <summary>고정 주소 저장소의 예상 가능한 범위 오류를 예외 없이 검사하는 선택형 계약입니다.</summary>
    /// <remarks>
    /// 검사는 데이터를 읽거나 변경하지 않아야 하며, 호출 사이에 주소 정책이 바뀌지 않아야 합니다.
    /// Server는 false를 IllegalDataAddress 응답으로 변환합니다. 저장소 자체의 인수 검사와 동기화는 여전히 필요합니다.
    /// </remarks>
    public interface IModbusDataStoreValidator
    {
        /// <summary>지정한 기능 코드에 대해 요청 주소 범위 전체를 허용하는지 확인합니다.</summary>
        /// <param name="function">읽기 또는 쓰기 Function Code입니다.</param>
        /// <param name="address">0 기반 시작 주소입니다.</param>
        /// <param name="count">Bit 또는 Register 수입니다. 단일 쓰기는 1입니다.</param>
        /// <returns>전체 범위를 허용하면 true이며, 지원하지 않는 기능 코드이거나 범위가 유효하지 않으면 false입니다.</returns>
        bool IsValidRange(FunctionCode function, ushort address, ushort count);
    }
}
