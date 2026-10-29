using VagabondK.nF.MMS;
using System;

namespace VagabondK.nF.IEC61850.Server
{
    /// <summary>SBOw 선택과 제어 실행 직전의 부작용 없는 동기 허용 검사입니다. Test 요청에도 호출됩니다.</summary>
    /// <param name="request">검증된 명령의 독립된 스냅샷입니다. origin은 인증 정보가 아닙니다.</param>
    /// <returns>-1은 허용, 0~11은 DataAccessError 거절입니다. 다른 값이나 예외는 세션을 Faulted로 만듭니다.</returns>
    public delegate int MmsControlValidator(MmsControlRequest request);

    /// <summary>허용된 제어 중 Test가 아닌 요청의 실행을 동기적으로 요청합니다. 요청당 한 번 호출하며 자동 재시도하지 않습니다.</summary>
    /// <param name="request">허용 검사에 전달한 것과 동일한 명령 스냅샷입니다.</param>
    /// <returns>-1은 실행 요청 수락, 0~11은 실패입니다. 다른 값이나 예외는 세션을 Faulted로 만듭니다.</returns>
    /// <remarks>대기·재진입 없이 반환하십시오. 수락은 물리 동작 완료나 CommandTermination이 아닙니다.
    /// 실패·예외·응답 유실 전에 외부 부작용이 발생했을 수 있습니다.</remarks>
    public delegate int MmsControlHandler(MmsControlRequest request);

    /// <summary>SPC/DPC/APC SBOw/Operate의 불변 스냅샷입니다. 시간·origin·제어 번호는 인증이나 재생 방지를 보장하지 않습니다.</summary>
    public sealed class MmsControlRequest
    {
        /// <summary>제어 domain입니다.</summary>
        public string Domain { get; private set; }
        /// <summary>LN$CO$object 이름입니다.</summary>
        public string Item { get; private set; }
        /// <summary>SPC/DPC 요청 Boolean 값입니다. APC는 예외이며 AnalogValue를 사용합니다.</summary>
        public bool Value
        {
            get
            {
                if (ValueKind == MmsControlValueKind.AnalogFloat32) throw new InvalidOperationException("Analog control request.");
                return booleanValue;
            }
        }
        private readonly bool booleanValue;
        private readonly float analogValue;
        /// <summary>SPC/DPC/APC의 명시적 값 프로파일입니다.</summary>
        public MmsControlValueKind ValueKind { get; private set; }
        /// <summary>APC Float32 요청 값입니다. 다른 프로파일은 예외입니다.</summary>
        public float AnalogValue
        {
            get
            {
                if (ValueKind != MmsControlValueKind.AnalogFloat32) throw new InvalidOperationException("Not an analog control request.");
                return analogValue;
            }
        }
        /// <summary>실행 없이 허용 검사만 수행하는 요청인지 나타냅니다.</summary>
        public bool Test { get; private set; }
        /// <summary>출처 범주(origin category) 0~8입니다.</summary>
        public int OriginCategory { get; private set; }
        /// <summary>제어 번호 0~255입니다. 중복 실행 방지 키가 아닙니다.</summary>
        public byte ControlNumber { get; private set; }
        private readonly byte[] origin;
        private readonly byte[] utcTime;
        /// <summary>0~64바이트 출처 식별자(origin)의 독립된 복사본을 반환합니다.</summary>
        /// <returns>호출자가 변경해도 요청 스냅샷에 영향을 주지 않는 출처 식별자 배열입니다.</returns>
        public byte[] GetOrigin() { return Copy(origin, 0, origin.Length); }
        /// <summary>명령 T의 원시 UTC 8바이트를 독립된 복사본으로 반환합니다. 시간 예약이나 동기화 검사를 수행하지 않습니다.</summary>
        /// <returns>초·소수부·시간 품질을 전송 순서대로 보관하는 8바이트 복사본입니다.</returns>
        public byte[] GetUtcTime() { return Copy(utcTime, 0, utcTime.Length); }

        internal MmsControlRequest(string domain, string item, bool value, bool test, int category,
            byte number, BerElement identifier, BerElement time,
            MmsControlValueKind valueKind = MmsControlValueKind.Boolean, float analog = 0)
        {
            Domain = domain; Item = item; booleanValue = value; Test = test;
            ValueKind = valueKind; analogValue = analog;
            OriginCategory = category; ControlNumber = number;
            origin = Copy(identifier.Buffer, identifier.Offset, identifier.Length);
            utcTime = Copy(time.Buffer, time.Offset, time.Length);
        }
        internal bool MatchesSelection(bool value, bool test, int category, byte number, BerElement identifier,
            MmsControlValueKind valueKind = MmsControlValueKind.Boolean, float analog = 0)
        {
            return ValueKind == valueKind && (valueKind == MmsControlValueKind.AnalogFloat32 ? analogValue == analog : booleanValue == value) &&
                Test == test && OriginCategory == category &&
                ControlNumber == number && identifier.Matches(origin);
        }
        private static byte[] Copy(byte[] source, int offset, int length)
        {
            var result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }
    }
}
