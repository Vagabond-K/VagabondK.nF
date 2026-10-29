using System;

namespace VagabondK.nF.Modbus.Client
{
    /// <summary>Modbus의 8개 기본 기능을 동기 요청으로 실행하는 Client입니다.</summary>
    /// <remarks>
    /// 이 인스턴스의 요청은 내부 잠금으로 직렬화합니다. 전송을 다른 Client나 Server와 공유하지 마십시오.
    /// 직렬 통신의 읽기 Unit ID는 1~247이며, 쓰기는 0을 브로드캐스트 주소로 사용할 수 있습니다.
    /// TCP Unit ID는 0~255를 허용합니다. 브로드캐스트 쓰기는 응답을 기다리지 않습니다.
    /// 시간 초과나 잘못된 응답 이후의 연결 복구와 직렬 채널의 잔여 데이터 처리는 호출자가 담당합니다.
    /// 요청 실패는 TimeoutException, ModbusProtocolException, ModbusException 또는 채널의 I/O 예외로 전달합니다.
    /// </remarks>
    public sealed class ModbusClient
    {
        private readonly IModbusTransport _transport;
        private readonly object _sync = new object();
        private readonly byte[] _request = new byte[Protocol.MaxPduLength];
        private readonly byte[] _response = new byte[Protocol.MaxPduLength];
        private ushort _transaction;

        /// <summary>지정한 전송을 사용하는 Client를 생성합니다.</summary>
        /// <param name="transport">호출자가 수명을 관리하는 전송입니다.</param>
        /// <exception cref="ArgumentNullException">전송이 null입니다.</exception>
        public ModbusClient(IModbusTransport transport)
        {
            if (transport == null)
                throw new ArgumentNullException("transport");
            _transport = transport;
        }

        /// <summary>연속된 Coil 상태를 읽습니다(Function 01).</summary>
        /// <param name="unitId">대상 Unit ID입니다. Serial에서는 1~247입니다.</param>
        /// <param name="address">0 기반 시작 Coil 주소입니다.</param>
        /// <param name="count">읽을 Coil 수입니다. 범위는 1~2000이며 끝 주소는 65535 이하여야 합니다.</param>
        /// <param name="values">결과를 저장할 배열입니다. 지정한 범위를 포함해야 합니다.</param>
        /// <param name="offset">결과를 저장할 배열의 시작 인덱스입니다.</param>
        /// <exception cref="ArgumentNullException">결과 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">수량, 주소 범위, 배열 범위 또는 Serial Unit ID가 유효하지 않습니다.</exception>
        /// <exception cref="ModbusException">Server가 Exception 응답을 반환했습니다.</exception>
        /// <exception cref="ModbusProtocolException">응답의 주소, Function 또는 길이가 일치하지 않습니다.</exception>
        /// <exception cref="TimeoutException">전송의 수신 기한이 만료되었습니다.</exception>
        public void ReadCoils(byte unitId, ushort address, ushort count, bool[] values, int offset)
        {
            ReadBits(unitId, address, count, values, offset, FunctionCode.ReadCoils);
        }

        /// <summary>연속된 Discrete Input 상태를 읽습니다(Function 02).</summary>
        /// <param name="unitId">대상 Unit ID입니다. Serial에서는 1~247입니다.</param>
        /// <param name="address">0 기반 시작 Discrete Input 주소입니다.</param>
        /// <param name="count">읽을 항목 수입니다. 범위는 1~2000이며 끝 주소는 65535 이하여야 합니다.</param>
        /// <param name="values">결과를 저장할 배열입니다. 지정한 범위를 포함해야 합니다.</param>
        /// <param name="offset">결과를 저장할 배열의 시작 인덱스입니다.</param>
        /// <exception cref="ArgumentNullException">결과 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">수량, 주소 범위, 배열 범위 또는 Serial Unit ID가 유효하지 않습니다.</exception>
        /// <exception cref="ModbusException">Server가 Exception 응답을 반환했습니다.</exception>
        /// <exception cref="ModbusProtocolException">응답이 요청과 일치하지 않거나 유효하지 않습니다.</exception>
        /// <exception cref="TimeoutException">전송의 수신 기한이 만료되었습니다.</exception>
        public void ReadDiscreteInputs(byte unitId, ushort address, ushort count, bool[] values, int offset)
        {
            ReadBits(unitId, address, count, values, offset, FunctionCode.ReadDiscreteInputs);
        }

        /// <summary>연속된 Holding Register를 읽습니다(Function 03).</summary>
        /// <param name="unitId">대상 Unit ID입니다. Serial에서는 1~247입니다.</param>
        /// <param name="address">0 기반 시작 Register 주소입니다.</param>
        /// <param name="count">읽을 Register 수입니다. 범위는 1~125이며 끝 주소는 65535 이하여야 합니다.</param>
        /// <param name="values">결과를 저장할 배열입니다. 지정한 범위를 포함해야 합니다.</param>
        /// <param name="offset">결과를 저장할 배열의 시작 인덱스입니다.</param>
        /// <exception cref="ArgumentNullException">결과 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">수량, 주소 범위, 배열 범위 또는 Serial Unit ID가 유효하지 않습니다.</exception>
        /// <exception cref="ModbusException">Server가 Exception 응답을 반환했습니다.</exception>
        /// <exception cref="ModbusProtocolException">응답이 요청과 일치하지 않거나 유효하지 않습니다.</exception>
        /// <exception cref="TimeoutException">전송의 수신 기한이 만료되었습니다.</exception>
        public void ReadHoldingRegisters(byte unitId, ushort address, ushort count, ushort[] values, int offset)
        {
            ReadRegisters(unitId, address, count, values, offset, FunctionCode.ReadHoldingRegisters);
        }

        /// <summary>연속된 Input Register를 읽습니다(Function 04).</summary>
        /// <param name="unitId">대상 Unit ID입니다. Serial에서는 1~247입니다.</param>
        /// <param name="address">0 기반 시작 Register 주소입니다.</param>
        /// <param name="count">읽을 Register 수입니다. 범위는 1~125이며 끝 주소는 65535 이하여야 합니다.</param>
        /// <param name="values">결과를 저장할 배열입니다. 지정한 범위를 포함해야 합니다.</param>
        /// <param name="offset">결과를 저장할 배열의 시작 인덱스입니다.</param>
        /// <exception cref="ArgumentNullException">결과 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">수량, 주소 범위, 배열 범위 또는 Serial Unit ID가 유효하지 않습니다.</exception>
        /// <exception cref="ModbusException">Server가 Exception 응답을 반환했습니다.</exception>
        /// <exception cref="ModbusProtocolException">응답이 요청과 일치하지 않거나 유효하지 않습니다.</exception>
        /// <exception cref="TimeoutException">전송의 수신 기한이 만료되었습니다.</exception>
        public void ReadInputRegisters(byte unitId, ushort address, ushort count, ushort[] values, int offset)
        {
            ReadRegisters(unitId, address, count, values, offset, FunctionCode.ReadInputRegisters);
        }

        private static void ValidateValues(Array values, int offset, int count)
        {
            if (values == null)
                throw new ArgumentNullException("values");
            if (offset < 0 || offset > values.Length - count)
                throw new ArgumentOutOfRangeException("offset");
        }

        private void Header(FunctionCode function, ushort address, ushort value)
        {
            _request[0] = (byte)function;
            Protocol.WriteUInt16(_request, 1, address);
            Protocol.WriteUInt16(_request, 3, value);
        }

        private int Exchange(byte unitId, int length, bool write)
        {
            if (_transport.IsSerial && (unitId > 247 || (!write && unitId == 0)))
                throw new ArgumentOutOfRangeException("unitId");
            _transaction = unchecked((ushort)(_transaction + 1));
            FrameContext sent = new FrameContext(unitId, _transaction);
            _transport.Send(sent, _request, length);
            if (_transport.IsSerial && unitId == 0)
                return 0;

            FrameContext received;
            int count = _transport.Receive(_response, out received);
            if (received.UnitId != unitId ||
                (!_transport.IsSerial && received.TransactionId != sent.TransactionId))
                throw new ModbusProtocolException("Response does not match the request context.");
            if (count < 1 || count > Protocol.MaxPduLength)
                throw new ModbusProtocolException("Invalid response length.");
            if (_response[0] == (_request[0] | 0x80))
            {
                if (count != 2)
                    throw new ModbusProtocolException("Invalid exception response.");
                throw new ModbusException((ExceptionCode)_response[1]);
            }
            if (_response[0] != _request[0])
                throw new ModbusProtocolException("Response function does not match the request.");
            return count;
        }

        private void ReadBits(byte unitId, ushort address, ushort count, bool[] values, int offset, FunctionCode function)
        {
            Protocol.ValidateRange(address, count, 2000);
            ValidateValues(values, offset, count);
            lock (_sync)
            {
                Header(function, address, count);
                int length = Exchange(unitId, 5, false);
                int bytes = (count + 7) / 8;
                if (length != bytes + 2 || _response[1] != bytes)
                    throw new ModbusProtocolException("Invalid bit response length.");
                for (int i = 0; i < count; i++)
                    values[offset + i] = (_response[2 + i / 8] & (1 << (i % 8))) != 0;
            }
        }

        private void ReadRegisters(byte unitId, ushort address, ushort count, ushort[] values, int offset, FunctionCode function)
        {
            Protocol.ValidateRange(address, count, 125);
            ValidateValues(values, offset, count);
            lock (_sync)
            {
                Header(function, address, count);
                int length = Exchange(unitId, 5, false);
                if (length != 2 + count * 2 || _response[1] != count * 2)
                    throw new ModbusProtocolException("Invalid register response length.");
                for (int i = 0; i < count; i++)
                    values[offset + i] = Protocol.ReadUInt16(_response, 2 + i * 2);
            }
        }

        /// <summary>Coil 하나를 씁니다(Function 05).</summary>
        /// <param name="unitId">대상 Unit ID입니다. Serial에서는 0~247이며 0은 Broadcast입니다.</param>
        /// <param name="address">0 기반 Coil 주소입니다.</param>
        /// <param name="value">기록할 Coil 상태입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">Serial Unit ID가 0~247 범위를 벗어납니다.</exception>
        /// <exception cref="ModbusException">Server가 Exception 응답을 반환했습니다.</exception>
        /// <exception cref="ModbusProtocolException">응답이 요청 내용을 올바르게 되돌려주지 않습니다.</exception>
        /// <exception cref="TimeoutException">송수신 기한이 만료되었습니다.</exception>
        public void WriteSingleCoil(byte unitId, ushort address, bool value)
        {
            WriteSingle(unitId, address, value ? (ushort)0xFF00 : (ushort)0, FunctionCode.WriteSingleCoil);
        }

        /// <summary>Holding Register 하나를 씁니다(Function 06).</summary>
        /// <param name="unitId">대상 Unit ID입니다. Serial에서는 0~247이며 0은 Broadcast입니다.</param>
        /// <param name="address">0 기반 Holding Register 주소입니다.</param>
        /// <param name="value">기록할 16비트 값입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">Serial Unit ID가 0~247 범위를 벗어납니다.</exception>
        /// <exception cref="ModbusException">Server가 Exception 응답을 반환했습니다.</exception>
        /// <exception cref="ModbusProtocolException">응답이 요청 내용을 올바르게 되돌려주지 않습니다.</exception>
        /// <exception cref="TimeoutException">송수신 기한이 만료되었습니다.</exception>
        public void WriteSingleRegister(byte unitId, ushort address, ushort value)
        {
            WriteSingle(unitId, address, value, FunctionCode.WriteSingleRegister);
        }

        private void WriteSingle(byte unitId, ushort address, ushort value, FunctionCode function)
        {
            lock (_sync)
            {
                Header(function, address, value);
                CheckWriteResponse(Exchange(unitId, 5, true));
            }
        }

        /// <summary>연속된 Coil을 하나의 요청으로 씁니다(Function 15).</summary>
        /// <param name="unitId">대상 Unit ID입니다. Serial에서는 0~247이며 0은 Broadcast입니다.</param>
        /// <param name="address">0 기반 시작 Coil 주소입니다.</param>
        /// <param name="count">쓸 Coil 수입니다. 범위는 1~1968이며 끝 주소는 65535 이하여야 합니다.</param>
        /// <param name="values">송신할 Coil 상태 배열입니다. 지정한 범위를 포함해야 합니다.</param>
        /// <param name="offset">송신할 배열의 시작 인덱스입니다.</param>
        /// <exception cref="ArgumentNullException">송신 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">수량, 주소 범위, 배열 범위 또는 Serial Unit ID가 유효하지 않습니다.</exception>
        /// <exception cref="ModbusException">Server가 Exception 응답을 반환했습니다.</exception>
        /// <exception cref="ModbusProtocolException">응답의 주소나 수량이 요청과 일치하지 않습니다.</exception>
        /// <exception cref="TimeoutException">송수신 기한이 만료되었습니다.</exception>
        public void WriteMultipleCoils(byte unitId, ushort address, ushort count, bool[] values, int offset)
        {
            Protocol.ValidateRange(address, count, 1968);
            ValidateValues(values, offset, count);
            lock (_sync)
            {
                Header(FunctionCode.WriteMultipleCoils, address, count);
                int bytes = (count + 7) / 8;
                _request[5] = (byte)bytes;
                Array.Clear(_request, 6, bytes);
                for (int i = 0; i < count; i++)
                    if (values[offset + i])
                        _request[6 + i / 8] |= (byte)(1 << (i % 8));
                CheckWriteResponse(Exchange(unitId, 6 + bytes, true));
            }
        }

        /// <summary>연속된 Holding Register를 하나의 요청으로 씁니다(Function 16).</summary>
        /// <param name="unitId">대상 Unit ID입니다. Serial에서는 0~247이며 0은 Broadcast입니다.</param>
        /// <param name="address">0 기반 시작 Register 주소입니다.</param>
        /// <param name="count">쓸 Register 수입니다. 범위는 1~123이며 끝 주소는 65535 이하여야 합니다.</param>
        /// <param name="values">송신할 16비트 값 배열입니다. 지정한 범위를 포함해야 합니다.</param>
        /// <param name="offset">송신할 배열의 시작 인덱스입니다.</param>
        /// <exception cref="ArgumentNullException">송신 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">수량, 주소 범위, 배열 범위 또는 Serial Unit ID가 유효하지 않습니다.</exception>
        /// <exception cref="ModbusException">Server가 Exception 응답을 반환했습니다.</exception>
        /// <exception cref="ModbusProtocolException">응답의 주소나 수량이 요청과 일치하지 않습니다.</exception>
        /// <exception cref="TimeoutException">송수신 기한이 만료되었습니다.</exception>
        public void WriteMultipleRegisters(byte unitId, ushort address, ushort count, ushort[] values, int offset)
        {
            Protocol.ValidateRange(address, count, 123);
            ValidateValues(values, offset, count);
            lock (_sync)
            {
                Header(FunctionCode.WriteMultipleRegisters, address, count);
                _request[5] = (byte)(count * 2);
                for (int i = 0; i < count; i++)
                    Protocol.WriteUInt16(_request, 6 + i * 2, values[offset + i]);
                CheckWriteResponse(Exchange(unitId, 6 + count * 2, true));
            }
        }

        private void CheckWriteResponse(int length)
        {
            if (length == 0)
                return; // 직렬 통신의 브로드캐스트에는 응답이 없습니다.
            if (length != 5)
                throw new ModbusProtocolException("Invalid write response length.");
            for (int i = 1; i < 5; i++)
                if (_response[i] != _request[i])
                    throw new ModbusProtocolException("Write response does not echo the request.");
        }
    }
}
