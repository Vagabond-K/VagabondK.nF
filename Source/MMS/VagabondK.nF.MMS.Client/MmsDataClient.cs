using System;

namespace VagabondK.nF.MMS.Client
{
    /// <summary>동기 MMS 연결에서 다중 변수 및 명명 변수 목록 읽기를 제공합니다.</summary>
    public partial class MmsClient
    {
        /// <summary>서버가 GetNamedVariableListAttributes를 광고했는지 나타냅니다.</summary>
        public bool SupportsNamedVariableListDirectory { get; private set; }
        /// <summary>최대 16개 변수를 한 요청으로 읽습니다. 항목별 오류는 결과에 보존합니다.</summary>
        /// <remarks>호출자가 접근을 직렬화한 상태에서 처리한 요청 하나의 스냅샷이며, 외부 장치 값의 동시 취득을 보장하지 않습니다.</remarks>
        /// <param name="variables">null 멤버가 없는 1~16개의 참조입니다. 호출 중 배열을 변경하지 마십시오.</param>
        /// <returns>요청 순서로 정렬된 독립된 값 스냅샷과 항목별 접근 오류입니다.</returns>
        /// <exception cref="ArgumentNullException">참조 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">참조 수가 1~16 범위를 벗어납니다.</exception>
        /// <exception cref="ArgumentException">참조 중 null이 있습니다.</exception>
        public MmsReadResult[] ReadMultiple(MmsVariableReference[] variables)
        {
            if (variables == null) throw new ArgumentNullException("variables");
            if (variables.Length < 1 || variables.Length > 16) throw new ArgumentOutOfRangeException("variables");
            var names = new byte[variables.Length][];
            for (int i = 0; i < names.Length; i++)
            {
                if (variables[i] == null) throw new ArgumentException("Null variable reference.");
                names[i] = variables[i].Specification();
            }
            return ReadList(MmsEncoding.Tlv(0xa0, names), variables.Length, false);
        }
        /// <summary>고정 명명 변수 목록의 최대 16개 참조를 조회합니다. 정상 서비스 오류는 연결을 유지합니다.</summary>
        /// <param name="domain">목록이 속한 MMS domain 식별자입니다.</param>
        /// <param name="item">명명 변수 목록의 MMS item 식별자입니다.</param>
        /// <returns>선언 순서와 삭제 가능 여부를 보관하는 디렉터리 스냅샷입니다.</returns>
        /// <exception cref="InvalidOperationException">디렉터리 서비스가 협상되지 않았거나 연결을 사용할 수 없습니다.</exception>
        public MmsNamedVariableList GetNamedVariableListDirectory(string domain, string item)
        {
            byte[] name = new MmsVariableReference(domain, item).Name();
            if (!SupportsNamedVariableListDirectory) throw new InvalidOperationException("named variable list directory was not negotiated.");
            Begin(true);
            try
            {
                var response = ExchangeService(MmsEncoding.Tlv(0xac, name), 0xac).Children();
                var flag = response.Read(0x80);
                bool deletable = BerEncoding.ReadBoolean(flag.Buffer, flag.Offset, flag.Length);
                var list = response.Read(0xa1).Children(); response.End();
                var entries = new MmsVariableReference[16]; int count = 0;
                while (list.More)
                {
                    if (count == entries.Length) throw new MmsProtocolException("named variable list exceeds 16 members.");
                    var variable = list.Read(0x30).Children();
                    var named = variable.Read(0xa0).Children(); variable.End();
                    entries[count++] = MmsVariableReference.Decode(named.Read()); named.End();
                }
                if (count == 0) throw new MmsProtocolException("Empty named variable list.");
                var members = new MmsVariableReference[count]; Array.Copy(entries, members, count);
                Remaining();
                return new MmsNamedVariableList(domain, item, deletable, members);
            }
            catch (MmsServiceException) { throw; }
            catch { Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }
        /// <summary>조회한 명명 변수 목록의 현재 값을 선언 순서로 읽습니다. 디렉터리와 응답 개수/참조를 확인합니다.</summary>
        /// <param name="dataSet">앞서 조회한 명명 변수 목록의 디렉터리입니다.</param>
        /// <returns>목록 순서로 정렬된 값 스냅샷과 항목별 접근 오류입니다.</returns>
        /// <exception cref="ArgumentNullException">디렉터리가 null입니다.</exception>
        public MmsReadResult[] ReadNamedVariableList(MmsNamedVariableList dataSet)
        {
            if (dataSet == null) throw new ArgumentNullException("dataSet");
            return ReadList(MmsEncoding.Tlv(0xa1, new MmsVariableReference(dataSet.Domain, dataSet.Item).Name()),
                dataSet.Count, true);
        }
        private MmsReadResult[] ReadList(byte[] specification, int count, bool withResult)
        {
            Begin(true);
            try
            {
                var response = ExchangeService(MmsEncoding.Tlv(0xa4,
                    withResult ? MmsEncoding.Tlv(0x80, new byte[] { 1 }) : new byte[0],
                    MmsEncoding.Tlv(0xa1, specification)), 0xa4).Children();
                if (withResult)
                {
                    var wrapper = response.Read(0xa0).Children();
                    var name = wrapper.Read(0xa1).Children(); wrapper.End();
                    var returned = name.Read(0xa1); name.End();
                    var expected = MmsEncoding.Single(specification, 0, specification.Length, 0xa1).Children().Read(0xa1);
                    if (!returned.Matches(expected.Copy())) throw new MmsProtocolException("named variable list response reference mismatch.");
                }
                var results = response.Read(0xa1).Children(); response.End();
                var result = new MmsReadResult[count];
                int remaining = 256;
                for (int i = 0; i < count; i++)
                {
                    BerElement value = results.Read();
                    if (value.Tag == 0x80)
                    {
                        long error = value.Integer();
                        if (error < 0 || error > 11) throw new MmsProtocolException("Invalid access error.");
                        result[i] = new MmsReadResult(null, (int)error);
                    }
                    else result[i] = new MmsReadResult(MmsValue.Decode(value, 0, ref remaining), -1);
                }
                results.End(); Remaining();
                return result;
            }
            catch (MmsServiceException) { throw; }
            catch { Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }
    }
}
