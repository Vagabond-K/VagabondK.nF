using System;

namespace VagabondK.nF.Modbus.TCP
{
    /// <summary>MBAP 헤더를 사용하는 Modbus TCP 전송입니다.</summary>
    /// <remarks>호출자는 이 인스턴스에 대한 접근을 직렬화해야 하며, 연결의 생성·수락·해제를 담당합니다.</remarks>
    public sealed class TcpTransport : ChannelTransport
    {
        private readonly byte[] _frame = new byte[260];

        /// <summary>Serial 주소 및 Broadcast 규칙을 사용하지 않으므로 항상 false입니다.</summary>
        public override bool IsSerial
        {
            get
            {
                return false;
            }
        }

        /// <summary>연결된 바이트 스트림 채널로 TCP 전송을 생성합니다.</summary>
        /// <param name="channel">호출자가 소유한 채널입니다. 일반적으로 SocketChannel을 사용합니다.</param>
        /// <param name="timeoutMilliseconds">각 송수신 작업 전체의 제한 시간(ms)이며 1 이상입니다.</param>
        /// <exception cref="ArgumentNullException">채널이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">제한 시간이 1ms 미만입니다.</exception>
        public TcpTransport(IByteChannel channel, int timeoutMilliseconds = 1000)
            : base(channel, timeoutMilliseconds) { }

        /// <inheritdoc/>
        /// <remarks>Protocol ID 0인 MBAP 헤더를 생성하며 Unit ID와 Transaction ID를 그대로 기록합니다.</remarks>
        public override void Send(FrameContext context, byte[] pdu, int count)
        {
            Protocol.ValidatePdu(pdu, count);
            Protocol.WriteUInt16(_frame, 0, context.TransactionId);
            _frame[2] = 0;
            _frame[3] = 0;
            Protocol.WriteUInt16(_frame, 4, (ushort)(count + 1));
            _frame[6] = context.UnitId;
            Array.Copy(pdu, 0, _frame, 7, count);
            Channel.Write(_frame, 0, count + 7, TimeoutMilliseconds);
        }

        /// <inheritdoc/>
        /// <remarks>잘못된 MBAP 헤더를 수신한 후에는 스트림의 프레임 경계를 복구하지 않습니다. 호출자는 해당 연결을 닫아야 합니다.</remarks>
        public override int Receive(byte[] pdu, out FrameContext context)
        {
            Protocol.ValidateBuffer(pdu, 0, Protocol.MaxPduLength);
            OperationDeadline deadline = Deadline();
            ReadExactly(_frame, 0, 7, deadline);
            int length = Protocol.ReadUInt16(_frame, 4);
            if (_frame[2] != 0 || _frame[3] != 0 || length < 2 || length > 254)
                throw new ModbusProtocolException("Invalid MBAP header. Close the connection.");
            context = new FrameContext(_frame[6], Protocol.ReadUInt16(_frame, 0));
            ReadExactly(pdu, 0, length - 1, deadline);
            return length - 1;
        }
    }
}
