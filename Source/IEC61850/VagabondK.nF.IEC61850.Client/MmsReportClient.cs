using VagabondK.nF.MMS;
using System;

namespace VagabondK.nF.IEC61850.Client
{
    /// <summary>수신한 URCB 보고를 직렬로 처리하는 콜백입니다. 같은 클라이언트에 재진입하거나 콜백 안에서 대기하지 마십시오.</summary>
    /// <param name="report">수신 버퍼와 독립된 보고 스냅샷입니다.</param>
    /// <remarks>원래 MMS 작업의 기한을 공유합니다. 검증·모델 반영을 미루려면 스냅샷을 앱의 용량 제한 큐에 보관하고 작업 종료 후 처리하십시오.</remarks>
    public delegate void MmsReportHandler(MmsReport report);
    /// <summary>연결 버퍼와 독립된 제한된 URCB 보고입니다. 미포함 항목의 Value는 null입니다.</summary>
    public sealed class MmsReport
    {
        /// <summary>보고 ID입니다.</summary>
        public string ReportId { get; private set; }
        /// <summary>순환하는 8비트 보고 번호입니다. 누락/재전송 방지 키가 아닙니다.</summary>
        public byte SequenceNumber { get; private set; }
        /// <summary>DataSet 참조입니다.</summary>
        public string DataSetReference { get; private set; }
        /// <summary>구성 버전입니다.</summary>
        public uint ConfigurationRevision { get; private set; }
        private readonly MmsValue[] values;
        private readonly byte[] reasons;
        /// <summary>전체 DataSet 항목 수입니다.</summary>
        public int Count { get { return values.Length; } }
        /// <summary>보고에 포함된 항목의 스냅샷을 반환합니다. 포함되지 않은 항목이면 null입니다.</summary>
        /// <param name="index">0 이상 Count 미만의 DataSet 항목 인덱스입니다.</param>
        /// <returns>포함된 값의 독립된 스냅샷이며, 포함되지 않은 항목이면 null입니다.</returns>
        /// <exception cref="IndexOutOfRangeException">인덱스가 DataSet 범위를 벗어납니다.</exception>
        public MmsValue GetValue(int index) { return values[index]; }
        /// <summary>원시 6비트 reason: dchg=0x40/qchg=0x20/integrity=0x08/GI=0x04입니다. 미포함은 0입니다.</summary>
        /// <param name="index">0 이상 Count 미만의 DataSet 항목 인덱스입니다.</param>
        /// <returns>해당 항목의 전송 위치 기준 사유 비트이며 미포함 시 0입니다.</returns>
        /// <exception cref="IndexOutOfRangeException">인덱스가 DataSet 범위를 벗어납니다.</exception>
        public byte GetReason(int index) { return reasons[index]; }
        internal MmsReport(string id, byte number, string dataSet, uint revision, MmsValue[] entries, byte[] causes)
        { ReportId = id; SequenceNumber = number; DataSetReference = dataSet; ConfigurationRevision = revision; values = entries; reasons = causes; }
    }
    /// <summary>고정 DataSet·고정 OptFlds의 제한된 URCB Client입니다. 구성은 읽고 확인하며 동적 변경하지 않습니다.</summary>
    /// <remarks>비활성 상태에서 구성·등록하고 모든 호출을 직렬화하십시오. 서비스 응답 대기 중 도착한 보고도 콜백으로 전달합니다.</remarks>
    public sealed class MmsReportClient
    {
        private readonly Iec61850Client client;
        private readonly string domain, item;
        internal readonly string Id, DataSetReference;
        internal readonly int Count;
        internal readonly uint Revision;
        /// <summary>보고 수신기입니다. 등록 보고의 수신 전에 지정해야 합니다.</summary>
        /// <remarks>동기 처리기 예외·기한 초과는 Client를 Faulted로 만듭니다. null로 설정하면 보고 전달 시 예외가 발생합니다.</remarks>
        public MmsReportHandler ReportReceived { get; set; }
        /// <summary>비활성 URCB의 고정 구성을 읽고 최대 4개 수신 슬롯 중 하나에 등록합니다.</summary>
        /// <param name="client">이미 Association을 맺은 호출자 소유 클라이언트입니다.</param>
        /// <param name="domain">URCB가 속한 MMS domain 식별자입니다.</param>
        /// <param name="item">최대 55자의 LN$RP$RCB 형식 item입니다.</param>
        /// <param name="dataSet">URCB의 DatSet과 일치하는 디렉터리 스냅샷입니다.</param>
        /// <param name="handler">수신 보고의 동기 처리기입니다. 재진입하거나 대기하지 마십시오.</param>
        /// <exception cref="ArgumentNullException">클라이언트, DataSet 또는 처리기가 null입니다.</exception>
        /// <exception cref="NotSupportedException">URCB가 활성 상태이거나 고정 옵션·DataSet 등 지원 프로파일과 다릅니다.</exception>
        /// <exception cref="InvalidOperationException">다른 작업이 진행 중이거나 보고 등록 슬롯 4개가 모두 사용 중입니다.</exception>
        public MmsReportClient(Iec61850Client client, string domain, string item, MmsDataSet dataSet, MmsReportHandler handler)
        {
            if (client == null || dataSet == null || handler == null) throw new ArgumentNullException("client/dataSet/handler");
            MmsEncoding.Identifier(domain); MmsEncoding.Identifier(item);
            if (item.IndexOf("$RP$") < 1 || item.Length > 55) throw new ArgumentException("Expected LN$RP$RCB.");
            var config = client.Read(domain, item);
            if (config.Kind != MmsValueKind.Structure || config.Count != 11 ||
                config.GetElement(1).Kind != MmsValueKind.Boolean || config.GetElement(1).Boolean ||
                config.GetElement(2).Kind != MmsValueKind.Boolean ||
                config.GetElement(0).Kind != MmsValueKind.VisibleString ||
                config.GetElement(3).Kind != MmsValueKind.VisibleString ||
                config.GetElement(4).Kind != MmsValueKind.Unsigned ||
                config.GetElement(4).Unsigned == 0 || config.GetElement(4).Unsigned > uint.MaxValue ||
                config.GetElement(5).Kind != MmsValueKind.BitString || config.GetElement(5).BitCount != 10 ||
                config.GetElement(6).Kind != MmsValueKind.Unsigned || config.GetElement(6).Unsigned != 0 ||
                config.GetElement(7).Kind != MmsValueKind.Unsigned || config.GetElement(7).Unsigned > 255 ||
                config.GetElement(8).Kind != MmsValueKind.BitString || config.GetElement(8).BitCount != 6 ||
                config.GetElement(9).Kind != MmsValueKind.Unsigned || config.GetElement(9).Unsigned > uint.MaxValue ||
                config.GetElement(10).Kind != MmsValueKind.Boolean || config.GetElement(10).Boolean)
                throw new NotSupportedException("URCB profile requires inactive fixed configuration and BufTm=0.");
            byte[] options = config.GetElement(5).GetBytes();
            if (options[0] != 0x58 || options[1] != 0x80)
                throw new NotSupportedException("URCB requires sequence/reason/DataSet/confRev options only.");
            byte triggers = config.GetElement(8).GetBytes()[0];
            if ((triggers & ~0x6c) != 0) throw new NotSupportedException("Unsupported URCB trigger options.");
            Id = config.GetElement(0).VisibleString; DataSetReference = dataSet.Reference;
            if (config.GetElement(3).VisibleString != DataSetReference) throw new NotSupportedException("Unexpected URCB DataSet.");
            Count = dataSet.Count; Revision = (uint)config.GetElement(4).Unsigned;
            this.client = client; this.domain = domain; this.item = item; ReportReceived = handler;
            client.RegisterReport(this);
        }
        /// <summary>예약 또는 예약 해제를 요청합니다. 자체 Server는 활성 중 변경을 거절합니다.</summary>
        /// <param name="reserve">예약하려면 true, 예약을 해제하려면 false입니다.</param>
        public void Reserve(bool reserve) { client.Write(domain, item + "$Resv", reserve); }
        /// <summary>보고를 활성화합니다. RCB 소유권/접근 오류는 예외입니다.</summary>
        public void Enable() { client.Write(domain, item + "$RptEna", true); }
        /// <summary>보고를 비활성화합니다. 이미 전송된 보고는 수신될 수 있습니다.</summary>
        public void Disable() { client.Write(domain, item + "$RptEna", false); }
        /// <summary>전체 DataSet 일반 조회 보고를 요청합니다. 보고는 ReceiveReport나 다음 서비스 대기 중 수신합니다.</summary>
        public void GeneralInterrogation() { client.Write(domain, item + "$GI", true); }
        /// <summary>비활성 상태의 integrity 주기를 설정합니다. 자체 Server 범위는 0 또는 최소 간격~60000ms입니다.</summary>
        /// <param name="milliseconds">0은 integrity 비활성화, 양수는 주기(ms)이며 최대 60000입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">주기가 0~60000ms 범위를 벗어납니다.</exception>
        /// <exception cref="MmsAccessException">서버가 활성 상태·소유권·최소 간격 등의 이유로 쓰기를 거절했습니다.</exception>
        public void SetIntegrityPeriod(int milliseconds)
        {
            if (milliseconds < 0 || milliseconds > 60000) throw new ArgumentOutOfRangeException("milliseconds");
            int code;
            if (!client.TryWriteUnsigned(domain, item + "$IntgPd", (uint)milliseconds, out code)) throw new MmsAccessException(code);
        }
        internal void Deliver(MmsReport report)
        {
            if (ReportReceived == null) throw new InvalidOperationException("Report handler is not installed.");
            ReportReceived(report);
        }
    }
    /// <summary>등록된 URCB 보고를 서비스 응답과 함께 직렬로 처리하는 IEC 61850 클라이언트입니다.</summary>
    public sealed partial class Iec61850Client
    {
        private MmsReportClient[] reportClients;
        internal void RegisterReport(MmsReportClient control)
        {
            if (active) throw new InvalidOperationException("An MMS operation is active.");
            if (reportClients == null) reportClients = new MmsReportClient[4];
            for (int i = 0; i < reportClients.Length; i++)
                if (reportClients[i] != null && reportClients[i].Id == control.Id)
                    throw new ArgumentException("Report ID must be unique on connection.");
            for (int i = 0; i < reportClients.Length; i++)
                if (reportClients[i] == null) { reportClients[i] = control; return; }
            throw new InvalidOperationException("At most four report controls per connection.");
        }
        /// <summary>등록된 URCB 보고 1개를 클라이언트의 전체 기한 안에서 기다리고 콜백으로 전달합니다.</summary>
        /// <remarks>호출 전 채널의 수신 준비 여부를 검사하면 유휴 상태의 시간 초과를 피할 수 있습니다. 시간 초과나 오류 시 Faulted 상태가 되며 자동 재연결하지 않습니다.</remarks>
        /// <returns>등록 수신기에 전달한 것과 동일한 독립된 보고 스냅샷입니다.</returns>
        /// <exception cref="InvalidOperationException">등록된 보고 제어가 없거나 연결을 사용할 수 없습니다.</exception>
        public MmsReport ReceiveReport()
        {
            if (reportClients == null) throw new InvalidOperationException("No report control registered.");
            Begin(true);
            try
            {
                MmsReport report;
                if (!DispatchReport(ReceiveServicePdu(), out report)) throw new MmsProtocolException("Expected registered URCB report.");
                return report;
            }
            catch { Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }
        private bool DispatchReport(BerElement pdu, out MmsReport report)
        {
            report = null;
            if (pdu.Tag != 0xa3 || reportClients == null) return false;
            var wrapper = pdu.Children(); var info = wrapper.Read(0xa0).Children(); wrapper.End();
            BerElement specification = info.Read();
            if (specification.Tag != 0xa1) return false;
            var named = specification.Children(); var name = named.Read(0x80); named.End();
            if (!name.Matches(new byte[] { 82, 80, 84 })) throw new MmsProtocolException("Unsupported report name.");
            var values = info.Read(0xa0).Children(); info.End();
            var id = MmsValue.Decode(values.Read(0x8a));
            MmsReportClient control = null;
            for (int i = 0; i < reportClients.Length; i++)
                if (reportClients[i] != null && reportClients[i].Id == id.VisibleString) control = reportClients[i];
            if (control == null) throw new MmsProtocolException("Unregistered report ID.");
            if (!values.Read(0x84).Matches(new byte[] { 6, 0x58, 0x80 })) throw new MmsProtocolException("Unsupported report options.");
            var number = MmsValue.Decode(values.Read(0x86));
            string dataSet = MmsValue.Decode(values.Read(0x8a)).VisibleString;
            var revision = MmsValue.Decode(values.Read(0x86));
            if (number.Unsigned > 255 || revision.Unsigned != control.Revision || dataSet != control.DataSetReference)
                throw new MmsProtocolException("Report configuration mismatch.");
            var inclusion = MmsValue.Decode(values.Read(0x84));
            if (inclusion.BitCount != control.Count) throw new MmsProtocolException("Report inclusion size mismatch.");
            byte[] bits = inclusion.GetBytes();
            var entries = new MmsValue[control.Count]; var reasons = new byte[control.Count]; int count = 0, remaining = 256;
            for (int i = 0; i < entries.Length; i++)
                if ((bits[i / 8] & (128 >> (i % 8))) != 0)
                { entries[i] = MmsValue.Decode(values.Read(), 0, ref remaining); count++; }
            if (count == 0) throw new MmsProtocolException("Empty report inclusion.");
            for (int i = 0; i < entries.Length; i++)
                if (entries[i] != null)
                {
                    var reason = values.Read(0x84);
                    if (reason.Length != 2 || reason.Buffer[reason.Offset] != 2)
                        throw new MmsProtocolException("Invalid report reason.");
                    byte flag = reason.Buffer[reason.Offset + 1];
                    if (flag == 0 || (flag & ~0x6c) != 0) throw new MmsProtocolException("Unsupported report reason.");
                    reasons[i] = flag;
                }
            values.End();
            if (Remaining() == 0) return true;
            report = new MmsReport(control.Id, (byte)number.Unsigned, dataSet, control.Revision, entries, reasons);
            control.Deliver(report);
            Remaining();
            return true;
        }
        internal override bool DispatchInformationReport(BerElement pdu)
        {
            MmsReport report;
            return DispatchReport(pdu, out report) || base.DispatchInformationReport(pdu);
        }
    }
}
