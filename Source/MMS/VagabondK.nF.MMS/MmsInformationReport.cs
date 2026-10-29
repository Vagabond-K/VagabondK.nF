using System;

namespace VagabondK.nF.MMS
{
    /// <summary>MMS ObjectName의 이름 공간입니다.</summary>
    public enum MmsObjectScope
    {
        /// <summary>VMD 전체 이름입니다.</summary>
        Vmd,
        /// <summary>Domain과 item으로 구성된 이름입니다.</summary>
        Domain,
        /// <summary>현재 Association의 이름입니다.</summary>
        Association
    }
    /// <summary>alternateAccess가 없는 MMS ObjectName 스냅샷입니다.</summary>
    public sealed class MmsObjectName
    {
        /// <summary>이름 공간입니다.</summary>
        public MmsObjectScope Scope { get; private set; }
        /// <summary>Domain 범위일 때 domain, 아니면 null입니다.</summary>
        public string Domain { get; private set; }
        /// <summary>원본 MMS 식별자입니다.</summary>
        public string Item { get; private set; }
        private MmsObjectName() { }
        internal static MmsObjectName Decode(BerElement name)
        {
            var result = new MmsObjectName();
            if (name.Tag == 0xa1)
            {
                var pair = name.Children();
                result.Scope = MmsObjectScope.Domain;
                result.Domain = MmsEncoding.ReadIdentifier(pair.Read(0x1a));
                result.Item = MmsEncoding.ReadIdentifier(pair.Read(0x1a)); pair.End();
            }
            else
            {
                if (name.Tag != 0x80 && name.Tag != 0x82) throw new MmsProtocolException("Unsupported ObjectName scope.");
                result.Scope = name.Tag == 0x80 ? MmsObjectScope.Vmd : MmsObjectScope.Association;
                result.Item = MmsEncoding.ReadIdentifier(name);
            }
            return result;
        }
    }
    /// <summary>수신 버퍼와 독립된 일반 MMS InformationReport입니다. IEC 61850 의미를 해석하지 않습니다.</summary>
    public sealed class MmsInformationReport
    {
        private MmsObjectName[] variables;
        private MmsReadResult[] results;
        /// <summary>명명 변수 목록 보고이면 목록 이름, 명시적 변수 목록 보고이면 null입니다.</summary>
        public MmsObjectName NamedVariableList { get; private set; }
        /// <summary>결과 수입니다. 결과는 최대 64개이며 전체 값 노드는 최대 256개입니다.</summary>
        public int Count { get { return results.Length; } }
        /// <summary>명시적 변수 목록의 이름입니다. 명명 변수 목록 보고이면 사용할 수 없습니다.</summary>
        /// <param name="index">0 이상 Count 미만의 결과 인덱스입니다.</param>
        /// <returns>해당 결과에 대응하는 MMS 이름 스냅샷입니다.</returns>
        /// <exception cref="InvalidOperationException">명명 변수 목록 보고라서 개별 이름이 없습니다.</exception>
        /// <exception cref="IndexOutOfRangeException">인덱스가 결과 범위를 벗어납니다.</exception>
        public MmsObjectName GetVariable(int index)
        {
            if (variables == null) throw new InvalidOperationException("Report uses a named variable list.");
            return variables[index];
        }
        /// <summary>값 스냅샷 또는 항목별 접근 오류를 반환합니다.</summary>
        /// <param name="index">0 이상 Count 미만의 결과 인덱스입니다.</param>
        /// <returns>요청 순서에 대응하는 값 또는 접근 오류입니다.</returns>
        /// <exception cref="IndexOutOfRangeException">인덱스가 결과 범위를 벗어납니다.</exception>
        public MmsReadResult GetResult(int index) { return results[index]; }
        internal static MmsInformationReport Decode(BerElement pdu)
        {
            pdu.Expect(0xa3);
            var wrapper = pdu.Children(); var info = wrapper.Read(0xa0).Children(); wrapper.End();
            var specification = info.Read();
            var report = new MmsInformationReport();
            int names = 0;
            if (specification.Tag == 0xa1)
            {
                var named = specification.Children();
                report.NamedVariableList = MmsObjectName.Decode(named.Read()); named.End();
            }
            else
            {
                specification.Expect(0xa0); var list = specification.Children();
                var entries = new MmsObjectName[64];
                while (list.More)
                {
                    if (names == entries.Length) throw new MmsProtocolException("InformationReport name limit exceeded.");
                    var entry = list.Read(0x30).Children(); var named = entry.Read(0xa0).Children(); entry.End();
                    entries[names++] = MmsObjectName.Decode(named.Read()); named.End();
                }
                if (names == 0) throw new MmsProtocolException("Empty report variable list.");
                report.variables = new MmsObjectName[names]; Array.Copy(entries, report.variables, names);
            }
            var values = info.Read(0xa0).Children(); info.End();
            var results = new MmsReadResult[64]; int count = 0, remaining = 256;
            while (values.More)
            {
                if (count == results.Length) throw new MmsProtocolException("InformationReport value limit exceeded.");
                var value = values.Read();
                if (value.Tag == 0x80)
                {
                    long code = value.Integer();
                    if (code < 0 || code > 11) throw new MmsProtocolException("Invalid report access error.");
                    results[count++] = new MmsReadResult(null, (int)code);
                }
                else results[count++] = new MmsReadResult(MmsValue.Decode(value, 0, ref remaining), -1);
            }
            if (count == 0 || (report.variables != null && names != count))
                throw new MmsProtocolException("InformationReport name/result count mismatch.");
            report.results = new MmsReadResult[count]; Array.Copy(results, report.results, count);
            return report;
        }
    }
}
