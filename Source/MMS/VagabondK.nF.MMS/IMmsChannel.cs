namespace VagabondK.nF.MMS
{
    /// <summary>이미 연결된 TCP 바이트 스트림을 MMS 클라이언트에 제공합니다.</summary>
    /// <remarks>연결 수명은 호출자가 관리합니다. TLS·접속·재접속은 이 인터페이스의 범위 밖입니다.</remarks>
    public interface IMmsChannel
    {
        /// <summary>시간 제한 계산에 사용하는 단조 증가 시각(ms)입니다.</summary>
        long Milliseconds { get; }

        /// <summary>제한 시간 안에 일부 데이터를 읽습니다. 0은 EOF입니다.</summary>
        /// <param name="buffer">대상 배열입니다.</param>
        /// <param name="offset">시작 바이트 위치입니다.</param>
        /// <param name="count">최대 바이트 수입니다.</param>
        /// <param name="timeoutMilliseconds">양수 제한 시간(ms)입니다. 시간 초과는 예외로 보고해야 합니다.</param>
        /// <returns>읽은 바이트 수(0~count)입니다.</returns>
        int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds);

        /// <summary>제한 시간 안에 일부 데이터를 씁니다.</summary>
        /// <param name="buffer">원본 배열입니다.</param>
        /// <param name="offset">시작 바이트 위치입니다.</param>
        /// <param name="count">최대 바이트 수입니다.</param>
        /// <param name="timeoutMilliseconds">양수 제한 시간(ms)입니다.</param>
        /// <returns>실제로 보낸 바이트 수(1~count)입니다.</returns>
        int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds);
    }

    /// <summary>준비 대기 시간 초과를 예외 없이 반환하는 선택적 MMS 채널입니다.</summary>
    /// <remarks>false는 시간 초과만 나타냅니다. EOF·소켓·인수 오류를 시간 초과로 숨기지 않습니다.</remarks>
    public interface IMmsTimeoutChannel : IMmsChannel
    {
        /// <summary>읽기 준비 대기 시간이 초과되면 false와 transferred=0을, EOF이면 true와 transferred=0을 반환합니다.</summary>
        /// <param name="buffer">읽은 데이터를 저장할 호출자 소유 배열입니다.</param>
        /// <param name="offset">저장할 첫 바이트의 0 기반 위치입니다.</param>
        /// <param name="count">읽을 최대 바이트 수입니다.</param>
        /// <param name="timeoutMilliseconds">양수 준비 대기 제한 시간(ms)입니다.</param>
        /// <param name="transferred">실제로 읽은 바이트 수이며 준비 대기 시간 초과나 EOF이면 0입니다.</param>
        /// <returns>읽기를 수행했으면 true, 준비 대기 시간이 초과되었으면 false입니다.</returns>
        bool TryRead(byte[] buffer, int offset, int count, int timeoutMilliseconds, out int transferred);

        /// <summary>쓰기 준비 대기 시간이 초과되면 false와 transferred=0을 반환하며, 성공 시 실제 전송량을 반환합니다.</summary>
        /// <param name="buffer">전송할 호출자 소유 배열입니다.</param>
        /// <param name="offset">전송할 첫 바이트의 0 기반 위치입니다.</param>
        /// <param name="count">전송할 최대 바이트 수입니다.</param>
        /// <param name="timeoutMilliseconds">양수 준비 대기 제한 시간(ms)입니다.</param>
        /// <param name="transferred">실제로 전송한 바이트 수이며 준비 대기 시간이 초과되면 0입니다.</param>
        /// <returns>쓰기를 수행했으면 true, 준비 대기 시간이 초과되었으면 false입니다.</returns>
        bool TryWrite(byte[] buffer, int offset, int count, int timeoutMilliseconds, out int transferred);
    }
}
