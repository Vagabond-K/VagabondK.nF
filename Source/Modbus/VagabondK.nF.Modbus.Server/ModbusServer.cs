using System;

namespace VagabondK.nF.Modbus.Server
{
    /// <summary>Modbus 요청을 저장소에 적용하고 응답하는 동기 Server입니다.</summary>
    /// <remarks>
    /// 작업 스레드를 생성하지 않으므로 애플리케이션이 처리 루프를 실행해야 합니다.
    /// 이 인스턴스의 처리 호출은 내부 잠금으로 직렬화합니다. 전송과 저장소는 해제하지 않습니다.
    /// 직렬 통신의 브로드캐스트는 쓰기만 처리하고 응답하지 않으며, 다른 Unit ID의 요청은 무시합니다.
    /// </remarks>
    public sealed class ModbusServer
    {
        private readonly IModbusTransport _transport;
        private readonly IModbusDataStore _store;
        private readonly byte _unitId;
        private readonly object _sync = new object();
        private readonly byte[] _request = new byte[Protocol.MaxPduLength];
        private readonly byte[] _response = new byte[Protocol.MaxPduLength];
        private readonly bool[] _bits = new bool[2000];
        private readonly ushort[] _registers = new ushort[125];

        /// <summary>대상 Unit ID, 전송 및 데이터 저장소를 설정합니다.</summary>
        /// <param name="unitId">Server의 Unit ID입니다. Serial에서는 1~247이어야 합니다.</param>
        /// <param name="transport">호출자가 관리하는 전송입니다.</param>
        /// <param name="store">Coil 및 Register를 제공하는 저장소입니다.</param>
        /// <exception cref="ArgumentNullException">전송 또는 저장소가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Serial Unit ID가 유효하지 않습니다.</exception>
        public ModbusServer(byte unitId, IModbusTransport transport, IModbusDataStore store)
        {
            if (transport == null)
                throw new ArgumentNullException("transport");
            if (store == null)
                throw new ArgumentNullException("store");
            if (transport.IsSerial && (unitId == 0 || unitId > 247))
                throw new ArgumentOutOfRangeException("unitId");
            _unitId = unitId;
            _transport = transport;
            _store = store;
        }

        /// <summary>다음 Frame 하나를 수신하고 대상 요청이면 처리합니다.</summary>
        /// <remarks>일반 요청의 Modbus 오류는 예외 응답으로 변환합니다. 브로드캐스트 처리 오류는 호출자에게 전달합니다.</remarks>
        /// <exception cref="TimeoutException">전송의 수신 기한이 만료되었습니다.</exception>
        /// <exception cref="ModbusProtocolException">수신 Frame 또는 PDU 길이가 유효하지 않습니다.</exception>
        /// <exception cref="ModbusException">Broadcast 요청을 처리하지 못했습니다.</exception>
        public void ProcessNextRequest()
        {
            lock (_sync)
            {
                FrameContext context;
                int length = _transport.Receive(_request, out context);
                ProcessRequest(context, length);
            }
        }

        /// <summary>예상 가능한 수신 오류를 상태로 반환하며 다음 요청 하나를 처리합니다.</summary>
        /// <returns>전송의 수신 상태입니다. Success는 유효한 Frame 수신을 뜻하며 주소 일치나 정상 응답을 보장하지 않습니다.</returns>
        /// <exception cref="NotSupportedException">전송이 상태 기반 수신을 지원하지 않습니다.</exception>
        /// <exception cref="ModbusException">Broadcast 요청을 처리하지 못했습니다.</exception>
        /// <remarks>인수, 채널 및 저장소의 예외는 상태로 변환하지 않고 전달합니다.</remarks>
        public ReceiveStatus TryProcessNextRequest()
        {
            var receiver = _transport as IModbusPollingTransport;
            if (receiver == null)
                throw new NotSupportedException("Transport does not support status-based receive.");
            lock (_sync)
            {
                FrameContext context;
                int length;
                ReceiveStatus status = receiver.TryReceive(_request, out context, out length);
                if (status == ReceiveStatus.Success)
                    ProcessRequest(context, length);
                return status;
            }
        }

        private void ProcessRequest(FrameContext context, int length)
        {
            if (length < 1 || length > Protocol.MaxPduLength)
                throw new ModbusProtocolException("Invalid request length.");
            bool broadcast = _transport.IsSerial && context.UnitId == 0;
            if (!broadcast && context.UnitId != _unitId)
                return;
            byte function = _request[0];
            if (broadcast && function != 5 && function != 6 && function != 15 && function != 16)
                return;

            int responseLength;
            try
            {
                responseLength = Process(length);
            }
            catch (ModbusException error)
            {
                if (broadcast)
                    throw; // 응답을 전송할 수 없으므로 앱에 오류를 전달합니다.
                _response[0] = (byte)(function | 0x80);
                _response[1] = (byte)error.Code;
                responseLength = 2;
            }
            if (responseLength < 0)
            {
                if (broadcast)
                    throw new ModbusException((ExceptionCode)(-responseLength));
                _response[0] = (byte)(function | 0x80);
                _response[1] = (byte)(-responseLength);
                responseLength = 2;
            }
            if (!broadcast)
                _transport.Send(context, _response, responseLength);
        }

        private int Process(int length)
        {
            byte function = _request[0];
            // 예상 가능한 요청 오류는 예외를 발생시키지 않고 음수 길이로 예외 응답 코드를 전달합니다.
            if (!(function == 1 || function == 2 || function == 3 || function == 4 ||
                function == 5 || function == 6 || function == 15 || function == 16))
                return -(int)ExceptionCode.IllegalFunction;
            if (length < 5)
                return -(int)ExceptionCode.IllegalDataValue;
            ushort address = Protocol.ReadUInt16(_request, 1);
            ushort value = Protocol.ReadUInt16(_request, 3);
            _response[0] = function;

            if (function <= 4)
            {
                if (!(length == 5 && value >= 1 && value <= (function <= 2 ? 2000 : 125)))
                    return -(int)ExceptionCode.IllegalDataValue;
                if ((int)address + value > 65536 || !IsValidRange(function, address, value))
                    return -(int)ExceptionCode.IllegalDataAddress;
                if (function <= 2)
                {
                    _store.ReadBits(function == 2, address, value, _bits);
                    int bytes = (value + 7) / 8;
                    _response[1] = (byte)bytes;
                    Array.Clear(_response, 2, bytes);
                    for (int i = 0; i < value; i++)
                        if (_bits[i])
                            _response[2 + i / 8] |= (byte)(1 << (i % 8));
                    return 2 + bytes;
                }
                _store.ReadRegisters(function == 4, address, value, _registers);
                _response[1] = (byte)(value * 2);
                for (int i = 0; i < value; i++)
                    Protocol.WriteUInt16(_response, 2 + i * 2, _registers[i]);
                return 2 + value * 2;
            }

            if (function == 5 || function == 6)
            {
                if (length != 5 || (function == 5 && value != 0 && value != 0xFF00))
                    return -(int)ExceptionCode.IllegalDataValue;
                if (!IsValidRange(function, address, 1))
                    return -(int)ExceptionCode.IllegalDataAddress;
                if (function == 5)
                {
                    _bits[0] = value != 0;
                    _store.WriteCoils(address, 1, _bits);
                }
                else
                {
                    _registers[0] = value;
                    _store.WriteHoldingRegisters(address, 1, _registers);
                }
            }
            else
            {
                if (!(length >= 6 && value >= 1 && value <= (function == 15 ? 1968 : 123)))
                    return -(int)ExceptionCode.IllegalDataValue;
                if ((int)address + value > 65536)
                    return -(int)ExceptionCode.IllegalDataAddress;
                int bytes = function == 15 ? (value + 7) / 8 : value * 2;
                if (_request[5] != bytes || length != 6 + bytes)
                    return -(int)ExceptionCode.IllegalDataValue;
                if (!IsValidRange(function, address, value))
                    return -(int)ExceptionCode.IllegalDataAddress;
                if (function == 15)
                {
                    for (int i = 0; i < value; i++)
                        _bits[i] = (_request[6 + i / 8] & (1 << (i % 8))) != 0;
                    _store.WriteCoils(address, value, _bits);
                }
                else
                {
                    for (int i = 0; i < value; i++)
                        _registers[i] = Protocol.ReadUInt16(_request, 6 + i * 2);
                    _store.WriteHoldingRegisters(address, value, _registers);
                }
            }
            Array.Copy(_request, 1, _response, 1, 4);
            return 5;
        }

        private bool IsValidRange(byte function, ushort address, ushort count)
        {
            var validator = _store as IModbusDataStoreValidator;
            return validator == null || validator.IsValidRange((FunctionCode)function, address, count);
        }
    }
}
