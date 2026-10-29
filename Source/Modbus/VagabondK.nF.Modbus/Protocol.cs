using System;

namespace VagabondK.nF.Modbus
{
    /// <summary>지원하는 Modbus 기능 코드(Function Code)입니다.</summary>
    public enum FunctionCode : byte
    {
        /// <summary>Coil 상태를 읽습니다(01).</summary>
        ReadCoils = 1,

        /// <summary>Discrete Input 상태를 읽습니다(02).</summary>
        ReadDiscreteInputs = 2,

        /// <summary>Holding Register를 읽습니다(03).</summary>
        ReadHoldingRegisters = 3,

        /// <summary>Input Register를 읽습니다(04).</summary>
        ReadInputRegisters = 4,

        /// <summary>Coil 하나를 씁니다(05).</summary>
        WriteSingleCoil = 5,

        /// <summary>Holding Register 하나를 씁니다(06).</summary>
        WriteSingleRegister = 6,

        /// <summary>연속된 Coil을 씁니다(15).</summary>
        WriteMultipleCoils = 15,

        /// <summary>연속된 Holding Register를 씁니다(16).</summary>
        WriteMultipleRegisters = 16
    }

    /// <summary>Modbus 예외 응답에서 사용하는 오류 코드입니다.</summary>
    public enum ExceptionCode : byte
    {
        /// <summary>지원하지 않는 Function Code입니다.</summary>
        IllegalFunction = 1,

        /// <summary>요청한 데이터 주소 또는 범위가 유효하지 않습니다.</summary>
        IllegalDataAddress = 2,

        /// <summary>요청한 수량이나 데이터 값이 유효하지 않습니다.</summary>
        IllegalDataValue = 3,

        /// <summary>Server에서 요청을 처리하는 중 오류가 발생했습니다.</summary>
        ServerDeviceFailure = 4
    }

    /// <summary>수신한 예외 응답 또는 Server가 예외 응답으로 변환할 Modbus 오류를 나타냅니다.</summary>
    public sealed class ModbusException : Exception
    {
        /// <summary>Modbus 오류 코드를 가져옵니다.</summary>
        public ExceptionCode Code { get; private set; }

        /// <summary>지정한 Modbus 오류 코드로 예외를 생성합니다.</summary>
        /// <param name="code">예외 응답에 사용할 오류 코드입니다.</param>
        public ModbusException(ExceptionCode code) : base("Modbus exception: " + (byte)code)
        {
            Code = code;
        }
    }

    /// <summary>프레임 형식이 잘못되었거나 응답이 요청과 일치하지 않거나 통신 채널 계약을 위반했을 때 발생합니다.</summary>
    public sealed class ModbusProtocolException : Exception
    {
        /// <summary>오류 설명으로 프로토콜 예외를 생성합니다.</summary>
        /// <param name="message">오류 원인을 설명하는 메시지입니다.</param>
        public ModbusProtocolException(string message) : base(message) { }
    }

    /// <summary>PDU와 함께 전달할 Unit ID 및 TCP Transaction ID입니다.</summary>
    public struct FrameContext
    {
        /// <summary>대상 Unit ID입니다. 직렬 통신에서 0은 브로드캐스트 주소입니다.</summary>
        public byte UnitId;

        /// <summary>TCP 요청과 응답을 연결하는 Transaction ID입니다. 직렬 통신에서는 사용하지 않습니다.</summary>
        public ushort TransactionId;

        /// <summary>프레임의 주소와 트랜잭션 정보를 초기화합니다.</summary>
        /// <param name="unitId">Unit ID입니다.</param>
        /// <param name="transactionId">TCP Transaction ID입니다.</param>
        public FrameContext(byte unitId, ushort transactionId)
        {
            UnitId = unitId;
            TransactionId = transactionId;
        }
    }

    /// <summary>Modbus PDU와 전송별 프레임 사이의 송수신 계약입니다.</summary>
    /// <remarks>하나의 Client 또는 Server가 사용을 직렬화해야 합니다. 채널의 연결과 해제는 호출자가 담당합니다.</remarks>
    public interface IModbusTransport
    {
        /// <summary>직렬 통신의 주소 제한과 브로드캐스트 규칙을 적용하면 <see langword="true"/>입니다.</summary>
        bool IsSerial { get; }

        /// <summary>PDU에 전송별 헤더와 검증 정보를 붙여 보냅니다.</summary>
        /// <param name="context">Unit ID 및 Transaction 정보입니다.</param>
        /// <param name="pdu">인덱스 0부터 송신할 PDU입니다.</param>
        /// <param name="count">PDU 길이입니다. 범위는 1~253바이트입니다.</param>
        void Send(FrameContext context, byte[] pdu, int count);

        /// <summary>프레임 하나를 수신하고 PDU를 추출합니다.</summary>
        /// <param name="pdu">PDU를 인덱스 0부터 저장할 버퍼입니다. 최소 253바이트를 제공해야 합니다.</param>
        /// <param name="context">수신한 Unit ID 및 Transaction 정보입니다.</param>
        /// <returns>수신한 PDU의 바이트 수입니다.</returns>
        /// <exception cref="TimeoutException">수신 기한이 만료되었습니다.</exception>
        /// <exception cref="ModbusProtocolException">프레임이 유효하지 않습니다.</exception>
        int Receive(byte[] pdu, out FrameContext context);
    }

    /// <summary>상태 기반 수신에서 정상 처리와 예상 가능한 수신 오류를 구분합니다.</summary>
    public enum ReceiveStatus
    {
        /// <summary>유효한 PDU를 수신했습니다. 대상 주소의 요청 처리 성공을 의미하지는 않습니다.</summary>
        Success,

        /// <summary>수신한 바이트 없이 대기 기한이 만료되었습니다.</summary>
        Idle,

        /// <summary>일부 바이트를 수신했지만 완전한 프레임을 기한 내에 수신하지 못했습니다.</summary>
        Timeout,

        /// <summary>프레임 길이나 시작·종료 형식이 유효하지 않습니다.</summary>
        InvalidLength,

        /// <summary>수신 프레임이 전송별 최대 길이를 초과합니다.</summary>
        FrameTooLong,

        /// <summary>RTU CRC가 일치하지 않습니다.</summary>
        CrcMismatch,

        /// <summary>직렬 통신의 Unit ID가 0~247 범위를 벗어났습니다.</summary>
        InvalidUnitId,

        /// <summary>ASCII LRC가 일치하지 않습니다.</summary>
        LrcMismatch,

        /// <summary>ASCII 프레임에 허용되지 않는 16진수 문자가 있습니다.</summary>
        InvalidCharacter
    }

    /// <summary>예상 가능한 수신 결과를 예외 대신 상태로 반환하는 전송 계약입니다.</summary>
    /// <remarks>잘못된 인수, 채널 계약 위반 및 I/O 오류는 여전히 예외로 전달합니다.</remarks>
    public interface IModbusPollingTransport : IModbusTransport
    {
        /// <summary>프레임 하나의 수신을 시도합니다.</summary>
        /// <param name="pdu">수신 PDU를 저장할 최소 253바이트의 버퍼입니다.</param>
        /// <param name="context">성공한 경우 수신 프레임의 주소와 트랜잭션 정보입니다.</param>
        /// <param name="length">성공한 경우 PDU 길이이며, 실패하면 0입니다.</param>
        /// <returns>수신 상태입니다. <see cref="ReceiveStatus.Success"/>일 때만 출력 PDU를 사용합니다.</returns>
        ReceiveStatus TryReceive(byte[] pdu, out FrameContext context, out int length);
    }

    /// <summary>Modbus 전송이 사용하는 동기 바이트 스트림 채널 계약입니다.</summary>
    /// <remarks>연결 종료 및 I/O 오류는 예외로 전달해야 합니다. UART 설정과 리소스 수명은 애플리케이션이 관리합니다.</remarks>
    public interface IByteChannel
    {
        /// <summary>지정한 버퍼 범위에 바이트를 읽어 저장합니다. 요청한 수보다 적게 읽을 수 있습니다.</summary>
        /// <param name="buffer">수신 버퍼입니다.</param>
        /// <param name="offset">저장할 시작 인덱스입니다.</param>
        /// <param name="count">읽을 최대 바이트 수입니다. 1 이상이어야 합니다.</param>
        /// <param name="timeoutMilliseconds">읽기 기한이며 단위는 ms입니다. 1 이상이어야 합니다.</param>
        /// <returns>읽은 바이트 수입니다. 읽기 기한이 만료되어 데이터가 없으면 0입니다.</returns>
        int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds);

        /// <summary>지정한 범위의 바이트를 모두 쓰거나 예외를 발생시킵니다.</summary>
        /// <param name="buffer">송신 버퍼입니다.</param>
        /// <param name="offset">송신할 시작 인덱스입니다.</param>
        /// <param name="count">송신할 바이트 수입니다. 1 이상이어야 합니다.</param>
        /// <param name="timeoutMilliseconds">쓰기 기한이며 단위는 ms입니다. 1 이상이어야 합니다.</param>
        void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds);
    }

    /// <summary>Modbus의 PDU 제한, Big-endian 변환 및 인수 검증을 제공합니다.</summary>
    public static class Protocol
    {
        /// <summary>Function Code를 포함한 최대 PDU 길이이며 253바이트입니다.</summary>
        public const int MaxPduLength = 253;

        /// <summary>연속된 두 바이트를 Big-endian UInt16으로 읽습니다.</summary>
        /// <param name="buffer">읽을 두 바이트가 있는 버퍼입니다.</param>
        /// <param name="offset">상위 바이트의 인덱스입니다. 유효한 범위는 호출자가 보장해야 합니다.</param>
        /// <returns>변환한 16비트 값입니다.</returns>
        public static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        /// <summary>UInt16을 상위 바이트부터 두 바이트로 기록합니다.</summary>
        /// <param name="buffer">기록할 버퍼입니다.</param>
        /// <param name="offset">상위 바이트를 기록할 인덱스입니다. 두 바이트의 공간이 필요합니다.</param>
        /// <param name="value">기록할 16비트 값입니다.</param>
        public static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        /// <summary>버퍼가 null이 아니고 지정한 범위를 포함하는지 검증합니다.</summary>
        /// <param name="buffer">검증할 버퍼입니다.</param>
        /// <param name="offset">0 이상의 시작 인덱스입니다.</param>
        /// <param name="count">0 이상의 바이트 수입니다.</param>
        /// <exception cref="ArgumentNullException">버퍼가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">인덱스나 길이가 버퍼 범위를 벗어납니다.</exception>
        public static void ValidateBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException("count");
        }

        /// <summary>버퍼와 PDU 길이가 유효한지 검증합니다.</summary>
        /// <param name="pdu">PDU 버퍼입니다.</param>
        /// <param name="count">PDU 길이입니다. 범위는 1~253바이트입니다.</param>
        /// <exception cref="ArgumentNullException">PDU 버퍼가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">길이가 유효하지 않거나 버퍼보다 큽니다.</exception>
        public static void ValidatePdu(byte[] pdu, int count)
        {
            ValidateBuffer(pdu, 0, count);
            if (count < 1 || count > MaxPduLength)
                throw new ArgumentOutOfRangeException("count");
        }

        /// <summary>요청 수량과 16비트 주소 범위의 끝을 검증합니다.</summary>
        /// <param name="address">0 기반 시작 주소입니다.</param>
        /// <param name="count">요청할 항목 수입니다.</param>
        /// <param name="maximum">허용할 최대 항목 수입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">수량이 1~maximum 범위를 벗어나거나 마지막 주소가 65535를 초과합니다.</exception>
        public static void ValidateRange(ushort address, int count, int maximum)
        {
            if (count < 1 || count > maximum || (int)address + count > 65536)
                throw new ArgumentOutOfRangeException("count");
        }
    }
}
