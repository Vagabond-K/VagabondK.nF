using System;

namespace VagabondK.nF.DNP3
{
    /// <summary>한 상대 장치와의 DNP3 링크 처리 및 전송 계층의 분할·재조립을 수행합니다.</summary>
    /// <remarks>호출자는 모든 접근을 직렬화해야 합니다. 반환된 프레임은 순서대로 전송하고,
    /// 모든 시각 인수에는 동일한 기준의 단조 증가 밀리초 시각을 사용해야 합니다.</remarks>
    public sealed class Dnp3Channel
    {
        private readonly ushort _local, _remote;
        private readonly bool _master;
        private readonly byte[] _assembly;
        private readonly int _timeout;
        private int _length;
        private byte _receiveSequence, _sendSequence;
        private long _started;
        private bool _receiving, _reset, _nextFcb = true;
        private byte[] _confirmed, _confirmedReply;

        /// <summary>로컬 장치의 Link 주소입니다.</summary>
        public ushort LocalAddress
        {
            get
            {
                return _local;
            }
        }

        /// <summary>상대 장치의 Link 주소입니다.</summary>
        public ushort RemoteAddress
        {
            get
            {
                return _remote;
            }
        }

        /// <summary>로컬 장치가 Master인지 나타냅니다.</summary>
        public bool IsMaster
        {
            get
            {
                return _master;
            }
        }

        /// <summary>주소와 Transport 제한을 지정하여 채널을 생성합니다.</summary>
        /// <param name="local">0~65519 범위의 로컬 주소입니다.</param>
        /// <param name="remote">0~65519 범위이며 로컬 주소와 다른 상대 주소입니다.</param>
        /// <param name="master">로컬 장치가 Master이면 true입니다.</param>
        /// <param name="maximumFragment">재조립 및 송신 가능한 Application Fragment의 최대 바이트 수입니다. 64~8192입니다.</param>
        /// <param name="timeoutMilliseconds">첫 Transport 세그먼트부터 재조립 완료까지 허용할 시간입니다. 1밀리초 이상입니다.</param>
        /// <exception cref="ArgumentException">주소 또는 채널 제한이 유효하지 않습니다.</exception>
        public Dnp3Channel(ushort local, ushort remote, bool master, int maximumFragment = 2048, int timeoutMilliseconds = 3000)
        {
            if (local >= 65520 || remote >= 65520 || local == remote || maximumFragment < 64 ||
                maximumFragment > 8192 || timeoutMilliseconds < 1)
                throw new ArgumentException("DNP3 channel limits.");
            _local = local;
            _remote = remote;
            _master = master;
            _assembly = new byte[maximumFragment];
            _timeout = timeoutMilliseconds;
        }

        internal static void Require(bool condition)
        {
            if (!condition)
                throw new Dnp3ProtocolException();
        }

        internal static int Word(byte[] bytes, int p)
        {
            return bytes[p] | (bytes[p + 1] << 8);
        }

        internal static void PutWord(byte[] bytes, int p, int value)
        {
            bytes[p] = (byte)value;
            bytes[p + 1] = (byte)(value >> 8);
        }

        internal static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        /// <summary>재조립, Link 확인 및 송신 Transport Sequence 상태를 초기화합니다.</summary>
        public void Reset()
        {
            _length = 0;
            _receiving = false;
            _reset = false;
            _nextFcb = true;
            _sendSequence = 0;
            _confirmed = null;
            _confirmedReply = null;
        }

        /// <summary>진행 중인 전송 계층 재조립의 제한 시간이 만료되었는지 확인합니다.</summary>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <exception cref="TimeoutException">재조립 시간이 만료되어 부분 메시지를 폐기했습니다.</exception>
        public void CheckTimeout(long now)
        {
            if (_receiving && now - _started >= _timeout)
            {
                _receiving = false;
                _length = 0;
                throw new TimeoutException("DNP3 transport fragment timed out.");
            }
        }

        private byte[] Link(byte control, byte[] data)
        {
            int count = data == null ? 0 : data.Length;
            var frame = new byte[10 + count + 2 * ((count + 15) / 16)];
            frame[0] = 5;
            frame[1] = 0x64;
            frame[2] = (byte)(count + 5);
            frame[3] = control;
            PutWord(frame, 4, _remote);
            PutWord(frame, 6, _local);
            PutWord(frame, 8, Dnp3Link.Crc(frame, 0, 8));
            for (int offset = 0, p = 10; offset < count;)
            {
                int size = count - offset > 16 ? 16 : count - offset;
                Array.Copy(data, offset, frame, p, size);
                PutWord(frame, p + size, Dnp3Link.Crc(data, offset, size));
                p += size + 2;
                offset += size;
            }
            return frame;
        }

        internal static byte[] Join(byte[] first, byte[] second)
        {
            if (first == null)
                return second;
            if (second == null)
                return first;
            var bytes = new byte[first.Length + second.Length];
            Array.Copy(first, bytes, first.Length);
            Array.Copy(second, 0, bytes, first.Length, second.Length);
            return bytes;
        }

        /// <summary>Application Fragment를 Transport 세그먼트와 CRC가 포함된 Link Frame들로 인코딩합니다.</summary>
        /// <param name="application">2바이트 이상이며 생성 시 지정한 최대 Fragment 크기 이하인 배열입니다.</param>
        /// <returns>순서대로 전송할 Link Frame들이 연결된 새 배열입니다.</returns>
        /// <remarks>송신에는 Unconfirmed User Data를 사용합니다. Application 내용의 의미는 검증하지 않습니다.</remarks>
        /// <exception cref="ArgumentException">배열이 null이거나 Fragment 길이가 허용 범위를 벗어납니다.</exception>
        public byte[] Encode(byte[] application)
        {
            if (application == null || application.Length < 2 || application.Length > _assembly.Length)
                throw new ArgumentException("DNP3 application fragment size.");
            int wireLength = 0;
            for (int offset = 0; offset < application.Length; offset += 249)
            {
                int dataLength = (application.Length - offset > 249 ? 249 : application.Length - offset) + 1;
                wireLength += 10 + dataLength + 2 * ((dataLength + 15) / 16);
            }
            var result = new byte[wireLength];
            int position = 0;
            for (int offset = 0; offset < application.Length;)
            {
                int count = application.Length - offset > 249 ? 249 : application.Length - offset;
                var data = new byte[count + 1];
                data[0] = (byte)((offset == 0 ? 0x40 : 0) | (offset + count == application.Length ? 0x80 : 0) | (_sendSequence++ & 63));
                Array.Copy(application, offset, data, 1, count);
                byte[] frame = Link(_master ? (byte)0xc4 : (byte)0x44, data);
                Array.Copy(frame, 0, result, position, frame.Length);
                position += frame.Length;
                offset += count;
            }
            return result;
        }

        /// <summary>하나의 Link Frame을 검사하고 Application Fragment를 재조립합니다.</summary>
        /// <param name="frame">헤더와 데이터 CRC를 포함하는 완전한 Link Frame입니다.</param>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <param name="linkReply">전송할 Link 응답이며, 응답이 필요 없으면 null입니다.</param>
        /// <returns>완성된 Application Fragment의 새 배열입니다. 다른 주소, Link 전용 메시지,
        /// 중복 Frame 또는 아직 재조립 중인 경우 null입니다.</returns>
        /// <exception cref="ArgumentNullException">Frame이 null입니다.</exception>
        /// <exception cref="Dnp3ProtocolException">CRC, 길이, 방향, 기능, Sequence 또는 재조립 크기가 유효하지 않습니다.</exception>
        /// <exception cref="TimeoutException">이전 전송 계층 재조립의 제한 시간이 만료되었습니다.</exception>
        public byte[] Receive(byte[] frame, long now, out byte[] linkReply)
        {
            linkReply = null;
            if (frame == null)
                throw new ArgumentNullException("frame");
            CheckTimeout(now);
            Require(frame.Length >= 10 && frame.Length == Dnp3Link.FrameLength(frame) && Word(frame, 8) == Dnp3Link.Crc(frame, 0, 8));
            int length = frame[2] - 5;
            var data = new byte[length];
            for (int offset = 0, p = 10; offset < length;)
            {
                int size = length - offset > 16 ? 16 : length - offset;
                Require(Word(frame, p + size) == Dnp3Link.Crc(frame, p, size));
                Array.Copy(frame, p, data, offset, size);
                offset += size;
                p += size + 2;
            }
            if (Word(frame, 4) != _local || Word(frame, 6) != _remote)
                return null;
            byte control = frame[3];
            Require(((control & 0x80) != 0) != _master);
            int function = control & 15;
            if ((control & 0x40) == 0)
            {
                Require(length == 0 && (control & 0x30) == 0 && (function == 0 || function == 1 || function == 11));
                return null;
            }
            if (function == 0 || function == 9)
            {
                Require(length == 0 && (control & 0x30) == 0);
                if (function == 0)
                {
                    _reset = true;
                    _nextFcb = true;
                    _confirmed = null;
                    _confirmedReply = null;
                    _length = 0;
                    _receiving = false;
                }
                linkReply = Link((byte)((_master ? 0x80 : 0) | (function == 9 ? 11 : 0)), null);
                return null;
            }
            Require((function == 3 || function == 4) && (function == 3) == ((control & 0x10) != 0));
            if (function == 3)
            {
                if (!_reset)
                {
                    linkReply = Link((byte)((_master ? 0x80 : 0) | 1), null);
                    return null;
                }
                if (((control & 0x20) != 0) != _nextFcb)
                {
                    Require(Equal(frame, _confirmed));
                    linkReply = _confirmedReply;
                    return null;
                }
                linkReply = Link(_master ? (byte)0x80 : (byte)0, null);
            }
            Require(length > 1);
            byte header = data[0];
            if ((header & 0x40) != 0)
            {
                _length = 0;
                _receiving = true;
                _started = now;
                _receiveSequence = (byte)(header & 63);
            }
            Require(_receiving && (header & 63) == _receiveSequence && length - 1 <= _assembly.Length - _length);
            _receiveSequence = (byte)((_receiveSequence + 1) & 63);
            Array.Copy(data, 1, _assembly, _length, length - 1);
            _length += length - 1;
            if (function == 3)
            {
                _confirmed = (byte[])frame.Clone();
                _confirmedReply = linkReply;
                _nextFcb = !_nextFcb;
            }
            if ((header & 0x80) == 0)
                return null;
            _receiving = false;
            var application = new byte[_length];
            Array.Copy(_assembly, application, _length);
            _length = 0;
            Require(application.Length >= 2);
            return application;
        }
    }
}
