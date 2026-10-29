using System;

namespace VagabondK.nF.MMS
{
    /// <summary>GetNameList 응답 한 페이지입니다. 전체 목록을 자동으로 누적하지 않습니다.</summary>
    public sealed class MmsNameListPage
    {
        /// <summary>이 응답의 원본 MMS 이름입니다. 연결 수신 버퍼를 참조하지 않습니다.</summary>
        public string[] Names { get; private set; }
        /// <summary>뒤에 이름이 더 있는지 나타냅니다.</summary>
        public bool MoreFollows { get; private set; }
        /// <summary>다음 요청의 continueAfter입니다. 마지막 페이지에서는 null입니다.</summary>
        public string ContinueAfter { get; private set; }
        internal MmsNameListPage(string[] names, bool more)
        { Names = names; MoreFollows = more; ContinueAfter = more ? names[names.Length - 1] : null; }
    }

    /// <summary>지원하는 스칼라 또는 제한된 구조체·배열의 형식 정보입니다.</summary>
    public sealed class MmsVariableAttributes
    {
        /// <summary>MMS 값 종류입니다.</summary>
        public MmsValueKind Kind { get; private set; }
        /// <summary>정수 또는 실수의 비트 수입니다. Boolean은 1, 복합 형식은 0입니다.</summary>
        public int BitWidth { get; private set; }
        /// <summary>실수 지수 비트 수입니다. 다른 종류는 0입니다.</summary>
        public int ExponentWidth { get; private set; }
        /// <summary>최대 길이입니다. BitString은 비트 단위, OctetString/VisibleString은 바이트 단위입니다.</summary>
        public int MaximumLength { get; private set; }
        /// <summary>MMS mmsDeletable 필드이며 원격 쓰기 가능 여부와는 다릅니다.</summary>
        public bool Deletable { get; private set; }
        private MmsVariableAttributes[] components;
        private string[] componentNames;
        /// <summary>배열의 선언된 원소 수입니다. 다른 종류는 0입니다.</summary>
        public int ArrayLength { get; private set; }
        /// <summary>배열 원소의 형식입니다. 다른 종류는 null입니다.</summary>
        public MmsVariableAttributes ElementType { get; private set; }
        /// <summary>구조체 성분 수입니다. 다른 종류는 0입니다.</summary>
        public int ComponentCount { get { return components == null ? 0 : components.Length; } }
        /// <summary>선언 순서의 구조체 성분 형식입니다.</summary>
        /// <param name="index">0부터 시작하는 성분 인덱스입니다.</param>
        /// <returns>성분의 선언된 형식입니다.</returns>
        /// <exception cref="InvalidOperationException">형식이 구조체가 아닙니다.</exception>
        /// <exception cref="IndexOutOfRangeException">인덱스가 성분 범위를 벗어납니다.</exception>
        public MmsVariableAttributes GetComponent(int index)
        {
            if (components == null) throw new InvalidOperationException("Not a structure type.");
            return components[index];
        }
        /// <summary>성분 이름입니다. 이름이 생략되면 null입니다.</summary>
        /// <param name="index">0부터 시작하는 성분 인덱스입니다.</param>
        /// <returns>원본 성분 이름 또는 null입니다.</returns>
        /// <exception cref="InvalidOperationException">형식이 구조체가 아닙니다.</exception>
        /// <exception cref="IndexOutOfRangeException">인덱스가 성분 범위를 벗어납니다.</exception>
        public string GetComponentName(int index)
        {
            if (componentNames == null) throw new InvalidOperationException("Not a structure type.");
            return componentNames[index];
        }
        internal MmsVariableAttributes(MmsValueKind kind, int bits, int exponent, bool deletable)
        { Kind = kind; BitWidth = bits; ExponentWidth = exponent; Deletable = deletable; }

        internal static MmsVariableAttributes Decode(BerElement specification, bool deletable)
        {
            int remaining = 256;
            return Decode(specification, deletable, 0, ref remaining);
        }
        private static MmsVariableAttributes Decode(BerElement specification, bool deletable, int depth, ref int remaining)
        {
            if (depth > 8 || --remaining < 0)
                throw new MmsProtocolException("MMS type exceeds depth 8 or total node limit 256.");
            var result = new MmsVariableAttributes((MmsValueKind)(specification.Tag & 31), 0, 0, deletable);
            if (specification.Tag == 0xa1 || specification.Tag == 0xa2)
            {
                var fields = specification.Children();
                BerElement first = fields.Read();
                if (first.Tag == 0x80)
                {
                    if (BerEncoding.ReadBoolean(first.Buffer, first.Offset, first.Length))
                        throw new MmsProtocolException("Packed composite types are unsupported.");
                    first = fields.Read();
                }
                if (specification.Tag == 0xa1)
                {
                    first.Expect(0x81);
                    long count = first.Integer();
                    if (count < 0 || count > 255) throw new MmsProtocolException("Array length exceeds 255.");
                    result.ArrayLength = (int)count;
                    var element = fields.Read(0xa2).Children(); fields.End();
                    BerElement type = element.Read(); element.End();
                    result.ElementType = Decode(type, false, depth + 1, ref remaining);
                }
                else
                {
                    first.Expect(0xa1); fields.End();
                    var entries = first.Children();
                    int count = 0;
                    while (entries.More)
                    {
                        entries.Read(0x30);
                        if (++count > remaining) throw new MmsProtocolException("Structure exceeds type node budget.");
                    }
                    result.components = new MmsVariableAttributes[count];
                    result.componentNames = new string[count];
                    entries = first.Children();
                    for (int i = 0; i < count; i++)
                    {
                        var entry = entries.Read(0x30).Children();
                        BerElement type = entry.Read();
                        if (type.Tag == 0x80)
                        {
                            result.componentNames[i] = MmsEncoding.ReadIdentifier(type);
                            type = entry.Read();
                        }
                        type.Expect(0xa1); entry.End();
                        var wrapped = type.Children();
                        BerElement child = wrapped.Read(); wrapped.End();
                        result.components[i] = Decode(child, false, depth + 1, ref remaining);
                    }
                }
            }
            else if (specification.Tag == 0x83 && specification.Length == 0) result.BitWidth = 1;
            else if (specification.Tag == 0x85 || specification.Tag == 0x86)
            {
                long width = specification.Integer();
                if (width < 1 || width > 64) throw new MmsProtocolException("Unsupported integer bit width.");
                result.BitWidth = (int)width;
            }
            else if (specification.Tag == 0x84 || specification.Tag == 0x89 || specification.Tag == 0x8a)
            {
                long length = specification.Integer();
                long maximum = length < 0 ? -length : length;
                int limit = specification.Tag == 0x84 ? 32 : specification.Tag == 0x89 ? 64 : 129;
                if (maximum < 0 || maximum > limit) throw new MmsProtocolException("String type exceeds bounded profile.");
                result.MaximumLength = (int)maximum;
            }
            else if (specification.Tag == 0x91 && specification.Length == 0) result.BitWidth = 64;
            else if (specification.Tag == 0xa7)
            {
                var floating = specification.Children();
                long width = floating.Read(2).Integer(), exp = floating.Read(2).Integer();
                floating.End();
                if (!((width == 32 && exp == 8) || (width == 64 && exp == 11)))
                    throw new MmsProtocolException("Unsupported floating point type.");
                result.BitWidth = (int)width; result.ExponentWidth = (int)exp;
            }
            else throw new MmsProtocolException("Unsupported MMS variable type.");
            return result;
        }
    }

    /// <summary>정상적인 MMS 서비스 거절입니다. 연결은 계속 사용할 수 있습니다.</summary>
    public class MmsServiceException : Exception
    {
        /// <summary>ServiceError의 errorClass를 나타내는 컨텍스트 태그 번호입니다. Reject 파생형에서는 -1입니다.</summary>
        public int ErrorClass { get; private set; }
        /// <summary>해당 errorClass 안의 오류 코드입니다. Reject 파생형은 -1입니다.</summary>
        public int ErrorCode { get; private set; }
        internal MmsServiceException(int errorClass, int code) : base("MMS service error: " + errorClass + "/" + code)
        { ErrorClass = errorClass; ErrorCode = code; }
        internal MmsServiceException(string message) : base(message) { ErrorClass = ErrorCode = -1; }
    }
    /// <summary>invoke ID가 일치하는 confirmed-requestPDU Reject입니다. 연결은 계속 사용할 수 있습니다.</summary>
    public sealed class MmsRejectException : MmsServiceException
    {
        /// <summary>confirmed-requestPDU 거절 이유 0~9입니다.</summary>
        public int Reason { get; private set; }
        internal MmsRejectException(int reason) : base("MMS confirmed request rejected: " + reason) { Reason = reason; }
    }
}
