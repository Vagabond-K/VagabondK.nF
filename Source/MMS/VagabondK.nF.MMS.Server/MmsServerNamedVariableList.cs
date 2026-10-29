using System;

namespace VagabondK.nF.MMS.Server
{
    /// <summary>최대 16개의 고정된 참조를 보관하는 정적 명명 변수 목록입니다. 목록의 동적 생성·삭제와 alternate access는 지원하지 않습니다.</summary>
    public class MmsServerNamedVariableList
    {
        /// <summary>명명 변수 목록의 원본 domain입니다.</summary>
        public string Domain { get; private set; }
        /// <summary>명명 변수 목록 이름입니다.</summary>
        public string Item { get; private set; }
        internal readonly MmsServerVariable[] Members;
        /// <summary>고정 멤버 수입니다.</summary>
        public int Count { get { return Members.Length; } }
        /// <summary>선언 순서의 공유 변수입니다.</summary>
        /// <param name="index">0 이상 Count 미만의 멤버 인덱스입니다.</param>
        /// <returns>복사본이 아닌 등록된 서버 변수 객체입니다.</returns>
        /// <exception cref="IndexOutOfRangeException">인덱스가 멤버 범위를 벗어납니다.</exception>
        public MmsServerVariable GetMember(int index) { return Members[index]; }
        /// <summary>등록된 서버 변수의 참조 목록을 복사합니다. 값은 공유하며 원격 삭제할 수 없습니다.</summary>
        /// <param name="domain">목록의 공백 없는 ASCII 1~64자 domain입니다.</param>
        /// <param name="item">목록의 공백 없는 ASCII 1~64자 item입니다.</param>
        /// <param name="members">null 멤버가 없는 1~16개 변수입니다. 전체 값 노드는 최대 256개입니다.</param>
        /// <exception cref="ArgumentNullException">멤버 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">멤버 수가 1~16 범위를 벗어납니다.</exception>
        /// <exception cref="ArgumentException">식별자, 멤버 또는 전체 값 노드 수가 지원 범위를 벗어납니다.</exception>
        public MmsServerNamedVariableList(string domain, string item, MmsServerVariable[] members)
        {
            MmsEncoding.Identifier(domain); MmsEncoding.Identifier(item);
            if (members == null) throw new ArgumentNullException("members");
            if (members.Length < 1 || members.Length > 16) throw new ArgumentOutOfRangeException("members");
            Members = new MmsServerVariable[members.Length];
            int nodes = 0;
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] == null) throw new ArgumentException("Null named variable list member.");
                nodes += members[i].NodeCount;
                if (nodes > 256) throw new ArgumentException("named variable list exceeds 256 total value nodes.");
                Members[i] = members[i];
            }
            Domain = domain; Item = item;
        }
        internal byte[] ObjectName()
        { return MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier(Domain), MmsEncoding.Identifier(Item)); }
        internal byte[] Directory()
        {
            var entries = new byte[Members.Length][];
            for (int i = 0; i < entries.Length; i++)
                entries[i] = MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa1,
                    MmsEncoding.Identifier(Members[i].Domain), MmsEncoding.Identifier(Members[i].Item))));
            return MmsEncoding.Tlv(0xac, MmsEncoding.Tlv(0x80, new byte[] { 0 }), MmsEncoding.Tlv(0xa1, entries));
        }
    }
}
