using VagabondK.nF.MMS;
using System;
using VagabondK.nF.Internal;

namespace VagabondK.nF.IEC61850.Client
{
    /// <summary>현재 제어 요청과 참조·출처·ctlNum이 일치한 LastApplError 진단입니다.</summary>
    public sealed class MmsControlError
    {
        /// <summary>원본 MMS 제어 변수 참조입니다.</summary>
        public string ObjectReference { get; private set; }
        /// <summary>ControlLastApplError(0~3)입니다.</summary>
        public int Error { get; private set; }
        /// <summary>요청 ctlNum(0~255)입니다.</summary>
        public int ControlNumber { get; private set; }
        /// <summary>ControlAddCause(0~27)입니다.</summary>
        public int AdditionalCause { get; private set; }
        internal MmsControlError(string reference, int error, int number, int cause)
        { ObjectReference = reference; Error = error; ControlNumber = number; AdditionalCause = cause; }
    }

    /// <summary>SPC/DPC/APC Direct/SBO normal/enhanced-security의 제한된 동기 Client입니다.</summary>
    /// <remarks>메모리 시험용입니다. 호출을 직렬화하고 응답 유실 시 자동 재시도하지 마십시오.
    /// normal 모델의 Operate 성공은 요청 수락입니다. enhanced 모델은 OperateAndWait로 완료를 별도 확인합니다.
    /// 시간 예약·interlock/synchroCheck는 지원하지 않습니다. 모델·오버로드·선택 상태가 맞지 않으면 InvalidOperationException이 발생합니다.
    /// UTC 시각은 Unix epoch 기준 0~4294967295999ms이며 범위 밖 시각과 NaN/Infinity는 인수 범위 예외입니다.
    /// 일반 통신 기한 초과는 예외와 Faulted로 전파되며 정상 접근 거절만 false와 errorCode로 반환합니다.</remarks>
    public sealed class MmsControlClient
    {
        private readonly Iec61850Client client;
        private readonly string domain, item;
        private readonly byte[] origin;
        private readonly int category;
        private byte number;
        private bool selected, selectedTest;
        private float lastValue;
        /// <summary>명시적으로 선택한 제어 값 프로파일입니다. 자동 CDC 추론은 하지 않습니다.</summary>
        public MmsControlValueKind ValueKind { get; private set; }
        /// <summary>서버에서 읽은 ctlModel입니다.</summary>
        public MmsControlModel Model { get; private set; }
        /// <summary>마지막 SBOw/Operate/Cancel 중 받은 선택적 상세 진단입니다. 진단이 없으면 null입니다.</summary>
        public MmsControlError LastError { get; private set; }
        /// <summary>제어 모델을 조회합니다. CO item은 예: GGIO1$CO$Selected입니다.</summary>
        /// <param name="client">협상된 연결입니다.</param>
        /// <param name="domain">원본 MMS domain입니다.</param>
        /// <param name="item">LN$CO$object 형식의 이름입니다. 중첩 경로는 지원하지 않습니다.</param>
        /// <param name="origin">복사할 0~64바이트의 출처 식별자이며 인증 정보가 아닙니다.</param>
        /// <param name="originCategory">출처 범주 0~8입니다.</param>
        /// <param name="valueKind">SPC/DPC/APC의 명시적 값 프로파일입니다.</param>
        /// <exception cref="ArgumentNullException">클라이언트 또는 출처 식별자가 null입니다.</exception>
        /// <exception cref="ArgumentException">이름·출처 길이·범주 또는 값 프로파일이 지원 범위를 벗어납니다.</exception>
        /// <exception cref="InvalidOperationException">쓰기 서비스가 협상되지 않았거나 연결을 사용할 수 없습니다.</exception>
        /// <exception cref="NotSupportedException">원격 ctlModel이 지원하는 Direct/SBO normal/enhanced 모델이 아닙니다.</exception>
        public MmsControlClient(Iec61850Client client, string domain, string item, byte[] origin, int originCategory = 3,
            MmsControlValueKind valueKind = MmsControlValueKind.Boolean)
        {
            if (client == null || origin == null) throw new ArgumentNullException("client/origin");
            if (origin.Length > 64 || originCategory < 0 || originCategory > 8) throw new ArgumentOutOfRangeException("origin");
            if (valueKind < MmsControlValueKind.Boolean || valueKind > MmsControlValueKind.AnalogFloat32)
                throw new ArgumentOutOfRangeException("valueKind");
            ValueKind = valueKind;
            IecControlEncoding.ControlItem(item);
            MmsEncoding.Identifier(domain);
            if (!client.SupportsWrite) throw new InvalidOperationException("Write was not negotiated.");
            this.client = client; this.domain = domain; this.item = item; category = originCategory;
            this.origin = new byte[origin.Length]; Array.Copy(origin, this.origin, origin.Length);
            MmsValue model = client.Read(domain, IecControlEncoding.ConfigurationItem(item));
            if (model.Kind != MmsValueKind.Integer || model.Integer < 1 || model.Integer > 4)
                throw new NotSupportedException("Only Direct/SBO normal/enhanced controls are supported.");
            Model = (MmsControlModel)model.Integer;
        }
        /// <summary>SBO를 선택합니다. 빈 응답은 false이며 선택은 원격 연결/기한에 종속됩니다.</summary>
        /// <returns>정확한 선택 참조를 받으면 true입니다.</returns>
        public bool Select()
        {
            if (Model != MmsControlModel.SboNormal) throw new InvalidOperationException("Not an SBO control.");
            selected = false;
            MmsValue result = client.Read(domain, item + "$SBO");
            if (result.Kind != MmsValueKind.VisibleString) client.InvalidControlResponse("Invalid SBO result type.");
            if (result.VisibleString.Length == 0) return false;
            if (result.VisibleString != domain + "/" + item) client.InvalidControlResponse("Unexpected SBO reference.");
            selected = true;
            return true;
        }
        /// <summary>SboEnhanced를 값·Test와 함께 선택합니다. 선택 성공은 실행이나 완료가 아닙니다.</summary>
        /// <param name="value">후속 OperateAndWait에서 사용할 값입니다.</param>
        /// <param name="utcMilliseconds">호출자가 제공하는 UTC Unix epoch ms입니다.</param>
        /// <param name="errorCode">성공/예외 전 -1, 정상 거절 0~11입니다.</param>
        /// <param name="test">선택과 실행에서 동일해야 하는 시험 플래그입니다.</param>
        /// <returns>SBOw Write 성공 여부입니다. 정상 거절은 연결을 유지합니다.</returns>
        /// <remarks>같은 연결에서 한 번만 실행하거나 Cancel하십시오. 선택 기한은 서버가 검사합니다.
        /// 통신 실패 시 선택이 남았을 수 있으며 자동 재선택하지 않습니다.</remarks>
        public bool SelectWithValue(bool value, long utcMilliseconds, out int errorCode, bool test = false)
        {
            errorCode = -1;
            RequireKind(false);
            return SelectValue(value ? 1 : 0, utcMilliseconds, out errorCode, test);
        }
        /// <summary>APC Float32 값을 SBOw로 선택합니다. NaN/Infinity와 다른 프로파일은 거절합니다.</summary>
        /// <param name="value">후속 실행에서 동일하게 지정할 유한한 Float32 값입니다.</param>
        /// <param name="utcMilliseconds">Unix 기준 시각(epoch)부터의 UTC 밀리초이며 예약 실행 시각이 아닙니다.</param>
        /// <param name="errorCode">성공/예외 전 -1, 정상 거절 시 DataAccessError 0~11입니다.</param>
        /// <param name="test">후속 실행에서도 동일하게 지정할 시험 플래그입니다.</param>
        /// <returns>SBOw 쓰기가 수락되면 true입니다. 동작 실행 또는 완료를 뜻하지 않습니다.</returns>
        /// <remarks>아직 선택하지 않은 SboEnhanced 모델에만 사용합니다. 통신 실패 후 자동 재선택하지 마십시오.</remarks>
        public bool SelectWithValue(float value, long utcMilliseconds, out int errorCode, bool test = false)
        {
            errorCode = -1;
            RequireKind(true);
            return SelectValue(value, utcMilliseconds, out errorCode, test);
        }
        private bool SelectValue(float value, long utcMilliseconds, out int errorCode, bool test)
        {
            errorCode = -1;
            if (Model != MmsControlModel.SboEnhanced || selected)
                throw new InvalidOperationException("SBOw requires an unselected SboEnhanced control.");
            byte[] encoded = Command(value, utcMilliseconds, test, true);
            LastError = null;
            try
            {
                selected = client.TryWriteControl(domain, item + "$SBOw", encoded, out errorCode);
                if (selected) { lastValue = value; selectedTest = test; }
                return selected;
            }
            finally { LastError = client.ControlError; }
        }
        /// <summary>Boolean 제어의 즉시 실행을 요청합니다. 정상 거절은 false와 오류 코드로 반환하며 연결을 유지합니다.</summary>
        /// <param name="value">요청 값입니다.</param>
        /// <param name="utcMilliseconds">호출자가 제공하는 UTC Unix epoch ms입니다. 시계 동기화를 대신하지 않습니다.</param>
        /// <param name="errorCode">성공/예외 전 -1, 정상 거절 0~11입니다.</param>
        /// <param name="test">시험 요청입니다. 자체 Server는 값을 변경하지 않습니다.</param>
        /// <returns>Write 성공 응답이면 true입니다. 설비 완료 보장이 아닙니다.</returns>
        public bool Operate(bool value, long utcMilliseconds, out int errorCode, bool test = false)
        {
            errorCode = -1;
            RequireKind(false);
            return OperateValue(value ? 1 : 0, utcMilliseconds, out errorCode, test);
        }
        /// <summary>APC Float32 실행을 요청합니다. 수락은 실제 설비 완료가 아닙니다.</summary>
        /// <param name="value">실행할 유한한 Float32 값입니다.</param>
        /// <param name="utcMilliseconds">Unix 기준 시각(epoch)부터의 UTC 밀리초이며 예약 실행 시각이 아닙니다.</param>
        /// <param name="errorCode">성공/예외 전 -1, 정상 거절 시 DataAccessError 0~11입니다.</param>
        /// <param name="test">시험 요청이며 자체 서버에서는 실제 값을 변경하지 않습니다.</param>
        /// <returns>normal 제어의 Write 성공 응답을 받으면 true입니다.</returns>
        /// <remarks>enhanced 모델에는 OperateAndWait를 사용하십시오. SBO normal은 같은 연결에서 먼저 Select해야 합니다.</remarks>
        public bool Operate(float value, long utcMilliseconds, out int errorCode, bool test = false)
        {
            errorCode = -1;
            RequireKind(true);
            return OperateValue(value, utcMilliseconds, out errorCode, test);
        }
        private bool OperateValue(float value, long utcMilliseconds, out int errorCode, bool test)
        {
            bool timedOut;
            return OperateValue(value, utcMilliseconds, out errorCode, test, false, out timedOut);
        }

        /// <summary>Boolean normal 제어의 정상 거절과 시간 초과를 예외 없이 구분합니다.</summary>
        /// <param name="value">요청할 Boolean 제어 값입니다.</param>
        /// <param name="utcMilliseconds">Unix 기준 시각(epoch)부터의 UTC 밀리초이며 예약 실행 시각이 아닙니다.</param>
        /// <param name="errorCode">정상 거절 시 DataAccessError 0~11, 그 외에는 -1입니다.</param>
        /// <param name="timedOut">통신 기한이 초과되었으면 true입니다. 실제 실행 여부는 알 수 없습니다.</param>
        /// <param name="test">시험 요청이며 자체 서버에서는 실제 값을 변경하지 않습니다.</param>
        /// <returns>Write 성공 응답이면 true, 정상 거절 또는 시간 초과이면 false입니다.</returns>
        /// <remarks>IMmsTimeoutChannel이 필요합니다. 시간 초과 시 이미 실행되었을 수 있으며 클라이언트는 Faulted 상태가 됩니다.
        /// 자동 재시도하지 마십시오. EOF·소켓·서비스·프로토콜 오류는 여전히 예외이며 enhanced에는 사용할 수 없습니다.</remarks>
        public bool TryOperate(bool value, long utcMilliseconds, out int errorCode, out bool timedOut, bool test = false)
        {
            errorCode = -1; timedOut = false;
            RequireKind(false);
            return OperateValue(value ? 1 : 0, utcMilliseconds, out errorCode, test, true, out timedOut);
        }

        /// <summary>APC Float32 normal 제어의 정상 거절과 시간 초과를 예외 없이 구분합니다.</summary>
        /// <param name="value">요청할 유한한 Float32 제어 값입니다.</param>
        /// <param name="utcMilliseconds">Unix 기준 시각(epoch)부터의 UTC 밀리초이며 예약 실행 시각이 아닙니다.</param>
        /// <param name="errorCode">정상 거절 시 DataAccessError 0~11, 그 외에는 -1입니다.</param>
        /// <param name="timedOut">통신 기한이 초과되었으면 true입니다. 실제 실행 여부는 알 수 없습니다.</param>
        /// <param name="test">시험 요청이며 자체 서버에서는 실제 값을 변경하지 않습니다.</param>
        /// <returns>Write 성공 응답이면 true, 정상 거절 또는 시간 초과이면 false입니다.</returns>
        /// <remarks>연결 수명과 실행 여부가 불확실한 경우의 처리 규칙은 Boolean TryOperate와 같습니다.</remarks>
        public bool TryOperate(float value, long utcMilliseconds, out int errorCode, out bool timedOut, bool test = false)
        {
            errorCode = -1; timedOut = false;
            RequireKind(true);
            return OperateValue(value, utcMilliseconds, out errorCode, test, true, out timedOut);
        }

        private bool OperateValue(float value, long utcMilliseconds, out int errorCode, bool test,
            bool timeoutResult, out bool timedOut)
        {
            errorCode = -1;
            timedOut = false;
            client.CheckTimeoutChannel(timeoutResult);
            if (Model == MmsControlModel.DirectEnhanced || Model == MmsControlModel.SboEnhanced)
                throw new InvalidOperationException("Use OperateAndWait for enhanced controls.");
            byte[] encoded = Command(value, utcMilliseconds, test, true);
            if (Model == MmsControlModel.SboNormal && !selected)
                throw new InvalidOperationException("Select on this connection before Operate.");
            selected = false;
            lastValue = value;
            LastError = null;
            try { return client.TryWriteControl(domain, item + "$Oper", encoded, out errorCode, timeoutResult, out timedOut); }
            finally { LastError = client.ControlError; }
        }
        /// <summary>enhanced 모델의 요청 수락과 후속 CommandTermination을 한 전체 기한 안에서 기다립니다.</summary>
        /// <param name="value">요청 값입니다.</param>
        /// <param name="utcMilliseconds">Unix 기준 시각(epoch)부터의 UTC 밀리초입니다. 예약 실행 시각이 아닙니다.</param>
        /// <param name="accepted">Write 성공을 받으면 true입니다. 이후 실패/예외라도 실행 요청은 전달되었을 수 있습니다.</param>
        /// <param name="errorCode">요청 거절 DataAccessError 0~11, 수락/통신 예외 전 -1입니다.</param>
        /// <param name="test">허용 검사만 수행하는 시험 요청입니다.</param>
        /// <returns>정상 완료 통지를 받으면 true, 요청 거절 또는 부정 완료이면 false입니다. 상세 진단은 LastError에 있습니다.</returns>
        /// <remarks>기한 초과·연결 유실은 예외/Faulted입니다. 물리 동작을 취소하거나 자동 재전송하지 않습니다.
        /// 일반 Report/다른 명령 통지는 허용하지 않습니다. 모든 호출을 직렬화하십시오.</remarks>
        public bool OperateAndWait(bool value, long utcMilliseconds, out bool accepted, out int errorCode, bool test = false)
        {
            accepted = false; errorCode = -1;
            RequireKind(false);
            return OperateValueAndWait(value ? 1 : 0, utcMilliseconds, out accepted, out errorCode, test);
        }
        /// <summary>APC Float32 요청 수락과 enhanced 완료 통지를 한 전체 기한 안에서 확인합니다.</summary>
        /// <param name="value">요청할 유한한 Float32 값입니다. SBO enhanced에서는 선택한 값과 같아야 합니다.</param>
        /// <param name="utcMilliseconds">Unix 기준 시각(epoch)부터의 UTC 밀리초이며 예약 실행 시각이 아닙니다.</param>
        /// <param name="accepted">Write 성공 응답을 받으면 true입니다. 이후 실패해도 동작이 시작되었을 수 있습니다.</param>
        /// <param name="errorCode">요청 거절 시 DataAccessError 0~11, 수락/통신 예외 전에는 -1입니다.</param>
        /// <param name="test">시험 요청이며 SBO enhanced에서는 선택한 플래그와 같아야 합니다.</param>
        /// <returns>정상 완료 통지를 받으면 true, 요청 거절 또는 부정 완료이면 false입니다.</returns>
        /// <remarks>기한 초과나 연결 유실은 예외/Faulted입니다. 직렬 호출하고 자동 재전송하지 마십시오.</remarks>
        public bool OperateAndWait(float value, long utcMilliseconds, out bool accepted, out int errorCode, bool test = false)
        {
            accepted = false; errorCode = -1;
            RequireKind(true);
            return OperateValueAndWait(value, utcMilliseconds, out accepted, out errorCode, test);
        }
        private bool OperateValueAndWait(float value, long utcMilliseconds, out bool accepted, out int errorCode, bool test)
        {
            accepted = false; errorCode = -1;
            bool sbo = Model == MmsControlModel.SboEnhanced;
            if (Model != MmsControlModel.DirectEnhanced && !sbo) throw new InvalidOperationException("Not an enhanced control.");
            if (sbo && (!selected || lastValue != value || selectedTest != test))
                throw new InvalidOperationException("Select the same value and Test before OperateAndWait.");
            byte[] encoded = Command(value, utcMilliseconds, test, true, !sbo);
            selected = false;
            LastError = null;
            try { return client.OperateAndWait(domain, item + "$Oper", encoded, out accepted, out errorCode); }
            finally { LastError = client.ControlError; }
        }
        /// <summary>이 연결의 SBO 선택을 취소합니다. 실행된 동작을 되돌리지는 않습니다.</summary>
        /// <param name="utcMilliseconds">호출자가 제공하는 UTC Unix epoch ms입니다.</param>
        /// <param name="errorCode">성공/예외 전 -1, 정상 거절 0~11입니다.</param>
        /// <returns>취소 성공 응답 여부입니다.</returns>
        public bool Cancel(long utcMilliseconds, out int errorCode)
        {
            errorCode = -1;
            bool enhanced = Model == MmsControlModel.SboEnhanced;
            if ((Model != MmsControlModel.SboNormal && !enhanced) || !selected)
                throw new InvalidOperationException("No local SBO selection.");
            byte[] encoded = Command(lastValue, utcMilliseconds, enhanced && selectedTest, false, !enhanced);
            selected = false;
            LastError = null;
            try { return client.TryWriteControl(domain, item + "$Cancel", encoded, out errorCode); }
            finally { LastError = client.ControlError; }
        }
        private void RequireKind(bool analog)
        {
            if ((ValueKind == MmsControlValueKind.AnalogFloat32) != analog)
                throw new InvalidOperationException("Control value profile does not match this overload.");
        }
        private byte[] Command(float value, long milliseconds, bool test, bool operate, bool advance = true)
        {
            if (milliseconds < 0 || milliseconds / 1000 > uint.MaxValue) throw new ArgumentOutOfRangeException("milliseconds");
            uint seconds = (uint)(milliseconds / 1000);
            uint fraction = (uint)(((milliseconds % 1000) * 16777216L) / 1000);
            byte[] time = { (byte)(seconds >> 24), (byte)(seconds >> 16), (byte)(seconds >> 8), (byte)seconds,
                (byte)(fraction >> 16), (byte)(fraction >> 8), (byte)fraction, 0x3f };
            byte[] controlValue;
            if (ValueKind == MmsControlValueKind.AnalogFloat32)
            {
                uint bits = RealBits.Encode(value);
                if ((bits & 0x7f800000U) == 0x7f800000U) throw new ArgumentOutOfRangeException("value");
                controlValue = MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0x87,
                    new byte[] { 8, (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits }));
            }
            else controlValue = MmsEncoding.Tlv(0x83, new byte[] { value != 0 ? (byte)1 : (byte)0 });
            if (advance) number++;
            return MmsEncoding.Tlv(0xa2, controlValue,
                MmsEncoding.Tlv(0xa2, MmsEncoding.Int(0x85, category), MmsEncoding.Tlv(0x89, origin)),
                MmsEncoding.Int(0x86, number), MmsEncoding.Tlv(0x91, time),
                MmsEncoding.Tlv(0x83, new byte[] { test ? (byte)1 : (byte)0 }),
                operate ? MmsEncoding.Tlv(0x84, new byte[] { 6, 0 }) : new byte[0]);
        }
    }
}
