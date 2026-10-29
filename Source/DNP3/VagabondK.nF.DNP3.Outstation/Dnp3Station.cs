using System;

namespace VagabondK.nF.DNP3
{
    /// <summary>한 Master에 대한 DNP3 Outstation 요청 및 Unsolicited 응답을 처리합니다.</summary>
    /// <remarks>호출자는 이 인스턴스에 대한 모든 접근을 직렬화해야 합니다. Database 값은 다른 스레드에서 갱신할 수 있습니다.
    /// 반환된 Frame들을 순서대로 전송하고 Process/Poll을 계속 호출해야 합니다.
    /// 시각 인수에는 동일한 기준의 단조 증가 밀리초 시각을 사용합니다.</remarks>
    public sealed class Dnp3Station : IDnp3Endpoint
    {
        private readonly Dnp3Database _database;
        private readonly Dnp3Channel _channel;
        private readonly int _fragmentSize, _confirmTimeout, _retries, _selectTimeout;
        private readonly IDnp3ControlHandler _controls;
        private readonly IDnp3TimeHandler _time;
        private bool _restart = true, _needTime, _nullConfirmed;
        private byte _enabled, _unsSequence, _selectedSequence;
        private byte[] _lastRequest, _lastResponse, _selection;
        private long _lastAt, _selectedAt;
        private Pending _pending, _unsolicited;

        /// <summary>Outstation의 로컬 Link 주소입니다.</summary>
        public ushort LocalAddress
        {
            get
            {
                return _channel.LocalAddress;
            }
        }

        /// <summary>상대 Master의 Link 주소입니다.</summary>
        public ushort RemoteAddress
        {
            get
            {
                return _channel.RemoteAddress;
            }
        }

        /// <summary>항상 false입니다.</summary>
        public bool IsMaster
        {
            get
            {
                return false;
            }
        }

        private sealed class Pending
        {
            internal Dnp3Record[] Records;
            internal int Position, SentStart, SentEnd, Attempts;
            internal byte Sequence;
            internal bool Unsolicited, First = true;
            internal byte[] Application;
            internal long Deadline;
        }

        /// <summary>Database, 주소 및 응답 정책을 지정하여 Outstation을 생성합니다.</summary>
        /// <param name="database">Static 값과 Event를 보관하는 null이 아닌 Database입니다.</param>
        /// <param name="address">0~65519 범위의 Outstation 주소입니다.</param>
        /// <param name="master">0~65519 범위이며 Outstation과 다른 Master 주소입니다.</param>
        /// <param name="controls">제어 검증 및 실행 처리기입니다. null이면 제어를 NotSupported로 거부합니다.</param>
        /// <param name="time">수신 UTC 시각 처리기입니다. null이면 시각 쓰기를 지원하지 않으며, 지정하면 초기 Need Time을 설정합니다.</param>
        /// <param name="fragmentSize">응답 Application Fragment의 최대 바이트 수입니다. 64~2048입니다.</param>
        /// <param name="confirmationTimeout">Application Confirmation 대기 시간입니다. 1밀리초 이상입니다.</param>
        /// <param name="retries">Confirmation 대기 시간 만료 후 추가로 재전송할 횟수입니다. 0~10입니다.</param>
        /// <param name="selectTimeout">Select 이후 일치하는 Operate를 허용하는 시간입니다. 1밀리초 이상이며 경계 시각은 만료로 취급합니다.</param>
        /// <param name="transportTimeout">Transport 재조립 제한 시간입니다. 1밀리초 이상입니다.</param>
        /// <remarks>Database와 처리기의 수명은 호출자가 관리합니다. 생성 직후 Device Restart를 표시하며,
        /// Poll은 최초 빈 Unsolicited 응답의 Confirmation을 먼저 요청합니다.</remarks>
        /// <exception cref="ArgumentException">Database, 주소 또는 제한 값이 유효하지 않습니다.</exception>
        public Dnp3Station(Dnp3Database database, ushort address, ushort master, IDnp3ControlHandler controls = null,
            IDnp3TimeHandler time = null, int fragmentSize = 1024, int confirmationTimeout = 3000, int retries = 2, int selectTimeout = 5000,
            int transportTimeout = 3000)
        {
            if (database == null || fragmentSize < 64 || fragmentSize > 2048 || confirmationTimeout < 1 ||
                retries < 0 || retries > 10 || selectTimeout < 1)
                throw new ArgumentException("DNP3 station limits.");
            _database = database;
            _fragmentSize = fragmentSize;
            _confirmTimeout = confirmationTimeout;
            _retries = retries;
            _selectTimeout = selectTimeout;
            _controls = controls;
            _time = time;
            _needTime = time != null;
            _channel = new Dnp3Channel(address, master, false, timeoutMilliseconds: transportTimeout);
        }

        /// <summary>연결별 재조립, 응답 대기, Select 및 Unsolicited 활성화 상태를 초기화합니다.</summary>
        /// <remarks>Database 값과 미확인 Event, Restart 및 Need Time 표시는 유지합니다. 통신 리소스는 닫지 않습니다.</remarks>
        public void ResetConnection()
        {
            _channel.Reset();
            _pending = null;
            _unsolicited = null;
            _lastRequest = null;
            _lastResponse = null;
            _selection = null;
            _enabled = 0;
            _nullConfirmed = false;
            _unsSequence = 0;
        }

        private byte Iin1()
        {
            return (byte)((_restart ? 0x80 : 0) | (_needTime ? 0x10 : 0) | _database.Classes());
        }

        private byte Iin2(byte error)
        {
            return (byte)(error | (_database.Overflow ? 8 : 0));
        }

        private byte[] Response(byte control, byte function, byte error, byte[] body)
        {
            var result = new byte[4 + (body == null ? 0 : body.Length)];
            result[0] = control;
            result[1] = function;
            result[2] = Iin1();
            result[3] = Iin2(error);
            if (body != null)
                Array.Copy(body, 0, result, 4, body.Length);
            return result;
        }

        /// <summary>Master의 Link Frame을 처리하고 필요한 응답을 생성합니다.</summary>
        /// <param name="frame">CRC를 포함하는 하나의 완전한 Link Frame입니다.</param>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>Link 및 Application 응답 Frame들의 연결된 배열입니다. 응답이 없으면 null입니다.</returns>
        /// <remarks>Application 요청은 단일 Fragment만 지원하며 수신 Fragment는 최대 2048바이트입니다.
        /// 한 요청의 제어 명령은 최대 32개입니다. 지원하지 않는 기능이나 잘못된 요청은 가능한 경우 IIN 오류로 응답합니다.
        /// Event는 해당 응답에 대한 Confirmation을 받은 뒤 제거되며, 새 요청으로 기존 응답이 대체되어도 미확인 Event는 보존됩니다.
        /// 사용자 제어 및 시각 처리기는 동기적으로 호출되고 해당 예외는 그대로 전파될 수 있습니다.</remarks>
        /// <exception cref="ArgumentNullException">Frame이 null입니다.</exception>
        /// <exception cref="Dnp3ProtocolException">Link/Transport 또는 Application 제어 형식이 잘못되었습니다.</exception>
        /// <exception cref="TimeoutException">Transport 재조립 시간이 만료되었습니다.</exception>
        public byte[] Process(byte[] frame, long now)
        {
            byte[] link;
            byte[] request = _channel.Receive(frame, now, out link);
            if (request == null)
                return link;
            byte control = request[0], function = request[1];
            Dnp3Channel.Require((control & 0xc0) == 0xc0 && (control & 0x20) == 0);
            if (function == 0)
            {
                Dnp3Channel.Require(request.Length == 2);
                Pending pending = (control & 0x10) != 0 ? _unsolicited : _pending;
                if (pending == null)
                    return link;
                if ((pending.Sequence & 15) != (control & 15))
                {
                    if (!pending.First && ((pending.Sequence - 1) & 15) == (control & 15))
                        return Dnp3Channel.Join(link, _channel.Encode(pending.Application));
                    return link;
                }
                _database.Confirm(pending.Records, pending.SentStart, pending.SentEnd);
                if (pending.Unsolicited && !_nullConfirmed)
                    _nullConfirmed = true;
                pending.Position = pending.SentEnd;
                if (pending.Position < pending.Records.Length)
                {
                    pending.Sequence = (byte)((pending.Sequence + 1) & 15);
                    pending.First = false;
                    return Dnp3Channel.Join(link, SendNext(pending, now));
                }
                if (pending.Unsolicited)
                    _unsSequence = (byte)((pending.Sequence + 1) & 15);
                if (pending.Unsolicited)
                    _unsolicited = null;
                else
                    _pending = null;
                return link;
            }
            Dnp3Channel.Require((control & 0x10) == 0);
            if (Dnp3Channel.Equal(request, _lastRequest))
                return Dnp3Channel.Join(link, _lastResponse == null ? null : _channel.Encode(_lastResponse));
            if (_lastRequest != null && (_lastRequest[0] & 15) == (control & 15) &&
                now - _lastAt < _confirmTimeout)
                return Dnp3Channel.Join(link, _channel.Encode(Response((byte)(0xc0 | (control & 15)), 0x81, 4, null)));
            // 새 요청으로 응답을 대체해도 확인받지 못한 이벤트는 삭제하지 않습니다.
            _pending = null;
            if (function != 3 && function != 4)
                _selection = null;
            byte[] application;
            byte error = 0;
            try
            {
                if (function == 1)
                {
                    Dnp3Record[] records = ReadRequest(request, out error);
                    if (error == 0)
                    {
                        var pending = new Pending { Records = records, Sequence = (byte)(control & 15) };
                        _pending = pending;
                        byte[] wire = SendNext(pending, now);
                        _lastRequest = request;
                        _lastAt = now;
                        return Dnp3Channel.Join(link, wire);
                    }
                    application = Response((byte)(0xc0 | (control & 15)), 0x81, error, null);
                }
                else if (function == 20 || function == 21)
                {
                    byte classes = ClassHeaders(request);
                    if (function == 20)
                        _enabled |= classes;
                    else
                        _enabled &= (byte)~classes;
                    application = Response((byte)(0xc0 | (control & 15)), 0x81, 0, null);
                }
                else if (function == 2)
                {
                    if (request.Length == 8 && request[2] == 80 && request[3] == 1 && request[4] == 0 &&
                        request[5] == 7 && request[6] == 7 && request[7] == 0)
                        _restart = false;
                    else if (request.Length == 12 && request[2] == 50 && request[3] == 1 && request[4] == 7 && request[5] == 1)
                    {
                        if (_time == null)
                            error = 1;
                        else
                        {
                            _time.SetUtcMilliseconds((long)Dnp3Objects.Read(request, 6, 6));
                            _needTime = false;
                        }
                    }
                    else
                        error = 4;
                    application = Response((byte)(0xc0 | (control & 15)), 0x81, error, null);
                }
                else if (function == 23 && request.Length == 2)
                    application = Response((byte)(0xc0 | (control & 15)), 0x81, 0, new byte[] { 52, 2, 7, 1, 0, 0 });
                else if (function >= 3 && function <= 6)
                {
                    byte[] body = Commands(request, function, now);
                    application = function == 6 ? null : Response((byte)(0xc0 | (control & 15)), 0x81, 0, body);
                }
                else
                    application = Response((byte)(0xc0 | (control & 15)), 0x81, 1, null);
            }
            catch (Dnp3ProtocolException)
            {
                application = function == 6 ? null : Response((byte)(0xc0 | (control & 15)), 0x81, 4, null);
            }
            _lastRequest = request;
            _lastResponse = application;
            _lastAt = now;
            return Dnp3Channel.Join(link, application == null ? null : _channel.Encode(application));
        }

        private byte ClassHeaders(byte[] request)
        {
            Dnp3Channel.Require(request.Length >= 5 && (request.Length - 2) % 3 == 0);
            byte mask = 0;
            for (int p = 2; p < request.Length; p += 3)
            {
                Dnp3Channel.Require(request[p] == 60 && request[p + 1] >= 2 && request[p + 1] <= 4 && request[p + 2] == 6);
                mask |= (byte)(1 << (request[p + 1] - 1));
            }
            return mask;
        }

        private Dnp3Record[] ReadRequest(byte[] request, out byte error)
        {
            error = 0;
            int p = 2;
            var all = new Dnp3Record[_database.PointCount + _database.EventCapacity];
            int count = 0;
            Dnp3Channel.Require(p < request.Length);
            while (p < request.Length)
            {
                Dnp3Channel.Require(request.Length - p >= 3);
                int group = request[p++], variation = request[p++], qualifier = request[p++];
                int start = 0, stop = 65535, maximum = int.MaxValue;
                if (qualifier == 0 || qualifier == 1)
                {
                    int width = qualifier == 0 ? 1 : 2;
                    Dnp3Channel.Require(request.Length - p >= width * 2);
                    start = (int)Dnp3Objects.Read(request, p, width);
                    stop = (int)Dnp3Objects.Read(request, p + width, width);
                    p += width * 2;
                    Dnp3Channel.Require(start <= stop);
                }
                else if (qualifier == 7 || qualifier == 8)
                {
                    int width = qualifier == 7 ? 1 : 2;
                    Dnp3Channel.Require(request.Length - p >= width);
                    maximum = (int)Dnp3Objects.Read(request, p, width);
                    p += width;
                }
                else
                    Dnp3Channel.Require(qualifier == 6);
                bool statics;
                byte classes;
                Dnp3PointType type;
                bool events;
                if (group == 60 && variation >= 1 && variation <= 4)
                {
                    statics = variation == 1;
                    classes = statics ? (byte)0 : (byte)(1 << (variation - 1));
                    group = 0;
                    variation = 0;
                }
                else if (Dnp3Objects.Type(group, out type, out events) &&
                    (variation == 0 || Dnp3Objects.Supported(type, events, variation)))
                {
                    statics = !events;
                    classes = events ? (byte)14 : (byte)0;
                }
                else
                {
                    error = 2;
                    return new Dnp3Record[0];
                }
                var records = _database.Snapshot(statics, classes, group, variation, start, stop, maximum);
                for (int i = 0; i < records.Length; i++)
                {
                    if (InUnsolicited(records[i].EventId))
                        continue;
                    bool duplicate = false;
                    for (int j = 0; j < count; j++)
                        if (all[j].Point == records[i].Point && all[j].EventId == records[i].EventId)
                            duplicate = true;
                    if (!duplicate)
                    {
                        Dnp3Channel.Require(count < all.Length);
                        all[count++] = records[i];
                    }
                }
            }
            var result = new Dnp3Record[count];
            Array.Copy(all, result, count);
            return result;
        }

        private bool InUnsolicited(long id)
        {
            if (id == 0 || _unsolicited == null)
                return false;
            for (int i = 0; i < _unsolicited.Records.Length; i++)
                if (_unsolicited.Records[i].EventId == id)
                    return true;
            return false;
        }

        private byte[] SendNext(Pending pending, long now)
        {
            var fragment = new byte[_fragmentSize];
            int p = 4, start = pending.Position;
            bool eventRecords = false;
            while (pending.Position < pending.Records.Length)
            {
                Dnp3Record record = pending.Records[pending.Position];
                int width = 7 + Dnp3Objects.Width(record.Point.Type, record.EventId != 0, record.Variation);
                if (width > fragment.Length - p)
                    break;
                p = Dnp3Objects.Write(fragment, p, record);
                pending.Position++;
                eventRecords |= record.EventId != 0;
            }
            bool last = pending.Position == pending.Records.Length;
            bool confirm = pending.Unsolicited || !last || eventRecords;
            fragment[0] = (byte)((pending.First ? 0x80 : 0) | (last ? 0x40 : 0) |
                (confirm ? 0x20 : 0) | (pending.Unsolicited ? 0x10 : 0) | pending.Sequence);
            fragment[1] = pending.Unsolicited ? (byte)0x82 : (byte)0x81;
            fragment[2] = Iin1();
            fragment[3] = Iin2(0);
            var application = new byte[p];
            Array.Copy(fragment, application, p);
            pending.Application = application;
            pending.SentStart = start;
            pending.SentEnd = pending.Position;
            pending.Attempts = 0;
            pending.Deadline = now + _confirmTimeout;
            if (!pending.Unsolicited)
                _lastResponse = application;
            if (!confirm)
                _pending = null;
            return _channel.Encode(application);
        }

        /// <summary>Confirmation을 기다리는 응답의 재전송, 최초 Unsolicited 응답 또는 활성화된 Class의 Unsolicited 전송을 진행합니다.</summary>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>전송할 Frame들의 연결된 배열이며, 현재 전송할 데이터가 없으면 null입니다.</returns>
        /// <exception cref="TimeoutException">Transport 재조립 시간이 만료되었거나 Confirmation 재시도를 소진했습니다.
        /// 확인받지 못한 Event는 Database에 유지됩니다.</exception>
        public byte[] Poll(long now)
        {
            _channel.CheckTimeout(now);
            Pending waiting = _pending != null && now >= _pending.Deadline ? _pending :
                _unsolicited != null && now >= _unsolicited.Deadline ? _unsolicited : null;
            if (waiting != null)
            {
                if (waiting.Attempts++ >= _retries)
                {
                    if (waiting.Unsolicited)
                        _unsolicited = null;
                    else
                        _pending = null;
                    throw new TimeoutException("DNP3 application confirmation retries exhausted; events retained.");
                }
                waiting.Deadline = now + _confirmTimeout;
                return _channel.Encode(waiting.Application);
            }
            if (_pending == null && _unsolicited == null && (!_nullConfirmed || (_enabled & _database.Classes()) != 0))
            {
                _unsolicited = new Pending
                {
                    Records = !_nullConfirmed ? new Dnp3Record[0] :
                    _database.Snapshot(false, _enabled, 0, 0, 0, 65535, int.MaxValue),
                    Sequence = _unsSequence,
                    Unsolicited = true
                };
                return SendNext(_unsolicited, now);
            }
            return null;
        }

        private byte[] Commands(byte[] request, int function, long now)
        {
            var commands = new Dnp3Command[32];
            var offsets = new int[32];
            int count = 0, p = 2;
            while (p < request.Length)
            {
                Dnp3Channel.Require(request.Length - p >= 4);
                int group = request[p++], variation = request[p++], qualifier = request[p++];
                Dnp3Channel.Require(qualifier == 0x17 || qualifier == 0x28);
                int width = qualifier == 0x17 ? 1 : 2;
                Dnp3Channel.Require(request.Length - p >= width);
                int number = (int)Dnp3Objects.Read(request, p, width);
                p += width;
                Dnp3Channel.Require(number > 0 && number <= commands.Length - count);
                for (int i = 0; i < number; i++)
                {
                    Dnp3Channel.Require(request.Length - p >= width);
                    ushort index = (ushort)Dnp3Objects.Read(request, p, width);
                    p += width;
                    commands[count] = Dnp3Controls.Parse(request, ref p, index, group, variation);
                    offsets[count++] = p - 1;
                }
            }
            Dnp3Channel.Require(count != 0);
            var body = new byte[request.Length - 2];
            Array.Copy(request, 2, body, 0, body.Length);
            bool selected = function == 4 && _selection != null && now - _selectedAt < _selectTimeout &&
                (request[0] & 15) == ((_selectedSequence + 1) & 15) && Dnp3Channel.Equal(_selection, body);
            bool allGood = true;
            var statuses = new Dnp3CommandStatus[count];
            for (int i = 0; i < count; i++)
            {
                statuses[i] = _controls == null ? Dnp3CommandStatus.NotSupported : _controls.Validate(commands[i]);
                allGood &= statuses[i] == Dnp3CommandStatus.Success;
            }
            if (function == 3)
            {
                _selection = allGood ? (byte[])body.Clone() : null;
                _selectedAt = now;
                _selectedSequence = (byte)(request[0] & 15);
            }
            else
            {
                _selection = null;
                for (int i = 0; i < count; i++)
                    if (statuses[i] == Dnp3CommandStatus.Success)
                        statuses[i] = function == 4 && !selected ? Dnp3CommandStatus.NoSelect : _controls.Operate(commands[i], selected);
            }
            for (int i = 0; i < count; i++)
                body[offsets[i] - 2] = (byte)statuses[i];
            return body;
        }
    }
}
