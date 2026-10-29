using System;

namespace VagabondK.nF.MMS.Server
{
    /// <summary>고정 명명 변수 목록 디렉터리와 순서가 보존되는 다중 읽기를 처리합니다.</summary>
    public partial class MmsServerSession
    {
        private MmsServerNamedVariableList[] namedVariableLists;

        private void InitializeDataServices(MmsServerNamedVariableList[] sets)
        {
            if (sets != null && sets.Length > 8) throw new ArgumentOutOfRangeException("namedVariableLists");
            namedVariableLists = sets == null ? null : new MmsServerNamedVariableList[sets.Length];
            if (sets != null)
                for (int i = 0; i < sets.Length; i++)
                {
                    if (sets[i] == null) throw new ArgumentException("Null named variable list.");
                    for (int j = 0; j < i; j++)
                        if (sets[i].Domain == sets[j].Domain && sets[i].Item == sets[j].Item)
                            throw new ArgumentException("Duplicate named variable list.");
                    for (int j = 0; j < sets[i].Count; j++)
                    {
                        bool found = false;
                        for (int k = 0; k < variables.Length; k++)
                            if (variables[k] == sets[i].GetMember(j)) found = true;
                        if (!found) throw new ArgumentException("named variable list member must be a registered variable instance.");
                    }
                    namedVariableLists[i] = sets[i];
                }
        }
        private MmsServerNamedVariableList FindNamedVariableList(BerElement name)
        {
            var pair = name.Children();
            var domain = pair.Read(0x1a); var item = pair.Read(0x1a); pair.End();
            ValidateIdentifier(domain); ValidateIdentifier(item);
            if (namedVariableLists != null)
                for (int i = 0; i < namedVariableLists.Length; i++)
                    if (Matches(domain, namedVariableLists[i].Domain) && Matches(item, namedVariableLists[i].Item)) return namedVariableLists[i];
            return null;
        }
        private void NamedVariableListDirectory(long invoke, BerElement service)
        {
            var fields = service.Children(); var name = fields.Read(0xa1); fields.End();
            var set = FindNamedVariableList(name);
            if (set == null) { ServiceError(invoke, 7, 2); return; }
            Confirm(invoke, set.Directory());
        }
        private byte[] ReadAccess(BerElement domain, BerElement item, ref int remaining)
        {
            if (DenyProfileRead(domain, item)) return MmsEncoding.Int(0x80, 3);
            for (int i = 0; i < variables.Length; i++)
                if (Matches(domain, variables[i].Domain) && Matches(item, variables[i].Item))
                {
                    remaining -= variables[i].NodeCount;
                    return remaining < 0 ? null : variables[i].EncodeValue();
                }
            return MmsEncoding.Int(0x80, 10);
        }
        private void ReadVariables(long invoke, BerElement specification, bool withResult)
        {
            var access = specification.Children();
            var list = access.Read(); access.End();
            byte[][] values;
            byte[] returned = null;
            if (list.Tag == 0xa1)
            {
                var wrapper = list.Children(); var name = wrapper.Read(0xa1); wrapper.End();
                var set = FindNamedVariableList(name);
                if (set == null) { ServiceError(invoke, 7, 2); return; }
                values = new byte[set.Count][];
                for (int i = 0; i < values.Length; i++) values[i] = set.GetMember(i).EncodeValue();
                if (withResult) returned = MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa1, set.ObjectName()));
            }
            else
            {
                list.Expect(0xa0);
                var entries = list.Children();
                var firstEntry = entries.Read(0x30).Children();
                var firstName = firstEntry.Read(0xa0).Children(); firstEntry.End();
                var firstReference = firstName.Read(0xa1); firstName.End();
                var firstPair = firstReference.Children();
                var firstDomain = firstPair.Read(0x1a); var firstItem = firstPair.Read(0x1a); firstPair.End();
                ValidateIdentifier(firstDomain); ValidateIdentifier(firstItem);
                if (!entries.More && !withResult) { ReadSingle(invoke, firstDomain, firstItem); return; }
                var references = new BerElement[16]; int count = 1;
                references[0] = firstReference;
                while (entries.More)
                {
                    if (count == 16) { ServiceError(invoke, 3, 0); return; }
                    var entry = entries.Read(0x30).Children();
                    var named = entry.Read(0xa0).Children(); entry.End();
                    references[count++] = named.Read(0xa1); named.End();
                }
                if (count == 0) throw new MmsProtocolException("Empty Read list.");
                values = new byte[count][];
                int remaining = 256;
                for (int i = 0; i < count; i++)
                {
                    var pair = references[i].Children();
                    var domain = pair.Read(0x1a); var item = pair.Read(0x1a); pair.End();
                    ValidateIdentifier(domain); ValidateIdentifier(item);
                    values[i] = ReadAccess(domain, item, ref remaining);
                    if (values[i] == null) { ServiceError(invoke, 3, 0); return; }
                }
                if (withResult) returned = MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa0, list.Copy()));
            }
            Confirm(invoke, returned == null ? MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, values)) :
                MmsEncoding.Tlv(0xa4, returned, MmsEncoding.Tlv(0xa1, values)));
        }
    }
}
