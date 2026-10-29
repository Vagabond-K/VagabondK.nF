using System;
using System.IO;

namespace VagabondK.nF.MQTT
{
    /// <summary>스트림에서 읽을 데이터(복호화되어 내부 버퍼에 남은 데이터 포함)나 EOF가 생길 때까지 최대 제한 시간만큼 기다립니다.</summary>
    /// <param name="timeoutMilliseconds">기다릴 최대 시간(밀리초)입니다. 구현은 이를 반드시 지켜야 합니다.</param>
    /// <returns>읽을 데이터가 있거나 EOF이면 <see langword="true"/>, 시간이 지나면 <see langword="false"/>입니다.</returns>
    public delegate bool MqttReadReady(int timeoutMilliseconds);
    /// <summary>호출자가 소유한 제한 시간 지원 스트림(인증을 마친 TLS 스트림 포함)을 MQTT 채널로 감쌉니다.</summary>
    /// <remarks><see cref="Stream.Read(byte[], int, int)"/>는 블로킹이므로 읽기 전에 준비 콜백으로 대기합니다.
    /// 준비 콜백은 제한 시간을 지키고, 복호화되어 버퍼에 남은 데이터를 고려하며, EOF를 읽기 가능으로 보고해야 합니다.</remarks>
    public sealed class StreamMqttChannel : IMqttSegmentChannel
    {
        private readonly Stream stream;
        private readonly MqttReadReady readable;
        /// <summary>스트림을 닫거나 인증·인증서 정책을 바꾸지 않고 감쌉니다.</summary>
        /// <remarks>TLS를 쓴다면 이 어댑터를 만들기 전에 인증과 상대 검증을 마쳐야 합니다.</remarks>
        /// <param name="stream">읽기·쓰기·제한 시간을 지원하는 연결된 스트림입니다.</param>
        /// <param name="readable">읽기 전에 데이터 준비를 기다리는 콜백입니다.</param>
        /// <exception cref="ArgumentNullException">인수가 <see langword="null"/>인 경우.</exception>
        /// <exception cref="ArgumentException">스트림이 읽기·쓰기·제한 시간을 모두 지원하지 않는 경우.</exception>
        public StreamMqttChannel(Stream stream, MqttReadReady readable)
        {
            this.stream = stream ?? throw new ArgumentNullException("stream");
            this.readable = readable ?? throw new ArgumentNullException("readable");
            if (!stream.CanRead || !stream.CanWrite || !stream.CanTimeout)
                throw new ArgumentException("A readable, writable timeout-capable stream is required.");
        }
        /// <inheritdoc />
        public long Milliseconds { get { return Environment.TickCount64; } }
        /// <inheritdoc />
        public bool TryRead(byte[] bytes, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            Validate(bytes, offset, count, timeoutMilliseconds);
            if (count == 0) throw new ArgumentOutOfRangeException("count");
            transferred = 0;
            long deadline = Milliseconds + timeoutMilliseconds;
            if (!readable(timeoutMilliseconds)) return false;
            int remaining = (int)(deadline - Milliseconds);
            if (remaining <= 0) return false;
            stream.ReadTimeout = remaining;
            transferred = stream.Read(bytes, offset, count);
            return true;
        }
        /// <inheritdoc />
        public void Write(byte[] bytes, int count, int timeoutMilliseconds)
        { Write(bytes, 0, count, timeoutMilliseconds); }
        /// <inheritdoc />
        public void Write(byte[] bytes, int offset, int count, int timeoutMilliseconds)
        {
            Validate(bytes, offset, count, timeoutMilliseconds);
            stream.WriteTimeout = timeoutMilliseconds;
            stream.Write(bytes, offset, count);
        }
        private static void Validate(byte[] bytes, int offset, int count, int timeout)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (offset < 0 || count < 0 || offset > bytes.Length || count > bytes.Length - offset || timeout <= 0)
                throw new ArgumentOutOfRangeException("offset/count/timeout");
        }
    }
}
