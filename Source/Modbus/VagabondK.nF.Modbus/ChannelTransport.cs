using System;

namespace VagabondK.nF.Modbus
{
    /// <summary>바이트 스트림 채널과 작업 기한 관리를 제공하는 Modbus 전송의 기반 클래스입니다.</summary>
    /// <remarks>호출자는 이 인스턴스에 대한 접근을 직렬화해야 합니다. 이 클래스는 채널을 해제하지 않습니다.</remarks>
    public abstract class ChannelTransport : IModbusTransport
    {
        /// <summary>호출자가 수명을 관리하는 송수신 채널입니다.</summary>
        protected readonly IByteChannel Channel;

        /// <summary>각 송수신 작업에 적용할 제한 시간(ms)을 가져옵니다.</summary>
        public int TimeoutMilliseconds { get; private set; }

        /// <inheritdoc/>
        public abstract bool IsSerial { get; }

        /// <summary>전송에 사용할 채널과 제한 시간을 설정합니다.</summary>
        /// <param name="channel">호출자가 소유하는 채널입니다.</param>
        /// <param name="timeoutMilliseconds">작업 기한이며 단위는 ms입니다. 1 이상이어야 합니다.</param>
        /// <exception cref="ArgumentNullException">채널이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">제한 시간이 1ms 미만입니다.</exception>
        protected ChannelTransport(IByteChannel channel, int timeoutMilliseconds)
        {
            if (channel == null)
                throw new ArgumentNullException("channel");
            if (timeoutMilliseconds < 1)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            Channel = channel;
            TimeoutMilliseconds = timeoutMilliseconds;
        }

        /// <summary>설정한 제한 시간을 적용한 새 작업 기한을 생성합니다.</summary>
        /// <returns>현재 시각부터 시작하는 작업 기한입니다.</returns>
        protected OperationDeadline Deadline()
        {
            return new OperationDeadline(TimeoutMilliseconds);
        }

        /// <summary>공유 기한 안에서 지정한 바이트 수를 모두 읽습니다.</summary>
        /// <param name="buffer">수신 버퍼입니다.</param>
        /// <param name="offset">저장할 시작 인덱스입니다.</param>
        /// <param name="count">읽을 바이트 수입니다. 버퍼 범위는 호출자가 보장해야 합니다.</param>
        /// <param name="deadline">부분 읽기 사이에서도 연장하지 않는 작업 기한입니다.</param>
        /// <exception cref="TimeoutException">모든 바이트를 읽기 전에 기한이 만료되었습니다.</exception>
        /// <exception cref="ModbusProtocolException">채널이 유효하지 않은 읽기 길이를 반환했습니다.</exception>
        protected void ReadExactly(byte[] buffer, int offset, int count, OperationDeadline deadline)
        {
            while (count > 0)
            {
                int read = Channel.Read(buffer, offset, count, deadline.RemainingMilliseconds);
                if (read == 0)
                    throw new TimeoutException();
                if (read < 0 || read > count)
                    throw new ModbusProtocolException("Invalid channel read count.");
                offset += read;
                count -= read;
            }
        }

        /// <inheritdoc/>
        public abstract void Send(FrameContext context, byte[] pdu, int count);

        /// <inheritdoc/>
        public abstract int Receive(byte[] pdu, out FrameContext context);
    }
}
