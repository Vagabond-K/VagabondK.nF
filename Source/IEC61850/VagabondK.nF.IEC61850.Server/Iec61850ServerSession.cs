using System;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Server;
namespace VagabondK.nF.IEC61850.Server
{
    /// <summary>고정 MMS 모델에 IEC 61850 제어·URCB 의미를 추가하는 직렬 세션입니다.</summary>
    public sealed partial class Iec61850ServerSession : MmsServerSession
    {
        private readonly MmsServerControl[] controls;
        private MmsServerControl pendingControl;
        /// <summary>이 연결에 수락되었지만 아직 완료 통지를 보내지 않은 enhanced 요청이 있는지 나타냅니다.</summary>
        public bool HasPendingControl { get { return pendingControl != null; } }


        /// <summary>일반 변수와 선택적인 IEC 61850 제어·DataSet·보고 모델을 지정합니다.</summary>
        /// <param name="channel">이미 연결된 호출자 소유 채널입니다. 세션은 채널을 해제하지 않습니다.</param>
        /// <param name="variables">일반 변수입니다. 제어·보고의 확장 변수를 합해 중복 없이 1~128개여야 하며 값 객체는 공유됩니다.</param>
        /// <param name="maximumPduSize">MMS PDU 크기의 상한(1024~32768바이트)입니다.</param>
        /// <param name="timeoutMilliseconds">Association 또는 요청 수신·응답 전체의 양수 제한 시간(ms)입니다.</param>
        /// <param name="maximumNamesPerPage">이름 목록 페이지의 항목 상한(1~128)입니다. PDU 크기에 따라 줄어들 수 있습니다.</param>
        /// <param name="controls">최대 16개의 공유 제어 모델입니다. 없으면 null입니다.</param>
        /// <param name="dataSets">등록 변수와 동일 인스턴스를 참조하는 최대 8개의 정적 DataSet입니다. 없으면 null입니다.</param>
        /// <param name="reports">등록 DataSet을 참조하는 최대 4개의 URCB입니다. 보고 ID는 연결 안에서 유일해야 합니다.</param>
        /// <exception cref="ArgumentNullException">채널 또는 일반 변수 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">용량·제한 시간·모델 수가 지원 범위를 벗어납니다.</exception>
        /// <exception cref="ArgumentException">이름 중복, null 멤버 또는 미등록 변수/DataSet 참조가 있습니다.</exception>
        public Iec61850ServerSession(IMmsChannel channel, MmsServerVariable[] variables,
            int maximumPduSize = 4096, int timeoutMilliseconds = 10000, int maximumNamesPerPage = 16,
            MmsServerControl[] controls = null, MmsServerDataSet[] dataSets = null, MmsServerReportControl[] reports = null)
            : base(channel, Expand(variables, controls, reports), maximumPduSize, timeoutMilliseconds, maximumNamesPerPage, dataSets)
        {
            if (controls != null) { this.controls = new MmsServerControl[controls.Length]; Array.Copy(controls, this.controls, controls.Length); }
            InitializeReports(dataSets, reports);
        }
        private static MmsServerVariable[] Expand(MmsServerVariable[] variables, MmsServerControl[] controls, MmsServerReportControl[] reports)
        {
            if (variables == null) throw new ArgumentNullException("variables");
            if (reports != null)
            {
                if (reports.Length > 4) throw new ArgumentOutOfRangeException("reports");
                int total = variables.Length;
                for (int i = 0; i < reports.Length; i++)
                {
                    if (reports[i] == null) throw new ArgumentException("Null RCB.");
                    total += reports[i].Variables.Length;
                }
                var expanded = new MmsServerVariable[total];
                Array.Copy(variables, expanded, variables.Length);
                total = variables.Length;
                for (int i = 0; i < reports.Length; i++)
                {
                    Array.Copy(reports[i].Variables, 0, expanded, total, reports[i].Variables.Length);
                    total += reports[i].Variables.Length;
                }
                variables = expanded;
            }
            if (controls != null)
            {
                if (controls.Length > 16) throw new ArgumentOutOfRangeException("controls");

                int count = variables.Length;
                for (int i = 0; i < controls.Length; i++)
                {
                    if (controls[i] == null) throw new ArgumentException("Null control.");
                    count += controls[i].Variables.Length;
                }
                var expanded = new MmsServerVariable[count];
                Array.Copy(variables, expanded, variables.Length);
                count = variables.Length;
                for (int i = 0; i < controls.Length; i++)
                {
                    Array.Copy(controls[i].Variables, 0, expanded, count, controls[i].Variables.Length);
                    count += controls[i].Variables.Length;
                }
                variables = expanded;
            }

            return variables;
        }
        internal override bool ReadProfile(long invoke, BerElement domain, BerElement item)
        {
                if (controls != null)
                    for (int i = 0; i < controls.Length; i++)
                    {
                        if (!Matches(domain, controls[i].Domain)) continue;
                        if (controls[i].Model == MmsControlModel.SboNormal && Matches(item, controls[i].Item + "$SBO"))
                        {
                            Remaining();
                            Confirm(invoke, MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, controls[i].Select(this, channel.Milliseconds))));
                            return true;
                        }
                        if (Matches(item, controls[i].Item) || Matches(item, controls[i].Item + "$Oper") ||
                            Matches(item, controls[i].Item + "$Cancel") ||
                            (controls[i].Model == MmsControlModel.SboEnhanced && Matches(item, controls[i].Item + "$SBOw")))
                        {
                            Confirm(invoke, MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, MmsEncoding.Int(0x80, 3))));
                            return true;
                        }
                    }

            return false;
        }
        internal override bool WriteProfile(long invoke, BerElement domain, BerElement item, BerElement encoded)
        {
            if (reports != null)
                for (int i = 0; i < reports.Length; i++)
                    if (Matches(domain, reports[i].Domain))
                        for (int j = 1; j < reports[i].Variables.Length; j++)
                            if (Matches(item, reports[i].Variables[j].Item))
                            {
                                string fullName = reports[i].Variables[j].Item;
                                int result = reports[i].Write(this, fullName.Substring(reports[i].Item.Length + 1),
                                    encoded, channel.Milliseconds);
                                Confirm(invoke, MmsEncoding.Tlv(0xa5, result == -1 ? MmsEncoding.Tlv(0x81) : MmsEncoding.Int(0x80, result)));
                                return true;
                            }
            if (controls != null)
                for (int i = 0; i < controls.Length; i++)
                    if (Matches(domain, controls[i].Domain))
                    {
                        bool operate = Matches(item, controls[i].Item + "$Oper");
                        bool select = controls[i].Model == MmsControlModel.SboEnhanced && Matches(item, controls[i].Item + "$SBOw");
                        if (operate || select || Matches(item, controls[i].Item + "$Cancel"))
                        {
                            Remaining();
                            int result = controls[i].Apply(this, channel.Milliseconds, encoded, operate, pendingControl != null, select);
                            if (result == -1 && controls[i].PendingFor(this)) pendingControl = controls[i];
                            if (result != -1)
                            {
                                byte[] diagnostic = controls[i].ErrorReport(encoded, operate, select);
                                if (diagnostic != null)
                                {
                                    if (diagnostic.Length > NegotiatedPduSize)
                                        throw new MmsProtocolException("Control diagnostic exceeds PDU.");
                                    SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, diagnostic)));
                                }
                            }
                            Confirm(invoke, MmsEncoding.Tlv(0xa5, result == -1 ? MmsEncoding.Tlv(0x81) : MmsEncoding.Int(0x80, result)));
                            return true;
                        }
                    }

            return false;
        }
        internal override void ReleaseProfile()
        {
            ReleaseReports();
            if (controls != null)
                for (int i = 0; i < controls.Length; i++) controls[i].Release(this);
            pendingControl = null;
        }
        /// <summary>준비된 완료 또는 완료 기한 초과 통지를 최대 1개 송신합니다. 수신 대기는 하지 않습니다.</summary>
        /// <returns>통지를 송신하면 true, 아직 완료되지 않았거나 요청이 없으면 false입니다.</returns>
        /// <remarks>호출자 이벤트 루프에서 정기적으로 호출하십시오. 별도 타이머/스레드가 없습니다.
        /// ProcessRequest의 수신 대기로 완료 처리가 막히지 않도록 채널 수신 가능 여부를 먼저 검사하십시오.
        /// 송신 실패 시 Faulted 상태로 전환하고 예외를 전파하며 선택과 진행 중 요청을 해제합니다. 실제 출력은 취소하지 않습니다.</remarks>
        public bool ProcessControlCompletion()
        {
            Begin(true);
            try
            {
                if (pendingControl == null) return false;
                byte[] report = pendingControl.CompletionReport(this, channel.Milliseconds);
                if (report == null) return false;
                if (report.Length > NegotiatedPduSize) throw new MmsProtocolException("Completion exceeds PDU.");
                SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, report)));
                pendingControl.Release(this); pendingControl = null;
                return true;
            }
            catch { ReleaseProfile(); Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }

    }
}