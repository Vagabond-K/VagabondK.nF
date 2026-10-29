using System;
using System.Threading;

namespace VagabondK.nF.Modbus.RTU
{
    /// <summary>RTU 수신 Frame의 끝을 판정하는 방법입니다.</summary>
    public enum RtuReceiveMode
    {
        /// <summary>관리 코드에서 관측한 무통신 간격으로 Frame을 구분합니다.</summary>
        SilentInterval,

        /// <summary>지원하는 요청의 Function 및 Byte Count로 길이를 계산합니다. Server에 사용합니다.</summary>
        RequestLength,

        /// <summary>지원하는 정상·Exception 응답의 길이를 계산합니다. Client에 사용합니다.</summary>
        ResponseLength
    }

    /// <summary>바이트 스트림에서 Modbus RTU 프레임을 송수신합니다.</summary>
    /// <remarks>
    /// 호출자는 이 인스턴스에 대한 접근을 직렬화해야 합니다. 밀리초 단위 폴링은 관리 코드에서 사용하는 대안이며 정밀한 UART t1.5/t3.5 판정을 보장하지 않습니다.
    /// 길이 기반 모드에서는 알 수 없는 기능 코드를 수신하면 무통신 간격 판정으로 전환합니다.
    /// Serial 채널과 잔여 데이터의 재동기화는 호출자가 관리합니다.
    /// </remarks>
    public sealed class RtuTransport : ChannelTransport, IModbusPollingTransport
    {
        private readonly byte[] _frame = new byte[RtuFrame.MaxLength + 1];
        private readonly int _frameGapMilliseconds;
        private readonly RtuReceiveMode _receiveMode;

        /// <summary>Serial 주소 및 Broadcast 규칙을 사용하므로 항상 true입니다.</summary>
        public override bool IsSerial
        {
            get
            {
                return true;
            }
        }

        /// <summary>무통신 간격으로 Frame을 구분하는 RTU 전송을 생성합니다.</summary>
        /// <param name="channel">호출자가 관리하는 채널입니다.</param>
        /// <param name="baudRate">1 이상의 Baud Rate입니다.</param>
        /// <param name="bitsPerCharacter">시작·데이터·패리티·정지 비트를 포함한 문자당 비트 수이며 9~12입니다.</param>
        /// <param name="timeoutMilliseconds">각 송수신 작업의 제한 시간(ms)이며 1 이상입니다.</param>
        /// <exception cref="ArgumentNullException">채널이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">통신 속도, 문자당 비트 수 또는 제한 시간이 유효하지 않습니다.</exception>
        public RtuTransport(IByteChannel channel, int baudRate, int bitsPerCharacter = 11,
            int timeoutMilliseconds = 1000)
            : this(channel, baudRate, RtuReceiveMode.SilentInterval, bitsPerCharacter, timeoutMilliseconds)
        {
        }

        /// <summary>Frame 끝 판정 방식을 지정하여 RTU 전송을 생성합니다.</summary>
        /// <param name="channel">호출자가 관리하는 채널입니다.</param>
        /// <param name="baudRate">1 이상의 Baud Rate입니다.</param>
        /// <param name="receiveMode">요청, 응답 또는 무통신 간격 기반 수신 방식입니다.</param>
        /// <param name="bitsPerCharacter">문자당 비트 수이며 범위는 9~12입니다.</param>
        /// <param name="timeoutMilliseconds">각 송수신 작업의 제한 시간(ms)이며 1 이상입니다.</param>
        /// <exception cref="ArgumentNullException">채널이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">통신 설정 또는 수신 방식이 유효하지 않습니다.</exception>
        public RtuTransport(IByteChannel channel, int baudRate, RtuReceiveMode receiveMode,
            int bitsPerCharacter = 11, int timeoutMilliseconds = 1000) : base(channel, timeoutMilliseconds)
        {
            if (baudRate < 1)
                throw new ArgumentOutOfRangeException("baudRate");
            if (bitsPerCharacter < 9 || bitsPerCharacter > 12)
                throw new ArgumentOutOfRangeException("bitsPerCharacter");
            if (receiveMode != RtuReceiveMode.SilentInterval &&
                receiveMode != RtuReceiveMode.RequestLength && receiveMode != RtuReceiveMode.ResponseLength)
                throw new ArgumentOutOfRangeException("receiveMode");
            _receiveMode = receiveMode;
            _frameGapMilliseconds = baudRate > 19200 ? 2 :
                (int)((3500L * bitsPerCharacter + baudRate - 1) / baudRate);
        }

        /// <inheritdoc/>
        /// <remarks>설정한 프레임 간격만큼 대기한 후 송신합니다. 이 대기 시간은 채널의 Write에 전달하는 제한 시간에 포함하지 않습니다.
        /// Transaction ID는 사용하지 않습니다.</remarks>
        public override void Send(FrameContext context, byte[] pdu, int count)
        {
            int length = RtuFrame.Encode(context.UnitId, pdu, count, _frame);
            Thread.Sleep(_frameGapMilliseconds);
            Channel.Write(_frame, 0, length, TimeoutMilliseconds);
        }

        /// <inheritdoc/>
        public override int Receive(byte[] pdu, out FrameContext context)
        {
            int length;
            ReceiveStatus status = TryReceive(pdu, out context, out length);
            switch (status)
            {
                case ReceiveStatus.Success:
                    return length;
                case ReceiveStatus.Idle:
                case ReceiveStatus.Timeout:
                    throw new TimeoutException();
                case ReceiveStatus.CrcMismatch:
                    throw new ModbusProtocolException("RTU CRC mismatch.");
                case ReceiveStatus.InvalidUnitId:
                    throw new ModbusProtocolException("Invalid serial unit ID.");
                case ReceiveStatus.FrameTooLong:
                    throw new ModbusProtocolException("RTU frame exceeds 256 bytes.");
                default:
                    throw new ModbusProtocolException("Invalid RTU frame length.");
            }
        }

        /// <inheritdoc/>
        /// <remarks>성공한 context의 Transaction ID는 0입니다. Frame 오류 뒤 UART 재동기화는 호출자가 담당합니다.</remarks>
        public ReceiveStatus TryReceive(byte[] pdu, out FrameContext context, out int length)
        {
            Protocol.ValidateBuffer(pdu, 0, Protocol.MaxPduLength);
            context = new FrameContext();
            length = 0;
            OperationDeadline deadline = Deadline();
            int count = 0;
            ReceiveStatus status = _receiveMode == RtuReceiveMode.SilentInterval ?
                ReceiveUntilSilence(deadline, ref count) : ReceiveByLength(deadline, ref count);
            if (status != ReceiveStatus.Success)
                return status;
            byte unitId;
            status = RtuFrame.TryDecode(_frame, count, pdu, out unitId, out length);
            if (status == ReceiveStatus.Success)
                context = new FrameContext(unitId, 0);
            return status;
        }

        private bool TryReadTo(int target, OperationDeadline deadline, ref int count)
        {
            while (count < target)
            {
                int remaining;
                if (!deadline.TryGetRemainingMilliseconds(out remaining))
                    return false;
                int read = Channel.Read(_frame, count, target - count, remaining);
                if (read < 0 || read > target - count)
                    throw new ModbusProtocolException("Invalid channel read count.");
                if (read == 0)
                    return false;
                count += read;
            }
            return true;
        }

        private ReceiveStatus ReceiveByLength(OperationDeadline deadline, ref int count)
        {
            if (!TryReadTo(2, deadline, ref count))
                return count == 0 ? ReceiveStatus.Idle : ReceiveStatus.Timeout;
            int function = _frame[1];
            int length;
            if (_receiveMode == RtuReceiveMode.ResponseLength && (function & 0x80) != 0)
            {
                length = 5;
            }
            else if (_receiveMode == RtuReceiveMode.ResponseLength && function >= 1 && function <= 4)
            {
                if (!TryReadTo(3, deadline, ref count))
                    return ReceiveStatus.Timeout;
                length = 5 + _frame[2];
            }
            else if ((_receiveMode == RtuReceiveMode.RequestLength && function >= 1 && function <= 4) ||
                function == 5 || function == 6 ||
                (_receiveMode == RtuReceiveMode.ResponseLength && (function == 15 || function == 16)))
            {
                length = 8;
            }
            else if (function == 15 || function == 16)
            {
                if (!TryReadTo(7, deadline, ref count))
                    return ReceiveStatus.Timeout;
                length = 9 + _frame[6];
            }
            else
            {
                // 알 수 없는 기능 코드는 길이 규칙이 없으므로 무통신 간격으로 구분하여 예외 응답을 보낼 수 있게 합니다.
                return ReceiveUntilSilence(deadline, ref count);
            }
            if (length > RtuFrame.MaxLength)
                return ReceiveStatus.FrameTooLong;
            return TryReadTo(length, deadline, ref count) ? ReceiveStatus.Success : ReceiveStatus.Timeout;
        }

        private ReceiveStatus ReceiveUntilSilence(OperationDeadline deadline, ref int count)
        {
            while (true)
            {
                int remaining;
                if (!deadline.TryGetRemainingMilliseconds(out remaining))
                    return count == 0 ? ReceiveStatus.Idle : ReceiveStatus.Timeout;
                int wait = count == 0 ? remaining :
                    (remaining < _frameGapMilliseconds ? remaining : _frameGapMilliseconds);
                int read = Channel.Read(_frame, count, _frame.Length - count, wait);
                if (read < 0 || read > _frame.Length - count)
                    throw new ModbusProtocolException("Invalid channel read count.");
                if (read == 0)
                {
                    if (count == 0)
                        return ReceiveStatus.Idle;
                    if (wait < _frameGapMilliseconds)
                        return ReceiveStatus.Timeout;
                    break;
                }
                count += read;
                if (count > RtuFrame.MaxLength)
                    return ReceiveStatus.FrameTooLong;
            }
            return ReceiveStatus.Success;
        }
    }
}
