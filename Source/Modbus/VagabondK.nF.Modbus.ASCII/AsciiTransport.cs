using System;

namespace VagabondK.nF.Modbus.ASCII
{
    /// <summary>바이트 스트림에서 Modbus ASCII 프레임을 송수신합니다.</summary>
    /// <remarks>
    /// 호출자는 이 인스턴스에 대한 접근을 직렬화해야 합니다. 전체 수신 기한과 별도로 프레임 시작 후 문자 사이의 대기 시간을 최대 1000ms로 제한합니다.
    /// 새 ':' 문자를 받으면 프레임 수집을 다시 시작하지만 전체 수신 기한은 연장하지 않습니다.
    /// 채널 설정, 수명 및 오류 후 재동기화는 호출자가 담당합니다.
    /// </remarks>
    public sealed class AsciiTransport : ChannelTransport, IModbusPollingTransport
    {
        private readonly byte[] _frame = new byte[AsciiFrame.MaxLength];
        private readonly byte[] _character = new byte[1];

        /// <summary>Serial 주소 및 Broadcast 규칙을 사용하므로 항상 true입니다.</summary>
        public override bool IsSerial
        {
            get
            {
                return true;
            }
        }

        /// <summary>채널과 전체 송수신 기한으로 ASCII 전송을 생성합니다.</summary>
        /// <param name="channel">호출자가 관리하는 채널입니다.</param>
        /// <param name="timeoutMilliseconds">각 송수신 작업의 제한 시간(ms)이며 1 이상입니다.</param>
        /// <exception cref="ArgumentNullException">채널이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">제한 시간이 1ms 미만입니다.</exception>
        public AsciiTransport(IByteChannel channel, int timeoutMilliseconds = 1000)
            : base(channel, timeoutMilliseconds) { }

        /// <inheritdoc/>
        /// <remarks>Transaction ID는 사용하지 않습니다.</remarks>
        public override void Send(FrameContext context, byte[] pdu, int count)
        {
            int length = AsciiFrame.Encode(context.UnitId, pdu, count, _frame);
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
                case ReceiveStatus.FrameTooLong:
                    throw new ModbusProtocolException("ASCII frame exceeds 513 bytes.");
                case ReceiveStatus.InvalidCharacter:
                    throw new ModbusProtocolException("Invalid ASCII hexadecimal character.");
                case ReceiveStatus.LrcMismatch:
                    throw new ModbusProtocolException("ASCII LRC mismatch.");
                case ReceiveStatus.InvalidUnitId:
                    throw new ModbusProtocolException("Invalid serial unit ID.");
                default:
                    throw new ModbusProtocolException("Invalid ASCII frame.");
            }
        }

        /// <inheritdoc/>
        /// <remarks>성공한 context의 Transaction ID는 0입니다. 시작 문자 전의 바이트는 건너뜁니다.</remarks>
        public ReceiveStatus TryReceive(byte[] pdu, out FrameContext context, out int length)
        {
            Protocol.ValidateBuffer(pdu, 0, Protocol.MaxPduLength);
            context = new FrameContext();
            length = 0;
            OperationDeadline deadline = Deadline();
            int count = 0;
            bool received = false;
            while (true)
            {
                int remaining;
                if (!deadline.TryGetRemainingMilliseconds(out remaining))
                    return received ? ReceiveStatus.Timeout : ReceiveStatus.Idle;
                int wait = count > 0 && remaining > 1000 ? 1000 : remaining;
                int read = Channel.Read(_character, 0, 1, wait);
                if (read == 0)
                    return received ? ReceiveStatus.Timeout : ReceiveStatus.Idle;
                if (read != 1)
                    throw new ModbusProtocolException("Invalid channel read count.");
                received = true;
                byte value = _character[0];
                if (value == ':')
                {
                    count = 1;
                    _frame[0] = value;
                    continue;
                }
                if (count == 0)
                    continue;
                if (count == _frame.Length)
                    return ReceiveStatus.FrameTooLong;
                _frame[count++] = value;
                if (value == 10)
                {
                    byte unitId;
                    ReceiveStatus status = AsciiFrame.TryDecode(_frame, count, pdu, out unitId, out length);
                    if (status == ReceiveStatus.Success)
                        context = new FrameContext(unitId, 0);
                    return status;
                }
            }
        }
    }
}
