using System;

namespace VagabondK.nF.MMS
{
    /// <summary>배열의 대체 접근(alternate access)을 지원하지 않는 고정 domain/item 참조입니다.</summary>
    public sealed class MmsVariableReference
    {
        /// <summary>원본 domain 이름입니다.</summary>
        public string Domain { get; private set; }
        /// <summary>원본 item 이름입니다.</summary>
        public string Item { get; private set; }
        /// <summary>ASCII 1~64자의 이름 쌍을 생성합니다.</summary>
        /// <param name="domain">공백을 제외한 ASCII 1~64자의 MMS domain 식별자입니다.</param>
        /// <param name="item">공백을 제외한 ASCII 1~64자의 MMS item 식별자입니다.</param>
        /// <exception cref="ArgumentException">이름이 null이거나 길이 또는 문자 범위를 벗어납니다.</exception>
        public MmsVariableReference(string domain, string item)
        {
            MmsEncoding.Identifier(domain); MmsEncoding.Identifier(item);
            Domain = domain; Item = item;
        }
        internal byte[] Name()
        { return MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier(Domain), MmsEncoding.Identifier(Item)); }
        internal byte[] Specification() { return MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0, Name())); }
        internal static MmsVariableReference Decode(BerElement name)
        {
            name.Expect(0xa1);
            var pair = name.Children();
            string domain = MmsEncoding.ReadIdentifier(pair.Read(0x1a));
            string item = MmsEncoding.ReadIdentifier(pair.Read(0x1a)); pair.End();
            return new MmsVariableReference(domain, item);
        }
    }
    /// <summary>다중 Read의 항목 하나에 대한 결과입니다. 접근 오류가 있어도 원래 요청 순서를 유지합니다.</summary>
    public sealed class MmsReadResult
    {
        /// <summary>읽기에 성공한 값의 스냅샷입니다. 접근 오류이면 null입니다.</summary>
        public MmsValue Value { get; private set; }
        /// <summary>성공 시 -1, 접근 오류 시 MMS DataAccessError 코드 0~11입니다.</summary>
        public int ErrorCode { get; private set; }
        internal MmsReadResult(MmsValue value, int error) { Value = value; ErrorCode = error; }
    }
    /// <summary>최대 16개 참조를 보관하는 고정 명명 변수 목록의 디렉터리 스냅샷입니다.</summary>
    public class MmsNamedVariableList
    {
        private readonly MmsVariableReference[] members;
        /// <summary>명명 변수 목록 domain입니다.</summary>
        public string Domain { get; private set; }
        /// <summary>명명 변수 목록 이름입니다.</summary>
        public string Item { get; private set; }
        /// <summary>원격 삭제 가능 여부입니다. 이 라이브러리의 서버에서는 false입니다.</summary>
        public bool Deletable { get; private set; }
        /// <summary>항목 수입니다.</summary>
        public int Count { get { return members.Length; } }
        /// <summary>선언 순서의 참조입니다.</summary>
        /// <param name="index">0 이상 Count 미만의 멤버 인덱스입니다.</param>
        /// <returns>해당 위치의 고정 domain/item 참조입니다.</returns>
        /// <exception cref="IndexOutOfRangeException">인덱스가 멤버 범위를 벗어납니다.</exception>
        public MmsVariableReference GetMember(int index) { return members[index]; }
        internal MmsNamedVariableList(string domain, string item, bool deletable, MmsVariableReference[] entries)
        { Domain = domain; Item = item; Deletable = deletable; members = entries; }
    }
}
