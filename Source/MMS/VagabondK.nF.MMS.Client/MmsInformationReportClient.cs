namespace VagabondK.nF.MMS.Client
{
    /// <summary>일반 보고를 직렬로 처리하는 콜백입니다. 같은 클라이언트에 재진입하지 마십시오.</summary>
    /// <param name="report">수신 버퍼와 독립된 일반 MMS 보고 스냅샷입니다.</param>
    /// <remarks>원래 MMS 작업의 기한 안에서 동기 실행됩니다. 검증·모델 반영을 미루려면 스냅샷을 앱의 용량 제한 큐에 보관하고 작업 종료 후 처리하십시오.</remarks>
    public delegate void MmsInformationReportHandler(MmsInformationReport report);

    /// <summary>일반 MMS InformationReport를 동기 수신하거나 서비스 응답 대기 중 전달합니다.</summary>
    public partial class MmsClient
    {
        /// <summary>서비스 응답 대기 중 일반 InformationReport를 처리할 수신기입니다. 기본 null이면 미처리 보고를 거절합니다.</summary>
        /// <remarks>처리기 예외와 처리 시간 초과는 Client를 Faulted로 만듭니다. 라이브러리는 보고 큐나 작업 스레드를 제공하지 않습니다.</remarks>
        public MmsInformationReportHandler InformationReportReceived { get; set; }
        internal virtual void ValidateWriteSuccess() { }
        internal virtual BerElement ReceiveServiceResponse() { return ReceiveNonReport(); }
        internal virtual bool DispatchInformationReport(BerElement pdu)
        {
            if (pdu.Tag != 0xa3 || InformationReportReceived == null) return false;
            var report = MmsInformationReport.Decode(pdu);
            if (Remaining() == 0) return true;
            InformationReportReceived(report); Remaining();
            return true;
        }
        internal BerElement ReceiveNonReport()
        {
            for (int i = 0; i <= 32; i++)
            {
                var pdu = ReceiveServicePdu();
                if (pdu == null) return null;
                if (!DispatchInformationReport(pdu)) return pdu;
                if (Remaining() == 0) return null;
                if (i == 32) throw new MmsProtocolException("Report burst exceeds 32 while waiting for service.");
            }
            throw new MmsProtocolException("Invalid report loop.");
        }
        /// <summary>일반 MMS 보고 하나를 전체 기한 안에서 수신합니다. 실패 시 Faulted 상태가 되며 자동 재시도하지 않습니다.</summary>
        /// <returns>선택적으로 등록한 수신기에도 전달되는 독립된 보고 스냅샷입니다.</returns>
        /// <remarks>수신기가 있으면 같은 호출 안에서 동기 실행됩니다. 처리기 예외와 처리 시간 초과도 연결을 Faulted로 만듭니다.</remarks>
        public MmsInformationReport ReceiveInformationReport()
        {
            Begin(true);
            try
            {
                var report = MmsInformationReport.Decode(ReceiveServicePdu());
                Remaining();
                if (InformationReportReceived != null) InformationReportReceived(report);
                Remaining(); return report;
            }
            catch { Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }
        internal bool TryWriteUnsigned(string domain, string item, uint value, out int errorCode)
        { return TryWriteEncoded(domain, item, MmsEncoding.Int(0x86, value), out errorCode); }
    }
}
