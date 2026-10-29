using System;

namespace VagabondK.nF.Modbus
{
    /// <summary>단조 시계를 사용하여 I/O 작업 전체의 제한 시간을 관리합니다.</summary>
    /// <remarks>시스템 시각 동기화의 영향을 받지 않습니다. 생성자로 초기화한 값을 사용해야 합니다.</remarks>
    public struct OperationDeadline
    {
        private readonly long _expires;

        /// <summary>현재 단조 시각으로부터 만료 기한을 설정합니다.</summary>
        /// <param name="timeoutMilliseconds">허용할 시간이며 단위는 ms입니다. 1 이상이어야 합니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">제한 시간이 1ms 미만입니다.</exception>
        public OperationDeadline(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 1)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            _expires = Now + (long)timeoutMilliseconds * TimeSpan.TicksPerMillisecond;
        }

        private static long Now
        {
            get
            {
                return Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
            }
        }

        /// <summary>남은 시간을 ms로 가져옵니다.</summary>
        /// <exception cref="TimeoutException">기한이 만료되었습니다.</exception>
        public int RemainingMilliseconds
        {
            get
            {
                int remaining;
                if (!TryGetRemainingMilliseconds(out remaining))
                    throw new TimeoutException();
                return remaining;
            }
        }

        /// <summary>예외 없이 남은 시간을 확인합니다.</summary>
        /// <param name="remaining">남은 시간(ms)입니다. 만료되면 0입니다.</param>
        /// <returns>남은 시간이 있으면 <see langword="true"/>, 만료되면 <see langword="false"/>입니다.</returns>
        public bool TryGetRemainingMilliseconds(out int remaining)
        {
            long ticks = _expires - Now;
            remaining = ticks <= 0 ? 0 :
                (int)((ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond);
            return remaining > 0;
        }
    }
}
