using System;
using VagabondK.nF.Internal;

namespace VagabondK.nF.MMS.Server
{
    /// <summary>메모리 값을 변경하기 전에 검증합니다. -1은 허용, 0~11은 DataAccessError에 따른 거절입니다. 부작용 없이 직렬로 호출하십시오.</summary>
    /// <param name="value">형식/Int32 범위 검사를 통과한 새 값의 독립된 스냅샷입니다.</param>
    /// <returns>-1은 허용, 0~11은 거절입니다. 다른 값이나 예외는 세션을 Faulted로 만듭니다.</returns>
    public delegate int MmsWriteValidator(MmsValue value);
    /// <summary>검증된 스칼라 쓰기를 앱 모델에 동기적으로 반영합니다. -1은 수락, 0~11은 거절입니다. 자동 재시도나 롤백은 하지 않습니다.</summary>
    /// <remarks>짧게 반환하고 세션에 재진입하지 마십시오. 검증과 달리 부작용이 있을 수 있으며 응답 유실은 실행 취소가 아닙니다.</remarks>
    /// <param name="value">기본 형식/범위 및 앱 검증을 통과한 독립된 스냅샷입니다.</param>
    /// <returns>-1은 수락, 0~11은 거절입니다. 다른 값이나 예외는 세션을 Faulted 상태로 만듭니다.</returns>
    public delegate int MmsWriteHandler(MmsValue value);
    /// <summary>고정 이름과 스칼라 값 또는 고정 구성의 구조체·배열을 가진 서버 변수입니다.</summary>
    /// <remarks>원격 쓰기는 기본 금지이며 스칼라 변수에만 명시적으로 허용할 수 있습니다.
    /// 업데이트·쓰기 허용 정책·세션 처리는 호출자가 직렬화해야 합니다.
    /// domain/item은 공백 없는 ASCII 1~64자입니다. 생성 후 값 형식을 변경할 수 없습니다.
    /// 로컬 Set 계열은 값이 같아도 변경 버전을 증가시키며 Update 계열은 전송 값이 다를 때만 증가시킵니다.</remarks>
    public sealed class MmsServerVariable
    {
        /// <summary>원본 MMS domain 이름입니다.</summary>
        public string Domain { get; private set; }
        /// <summary>원본 MMS item 이름입니다.</summary>
        public string Item { get; private set; }
        private readonly MmsValueKind kind;
        private readonly MmsServerVariable[] children;
        internal readonly byte[] rawType;

        internal long version;
        internal long ChangeVersion
        {
            get
            {
                long result = version;
                if (children != null)
                    for (int i = 0; i < children.Length; i++) result = unchecked(result + children[i].ChangeVersion);
                return result;
            }
        }
        internal void ReplaceEncoded(byte[] value) { Encoded = value; EncodedLength = value.Length; version++; }
        internal bool IsRaw { get { return rawType != null; } }
        internal int NodeCount
        {
            get
            {
                int result = 1;
                if (children != null)
                    for (int i = 0; i < children.Length; i++) result += children[i].NodeCount;
                return result;
            }
        }
        internal MmsServerVariable(string domain, string item, byte[] type, byte[] encoded)
        {
            Initialize(domain, item);
            rawType = type; Encoded = encoded; EncodedLength = encoded.Length;
        }
        internal byte[] Encoded { get; private set; }
        internal int EncodedLength { get; private set; }
        private MmsWriteValidator writeValidator;
        private MmsWriteHandler writeHandler;
        /// <summary>원격 스칼라 쓰기를 명시적으로 허용했는지 나타냅니다. 기본값은 false입니다.</summary>
        public bool Writable { get; private set; }
        /// <summary>이 스칼라 변수의 원격 쓰기를 명시적으로 허용합니다. 검증기가 null이면 형식 검사만 적용합니다.</summary>
        /// <param name="validator">부작용과 대기가 없는 값 검증기 또는 null입니다. 설비를 구동하는 콜백이 아닙니다.</param>
        /// <exception cref="InvalidOperationException">구조체·배열 또는 IEC 상태·품질·시각 등의 원시 형식 변수입니다.</exception>
        /// <remarks>검증은 로컬 setter에 적용되지 않습니다. 인증/접근 주체별 권한 검사가 아닙니다.</remarks>
        public void EnableWrite(MmsWriteValidator validator = null)
        { EnableWrite(validator, null); }
        /// <summary>부작용 없는 검증과 모델 반영을 구분하여 원격 스칼라 쓰기를 허용합니다.</summary>
        /// <param name="validator">형식 검사 후 호출할 검증기 또는 null입니다.</param>
        /// <param name="handler">검증 성공 후 변수 갱신 전에 한 번 호출할 모델 반영기 또는 null입니다.</param>
        /// <exception cref="InvalidOperationException">일반 Boolean·Int32·Float32 스칼라 변수가 아닙니다.</exception>
        /// <remarks>일반 속성 반영용이며 설비 제어 API가 아닙니다. 반환값·예외·부작용에 대해 롤백하거나 재시도하지 않습니다.</remarks>
        public void EnableWrite(MmsWriteValidator validator, MmsWriteHandler handler)
        {
            if (children != null || rawType != null) throw new InvalidOperationException("Only ordinary scalar writes can be enabled.");
            writeValidator = validator;
            writeHandler = handler;
            Writable = true;
        }
        /// <summary>원격 쓰기를 차단합니다. 로컬 setter에는 영향을 주지 않습니다.</summary>
        public void DisableWrite() { Writable = false; writeValidator = null; writeHandler = null; }
        internal int ApplyWrite(BerElement encoded)
        {
            if (!Writable) return 3;
            byte expected = kind == MmsValueKind.Boolean ? (byte)0x83 : kind == MmsValueKind.Integer ? (byte)0x85 : (byte)0x87;
            if (encoded.Tag != expected) return 7;
            MmsValue value;
            if (kind == MmsValueKind.Boolean)
                value = MmsValue.FromBoolean(BerEncoding.ReadBoolean(encoded.Buffer, encoded.Offset, encoded.Length));
            else if (kind == MmsValueKind.Integer)
            {
                long number = encoded.Integer();
                if (number < int.MinValue || number > int.MaxValue) return 11;
                value = MmsValue.FromInteger((int)number);
            }
            else
            {
                if (encoded.Length != 5 || encoded.Buffer[encoded.Offset] != 8) return 7;
                uint bits = 0;
                for (int i = 1; i < 5; i++) bits = (bits << 8) | encoded.Buffer[encoded.Offset + i];
                value = MmsValue.FromFloatingPoint(RealBits.Decode(bits));
            }
            int code = writeValidator == null ? -1 : writeValidator(value);
            if (code < -1 || code > 11) throw new InvalidOperationException("Invalid Write validator result.");
            if (code != -1) return code;
            if (writeHandler != null)
            {
                code = writeHandler(value);
                if (code < -1 || code > 11) throw new InvalidOperationException("Invalid Write handler result.");
                if (code != -1) return code;
            }
            if (kind == MmsValueKind.Boolean) SetBoolean(value.Boolean);
            else if (kind == MmsValueKind.Integer) SetInteger((int)value.Integer);
            else SetFloatingPoint((float)value.FloatingPoint);
            return -1;
        }

        /// <summary>Boolean 변수를 생성합니다.</summary>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <param name="value">초기 값입니다.</param>
        public MmsServerVariable(string domain, string item, bool value)
        {
            Initialize(domain, item);
            kind = MmsValueKind.Boolean;
            Encoded = new byte[3];
            SetBoolean(value);
        }
        /// <summary>Int32 변수를 생성합니다.</summary>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <param name="value">초기 값입니다.</param>
        public MmsServerVariable(string domain, string item, int value)
        {
            Initialize(domain, item);
            kind = MmsValueKind.Integer;
            Encoded = new byte[6];
            SetInteger(value);
        }
        /// <summary>IEEE754 Float32 변수를 생성합니다.</summary>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <param name="value">초기 값입니다.</param>
        public MmsServerVariable(string domain, string item, float value)
        {
            Initialize(domain, item);
            kind = MmsValueKind.FloatingPoint;
            Encoded = new byte[7];
            SetFloatingPoint(value);
        }
        /// <summary>고정 구성의 구조체 또는 동종 배열을 생성합니다. 자식 값은 공유하며 배열 구성은 복사합니다.</summary>
        /// <param name="domain">원본 domain 이름입니다.</param>
        /// <param name="item">원본 item 이름입니다.</param>
        /// <param name="elements">1~255개 자식입니다. 구조체의 성분 이름은 각 자식의 Item입니다.</param>
        /// <param name="isArray">true는 동종 배열, false는 구조체입니다.</param>
        /// <exception cref="ArgumentNullException">이름 또는 자식 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">이름 길이 또는 자식 수가 지원 범위를 벗어납니다.</exception>
        /// <exception cref="ArgumentException">이름·자식이 잘못되었거나 구조체 이름 중복·배열 형식 불일치·깊이 8 또는 전체 256노드 상한 위반입니다.</exception>
        public MmsServerVariable(string domain, string item, MmsServerVariable[] elements, bool isArray = false)
        {
            Initialize(domain, item);
            if (elements == null) throw new ArgumentNullException("elements");
            if (elements.Length < 1 || elements.Length > 255) throw new ArgumentOutOfRangeException("elements");
            kind = isArray ? MmsValueKind.Array : MmsValueKind.Structure;
            children = new MmsServerVariable[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                if (elements[i] == null) throw new ArgumentException("Null composite element.");
                if (!isArray)
                    for (int j = 0; j < i; j++)
                        if (elements[i].Item == elements[j].Item) throw new ArgumentException("Duplicate component name.");
                children[i] = elements[i];
            }
            int remaining = 256;
            ValidateTree(0, ref remaining);
            if (isArray)
            {
                byte[] expected = children[0].TypeDefinition();
                for (int i = 1; i < children.Length; i++)
                {
                    byte[] actual = children[i].TypeDefinition();
                    if (expected.Length != actual.Length) throw new ArgumentException("Array element types differ.");
                    for (int j = 0; j < actual.Length; j++)
                        if (expected[j] != actual[j]) throw new ArgumentException("Array element types differ.");
                }
            }
        }
        private void ValidateTree(int depth, ref int remaining)
        {
            if (depth > 8 || --remaining < 0) throw new ArgumentException("Composite exceeds depth 8 or total nodes 256.");
            if (children != null)
                for (int i = 0; i < children.Length; i++) children[i].ValidateTree(depth + 1, ref remaining);
        }
        internal bool IsComposite { get { return children != null; } }
        internal byte[] EncodeValue()
        {
            if (children == null)
            {
                byte[] scalar = new byte[EncodedLength];
                Array.Copy(Encoded, scalar, EncodedLength);
                return scalar;
            }
            byte[][] values = new byte[children.Length][];
            for (int i = 0; i < values.Length; i++) values[i] = children[i].EncodeValue();
            return MmsEncoding.Tlv(kind == MmsValueKind.Array ? (byte)0xa1 : (byte)0xa2, values);
        }
        internal byte[] TypeDefinition()
        {
            if (rawType != null) return rawType;
            if (kind == MmsValueKind.Boolean) return MmsEncoding.Tlv(0x83);
            if (kind == MmsValueKind.Integer) return MmsEncoding.Int(0x85, 32);
            if (kind == MmsValueKind.FloatingPoint)
                return MmsEncoding.Tlv(0xa7, MmsEncoding.Int(2, 32), MmsEncoding.Int(2, 8));
            if (kind == MmsValueKind.Array)
                return MmsEncoding.Tlv(0xa1, MmsEncoding.Int(0x81, children.Length),
                    MmsEncoding.Tlv(0xa2, children[0].TypeDefinition()));
            byte[][] entries = new byte[children.Length][];
            for (int i = 0; i < entries.Length; i++)
            {
                byte[] name = MmsEncoding.Identifier(children[i].Item);
                name[0] = 0x80;
                entries[i] = MmsEncoding.Tlv(0x30, name, MmsEncoding.Tlv(0xa1, children[i].TypeDefinition()));
            }
            return MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0xa1, entries));
        }
        private void Initialize(string domain, string item)
        {
            MmsEncoding.Identifier(domain);
            MmsEncoding.Identifier(item);
            Domain = domain;
            Item = item;
        }
        private void Require(MmsValueKind expected)
        {
            if (kind != expected) throw new InvalidOperationException("MMS server variable type cannot change.");
        }
        /// <summary>Boolean 전송 값이 다를 때만 로컬 버퍼와 변경 버전을 갱신합니다. 쓰기 콜백은 호출하지 않습니다.</summary>
        /// <param name="value">새 값입니다.</param>
        /// <exception cref="InvalidOperationException">Boolean 스칼라 변수가 아닙니다.</exception>
        public void UpdateBoolean(bool value)
        {
            Require(MmsValueKind.Boolean);
            if (Encoded[2] != (value ? (byte)1 : (byte)0)) SetBoolean(value);
        }
        /// <summary>Int32 전송 값이 다를 때만 로컬 버퍼와 변경 버전을 갱신합니다. 쓰기 콜백은 호출하지 않습니다.</summary>
        /// <param name="value">새 값입니다.</param>
        /// <exception cref="InvalidOperationException">Integer 스칼라 변수가 아닙니다.</exception>
        public void UpdateInteger(int value)
        {
            Require(MmsValueKind.Integer);
            if (BerEncoding.ReadInteger(Encoded, 2, EncodedLength - 2) != value) SetInteger(value);
        }
        /// <summary>Float32 전송 비트가 다를 때만 로컬 버퍼와 변경 버전을 갱신합니다. +0/-0을 구별합니다.</summary>
        /// <param name="value">새 값입니다.</param>
        /// <exception cref="InvalidOperationException">FloatingPoint 스칼라 변수가 아닙니다.</exception>
        public void UpdateFloatingPoint(float value)
        {
            Require(MmsValueKind.FloatingPoint);
            uint bits = RealBits.Encode(value);
            if (Encoded[3] != (byte)(bits >> 24) || Encoded[4] != (byte)(bits >> 16) ||
                Encoded[5] != (byte)(bits >> 8) || Encoded[6] != (byte)bits) SetFloatingPoint(value);
        }
        /// <summary>Boolean 로컬 버퍼를 덮어쓰고 변경 버전을 증가시킵니다. 원격 쓰기 검증기나 처리기는 호출하지 않습니다.</summary>
        /// <param name="value">새 값입니다.</param>
        /// <exception cref="InvalidOperationException">Boolean 스칼라 변수가 아닙니다.</exception>
        public void SetBoolean(bool value)
        {
            Require(MmsValueKind.Boolean);
            version++;
            Encoded[0] = 0x83; Encoded[1] = 1; Encoded[2] = value ? (byte)1 : (byte)0;
            EncodedLength = 3;
        }
        /// <summary>Int32 로컬 값을 BER 정수로 다시 기록하고 변경 버전을 증가시킵니다. 원격 쓰기 정책은 적용하지 않습니다.</summary>
        /// <param name="value">새 값입니다.</param>
        /// <exception cref="InvalidOperationException">Integer 스칼라 변수가 아닙니다.</exception>
        public void SetInteger(int value)
        {
            Require(MmsValueKind.Integer);
            version++;
            EncodedLength = BerEncoding.WriteInteger(Encoded, 0, 0x85, value);
        }
        /// <summary>Float32 전송 비트를 로컬 버퍼에 기록하고 변경 버전을 증가시킵니다. 원격 쓰기 정책은 적용하지 않습니다.</summary>
        /// <param name="value">새 값입니다.</param>
        /// <exception cref="InvalidOperationException">FloatingPoint 스칼라 변수가 아닙니다.</exception>
        public void SetFloatingPoint(float value)
        {
            Require(MmsValueKind.FloatingPoint);
            version++;
            uint bits = RealBits.Encode(value);
            Encoded[0] = 0x87; Encoded[1] = 5; Encoded[2] = 8;
            Encoded[3] = (byte)(bits >> 24); Encoded[4] = (byte)(bits >> 16);
            Encoded[5] = (byte)(bits >> 8); Encoded[6] = (byte)bits;
            EncodedLength = 7;
        }
    }
}
