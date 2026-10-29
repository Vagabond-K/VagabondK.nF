using System;

namespace VagabondK.nF.DNP3
{
    /// <summary>Outstation의 Static 값과 확인 대기 Event를 보관하는 고정 용량 Database입니다.</summary>
    /// <remarks>읽기와 갱신은 내부에서 동기화하므로 통신 처리 스레드와 다른 스레드에서도 값을 갱신할 수 있습니다.
    /// 이벤트는 Master의 확인을 받은 뒤 제거됩니다.</remarks>
    public sealed class Dnp3Database
    {
        private readonly object _gate = new object();
        private readonly Dnp3PointDefinition[] _points;
        private readonly Dnp3Value[] _values, _baseline;
        private readonly Dnp3Record[] _events;
        private int _count;
        private long _id;

        /// <summary>Event 용량 부족으로 새 Event를 버린 적이 있으면 true입니다. 명시적으로 해제할 때까지 유지됩니다.</summary>
        public bool Overflow
        {
            get
            {
                lock (_gate)
                    return _overflow;
            }
        }
        private bool _overflow;

        /// <summary>정의된 Point 수입니다.</summary>
        public int PointCount
        {
            get
            {
                return _points.Length;
            }
        }

        /// <summary>보관 가능한 Event의 최대 개수입니다.</summary>
        public int EventCapacity
        {
            get
            {
                return _events.Length;
            }
        }

        /// <summary>아직 확인되어 제거되지 않은 Event 개수입니다.</summary>
        public int EventCount
        {
            get
            {
                lock (_gate)
                    return _count;
            }
        }

        /// <summary>Point 정의와 Event 보관 용량으로 Database를 생성합니다.</summary>
        /// <param name="points">0~512개의 null이 아닌 Point 정의입니다. 같은 종류와 인덱스의 중복은 허용하지 않습니다.</param>
        /// <param name="eventCapacity">Event 최대 개수입니다. 1~2048입니다.</param>
        /// <remarks>입력 배열을 복사하며 호출자의 배열 순서는 변경하지 않습니다. 초기값으로는 Event를 생성하지 않습니다.</remarks>
        /// <exception cref="ArgumentException">배열, Point 정의, 중복 또는 용량이 유효하지 않습니다.</exception>
        public Dnp3Database(Dnp3PointDefinition[] points, int eventCapacity = 128)
        {
            if (points == null || points.Length > 512 || eventCapacity < 1 || eventCapacity > 2048)
                throw new ArgumentException("DNP3 database capacity.");
            _points = new Dnp3PointDefinition[points.Length];
            _values = new Dnp3Value[points.Length];
            _baseline = new Dnp3Value[points.Length];
            _events = new Dnp3Record[eventCapacity];
            Array.Copy(points, _points, points.Length);
            for (int i = 0; i < points.Length; i++)
            {
                if (_points[i] == null)
                    throw new ArgumentException("Null DNP3 point.");
                for (int j = 0; j < i; j++)
                    if (_points[j].Type == _points[i].Type && _points[j].Index == _points[i].Index)
                        throw new ArgumentException("Duplicate DNP3 point.");
            }
            for (int i = 0; i < _points.Length; i++)
                for (int j = i + 1; j < _points.Length; j++)
                    if (_points[j].Type < _points[i].Type || _points[j].Type == _points[i].Type && _points[j].Index < _points[i].Index)
                    {
                        Dnp3PointDefinition point = _points[i];
                        _points[i] = _points[j];
                        _points[j] = point;
                    }
            for (int i = 0; i < _points.Length; i++)
                _values[i] = _baseline[i] = _points[i].Initial;
        }

        /// <summary>지정한 Point의 현재 Static 값을 읽습니다.</summary>
        /// <param name="type">Point 종류입니다.</param>
        /// <param name="index">Point 인덱스입니다.</param>
        /// <returns>마지막으로 갱신된 값이며, 아직 갱신하지 않았다면 초기값입니다.</returns>
        /// <exception cref="ArgumentException">일치하는 Point가 없습니다.</exception>
        public Dnp3Value Read(Dnp3PointType type, ushort index)
        {
            lock (_gate)
                return _values[Find(type, index)];
        }

        private int Find(Dnp3PointType type, ushort index)
        {
            int low = 0, high = _points.Length - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                Dnp3PointDefinition point = _points[middle];
                Dnp3PointType pointType = point.Type;
                ushort pointIndex = point.Index;
                if (pointType == type && pointIndex == index)
                    return middle;
                if (pointType < type || pointType == type && pointIndex < index)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            throw new ArgumentException("Unknown DNP3 point.");
        }

        /// <summary>Static 값을 갱신하고 변화 조건을 만족하면 Event를 추가합니다.</summary>
        /// <param name="type">Point 종류입니다.</param>
        /// <param name="index">Point 인덱스입니다.</param>
        /// <param name="value">Point 종류의 범위를 만족하는 새 값입니다.</param>
        /// <returns>필요한 이벤트를 모두 저장했거나 이벤트가 필요하지 않으면 true입니다. 이벤트가 필요하지만 버퍼가 가득 차 새 이벤트를 버렸으면 false입니다.</returns>
        /// <remarks>Timestamp만 바뀌면 Event를 만들지 않습니다. Flags 변화는 Event를 만들며, Flags가 같을 때
        /// Analog 변화량은 마지막으로 저장된 Event 기준 Deadband를 초과해야 합니다(첫 Event 전에는 초기값 기준).
        /// EventClass가 0이면 Event를 만들지 않습니다. Overflow 시에도 Static 값은 갱신되지만
        /// 이벤트 생성의 비교 기준은 갱신하지 않으며 Overflow가 설정됩니다.</remarks>
        /// <exception cref="ArgumentException">Point가 없거나 값이 해당 종류에 유효하지 않습니다.</exception>
        /// <exception cref="InvalidOperationException">Event 식별자 공간을 소진했습니다. Static 값은 이미 갱신되었을 수 있습니다.</exception>
        public bool Update(Dnp3PointType type, ushort index, Dnp3Value value)
        {
            lock (_gate)
            {
                int slot = Find(type, index);
                Dnp3PointDefinition point = _points[slot];
                point.Validate(value);
                Dnp3Value old = _baseline[slot];
                bool changed = old.Flags != value.Flags || old.Raw != value.Raw;
                if (changed && old.Flags == value.Flags && (type == Dnp3PointType.Analog || type == Dnp3PointType.AnalogOutputStatus))
                {
                    double difference = old.AsAnalog() - value.AsAnalog();
                    changed = difference > point.Deadband || difference < -point.Deadband;
                }
                _values[slot] = value;
                if (!changed || point.EventClass == 0)
                    return true;
                if (_count == _events.Length)
                {
                    _overflow = true;
                    return false;
                }
                if (_id == long.MaxValue)
                    throw new InvalidOperationException("DNP3 event generation exhausted.");
                _events[_count++] = new Dnp3Record { Point = point, Value = value, EventId = ++_id, Variation = point.EventVariation };
                _baseline[slot] = value;
                return true;
            }
        }

        /// <summary>모든 Event가 제거된 상태에서 Overflow 표시를 해제합니다.</summary>
        /// <exception cref="InvalidOperationException">아직 보관 중인 Event가 있습니다.</exception>
        public void ClearOverflow()
        {
            lock (_gate)
            {
                if (_count != 0)
                    throw new InvalidOperationException("Drain DNP3 events before clearing overflow.");
                _overflow = false;
            }
        }

        internal byte Classes()
        {
            lock (_gate)
            {
                byte classes = 0;
                for (int i = 0; i < _count; i++)
                    classes |= (byte)(1 << _events[i].Point.EventClass);
                return classes;
            }
        }

        internal Dnp3Record[] Snapshot(bool statics, byte classes, int group, int variation, int start, int stop, int maximum)
        {
            lock (_gate)
            {
                var items = new Dnp3Record[_points.Length + _count];
                int count = 0;
                if (statics)
                    for (int i = 0; i < _points.Length && count < maximum; i++)
                        if ((group == 0 || Dnp3Objects.Group(_points[i].Type, false) == group) &&
                            _points[i].Index >= start && _points[i].Index <= stop)
                            items[count++] = new Dnp3Record
                            {
                                Point = _points[i],
                                Value = _values[i],
                                Variation = variation == 0 ? _points[i].StaticVariation : (byte)variation
                            };
                for (int i = 0; i < _count && count < maximum; i++)
                    if ((classes & (1 << _events[i].Point.EventClass)) != 0 &&
                        (group == 0 || Dnp3Objects.Group(_events[i].Point.Type, true) == group) &&
                        _events[i].Point.Index >= start && _events[i].Point.Index <= stop)
                    {
                        items[count] = _events[i];
                        if (variation != 0)
                            items[count].Variation = (byte)variation;
                        count++;
                    }
                var result = new Dnp3Record[count];
                Array.Copy(items, result, count);
                return result;
            }
        }

        internal void Confirm(Dnp3Record[] records, int start, int end)
        {
            lock (_gate)
                for (int r = start; r < end; r++)
                    if (records[r].EventId != 0)
                        for (int i = 0; i < _count; i++)
                            if (_events[i].EventId == records[r].EventId)
                            {
                                for (int j = i + 1; j < _count; j++)
                                    _events[j - 1] = _events[j];
                                _events[--_count] = new Dnp3Record();
                                break;
                            }
        }
    }
}
