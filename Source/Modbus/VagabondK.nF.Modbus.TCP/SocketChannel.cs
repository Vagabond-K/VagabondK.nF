using System;
using System.Net.Sockets;

namespace VagabondK.nF.Modbus.TCP
{
    /// <summary>이미 연결되었거나 수락된 TCP 소켓을 바이트 스트림 채널로 제공합니다.</summary>
    /// <remarks>호출자는 소켓을 이 채널 전용으로 사용하고 수명을 관리해야 합니다. 이 클래스는 소켓을 닫지 않으며 송수신 제한 시간 설정을 변경합니다.</remarks>
    public sealed class SocketChannel : IByteChannel
    {
        private readonly Socket _socket;

        /// <summary>호출자가 제공한 연결된 TCP 소켓을 사용하는 채널을 생성합니다.</summary>
        /// <param name="socket">이미 연결된 Socket입니다.</param>
        /// <exception cref="ArgumentNullException">Socket이 null입니다.</exception>
        public SocketChannel(Socket socket)
        {
            if (socket == null)
                throw new ArgumentNullException("socket");
            _socket = socket;
        }

        /// <inheritdoc/>
        /// <remarks>읽기 준비를 기다리는 중 제한 시간이 만료되면 0을 반환합니다. 준비 이후 Socket.Receive에서 발생한 예외는 그대로 전달합니다.</remarks>
        /// <exception cref="ModbusProtocolException">상대가 연결을 종료했습니다.</exception>
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            Validate(buffer, offset, count, timeoutMilliseconds);
            OperationDeadline deadline = new OperationDeadline(timeoutMilliseconds);
            int remaining;
            try
            {
                Wait(SelectMode.SelectRead, deadline);
                remaining = deadline.RemainingMilliseconds;
            }
            catch (TimeoutException)
            {
                return 0;
            }
            _socket.ReceiveTimeout = remaining;
            int read = _socket.Receive(buffer, offset, count, SocketFlags.None);
            if (read == 0)
                throw new ModbusProtocolException("TCP peer closed the connection.");
            return read;
        }

        /// <inheritdoc/>
        /// <remarks>부분 송신마다 같은 작업 기한을 사용하여 전체 바이트를 송신합니다.</remarks>
        /// <exception cref="TimeoutException">송신 기한이 만료되었습니다.</exception>
        /// <exception cref="ModbusProtocolException">Socket 송신이 더 진행되지 않습니다.</exception>
        public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            Validate(buffer, offset, count, timeoutMilliseconds);
            OperationDeadline deadline = new OperationDeadline(timeoutMilliseconds);
            while (count > 0)
            {
                Wait(SelectMode.SelectWrite, deadline);
                _socket.SendTimeout = deadline.RemainingMilliseconds;
                int written = _socket.Send(buffer, offset, count, SocketFlags.None);
                if (written == 0)
                    throw new ModbusProtocolException("TCP send made no progress.");
                offset += written;
                count -= written;
            }
        }

        private void Wait(SelectMode mode, OperationDeadline deadline)
        {
            while (true)
            {
                int remaining = deadline.RemainingMilliseconds;
                int microseconds = remaining > int.MaxValue / 1000 ? int.MaxValue : remaining * 1000;
                if (_socket.Poll(microseconds, mode))
                    return;
            }
        }

        private static void Validate(byte[] buffer, int offset, int count, int timeout)
        {
            Protocol.ValidateBuffer(buffer, offset, count);
            if (count == 0 || timeout < 1)
                throw new ArgumentOutOfRangeException("count");
        }
    }
}
