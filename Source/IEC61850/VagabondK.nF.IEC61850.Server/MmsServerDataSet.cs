using VagabondK.nF.MMS;
using System;
using VagabondK.nF.MMS.Server;
namespace VagabondK.nF.IEC61850.Server
{
    /// <summary>제어/RCB 객체 자체를 제외하는 IEC 61850 정적 DataSet입니다.</summary>
    public sealed class MmsServerDataSet : MmsServerNamedVariableList
    {
        /// <summary>최대 16개 등록 변수의 참조를 복사합니다.</summary>
        /// <param name="domain">DataSet의 공백 없는 ASCII 1~64자 domain입니다.</param>
        /// <param name="item">DataSet의 공백 없는 ASCII 1~64자 item입니다.</param>
        /// <param name="members">null 멤버가 없는 1~16개 공유 변수입니다. 전체 값 노드는 최대 256개입니다.</param>
        public MmsServerDataSet(string domain, string item, MmsServerVariable[] members) : base(domain, item, members)
        {
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i].Item.IndexOf("$CO$") >= 0 || members[i].Item.IndexOf("$RP$") >= 0)
                    throw new ArgumentException("Control/report objects cannot be DataSet members.");

            }
        }
    }
}