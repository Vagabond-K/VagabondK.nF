using System;

namespace VagabondK.nF.DNP3.Serial
{
    /// <summary>DNP3 Serial 세션이 사용하는 바이트 송수신 및 시간 기준 계약입니다.</summary>
    /// <remarks>구현은 지정된 버퍼 범위와 양수인 제한 시간을 처리해야 합니다. RS485에서는 마지막 정지 비트까지 송신 방향을 유지해야 합니다.
    /// 채널의 열기, 닫기 및 수명 관리는 호출자가 담당합니다.</remarks>
    public interface IDnp3ByteChannel
    {
        /// <summary>세션의 Timeout 계산에 사용하는 단조 증가 밀리초 시각입니다.</summary>
        long Milliseconds { get; }

        /// <summary>제한 시간 내 수신 가능한 바이트를 읽습니다.</summary>
        /// <param name="buffer">수신 데이터를 기록할 배열입니다.</param>
        /// <param name="offset">기록할 0 기반 바이트 위치입니다.</param>
        /// <param name="count">기록할 최대 바이트 수입니다. 1 이상이며 배열 범위 안이어야 합니다.</param>
        /// <param name="timeoutMilliseconds">양수인 수신 대기 제한 시간입니다. 단위는 밀리초입니다.</param>
        /// <returns>실제로 읽은 0~count 바이트 수입니다. 0은 제한 시간 내 수신한 바이트가 없음을 뜻합니다.</returns>
        int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds);

        /// <summary>지정된 바이트 범위를 모두 전송하거나 실패 시 예외를 발생시킵니다.</summary>
        /// <param name="buffer">전송할 데이터 배열입니다.</param>
        /// <param name="offset">전송할 0 기반 바이트 위치입니다.</param>
        /// <param name="count">전송할 바이트 수입니다. 1 이상이며 배열 범위 안이어야 합니다.</param>
        /// <param name="timeoutMilliseconds">양수인 전송 제한 시간입니다. 단위는 밀리초입니다.</param>
        /// <exception cref="TimeoutException">전체 쓰기를 완료하기 전에 제한 시간이 만료되었습니다.</exception>
        void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds);
    }

    /// <summary>한 상대 장치와 직렬 통신으로 DNP3 프레임을 송수신하며, 선택적으로 Confirmed Link 재전송을 수행합니다.</summary>
    /// <remarks>호출자는 모든 호출과 Endpoint 접근을 직렬화해야 합니다. 채널과 Endpoint는 호출자 소유이며 세션이 열거나 닫지 않습니다.
    /// Master 요청을 직접 시작할 때도 채널의 Milliseconds와 같은 시간 기준을 사용합니다.</remarks>
    public sealed class Dnp3SerialSession
    {
        private readonly IDnp3ByteChannel _io;
        private readonly IDnp3Endpoint _endpoint;
        private readonly Dnp3StreamDecoder _decoder;
        private readonly byte[] _queue, _input = new byte[64];
        private readonly bool _confirmed;
        private readonly int _ackTimeout, _writeTimeout, _maximumRetries;
        private int _queued, _attempts;
        private byte[] _pending;
        private bool _ready, _resetPending, _fcb = true;
        private long _deadline;

        /// <summary>처리 실패 후 Reset이 필요하면 true입니다.</summary>
        public bool Faulted { get; private set; }

        /// <summary>송신 큐가 비어 있고 대기 중인 링크 ACK도 없으면 true입니다. Endpoint 요청의 완료 여부와는 다릅니다.</summary>
        public bool TransmitIdle
        {
            get
            {
                return _pending == null && _queued == 0;
            }
        }

        /// <summary>ACK 대기 시간이 만료되어 링크 프레임을 다시 보낸 누적 횟수입니다.</summary>
        public long Retransmissions { get; private set; }

        /// <summary>주소 또는 방향이 현재 세션과 달라 무시한 유효 Frame의 누적 개수입니다.</summary>
        public long ForeignFrames { get; private set; }

        /// <summary>세션의 Frame Decoder입니다. 진단용으로 사용하고 세션과 동시에 조작하지 않습니다.</summary>
        public Dnp3StreamDecoder Decoder
        {
            get
            {
                return _decoder;
            }
        }

        /// <summary>채널, Endpoint 및 Serial Link 전송 정책으로 세션을 생성합니다.</summary>
        /// <param name="channel">바이트 송수신 및 단조 증가 밀리초 시각을 제공하는 채널입니다.</param>
        /// <param name="endpoint">전용으로 사용할 Master 또는 Outstation Endpoint입니다.</param>
        /// <param name="confirmedLink">true이면 Link Reset과 Confirmed User Data의 ACK/재시도를 사용합니다.</param>
        /// <param name="ackTimeoutMilliseconds">각 Link ACK의 대기 시간입니다. 1밀리초 이상입니다.</param>
        /// <param name="retries">ACK 대기 시간 만료 후 추가로 재전송할 횟수입니다. 0~10입니다.</param>
        /// <param name="frameTimeoutMilliseconds">부분 Link Frame의 수신 제한 시간입니다. 1밀리초 이상입니다.</param>
        /// <param name="writeTimeoutMilliseconds">채널의 각 Write에 전달하는 제한 시간입니다. 1밀리초 이상입니다.</param>
        /// <param name="queueCapacity">송신 대기 Frame을 보관할 버퍼 바이트 수입니다. 292~32768입니다.</param>
        /// <exception cref="ArgumentNullException">채널 또는 Endpoint가 null입니다.</exception>
        /// <exception cref="ArgumentException">ACK 대기 시간, 쓰기 제한 시간, 재시도 횟수 또는 큐 용량이 유효하지 않습니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">프레임 수신 제한 시간이 1밀리초 미만입니다.</exception>
        public Dnp3SerialSession(IDnp3ByteChannel channel, IDnp3Endpoint endpoint, bool confirmedLink = true,
            int ackTimeoutMilliseconds = 1000, int retries = 2, int frameTimeoutMilliseconds = 3000,
            int writeTimeoutMilliseconds = 3000, int queueCapacity = 8192)
        {
            if (channel == null || endpoint == null)
                throw new ArgumentNullException("dependency");
            if (ackTimeoutMilliseconds < 1 || retries < 0 || retries > 10 || writeTimeoutMilliseconds < 1 ||
                queueCapacity < 292 || queueCapacity > 32768)
                throw new ArgumentException("DNP3 serial limits.");
            _io = channel;
            _endpoint = endpoint;
            _confirmed = confirmedLink;
            _ackTimeout = ackTimeoutMilliseconds;
            _maximumRetries = retries;
            _writeTimeout = writeTimeoutMilliseconds;
            _queue = new byte[queueCapacity];
            _decoder = new Dnp3StreamDecoder(frameTimeoutMilliseconds);
        }

        /// <summary>Endpoint와 수신 버퍼, 송신 큐 및 Link 상태를 초기화하고 Faulted를 해제합니다.</summary>
        /// <remarks>대기 중인 송신은 폐기합니다. 진단 카운터는 유지하며 채널을 열거나 닫지 않습니다.</remarks>
        public void Reset()
        {
            _endpoint.ResetConnection();
            _decoder.Reset();
            _queued = 0;
            _pending = null;
            _ready = false;
            _fcb = true;
            _resetPending = false;
            Faulted = false;
        }

        private void Healthy()
        {
            if (Faulted)
                throw new InvalidOperationException("Reset the faulted DNP3 serial session before reuse.");
        }

        /// <summary>Endpoint가 생성한 Frame 배열을 검사하여 송신 큐에 추가합니다.</summary>
        /// <param name="frames">완전한 프레임들이 연결된 배열입니다. Master 작업이 반환한 배열 전체를 전달합니다. null이면 아무 작업도 하지 않습니다.</param>
        /// <remarks>Primary Frame은 Unconfirmed User Data만 받아 큐에 복사하며, Secondary Frame은 즉시 씁니다.
        /// 실제 Primary 전송과 Confirmed Link 변환은 RunOnce가 수행하므로 호출자가 별도로 전송하면 안 됩니다.
        /// 처리 실패 시 Faulted가 설정됩니다.</remarks>
        /// <exception cref="InvalidOperationException">Faulted 상태이거나 송신 큐의 남은 용량이 부족합니다.</exception>
        /// <exception cref="Dnp3ProtocolException">Frame 길이, CRC, 주소, 방향 또는 Primary 기능이 잘못되었습니다.</exception>
        /// <exception cref="TimeoutException">즉시 전송하는 Link 응답의 채널 쓰기 시간이 만료되었습니다.</exception>
        public void Send(byte[] frames)
        {
            Healthy();
            try
            {
                Enqueue(frames);
            }
            catch
            {
                Faulted = true;
                throw;
            }
        }

        private void Enqueue(byte[] frames)
        {
            if (frames == null)
                return;
            int needed = 0;
            for (int p = 0; p < frames.Length;)
            {
                if (frames.Length - p < 10)
                    throw new Dnp3ProtocolException();
                var header = new byte[3];
                Array.Copy(frames, p, header, 0, 3);
                int length = Dnp3Link.FrameLength(header);
                Dnp3Channel.Require(length <= frames.Length - p && Dnp3Channel.Word(frames, p + 8) == Dnp3Link.Crc(frames, p, 8));
                int data = frames[p + 2] - 5;
                for (int block = p + 10; data > 0;)
                {
                    int size = data > 16 ? 16 : data;
                    Dnp3Channel.Require(Dnp3Channel.Word(frames, block + size) == Dnp3Link.Crc(frames, block, size));
                    block += size + 2;
                    data -= size;
                }
                Dnp3Channel.Require(Dnp3Channel.Word(frames, p + 4) == _endpoint.RemoteAddress &&
                    Dnp3Channel.Word(frames, p + 6) == _endpoint.LocalAddress &&
                    ((frames[p + 3] & 0x80) != 0) == _endpoint.IsMaster);
                if ((frames[p + 3] & 0x40) != 0)
                {
                    Dnp3Channel.Require((frames[p + 3] & 0x3f) == 4);
                    needed += length;
                }
                p += length;
            }
            if (needed > _queue.Length - _queued)
                throw new InvalidOperationException("DNP3 serial transmit queue capacity exceeded.");
            for (int p = 0; p < frames.Length;)
            {
                int data = frames[p + 2] - 5, length = 10 + data + 2 * ((data + 15) / 16);
                if ((frames[p + 3] & 0x40) == 0)
                    Write(frames, p, length);
                else
                {
                    Array.Copy(frames, p, _queue, _queued, length);
                    _queued += length;
                }
                p += length;
            }
        }

        private void Write(byte[] bytes, int offset, int count)
        {
            _io.Write(bytes, offset, count, _writeTimeout);
        }

        private byte[] ResetFrame()
        {
            var frame = new byte[10];
            frame[0] = 5;
            frame[1] = 0x64;
            frame[2] = 5;
            frame[3] = _endpoint.IsMaster ? (byte)0xc0 : (byte)0x40;
            Dnp3Channel.PutWord(frame, 4, _endpoint.RemoteAddress);
            Dnp3Channel.PutWord(frame, 6, _endpoint.LocalAddress);
            Dnp3Channel.PutWord(frame, 8, Dnp3Link.Crc(frame, 0, 8));
            return frame;
        }

        private void Pump()
        {
            long now = _io.Milliseconds;
            if (_pending != null)
            {
                if (now < _deadline)
                    return;
                if (_attempts++ >= _maximumRetries)
                    throw new TimeoutException("DNP3 serial link acknowledgement retries exhausted; reset required.");
                Retransmissions++;
                Write(_pending, 0, _pending.Length);
                _deadline = _io.Milliseconds + _ackTimeout;
                return;
            }
            if (_queued == 0)
                return;
            if (_confirmed && !_ready)
            {
                _pending = ResetFrame();
                _resetPending = true;
            }
            else
            {
                int data = _queue[2] - 5, length = 10 + data + 2 * ((data + 15) / 16);
                var frame = new byte[length];
                Array.Copy(_queue, frame, length);
                Array.Copy(_queue, length, _queue, 0, _queued - length);
                _queued -= length;
                if (_confirmed)
                {
                    frame[3] = (byte)((_endpoint.IsMaster ? 0xc0 : 0x40) | 0x13 | (_fcb ? 0x20 : 0));
                    Dnp3Channel.PutWord(frame, 8, Dnp3Link.Crc(frame, 0, 8));
                    _pending = frame;
                }
                else
                {
                    Write(frame, 0, frame.Length);
                    return;
                }
            }
            _attempts = 0;
            Write(_pending, 0, _pending.Length);
            _deadline = _io.Milliseconds + _ackTimeout;
        }

        private void Receive(byte[] frame)
        {
            if (Dnp3Channel.Word(frame, 4) != _endpoint.LocalAddress || Dnp3Channel.Word(frame, 6) != _endpoint.RemoteAddress ||
                ((frame[3] & 0x80) != 0) == _endpoint.IsMaster)
            {
                ForeignFrames++;
                return;
            }
            if ((frame[3] & 0x40) == 0)
            {
                Dnp3Channel.Require(frame.Length == 10 && (frame[3] & 0x20) == 0);
                int function = frame[3] & 15;
                Dnp3Channel.Require(function == 0 || function == 1 || function == 11 || function == 15);
                if (_pending == null)
                    return;
                if ((frame[3] & 0x10) != 0 || function == 1 || function == 15)
                    throw new InvalidOperationException("DNP3 serial peer rejected the link frame or reported busy; reset required.");
                if (function != 0)
                    return;
                if (_resetPending)
                {
                    _ready = true;
                    _fcb = true;
                    _resetPending = false;
                }
                else
                    _fcb = !_fcb;
                _pending = null;
                return;
            }
            Enqueue(_endpoint.Process(frame, _io.Milliseconds));
        }

        /// <summary>한 번의 수신, Endpoint 처리 및 송신/ACK 재시도를 진행합니다.</summary>
        /// <param name="readTimeoutMilliseconds">채널 Read 대기 시간입니다. 1~ackTimeoutMilliseconds 범위의 밀리초입니다.</param>
        /// <remarks>읽기 대기 시간은 전체 호출 시간의 상한이 아닙니다. 한 번에 최대 64바이트를 읽습니다.
        /// 송신 큐가 비어 있고 대기 중인 ACK도 없을 때만 Endpoint의 Poll을 호출합니다.
        /// 인수 검사 이후 처리 중 예외는 Faulted를 설정한 뒤 전파되므로 재사용 전에 Reset해야 합니다.
        /// 채널 및 사용자 Endpoint의 다른 예외도 전파됩니다.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">읽기 Timeout이 허용 범위를 벗어납니다.</exception>
        /// <exception cref="InvalidOperationException">Faulted 상태, 잘못된 채널 반환 수,
        /// 큐 용량 초과 또는 상대의 Link 거부/Busy 상태입니다.</exception>
        /// <exception cref="TimeoutException">ACK 재시도를 소진했거나 쓰기 또는 Endpoint Timeout이 발생했습니다.</exception>
        /// <exception cref="Dnp3ProtocolException">수신 또는 Endpoint가 생성한 Frame이 유효하지 않습니다.</exception>
        public void RunOnce(int readTimeoutMilliseconds = 1)
        {
            Healthy();
            if (readTimeoutMilliseconds < 1 || readTimeoutMilliseconds > _ackTimeout)
                throw new ArgumentOutOfRangeException("readTimeoutMilliseconds");
            try
            {
                int read = _io.Read(_input, 0, _input.Length, readTimeoutMilliseconds);
                if (read < 0 || read > _input.Length)
                    throw new InvalidOperationException("Invalid DNP3 byte channel read count.");
                for (int i = 0; i < read; i++)
                {
                    byte[] frame = _decoder.Push(_input[i], _io.Milliseconds);
                    while (frame != null)
                    {
                        Receive(frame);
                        frame = _decoder.Take(_io.Milliseconds);
                    }
                }
                byte[] expired = _decoder.Take(_io.Milliseconds);
                while (expired != null)
                {
                    Receive(expired);
                    expired = _decoder.Take(_io.Milliseconds);
                }
                if (TransmitIdle)
                    Enqueue(_endpoint.Poll(_io.Milliseconds));
                Pump();
            }
            catch
            {
                Faulted = true;
                throw;
            }
        }
    }
}
