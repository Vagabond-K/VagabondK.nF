using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("VagabondK.nF.IEC61850.Server")]

namespace VagabondK.nF.MMS.Server
{
    /// <summary>등록된 명명 변수 목록을 일반 MMS InformationReport로 송신합니다.</summary>
    public partial class MmsServerSession
    {
        internal virtual void ReleaseProfile() { }
        internal virtual bool ReadProfile(long invoke, BerElement domain, BerElement item) { return false; }
        internal virtual bool DenyProfileRead(BerElement domain, BerElement item) { return false; }
        internal virtual bool WriteProfile(long invoke, BerElement domain, BerElement item, BerElement value) { return false; }
        /// <summary>등록된 명명 변수 목록의 현재 값을 일반 InformationReport로 송신합니다. 보고 보관 버퍼나 재전송 큐는 없습니다.</summary>
        /// <param name="list">이 세션에 등록한 것과 동일한 목록 인스턴스입니다.</param>
        /// <exception cref="ArgumentNullException">목록이 null입니다.</exception>
        /// <exception cref="ArgumentException">목록이 이 세션에 등록되지 않았습니다.</exception>
        /// <remarks>값 갱신·요청 처리와 직렬화하십시오. 전체 송신 기한을 사용하며 송신 실패는 Faulted로 전파됩니다.</remarks>
        public void SendInformationReport(MmsServerNamedVariableList list)
        {
            if (list == null) throw new ArgumentNullException("list");
            bool found = false;
            if (namedVariableLists != null)
                for (int i = 0; i < namedVariableLists.Length; i++) if (namedVariableLists[i] == list) found = true;
            if (!found) throw new ArgumentException("Named variable list must be registered.");
            Begin(true);
            try
            {
                var values = new byte[list.Count][];
                for (int i = 0; i < values.Length; i++) values[i] = list.GetMember(i).EncodeValue();
                byte[] report = MmsEncoding.Tlv(0xa3, MmsEncoding.Tlv(0xa0,
                    MmsEncoding.Tlv(0xa1, list.ObjectName()), MmsEncoding.Tlv(0xa0, values)));
                if (report.Length > NegotiatedPduSize) throw new MmsProtocolException("InformationReport exceeds PDU.");
                SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, report)));
            }
            catch { ReleaseProfile(); Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }
    }
}
