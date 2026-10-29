using System;
using System.Net.Sockets;

namespace VagabondK.nF.MMS.TCP
{
    /// <summary>호출자가 소유한 연결된 소켓을 동기 MMS 채널로 사용합니다.</summary>
    /// <remarks>소켓을 연결하거나 해제하지 않습니다. SendTimeout·ReceiveTimeout을 변경하며,
    /// Poll의 준비 대기 시간을 단조 시계로 제한합니다. 모든 접근을 직렬화하고 소켓을 독점 사용해야 합니다.
    /// 소켓 예외는 그대로 전파합니다. 기한 만료 예외의 생성·전파 비용은 반환 시각을 지연시킬 수 있습니다.</remarks>
    public sealed class MmsSocketChannel : IMmsTimeoutChannel
    {
        private readonly Socket socket;

        /// <summary>이미 연결된 Socket을 지정합니다.</summary>
        /// <param name="socket">호출자 소유 TCP Socket입니다.</param>
        /// <exception cref="ArgumentNullException">Socket이 null입니다.</exception>
        public MmsSocketChannel(Socket socket)
        {
            if (socket == null) throw new ArgumentNullException("socket");
            this.socket = socket;
        }

        /// <summary>Environment.TickCount64를 기준으로 한 단조 증가 시각(ms)입니다.</summary>
        public long Milliseconds { get { return Environment.TickCount64; } }

        /// <summary>일부 데이터를 읽으며 0은 TCP EOF입니다.</summary>
        /// <param name="buffer">수신 배열입니다.</param>
        /// <param name="offset">수신 시작 바이트 위치입니다.</param>
        /// <param name="count">최대 수신 바이트 수입니다.</param>
        /// <param name="timeoutMilliseconds">양수 수신 제한 시간(ms)입니다.</param>
        /// <returns>수신한 바이트 수입니다.</returns>
        /// <exception cref="ArgumentNullException">버퍼가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">버퍼 범위가 잘못되었거나 개수 또는 제한 시간이 양수가 아닙니다.</exception>
        /// <exception cref="TimeoutException">읽기 준비 기한이 만료되었습니다.</exception>
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            long started = Milliseconds;
            int transferred;
            if (!TryRead(buffer, offset, count, timeoutMilliseconds, out transferred))
                throw new TimeoutException("MMS socket readiness deadline expired; waited=" + (Milliseconds - started));
            return transferred;
        }

        /// <summary>일부 데이터를 전송합니다.</summary>
        /// <param name="buffer">송신 배열입니다.</param>
        /// <param name="offset">송신 시작 바이트 위치입니다.</param>
        /// <param name="count">최대 송신 바이트 수입니다.</param>
        /// <param name="timeoutMilliseconds">양수 송신 제한 시간(ms)입니다.</param>
        /// <returns>실제로 송신한 바이트 수입니다.</returns>
        /// <exception cref="ArgumentNullException">버퍼가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">버퍼 범위가 잘못되었거나 개수 또는 제한 시간이 양수가 아닙니다.</exception>
        /// <exception cref="TimeoutException">쓰기 준비 기한이 만료되었습니다.</exception>
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            long started = Milliseconds;
            int transferred;
            if (!TryWrite(buffer, offset, count, timeoutMilliseconds, out transferred))
                throw new TimeoutException("MMS socket readiness deadline expired; waited=" + (Milliseconds - started));
            return transferred;
        }

        /// <summary>읽기 준비 시간 초과만 false로 반환합니다. EOF는 true/transferred=0이며 소켓 예외는 전파합니다.</summary>
        /// <param name="buffer">수신할 호출자 소유 배열입니다.</param>
        /// <param name="offset">수신 시작 바이트 위치입니다.</param>
        /// <param name="count">최대 수신 바이트 수이며 양수여야 합니다.</param>
        /// <param name="timeoutMilliseconds">양수 준비 대기 제한 시간(ms)입니다.</param>
        /// <param name="transferred">수신한 바이트 수입니다. 준비 대기 시간 초과 또는 EOF이면 0입니다.</param>
        /// <returns>소켓 읽기를 수행했으면 true, 준비 대기 시간이 만료되었으면 false입니다.</returns>
        /// <exception cref="ArgumentNullException">수신 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">버퍼 범위가 잘못되었거나 개수 또는 제한 시간이 양수가 아닙니다.</exception>
        public bool TryRead(byte[] buffer, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            Validate(buffer, offset, count, timeoutMilliseconds);
            transferred = 0;
            int remaining = Wait(SelectMode.SelectRead, timeoutMilliseconds);
            if (remaining == 0) return false;
            socket.ReceiveTimeout = remaining;
            transferred = socket.Receive(buffer, offset, count, SocketFlags.None);
            return true;
        }

        /// <summary>쓰기 준비 시간 초과만 false로 반환합니다. 소켓 예외는 전파하며 자동 재시도하지 않습니다.</summary>
        /// <param name="buffer">송신할 호출자 소유 배열입니다.</param>
        /// <param name="offset">송신 시작 바이트 위치입니다.</param>
        /// <param name="count">최대 송신 바이트 수이며 양수여야 합니다.</param>
        /// <param name="timeoutMilliseconds">양수 준비 대기 제한 시간(ms)입니다.</param>
        /// <param name="transferred">실제로 송신한 바이트 수입니다. 준비 대기 시간이 초과되면 0입니다.</param>
        /// <returns>소켓 쓰기를 수행했으면 true, 준비 대기 시간이 만료되었으면 false입니다.</returns>
        /// <exception cref="ArgumentNullException">송신 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">버퍼 범위가 잘못되었거나 개수 또는 제한 시간이 양수가 아닙니다.</exception>
        public bool TryWrite(byte[] buffer, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            Validate(buffer, offset, count, timeoutMilliseconds);
            transferred = 0;
            int remaining = Wait(SelectMode.SelectWrite, timeoutMilliseconds);
            if (remaining == 0) return false;
            socket.SendTimeout = remaining;
            transferred = socket.Send(buffer, offset, count, SocketFlags.None);
            return true;
        }

        private int Wait(SelectMode mode, int timeoutMilliseconds)
        {
            long deadline = Milliseconds + timeoutMilliseconds;
            while (true)
            {
                long remaining = deadline - Milliseconds;
                if (remaining <= 0) return 0;
                int microseconds = remaining > int.MaxValue / 1000 ? int.MaxValue : (int)remaining * 1000;
                if (socket.Poll(microseconds, mode))
                {
                    remaining = deadline - Milliseconds;
                    if (remaining <= 0) return 0;
                    return (int)remaining;
                }
            }
        }

        private static void Validate(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 1 || offset > buffer.Length || count > buffer.Length - offset || timeoutMilliseconds < 1)
                throw new ArgumentOutOfRangeException("buffer/count/timeout");
        }
    }
}
