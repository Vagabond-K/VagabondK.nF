using System;

namespace VagabondK.nF.DNP3
{
    /// <summary>바이트 스트림에서 CRC가 유효한 DNP3 Link Frame을 추출합니다.</summary>
    /// <remarks>호출자는 이 인스턴스에 대한 접근을 직렬화해야 합니다. 시각 인수에는 동일한 기준의 단조 증가 밀리초 시각을 사용합니다.
    /// 잘못된 바이트와 수신 기한이 만료된 부분 프레임은 폐기하면서 다음 프레임 시작점을 찾습니다.</remarks>
    public sealed class Dnp3StreamDecoder
    {
        private readonly byte[] _buffer = new byte[292];
        private readonly int _timeout;
        private int _count;
        private long _started;

        /// <summary>재동기화 과정에서 폐기한 누적 바이트 수입니다.</summary>
        public long DiscardedBytes { get; private set; }

        /// <summary>헤더 또는 데이터 CRC 검사에 실패한 누적 횟수입니다.</summary>
        public long CrcErrors { get; private set; }

        /// <summary>부분 프레임의 수신 제한 시간 만료를 감지하여 바이트를 폐기한 누적 횟수입니다.</summary>
        public long FrameTimeouts { get; private set; }

        /// <summary>프레임 수신 제한 시간을 지정하여 디코더를 생성합니다.</summary>
        /// <param name="frameTimeoutMilliseconds">부분 Frame의 수신 제한 시간입니다. 1밀리초 이상입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">제한 시간이 1밀리초 미만입니다.</exception>
        public Dnp3StreamDecoder(int frameTimeoutMilliseconds = 3000)
        {
            if (frameTimeoutMilliseconds < 1)
                throw new ArgumentOutOfRangeException("frameTimeoutMilliseconds");
            _timeout = frameTimeoutMilliseconds;
        }

        /// <summary>수신 중인 바이트를 비웁니다. 누적 진단 카운터는 유지합니다.</summary>
        public void Reset()
        {
            _count = 0;
        }

        private void Drop(int count, long now)
        {
            Array.Copy(_buffer, count, _buffer, 0, _count - count);
            _count -= count;
            DiscardedBytes += count;
            _started = now;
        }

        /// <summary>한 바이트를 추가하고 추출 가능한 첫 Frame을 반환합니다.</summary>
        /// <param name="value">수신한 바이트입니다.</param>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>CRC가 유효한 완전한 Frame의 새 배열이며, 아직 없으면 null입니다.</returns>
        /// <remarks>반환 후 <see cref="Take"/>를 null이 나올 때까지 호출하여 남은 Frame을 배출합니다.</remarks>
        /// <exception cref="InvalidOperationException">292바이트 버퍼가 가득 찼습니다. 추가 입력 전에 Frame을 배출해야 합니다.</exception>
        public byte[] Push(byte value, long now)
        {
            if (_count > 0 && now - _started >= _timeout)
            {
                FrameTimeouts++;
                Drop(1, now);
            }
            if (_count == _buffer.Length)
                throw new InvalidOperationException("Drain decoded DNP3 frames before pushing more bytes.");
            if (_count == 0)
                _started = now;
            _buffer[_count++] = value;
            return Take(now);
        }

        /// <summary>버퍼에서 Frame을 추출하고 잘못되었거나 만료된 부분 데이터를 정리합니다.</summary>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>CRC가 유효한 완전한 Frame의 새 배열이며, 없으면 null입니다.</returns>
        public byte[] Take(long now)
        {
            while (_count > 0)
            {
                if (_buffer[0] != 5 || _count >= 2 && _buffer[1] != 0x64 || _count >= 3 && _buffer[2] < 5)
                {
                    Drop(1, now);
                    continue;
                }
                if (_count >= 10)
                {
                    if (Dnp3Channel.Word(_buffer, 8) != Dnp3Link.Crc(_buffer, 0, 8))
                    {
                        CrcErrors++;
                        Drop(1, now);
                        continue;
                    }
                    int length = Dnp3Link.FrameLength(_buffer);
                    if (_count >= length)
                    {
                        bool valid = true;
                        int data = _buffer[2] - 5;
                        for (int p = 10; data > 0;)
                        {
                            int size = data > 16 ? 16 : data;
                            if (Dnp3Channel.Word(_buffer, p + size) != Dnp3Link.Crc(_buffer, p, size))
                                valid = false;
                            p += size + 2;
                            data -= size;
                        }
                        if (!valid)
                        {
                            CrcErrors++;
                            Drop(1, now);
                            continue;
                        }
                        var frame = new byte[length];
                        Array.Copy(_buffer, frame, length);
                        Array.Copy(_buffer, length, _buffer, 0, _count - length);
                        _count -= length;
                        _started = now;
                        return frame;
                    }
                }
                if (now - _started >= _timeout)
                {
                    FrameTimeouts++;
                    Drop(1, now);
                    continue;
                }
                return null;
            }
            return null;
        }
    }
}
