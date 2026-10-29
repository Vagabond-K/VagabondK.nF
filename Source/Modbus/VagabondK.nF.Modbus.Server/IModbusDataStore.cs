namespace VagabondK.nF.Modbus.Server
{
    /// <summary>Modbus Server의 Coil, Discrete Input 및 Register 저장소 계약입니다.</summary>
    /// <remarks>
    /// 구현은 데이터를 변경하기 전에 요청 범위 전체를 검증하고 애플리케이션의 갱신 작업과 동기화해야 합니다.
    /// 요청 오류를 ModbusException으로 전달하면 Server가 예외 응답으로 변환합니다.
    /// 주소 정책이 고정된 저장소는 선택적으로 IModbusDataStoreValidator를 구현하여 예상 가능한 범위 오류에 예외를 발생시키지 않고 응답할 수 있습니다.
    /// 전달받은 배열은 Server가 재사용하므로 호출이 끝난 후 보관하지 마십시오.
    /// </remarks>
    public interface IModbusDataStore
    {
        /// <summary>지정한 비트 영역의 값을 읽어 결과 배열의 인덱스 0부터 저장합니다.</summary>
        /// <param name="discreteInputs">Discrete Input이면 true, Coil이면 false입니다.</param>
        /// <param name="address">0 기반 시작 주소입니다.</param>
        /// <param name="count">읽을 항목 수입니다.</param>
        /// <param name="values">최소 count개의 공간을 가진 결과 배열입니다.</param>
        void ReadBits(bool discreteInputs, ushort address, ushort count, bool[] values);

        /// <summary>지정한 레지스터 영역의 값을 읽어 결과 배열의 인덱스 0부터 저장합니다.</summary>
        /// <param name="inputRegisters">Input Register이면 true, Holding Register이면 false입니다.</param>
        /// <param name="address">0 기반 시작 주소입니다.</param>
        /// <param name="count">읽을 Register 수입니다.</param>
        /// <param name="values">최소 count개의 공간을 가진 결과 배열입니다.</param>
        void ReadRegisters(bool inputRegisters, ushort address, ushort count, ushort[] values);

        /// <summary>배열의 인덱스 0부터 지정한 수량의 Coil을 씁니다.</summary>
        /// <param name="address">0 기반 시작 Coil 주소입니다.</param>
        /// <param name="count">쓸 Coil 수입니다.</param>
        /// <param name="values">최소 count개의 값을 가진 배열입니다.</param>
        void WriteCoils(ushort address, ushort count, bool[] values);

        /// <summary>배열의 인덱스 0부터 지정한 수량의 Holding Register를 씁니다.</summary>
        /// <param name="address">0 기반 시작 Register 주소입니다.</param>
        /// <param name="count">쓸 Register 수입니다.</param>
        /// <param name="values">최소 count개의 값을 가진 배열입니다.</param>
        void WriteHoldingRegisters(ushort address, ushort count, ushort[] values);
    }
}
