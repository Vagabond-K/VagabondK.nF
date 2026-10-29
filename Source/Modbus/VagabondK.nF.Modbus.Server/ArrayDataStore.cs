using System;

namespace VagabondK.nF.Modbus.Server
{
    /// <summary>4개의 독립 배열로 구성한 0 기반 Modbus 데이터 저장소입니다.</summary>
    /// <remarks>각 읽기와 쓰기는 내부 잠금으로 보호합니다. 여러 호출을 묶은 트랜잭션은 제공하지 않습니다.</remarks>
    public sealed class ArrayDataStore : IModbusDataStore
    {
        private readonly bool[] _coils;
        private readonly bool[] _inputs;
        private readonly ushort[] _holding;
        private readonly ushort[] _registers;
        private readonly object _sync = new object();

        /// <summary>각 영역을 지정한 크기로 할당하고 false 또는 0으로 초기화합니다.</summary>
        /// <param name="coilCount">Coil 수이며 범위는 0~65536입니다.</param>
        /// <param name="discreteInputCount">Discrete Input 수이며 범위는 0~65536입니다.</param>
        /// <param name="holdingRegisterCount">Holding Register 수이며 범위는 0~65536입니다.</param>
        /// <param name="inputRegisterCount">Input Register 수이며 범위는 0~65536입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">영역 크기가 허용 범위를 벗어납니다.</exception>
        public ArrayDataStore(int coilCount, int discreteInputCount, int holdingRegisterCount, int inputRegisterCount)
        {
            CheckSize(coilCount);
            CheckSize(discreteInputCount);
            CheckSize(holdingRegisterCount);
            CheckSize(inputRegisterCount);
            _coils = new bool[coilCount];
            _inputs = new bool[discreteInputCount];
            _holding = new ushort[holdingRegisterCount];
            _registers = new ushort[inputRegisterCount];
        }

        private static void CheckSize(int count)
        {
            if (count < 0 || count > 65536)
                throw new ArgumentOutOfRangeException("count");
        }

        private static void Check(Array source, ushort address, ushort count, Array values)
        {
            if (count == 0 || (int)address + count > source.Length)
                throw new ModbusException(ExceptionCode.IllegalDataAddress);
            if (values == null || values.Length < count)
                throw new ArgumentException("values");
        }

        /// <inheritdoc/>
        /// <exception cref="ModbusException">수량이 0이거나 요청 범위가 영역을 벗어납니다.</exception>
        /// <exception cref="ArgumentException">결과 배열이 null이거나 공간이 부족합니다.</exception>
        public void ReadBits(bool discreteInputs, ushort address, ushort count, bool[] values)
        {
            bool[] source = discreteInputs ? _inputs : _coils;
            Check(source, address, count, values);
            lock (_sync)
                Array.Copy(source, address, values, 0, count);
        }

        /// <inheritdoc/>
        /// <exception cref="ModbusException">수량이 0이거나 요청 범위가 영역을 벗어납니다.</exception>
        /// <exception cref="ArgumentException">결과 배열이 null이거나 공간이 부족합니다.</exception>
        public void ReadRegisters(bool inputRegisters, ushort address, ushort count, ushort[] values)
        {
            ushort[] source = inputRegisters ? _registers : _holding;
            Check(source, address, count, values);
            lock (_sync)
                Array.Copy(source, address, values, 0, count);
        }

        /// <inheritdoc/>
        /// <exception cref="ModbusException">수량이 0이거나 요청 범위가 영역을 벗어납니다.</exception>
        /// <exception cref="ArgumentException">입력 배열이 null이거나 값이 부족합니다.</exception>
        public void WriteCoils(ushort address, ushort count, bool[] values)
        {
            Check(_coils, address, count, values);
            lock (_sync)
                Array.Copy(values, 0, _coils, address, count);
        }

        /// <inheritdoc/>
        /// <exception cref="ModbusException">수량이 0이거나 요청 범위가 영역을 벗어납니다.</exception>
        /// <exception cref="ArgumentException">입력 배열이 null이거나 값이 부족합니다.</exception>
        public void WriteHoldingRegisters(ushort address, ushort count, ushort[] values)
        {
            Check(_holding, address, count, values);
            lock (_sync)
                Array.Copy(values, 0, _holding, address, count);
        }

        /// <summary>애플리케이션에서 Discrete Input 값을 갱신합니다.</summary>
        /// <param name="address">0 기반 시작 Discrete Input 주소입니다.</param>
        /// <param name="count">갱신할 항목 수이며 1 이상이어야 합니다.</param>
        /// <param name="values">인덱스 0부터 복사할 입력 배열입니다.</param>
        /// <exception cref="ModbusException">요청 범위가 유효하지 않습니다.</exception>
        /// <exception cref="ArgumentException">입력 배열이 null이거나 값이 부족합니다.</exception>
        public void SetDiscreteInputs(ushort address, ushort count, bool[] values)
        {
            Check(_inputs, address, count, values);
            lock (_sync)
                Array.Copy(values, 0, _inputs, address, count);
        }

        /// <summary>애플리케이션에서 Input Register 값을 갱신합니다.</summary>
        /// <param name="address">0 기반 시작 Input Register 주소입니다.</param>
        /// <param name="count">갱신할 Register 수이며 1 이상이어야 합니다.</param>
        /// <param name="values">인덱스 0부터 복사할 입력 배열입니다.</param>
        /// <exception cref="ModbusException">요청 범위가 유효하지 않습니다.</exception>
        /// <exception cref="ArgumentException">입력 배열이 null이거나 값이 부족합니다.</exception>
        public void SetInputRegisters(ushort address, ushort count, ushort[] values)
        {
            Check(_registers, address, count, values);
            lock (_sync)
                Array.Copy(values, 0, _registers, address, count);
        }
    }
}
