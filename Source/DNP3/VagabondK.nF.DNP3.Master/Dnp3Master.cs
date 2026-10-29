using System;

namespace VagabondK.nF.DNP3
{
    /// <summary>Master가 수신한 하나의 Static 또는 Event 측정값입니다.</summary>
    public struct Dnp3Measurement
    {
        /// <summary>수신한 Point의 종류입니다.</summary>
        public Dnp3PointType Type { get; internal set; }

        /// <summary>수신한 Point의 16비트 인덱스입니다.</summary>
        public ushort Index { get; internal set; }

        /// <summary>값, 품질 Flags 및 Variation에 포함된 Timestamp입니다.</summary>
        public Dnp3Value Value { get; internal set; }

        /// <summary>Event Object에서 수신했으면 true이고 Static Object이면 false입니다.</summary>
        public bool IsEvent { get; internal set; }
    }

    /// <summary>Master가 검증한 Application Fragment의 측정값을 전달받는 처리기입니다.</summary>
    public interface IDnp3MasterHandler
    {
        /// <summary>완전히 검증된 Fragment의 측정값을 Confirmation 전송 전에 전달받습니다.</summary>
        /// <param name="measurements">현재 Fragment의 측정값을 담은 새 배열입니다. 빈 배열일 수 있습니다.</param>
        /// <param name="response">현재 Fragment의 Sequence, 경계 및 IIN 정보입니다.</param>
        /// <remarks>Process 호출 중 동기적으로 실행됩니다. 배열은 후속 수신에 재사용되지 않습니다.
        /// 여러 Fragment를 하나의 응답으로 묶으려면 First/Final 정보를 사용해야 합니다.
        /// 처리기 예외는 호출자에게 전파되어 해당 호출이 Confirmation을 반환하지 않습니다.</remarks>
        void OnMeasurements(Dnp3Measurement[] measurements, Dnp3ResponseInfo response);
    }

    /// <summary>수신한 DNP3 Application 응답 Fragment의 제어 및 IIN 정보입니다.</summary>
    public struct Dnp3ResponseInfo
    {
        /// <summary>Unsolicited 응답이면 true입니다.</summary>
        public bool Unsolicited { get; internal set; }

        /// <summary>응답의 첫 Fragment이면 true입니다.</summary>
        public bool First { get; internal set; }

        /// <summary>응답의 마지막 Fragment이면 true입니다.</summary>
        public bool Final { get; internal set; }

        /// <summary>0~15 범위의 Application Sequence입니다.</summary>
        public byte Sequence { get; internal set; }

        /// <summary>수신한 첫 번째 Internal Indications 바이트입니다.</summary>
        public byte Iin1 { get; internal set; }

        /// <summary>수신한 두 번째 Internal Indications 바이트입니다.</summary>
        public byte Iin2 { get; internal set; }
    }

    /// <summary>Master의 요청 처리 상태입니다.</summary>
    public enum Dnp3MasterResult
    {
        /// <summary>초기 상태이거나 연결 상태가 초기화되었습니다.</summary>
        Idle,

        /// <summary>요청의 응답 또는 후속 단계를 기다리고 있습니다.</summary>
        Pending,

        /// <summary>요청이 성공적으로 완료되었습니다.</summary>
        Success,

        /// <summary>상대의 IIN2 하위 오류 비트로 요청이 실패했습니다.</summary>
        RemoteError,

        /// <summary>제어 명령이 Success 이외의 상태로 거부되었습니다.</summary>
        CommandRejected,

        /// <summary>응답 재시도 한도를 소진했습니다.</summary>
        TimedOut
    }

    /// <summary>한 Outstation에 대한 읽기, 제어, 시각 동기화 및 응답 처리를 수행하는 Master입니다.</summary>
    /// <remarks>한 번에 하나의 요청만 허용합니다. 호출자는 모든 접근과 처리기 호출을 포함한 작업 흐름을 직렬화해야 합니다.
    /// 반환된 Frame들을 순서대로 전송하고 Process/Poll을 계속 호출하여 통신을 진행합니다.
    /// 시각 인수에는 동일한 기준의 단조 증가 밀리초 시각을 사용합니다.</remarks>
    public sealed class Dnp3Master : IDnp3Endpoint
    {
        private readonly Dnp3Channel _channel;
        private readonly IDnp3MasterHandler _handler;
        private readonly int _timeout, _maximumRetries;
        private readonly Dnp3Measurement[] _measurements, _unsMeasurements;
        private byte _sequence;
        private byte[] _request, _retryApplication, _lastSolicited, _lastUnsolicited, _command;
        private int _count, _unsCount, _attempts, _operation;
        private bool _receiving, _unsReceiving;
        private byte _expected, _unsExpected;
        private long _deadline, _unsDeadline, _started, _utc;

        /// <summary>현재 또는 마지막 요청의 처리 상태입니다.</summary>
        public Dnp3MasterResult Result { get; private set; }

        /// <summary>마지막으로 처리한 응답의 첫 번째 IIN 바이트입니다.</summary>
        public byte Iin1 { get; private set; }

        /// <summary>마지막으로 처리한 응답의 두 번째 IIN 바이트입니다.</summary>
        public byte Iin2 { get; private set; }

        /// <summary>마지막 제어 응답의 상태입니다. 새 명령 시작 시 Success로 초기화되므로 완료 여부는 Result로 확인합니다.</summary>
        public Dnp3CommandStatus CommandStatus { get; private set; }

        /// <summary>완료되지 않은 요청이 있으면 true입니다.</summary>
        public bool Busy
        {
            get
            {
                return Result == Dnp3MasterResult.Pending;
            }
        }

        /// <summary>Master의 로컬 Link 주소입니다.</summary>
        public ushort LocalAddress
        {
            get
            {
                return _channel.LocalAddress;
            }
        }

        /// <summary>상대 Outstation의 Link 주소입니다.</summary>
        public ushort RemoteAddress
        {
            get
            {
                return _channel.RemoteAddress;
            }
        }

        /// <summary>항상 true입니다.</summary>
        public bool IsMaster
        {
            get
            {
                return true;
            }
        }

        /// <summary>Master의 주소, 수신 처리기 및 요청 제한을 설정합니다.</summary>
        /// <param name="address">0~65519 범위의 Master 주소입니다.</param>
        /// <param name="outstation">0~65519 범위이며 Master와 다른 Outstation 주소입니다.</param>
        /// <param name="handler">측정값을 동기적으로 전달받을 null이 아닌 처리기입니다.</param>
        /// <param name="responseTimeout">응답 또는 후속 Fragment를 기다리는 시간입니다. 1밀리초 이상입니다.</param>
        /// <param name="retries">응답 제한 시간 만료 후 추가로 재전송할 횟수입니다. 0~10입니다.</param>
        /// <param name="maximumMeasurements">하나의 수신 Fragment에 허용할 측정값 수입니다. 1~8192입니다.</param>
        /// <param name="transportTimeout">Transport Fragment 재조립 제한 시간입니다. 1밀리초 이상입니다.</param>
        /// <exception cref="ArgumentException">처리기, 주소 또는 제한 값이 유효하지 않습니다.</exception>
        public Dnp3Master(ushort address, ushort outstation, IDnp3MasterHandler handler,
            int responseTimeout = 3000, int retries = 2, int maximumMeasurements = 512, int transportTimeout = 3000)
        {
            if (handler == null || responseTimeout < 1 || retries < 0 || retries > 10 ||
                maximumMeasurements < 1 || maximumMeasurements > 8192)
                throw new ArgumentException("DNP3 master limits.");
            _channel = new Dnp3Channel(address, outstation, true, timeoutMilliseconds: transportTimeout);
            _handler = handler;
            _timeout = responseTimeout;
            _maximumRetries = retries;
            _measurements = new Dnp3Measurement[maximumMeasurements];
            _unsMeasurements = new Dnp3Measurement[maximumMeasurements];
        }

        /// <summary>진행 중인 요청과 응답 재조립 상태를 폐기하고 Result를 Idle로 초기화합니다.</summary>
        /// <remarks>통신 리소스는 소유하지 않으며 IIN 및 CommandStatus의 이전 값은 유지됩니다.</remarks>
        public void ResetConnection()
        {
            _channel.Reset();
            _request = null;
            _command = null;
            _lastSolicited = null;
            _lastUnsolicited = null;
            _receiving = _unsReceiving = false;
            _count = _unsCount = 0;
            Result = Dnp3MasterResult.Idle;
        }

        private byte[] Start(byte function, byte[] body, long now, int operation)
        {
            if (Busy)
                throw new InvalidOperationException("One outstanding DNP3 master request is allowed.");
            var application = new byte[2 + (body == null ? 0 : body.Length)];
            application[0] = (byte)(0xc0 | (_sequence++ & 15));
            application[1] = function;
            if (body != null)
                Array.Copy(body, 0, application, 2, body.Length);
            _request = _retryApplication = application;
            _lastSolicited = null;
            _expected = (byte)(application[0] & 15);
            _receiving = false;
            _count = 0;
            _attempts = 0;
            _deadline = now + _timeout;
            _started = now;
            _operation = operation;
            Result = Dnp3MasterResult.Pending;
            return _channel.Encode(application);
        }

        /// <summary>지정한 Class들의 읽기 요청을 시작합니다.</summary>
        /// <param name="mask">비트 0은 Class 0 Static, 비트 1~3은 Event Class 1~3입니다. 1~15입니다.</param>
        /// <param name="now">요청을 시작하는 단조 증가 밀리초 시각입니다.</param>
        /// <returns>전송할 읽기 요청 Frame들의 연결된 배열입니다.</returns>
        /// <exception cref="ArgumentException">Class Mask가 1~15 범위가 아닙니다.</exception>
        /// <exception cref="InvalidOperationException">이미 요청이 진행 중입니다.</exception>
        public byte[] ReadClasses(byte mask, long now)
        {
            if (mask == 0 || mask > 15)
                throw new ArgumentException("Class mask bits 0..3.");
            var body = new byte[12];
            int p = 0;
            for (int i = 0; i < 4; i++)
                if ((mask & (1 << i)) != 0)
                {
                    body[p++] = 60;
                    body[p++] = (byte)(i + 1);
                    body[p++] = 6;
                }
            var headers = new byte[p];
            Array.Copy(body, headers, p);
            return Start(1, headers, now, 0);
        }

        /// <summary>지원하는 Point Group의 전체 또는 인덱스 범위 읽기를 시작합니다.</summary>
        /// <param name="group">지원하는 Static 또는 Event Object Group입니다.</param>
        /// <param name="variation">지원하는 Variation이며, 0이면 Outstation의 기본 Variation을 요청합니다.</param>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <param name="start">포함할 첫 인덱스입니다. 전체 요청은 start와 stop 모두 -1로 지정합니다.</param>
        /// <param name="stop">포함할 마지막 인덱스입니다. 범위 요청은 0 ≤ start ≤ stop ≤ 65535입니다.</param>
        /// <returns>전송할 읽기 요청 Frame들의 연결된 배열입니다.</returns>
        /// <exception cref="ArgumentException">Group/Variation이 지원되지 않거나 인덱스 범위가 잘못되었습니다.</exception>
        /// <exception cref="InvalidOperationException">이미 요청이 진행 중입니다.</exception>
        public byte[] ReadGroup(byte group, byte variation, long now, int start = -1, int stop = -1)
        {
            Dnp3PointType type;
            bool events;
            if (!Dnp3Objects.Type(group, out type, out events) || variation != 0 && !Dnp3Objects.Supported(type, events, variation))
                throw new ArgumentException("Unsupported DNP3 read group/variation.");
            byte[] body;
            if (start == -1 && stop == -1)
                body = new byte[] { group, variation, 6 };
            else
            {
                if (start < 0 || stop < start || stop > 65535)
                    throw new ArgumentException("DNP3 read range.");
                body = new byte[] { group, variation, 1, (byte)start, (byte)(start >> 8), (byte)stop, (byte)(stop >> 8) };
            }
            return Start(1, body, now, 0);
        }

        /// <summary>Outstation의 지정 Event Class에 대한 Unsolicited 전송을 설정합니다.</summary>
        /// <param name="eventClassMask">비트 1~3으로 Class 1~3을 선택하는 0이 아닌 Mask입니다. 비트 0은 허용하지 않습니다.</param>
        /// <param name="enabled">활성화하면 true, 비활성화하면 false입니다.</param>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>전송할 설정 요청 Frame들의 연결된 배열입니다.</returns>
        /// <exception cref="ArgumentException">Event Class Mask가 잘못되었습니다.</exception>
        /// <exception cref="InvalidOperationException">이미 요청이 진행 중입니다.</exception>
        public byte[] SetUnsolicited(byte eventClassMask, bool enabled, long now)
        {
            if (eventClassMask == 0 || (eventClassMask & ~14) != 0)
                throw new ArgumentException("Event class bits 1..3.");
            var body = new byte[9];
            int p = 0;
            for (int i = 1; i <= 3; i++)
                if ((eventClassMask & (1 << i)) != 0)
                {
                    body[p++] = 60;
                    body[p++] = (byte)(i + 1);
                    body[p++] = 6;
                }
            var headers = new byte[p];
            Array.Copy(body, headers, p);
            return Start(enabled ? (byte)20 : (byte)21, headers, now, 1);
        }

        /// <summary>Outstation의 Device Restart IIN 비트를 해제하는 요청을 시작합니다.</summary>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>전송할 Write 요청 Frame들의 연결된 배열입니다.</returns>
        /// <exception cref="InvalidOperationException">이미 요청이 진행 중입니다.</exception>
        public byte[] ClearRestart(long now)
        {
            return Start(2, new byte[] { 80, 1, 0, 7, 7, 0 }, now, 1);
        }

        /// <summary>Delay Measurement 후 UTC 시각을 쓰는 시각 동기화 절차를 시작합니다.</summary>
        /// <param name="utcMilliseconds">now 시점의 UTC 시각입니다. 1970-01-01 UTC부터의 밀리초이며
        /// 0~(0xFFFFFFFFFFFF - 86400000) 범위입니다.</param>
        /// <param name="now">위 UTC 시각에 대응하는 단조 증가 밀리초 시각입니다.</param>
        /// <returns>첫 Delay Measurement 요청 Frame들의 연결된 배열입니다.</returns>
        /// <remarks>후속 시간 쓰기 요청은 Process가 반환하므로 계속 전달해야 합니다. 동기화 정확도를 보장하지 않습니다.</remarks>
        /// <exception cref="InvalidOperationException">이미 요청이 진행 중입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">UTC 시각이 허용 범위를 벗어납니다.</exception>
        public byte[] SynchronizeTime(long utcMilliseconds, long now)
        {
            if (Busy)
                throw new InvalidOperationException("DNP3 master is busy.");
            if (utcMilliseconds < 0 || utcMilliseconds > 0xffffffffffffL - 86400000)
                throw new ArgumentOutOfRangeException("utcMilliseconds");
            _utc = utcMilliseconds;
            return Start(23, null, now, 3);
        }

        /// <summary>하나의 CROB 또는 Analog Output 제어 요청을 시작합니다.</summary>
        /// <param name="command">팩터리 메서드로 생성한 유효한 명령입니다.</param>
        /// <param name="selectBeforeOperate">true이면 Select 성공 후 Operate를 수행하고, false이면 Direct Operate를 수행합니다.</param>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>최초 제어 요청 Frame들의 연결된 배열입니다.</returns>
        /// <remarks>후속 Operate는 Process가 반환합니다. 통신 재시도가 장치의 물리 동작을 정확히 한 번만 수행함을 보장하지 않습니다.</remarks>
        /// <exception cref="InvalidOperationException">이미 요청이 진행 중입니다.</exception>
        /// <exception cref="ArgumentException">명령이 초기화되지 않았습니다.</exception>
        public byte[] Command(Dnp3Command command, bool selectBeforeOperate, long now)
        {
            if (Busy)
                throw new InvalidOperationException("DNP3 master is busy.");
            _command = Dnp3Controls.Encode(command);
            CommandStatus = Dnp3CommandStatus.Success;
            return Start(selectBeforeOperate ? (byte)3 : (byte)5, _command, now, selectBeforeOperate ? 4 : 5);
        }

        /// <summary>수신 제한 시간 만료를 확인하고, 대기 중인 요청이나 Confirmation을 재전송합니다.</summary>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>재전송할 Frame들의 연결된 배열이며, 없으면 null입니다.</returns>
        /// <exception cref="TimeoutException">Transport 또는 Unsolicited 후속 Fragment가 만료되었거나
        /// 요청 재시도를 소진했습니다. 요청 재시도 소진 시 Result는 TimedOut입니다.</exception>
        public byte[] Poll(long now)
        {
            _channel.CheckTimeout(now);
            if (_unsReceiving && now >= _unsDeadline)
            {
                _unsReceiving = false;
                _unsCount = 0;
                throw new TimeoutException("DNP3 unsolicited application continuation timed out.");
            }
            if (!Busy || now < _deadline)
                return null;
            if (_attempts++ >= _maximumRetries)
            {
                Result = Dnp3MasterResult.TimedOut;
                _receiving = false;
                throw new TimeoutException("DNP3 master response retries exhausted.");
            }
            if (!_receiving)
            {
                _count = 0;
                _expected = (byte)(_request[0] & 15);
            }
            _deadline = now + _timeout;
            return _channel.Encode(_retryApplication);
        }

        /// <summary>Outstation의 Link Frame을 처리하여 응답을 검증하고 측정값 또는 명령 결과를 갱신합니다.</summary>
        /// <param name="frame">CRC를 포함하는 하나의 완전한 Link Frame입니다.</param>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>Link 응답, Confirmation 및 후속 요청이 연결된 전송 배열이며, 없으면 null입니다.</returns>
        /// <remarks>측정값은 Fragment 단위로 처리기에 전달합니다. 처리기 예외는 그대로 전파됩니다.
        /// 수신 가능한 Application Fragment는 최대 2048바이트입니다.</remarks>
        /// <exception cref="ArgumentNullException">Frame이 null입니다.</exception>
        /// <exception cref="Dnp3ProtocolException">Frame, 응답 형식, Sequence 또는 측정값 수가 지원 범위를 벗어납니다.</exception>
        /// <exception cref="TimeoutException">Transport 재조립 시간이 만료되었습니다.</exception>
        public byte[] Process(byte[] frame, long now)
        {
            byte[] link;
            byte[] application = _channel.Receive(frame, now, out link);
            if (application == null)
                return link;
            Dnp3Channel.Require(application.Length >= 4);
            byte control = application[0];
            bool unsolicited = (control & 0x10) != 0;
            Dnp3Channel.Require(application[1] == (unsolicited ? 0x82 : 0x81));
            byte[] confirmation = new byte[] { (byte)(0xc0 | (control & 0x1f)), 0 };
            byte[] confirm = (control & 0x20) == 0 ? null : _channel.Encode(confirmation);
            if (Dnp3Channel.Equal(application, unsolicited ? _lastUnsolicited : _lastSolicited))
                return Dnp3Channel.Join(link, confirm);
            if (!unsolicited && !Busy)
                return link;
            bool receiving = unsolicited ? _unsReceiving : _receiving;
            byte expected = unsolicited ? _unsExpected : _expected;
            if ((control & 0x80) != 0)
            {
                Dnp3Channel.Require(!receiving);
                if (!unsolicited && (control & 15) != expected)
                    return link;
                if (unsolicited)
                    _unsCount = 0;
                else
                    _count = 0;
                expected = (byte)(control & 15);
            }
            else
                Dnp3Channel.Require(receiving);
            Dnp3Channel.Require((control & 15) == expected);
            Iin1 = application[2];
            Iin2 = application[3];
            if (!unsolicited && (Iin2 & 7) != 0)
            {
                Result = Dnp3MasterResult.RemoteError;
                _receiving = false;
                return Dnp3Channel.Join(link, confirm);
            }
            if (!unsolicited && _operation >= 4)
            {
                Dnp3Channel.Require((control & 0xc0) == 0xc0 && application.Length == _command.Length + 4);
                for (int i = 0; i < _command.Length - 1; i++)
                    Dnp3Channel.Require(application[i + 4] == _command[i]);
                CommandStatus = (Dnp3CommandStatus)application[application.Length - 1];
                Result = CommandStatus == Dnp3CommandStatus.Success ? Dnp3MasterResult.Success : Dnp3MasterResult.CommandRejected;
                _lastSolicited = application;
                if (Result == Dnp3MasterResult.Success && _operation == 4)
                    return Dnp3Channel.Join(Dnp3Channel.Join(link, confirm), Start(4, _command, now, 5));
                return Dnp3Channel.Join(link, confirm);
            }
            if (!unsolicited && _operation == 3)
            {
                Dnp3Channel.Require((control & 0xc0) == 0xc0 && application.Length == 10 &&
                    application[4] == 52 && application[5] == 2 && application[6] == 7 && application[7] == 1);
                long elapsed = now - _started, turnaround = (long)Dnp3Objects.Read(application, 8, 2);
                Dnp3Channel.Require(elapsed >= turnaround && elapsed <= 86400000);
                long utc = _utc + elapsed + (elapsed - turnaround) / 2;
                Dnp3Channel.Require(utc <= 0xffffffffffffL);
                var body = new byte[10];
                body[0] = 50;
                body[1] = 1;
                body[2] = 7;
                body[3] = 1;
                int p = 4;
                Dnp3Objects.Put(body, ref p, (ulong)utc, 6);
                Result = Dnp3MasterResult.Success;
                return Dnp3Channel.Join(Dnp3Channel.Join(link, confirm), Start(2, body, now, 1));
            }
            if (unsolicited || _operation == 0)
                Parse(application, unsolicited);
            else
                Dnp3Channel.Require(application.Length == 4);
            bool last = (control & 0x40) != 0;
            if (!last)
                Dnp3Channel.Require((control & 0x20) != 0);
            int count = unsolicited ? _unsCount : _count;
            if (unsolicited || _operation == 0)
            {
                var values = new Dnp3Measurement[count];
                Array.Copy(unsolicited ? _unsMeasurements : _measurements, values, count);
                _handler.OnMeasurements(values, new Dnp3ResponseInfo
                {
                    Unsolicited = unsolicited,
                    First = (control & 0x80) != 0,
                    Final = last,
                    Sequence = (byte)(control & 15),
                    Iin1 = Iin1,
                    Iin2 = Iin2
                });
            }
            if (unsolicited)
            {
                _lastUnsolicited = application;
                _unsExpected = (byte)((expected + 1) & 15);
                _unsReceiving = !last;
                _unsDeadline = now + _timeout;
            }
            else
            {
                _lastSolicited = application;
                _expected = (byte)((expected + 1) & 15);
                _receiving = !last;
                if (!last)
                    _retryApplication = confirmation;
                _deadline = now + _timeout;
                _attempts = 0;
                if (last)
                {
                    Result = Dnp3MasterResult.Success;
                    _sequence = (byte)((expected + 1) & 15);
                }
            }
            return Dnp3Channel.Join(link, confirm);
        }

        private void Parse(byte[] application, bool unsolicited)
        {
            int p = 4, count = 0;
            Dnp3Measurement[] output = unsolicited ? _unsMeasurements : _measurements;
            while (p < application.Length)
            {
                Dnp3Channel.Require(application.Length - p >= 3);
                int group = application[p++], variation = application[p++], qualifier = application[p++];
                Dnp3PointType type;
                bool events;
                Dnp3Channel.Require(Dnp3Objects.Type(group, out type, out events) && Dnp3Objects.Supported(type, events, variation));
                int number, index = 0, width = 0;
                if (qualifier == 0 || qualifier == 1)
                {
                    int w = qualifier == 0 ? 1 : 2;
                    Dnp3Channel.Require(application.Length - p >= w * 2);
                    index = (int)Dnp3Objects.Read(application, p, w);
                    int stop = (int)Dnp3Objects.Read(application, p + w, w);
                    p += w * 2;
                    Dnp3Channel.Require(stop >= index);
                    number = stop - index + 1;
                }
                else
                {
                    Dnp3Channel.Require(qualifier == 0x17 || qualifier == 0x28);
                    width = qualifier == 0x17 ? 1 : 2;
                    Dnp3Channel.Require(application.Length - p >= width);
                    number = (int)Dnp3Objects.Read(application, p, width);
                    p += width;
                }
                Dnp3Channel.Require(number <= output.Length - count);
                for (int i = 0; i < number; i++)
                {
                    if (width != 0)
                    {
                        Dnp3Channel.Require(application.Length - p >= width);
                        index = (int)Dnp3Objects.Read(application, p, width);
                        p += width;
                    }
                    output[count++] = new Dnp3Measurement
                    {
                        Type = type,
                        Index = (ushort)index,
                        Value = Dnp3Objects.Decode(application, ref p, type, events, variation),
                        IsEvent = events
                    };
                    index++;
                }
            }
            if (unsolicited)
                _unsCount = count;
            else
                _count = count;
        }
    }
}
