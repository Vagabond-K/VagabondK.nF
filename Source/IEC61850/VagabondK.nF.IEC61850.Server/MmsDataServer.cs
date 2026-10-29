using System;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Server;
namespace VagabondK.nF.IEC61850.Server
{
    /// <summary>등록된 정적 DataSet과 비버퍼형 보고의 송신 수명을 관리합니다.</summary>
    public sealed partial class Iec61850ServerSession
    {
        private MmsServerReportControl[] reports;
        private void InitializeReports(MmsServerDataSet[] sets, MmsServerReportControl[] blocks)
        {
            if (blocks == null) return;
            if (blocks.Length > 4) throw new ArgumentOutOfRangeException("reports");
            reports = new MmsServerReportControl[blocks.Length];
            for (int i = 0; i < blocks.Length; i++)
            {
                if (blocks[i] == null) throw new ArgumentException("Null RCB.");
                bool found = false;
                if (sets != null)
                    for (int j = 0; j < sets.Length; j++) if (blocks[i].DataSet == sets[j]) found = true;
                if (!found) throw new ArgumentException("RCB DataSet must be registered.");
                for (int j = 0; j < i; j++)
                    if (blocks[j].ReportId == blocks[i].ReportId) throw new ArgumentException("Duplicate report ID.");
                reports[i] = blocks[i];
            }
        }
        private void ReleaseReports()
        {
            if (reports != null)
                for (int i = 0; i < reports.Length; i++) reports[i].Release(this);
        }

        internal override bool DenyProfileRead(BerElement domain, BerElement item)
        {
            if (controls != null)
                for (int i = 0; i < controls.Length; i++)
                    if (Matches(domain, controls[i].Domain) && (Matches(item, controls[i].Item) ||
                        Matches(item, controls[i].Item + "$Oper") || Matches(item, controls[i].Item + "$Cancel") ||
                        Matches(item, controls[i].Item + "$SBO") || Matches(item, controls[i].Item + "$SBOw")))
                        return true;

            return false;
        }
        /// <summary>준비된 URCB 보고를 블록당 최대 1개 송신합니다. 호출자 이벤트 루프에서 정기적으로 호출하십시오.</summary>
        /// <remarks>수신 대기·백그라운드 작업·연결 단절 중 보고를 보관하는 버퍼는 없습니다. 송신 오류 시 Faulted 상태로 전환하고 예외를 전파합니다.</remarks>
        /// <returns>이번 호출에서 송신한 보고 수(0~4)입니다.</returns>
        public int ProcessReports()
        {
            Begin(true);
            try
            {
                int sent = 0;
                if (reports != null)
                    for (int i = 0; i < reports.Length; i++)
                    {
                        byte[] report = reports[i].Build(this, channel.Milliseconds);
                        if (report == null) continue;
                        if (report.Length > NegotiatedPduSize) throw new MmsProtocolException("Report exceeds PDU; segmentation unsupported.");
                        SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, report)));
                        sent++;
                    }
                return sent;
            }
            catch { ReleaseProfile(); Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }
    }
}
