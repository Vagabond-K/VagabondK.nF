using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;

namespace VagabondK.nF.IEC61850.Server
{
    /// <summary>고정 DataSet의 제한된 비버퍼형 RCB입니다. 변경·GI·integrity 보고만 지원하며 연결 단절 중 이벤트를 보관하지 않습니다.</summary>
    /// <remarks>모든 접근을 직렬화하고 세션의 ProcessReports를 정기적으로 호출하십시오. 인증·백그라운드 타이머·분할 보고는 제공하지 않습니다.</remarks>
    public sealed class MmsServerReportControl
    {
        /// <summary>고정 DataSet입니다.</summary>
        public MmsServerDataSet DataSet { get; private set; }
        /// <summary>RCB domain입니다.</summary>
        public string Domain { get; private set; }
        /// <summary>LN$RP$RCB 이름입니다.</summary>
        public string Item { get; private set; }
        /// <summary>보고 ID입니다.</summary>
        public string ReportId { get; private set; }
        /// <summary>고정 구성 버전입니다.</summary>
        public uint ConfigurationRevision { get; private set; }
        private readonly int minimumInterval;
        private object owner;
        private bool enabled, reserved, gi;
        private byte sequence;
        private int integrity;
        private long nextIntegrity, nextAllowed;
        private byte[][] previous;
        private long[] versions;
        private readonly MmsServerVariable[] fields;
        internal readonly MmsServerVariable[] Variables;
        private static readonly string[] names = { "RptID", "RptEna", "Resv", "DatSet", "ConfRev", "OptFlds",
            "BufTm", "SqNum", "TrgOps", "IntgPd", "GI" };
        /// <summary>명시적으로 고정한 보고 구성을 생성합니다. 기본 비활성, 최소 송신 간격 기본 100ms입니다.</summary>
        /// <param name="domain">URCB의 공백 없는 ASCII 1~64자 domain입니다.</param>
        /// <param name="item">최대 55자의 LN$RP$RCB 형식 item입니다.</param>
        /// <param name="reportId">연결 내에서 유일하게 등록할 공백 없는 ASCII 1~64자 보고 ID입니다.</param>
        /// <param name="dataSet">보고할 고정 DataSet입니다. 변수 객체는 복제하지 않습니다.</param>
        /// <param name="configurationRevision">0이 아닌 고정 구성 버전입니다.</param>
        /// <param name="integrityMilliseconds">0이면 주기 보고 비활성화, 그 외에는 최소 송신 간격 이상 60000ms 이하입니다.</param>
        /// <param name="minimumIntervalMilliseconds">보고 간 최소 간격(1~60000ms)입니다. 실제 송신은 ProcessReports 호출에 의존합니다.</param>
        /// <exception cref="ArgumentNullException">DataSet이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">구성 버전이나 주기가 지원 범위를 벗어납니다.</exception>
        /// <exception cref="ArgumentException">URCB 이름 또는 식별자 형식이 잘못되었습니다.</exception>
        public MmsServerReportControl(string domain, string item, string reportId, MmsServerDataSet dataSet,
            uint configurationRevision = 1, int integrityMilliseconds = 0, int minimumIntervalMilliseconds = 100)
        {
            MmsEncoding.Identifier(domain); MmsEncoding.Identifier(item); MmsEncoding.Identifier(reportId);
            if (item.IndexOf("$RP$") < 1 || item.Length > 55) throw new ArgumentException("Expected LN$RP$RCB <=55 characters.");
            if (dataSet == null) throw new ArgumentNullException("dataSet");
            if (configurationRevision == 0 || minimumIntervalMilliseconds < 1 || minimumIntervalMilliseconds > 60000 ||
                integrityMilliseconds < 0 || integrityMilliseconds > 60000 ||
                (integrityMilliseconds != 0 && integrityMilliseconds < minimumIntervalMilliseconds))
                throw new ArgumentOutOfRangeException("limits");
            Domain = domain; Item = item; ReportId = reportId; DataSet = dataSet;
            ConfigurationRevision = configurationRevision; integrity = integrityMilliseconds; minimumInterval = minimumIntervalMilliseconds;
            fields = new MmsServerVariable[names.Length]; Variables = new MmsServerVariable[names.Length + 1];
            byte[][] types = { MmsEncoding.Int(0x8a, -129), MmsEncoding.Tlv(0x83), MmsEncoding.Tlv(0x83),
                MmsEncoding.Int(0x8a, -129), MmsEncoding.Int(0x86, 32), MmsEncoding.Int(0x84, 10),
                MmsEncoding.Int(0x86, 32), MmsEncoding.Int(0x86, 8), MmsEncoding.Int(0x84, 6),
                MmsEncoding.Int(0x86, 32), MmsEncoding.Tlv(0x83) };
            byte[][] values = { Text(reportId), Bool(false), Bool(false), Text(dataSet.Domain + "/" + dataSet.Item),
                MmsEncoding.Int(0x86, configurationRevision), MmsEncoding.Tlv(0x84, new byte[] { 6, 0x58, 0x80 }),
                MmsEncoding.Int(0x86, 0), MmsEncoding.Int(0x86, 0), MmsEncoding.Tlv(0x84, new byte[] { 2, 0x6c }),
                MmsEncoding.Int(0x86, integrity), Bool(false) };
            for (int i = 0; i < names.Length; i++)
            {
                fields[i] = new MmsServerVariable(domain, names[i], types[i], values[i]);
                Variables[i + 1] = new MmsServerVariable(domain, item + "$" + names[i], types[i], values[i]);
            }
            Variables[0] = new MmsServerVariable(domain, item, fields);
        }
        private static byte[] Text(string value)
        {
            if (value.Length > 129) throw new ArgumentOutOfRangeException("value");
            var bytes = new byte[value.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)value[i];
            return MmsEncoding.Tlv(0x8a, bytes);
        }
        private static byte[] Bool(bool value) { return MmsEncoding.Tlv(0x83, new byte[] { value ? (byte)1 : (byte)0 }); }
        private void Field(int index, byte[] value) { fields[index].ReplaceEncoded(value); Variables[index + 1].ReplaceEncoded(value); }
        internal int Write(object session, string attribute, BerElement value, long now)
        {
            if (owner != null && owner != session) return 3;
            if (attribute == "RptEna" || attribute == "Resv" || attribute == "GI")
            {
                if (value.Tag != 0x83) return 7;
                bool flag = BerEncoding.ReadBoolean(value.Buffer, value.Offset, value.Length);
                if (attribute == "RptEna")
                {
                    if (flag && !enabled)
                    {
                        var snapshot = new byte[DataSet.Count][]; var revisions = new long[DataSet.Count];
                        for (int i = 0; i < snapshot.Length; i++)
                        { snapshot[i] = DataSet.GetMember(i).EncodeValue(); revisions[i] = DataSet.GetMember(i).ChangeVersion; }
                        previous = snapshot; versions = revisions; owner = session; enabled = true; sequence = 0;
                        nextAllowed = now; nextIntegrity = now + integrity; Field(7, MmsEncoding.Int(0x86, 0));
                    }
                    else if (!flag)
                    { enabled = false; gi = false; previous = null; versions = null; if (!reserved) owner = null; }
                    Field(1, Bool(enabled)); return -1;
                }
                if (attribute == "Resv")
                {
                    if (enabled) return 3;
                    reserved = flag; owner = flag ? session : null; Field(2, Bool(flag)); return -1;
                }
                if (!enabled || !flag) return 3;
                gi = true; return -1;
            }
            if (attribute == "IntgPd")
            {
                if (enabled) return 3;
                if (value.Tag != 0x86) return 7;
                long period = value.Integer();
                if (period < 0 || period > 60000 || (period != 0 && period < minimumInterval)) return 11;
                integrity = (int)period; Field(9, MmsEncoding.Int(0x86, period)); return -1;
            }
            return 3;
        }
        internal void Release(object session)
        {
            if (owner != session) return;
            owner = null; enabled = reserved = gi = false; previous = null; versions = null;
            Field(1, Bool(false)); Field(2, Bool(false));
        }
        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        internal byte[] Build(object session, long now)
        {
            if (owner != session || !enabled || now < nextAllowed) return null;
            bool all = gi || (integrity != 0 && now >= nextIntegrity);
            byte reason = gi ? (byte)4 : (byte)8;
            byte[] inclusion = null; byte[][] values = null, reasons = null;
            int count = 0;
            for (int i = 0; i < DataSet.Count; i++)
            {
                var member = DataSet.GetMember(i);
                long revision = member.ChangeVersion;
                byte[] current = previous[i];
                bool changed = false;
                if (versions[i] != revision)
                {
                    current = member.EncodeValue(); changed = !Equal(current, previous[i]);
                    versions[i] = revision;
                }
                if (!all && !changed) continue;
                if (inclusion == null)
                {
                    inclusion = new byte[(DataSet.Count + 7) / 8 + 1];
                    inclusion[0] = (byte)((8 - DataSet.Count % 8) % 8);
                    values = new byte[DataSet.Count][]; reasons = new byte[DataSet.Count][];
                }
                inclusion[1 + i / 8] |= (byte)(128 >> (i % 8));
                values[count] = current;
                reasons[count++] = MmsEncoding.Tlv(0x84, new byte[] { 2, all ? reason :
                    (member.IsRaw && member.Encoded[0] == 0x84 && member.EncodedLength == 5) ? (byte)32 : (byte)64 });
                previous[i] = current;
            }
            if (count == 0) return null;
            var data = new byte[6 + count * 2][];
            data[0] = Text(ReportId); data[1] = MmsEncoding.Tlv(0x84, new byte[] { 6, 0x58, 0x80 });
            data[2] = MmsEncoding.Int(0x86, sequence);
            data[3] = Text(DataSet.Domain + "/" + DataSet.Item);
            data[4] = MmsEncoding.Int(0x86, ConfigurationRevision); data[5] = MmsEncoding.Tlv(0x84, inclusion);
            for (int i = 0; i < count; i++) { data[6 + i] = values[i]; data[6 + count + i] = reasons[i]; }
            sequence++; gi = false; nextAllowed = now + minimumInterval;
            if (all) nextIntegrity = now + integrity;
            Field(7, MmsEncoding.Int(0x86, sequence));
            return MmsEncoding.Tlv(0xa3, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa1,
                MmsEncoding.Tlv(0x80, new byte[] { 82, 80, 84 })), MmsEncoding.Tlv(0xa0, data)));
        }
    }
}
