using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using VagabondK.nF.Internal;

namespace VagabondK.nF.IEC61850.Server
{
    /// <summary>애플리케이션 실행 처리기를 연결할 수 있는 SPC/DPC/APC Direct/SBO normal/enhanced-security 제어입니다.</summary>
    /// <remarks>여러 세션과 모델 접근을 직렬화하십시오. origin은 인증 정보가 아닙니다.
    /// Test는 값을 바꾸지 않습니다. 시간 예약·Check 요청은 지원하지 않습니다.</remarks>
    public sealed class MmsServerControl
    {
        /// <summary>원본 domain입니다.</summary>
        public string Domain { get; private set; }
        /// <summary>LN$CO$object 이름입니다.</summary>
        public string Item { get; private set; }
        /// <summary>고정 제어 모델입니다.</summary>
        public MmsControlModel Model { get; private set; }
        /// <summary>현재 Boolean 메모리 제어 값입니다. APC에서는 InvalidOperationException이며 설비 상태 보장이 아닙니다.</summary>
        public bool Value
        {
            get
            {
                if (ValueKind == MmsControlValueKind.AnalogFloat32) throw new InvalidOperationException("Analog control.");
                return booleanValue;
            }
        }
        private bool booleanValue;
        private float analogValue;
        /// <summary>SPC/DPC/APC의 명시적 값 프로파일입니다.</summary>
        public MmsControlValueKind ValueKind { get; private set; }
        /// <summary>현재 APC Float32 메모리 값입니다. 다른 프로파일은 예외입니다.</summary>
        public float AnalogValue
        {
            get
            {
                if (ValueKind != MmsControlValueKind.AnalogFloat32) throw new InvalidOperationException("Not analog control.");
                return analogValue;
            }
        }
        private readonly int selectionTimeout;
        private readonly MmsServerVariable state;
        internal readonly MmsServerVariable[] Variables;
        private object owner;
        private MmsControlRequest selection;
        private long expires;
        private bool active;
        private MmsControlValidator validator;
        private MmsControlHandler handler;
        private int failureCause = -1, failureError;
        private bool reportControlErrors;
        private readonly int completionTimeout;
        private object pendingOwner;
        private MmsControlRequest pendingRequest;
        private byte[] pendingCommand;
        private long completionDeadline;
        private bool completionReady, completionSuccess;
        private int completionCause;
        /// <summary>이 객체에 완료되지 않은 enhanced 요청이 있는지 나타냅니다.</summary>
        public bool HasPendingOperation { get { return pendingOwner != null; } }
        /// <summary>유효한 제어 요청 거절 시 LastApplError를 Write 실패 앞에 송신합니다. 기본 false입니다.</summary>
        /// <remarks>수신 피어가 제어 진단 InformationReport를 처리할 때만 활성화하십시오.
        /// 형식·범위 오류와 실행 처리기의 예외에는 진단을 생성하지 않습니다.</remarks>
        /// <exception cref="InvalidOperationException">제어 처리 중이거나 완료 대기 요청이 있어 정책을 변경할 수 없습니다.</exception>
        public bool ReportControlErrors
        {
            get { return reportControlErrors; }
            set { RequireIdle(); reportControlErrors = value; }
        }
        /// <summary>기본 비활성인 SPC/DPC/APC 메모리 제어를 생성합니다.</summary>
        /// <param name="domain">원본 MMS domain입니다.</param>
        /// <param name="item">최대 53자의 LN$CO$object 이름입니다.</param>
        /// <param name="model">DirectNormal/SboNormal/DirectEnhanced/SboEnhanced입니다.</param>
        /// <param name="selectionTimeoutMilliseconds">SBO 선택 기한 1~60000ms입니다.</param>
        /// <param name="completionTimeoutMilliseconds">enhanced 실행 요청의 완료 기한 1~60000ms입니다. 완료 처리 호출 시 검사합니다.</param>
        /// <param name="valueKind">SPC Boolean, DPC Boolean/Dbpos 또는 APC AnalogueValue.f 프로파일입니다.</param>
        /// <exception cref="ArgumentException">이름이 잘못되었거나 모델·값 프로파일·기한이 지원 범위를 벗어납니다.</exception>
        public MmsServerControl(string domain, string item, MmsControlModel model, int selectionTimeoutMilliseconds = 1000,
            int completionTimeoutMilliseconds = 5000, MmsControlValueKind valueKind = MmsControlValueKind.Boolean)
        {
            MmsEncoding.Identifier(domain); IecControlEncoding.ControlItem(item);
            if (valueKind < MmsControlValueKind.Boolean || valueKind > MmsControlValueKind.AnalogFloat32)
                throw new ArgumentOutOfRangeException("valueKind");
            ValueKind = valueKind;
            if (item.Length > 53) throw new ArgumentOutOfRangeException("item");
            if ((model != MmsControlModel.DirectNormal && model != MmsControlModel.SboNormal &&
                model != MmsControlModel.DirectEnhanced && model != MmsControlModel.SboEnhanced) ||
                selectionTimeoutMilliseconds < 1 || selectionTimeoutMilliseconds > 60000 ||
                completionTimeoutMilliseconds < 1 || completionTimeoutMilliseconds > 60000)
                throw new ArgumentOutOfRangeException("model/selectionTimeoutMilliseconds/completionTimeoutMilliseconds");
            completionTimeout = completionTimeoutMilliseconds;
            Domain = domain; Item = item; Model = model; selectionTimeout = selectionTimeoutMilliseconds;
            int p = item.IndexOf("$CO$");
            string stateName = item.Substring(0, p) + (valueKind == MmsControlValueKind.AnalogFloat32 ? "$MX$" : "$ST$") +
                item.Substring(p + 4) + (valueKind == MmsControlValueKind.AnalogFloat32 ? "$mxVal" : "$stVal");
            analogState = valueKind == MmsControlValueKind.AnalogFloat32 ?
                new MmsServerVariable(domain, stateName + "$f", 0f) : null;
            analogStateComponent = analogState != null ? new MmsServerVariable(domain, "f", 0f) : null;
            state = valueKind == MmsControlValueKind.DoublePoint ? Iec61850ServerData.CreateDoublePoint(domain, stateName) :
                analogState != null ? new MmsServerVariable(domain, stateName, new MmsServerVariable[] { analogStateComponent }) :
                new MmsServerVariable(domain, stateName, false);
            var oper = CommandVariable("Oper", true);
            var cancel = CommandVariable("Cancel", false);
            var sbo = Raw("SBO", MmsEncoding.Int(0x8a, -129), MmsEncoding.Tlv(0x8a));
            bool selected = model == MmsControlModel.SboNormal || model == MmsControlModel.SboEnhanced;
            var select = model == MmsControlModel.SboEnhanced ? CommandVariable("SBOw", true) : sbo;
            var parent = new MmsServerVariable(domain, item, model == MmsControlModel.SboNormal ?
                new MmsServerVariable[] { sbo, oper, cancel } : model == MmsControlModel.SboEnhanced ?
                new MmsServerVariable[] { select, oper, cancel } : new MmsServerVariable[] { oper, cancel });
            var operName = Raw(item + "$Oper", oper.TypeDefinition(), oper.EncodeValue());
            var cancelName = Raw(item + "$Cancel", cancel.TypeDefinition(), cancel.EncodeValue());
            var config = new MmsServerVariable(domain, IecControlEncoding.ConfigurationItem(item), (int)model);
            string configuration = item.Substring(0, p) + "$CF$" + item.Substring(p + 4);
            Variables = selected ?
                new MmsServerVariable[] { parent, operName, cancelName, config, state,
                    Raw(item + (model == MmsControlModel.SboEnhanced ? "$SBOw" : "$SBO"), select.TypeDefinition(), select.EncodeValue()),
                    Raw(configuration + "$sboTimeout", MmsEncoding.Int(0x86, 32), MmsEncoding.Int(0x86, selectionTimeout)),
                    new MmsServerVariable(domain, configuration + "$sboClass", 0) } :
                new MmsServerVariable[] { parent, operName, cancelName, config, state };
            if (analogState != null)
            {
                var expanded = new MmsServerVariable[Variables.Length + 1];
                Array.Copy(Variables, expanded, Variables.Length); expanded[Variables.Length] = analogState; Variables = expanded;
            }
        }
        private readonly MmsServerVariable analogState, analogStateComponent;
        /// <summary>제어 허용 여부입니다. 기본 false입니다. false로 설정하면 선택도 해제합니다.</summary>
        /// <exception cref="InvalidOperationException">제어 처리 중이거나 완료 대기 요청이 있어 허용 정책을 변경할 수 없습니다.</exception>
        public bool Enabled
        {
            get { return enabled; }
            set { RequireIdle(); enabled = value; if (!value) ClearSelection(); }
        }
        private bool enabled;
        /// <summary>허용 검사와 실행 처리기를 함께 등록한 뒤 제어를 비활성화하고 선택을 해제합니다. 이후 Enabled=true로 설정해야 합니다.</summary>
        /// <param name="validator">null이 아닌 부작용 없는 동기 검사입니다. SBOw 선택과 실제 실행 직전, Test 요청에도 호출합니다.</param>
        /// <param name="handler">null이 아닌 동기 실행 처리기입니다. 허용된 Operate 중 Test가 아닌 요청에만 호출합니다.</param>
        /// <exception cref="ArgumentNullException">검증기 또는 실행 처리기가 null입니다.</exception>
        /// <exception cref="InvalidOperationException">제어 처리 중이거나 완료 대기 요청이 남아 있습니다.</exception>
        /// <remarks>normal 모델은 미등록 시 기존 메모리 전용 모드입니다. enhanced 모델은 핸들러 등록이 필수입니다.
        /// SBOw에는 허용 검사만, normal SBO 선택/취소에는 핸들러를 호출하지 않습니다.
        /// 핸들러 안에서 모델/세션 재진입·정책 변경·대기하지 마십시오. 모든 세션 처리를 직렬화하십시오.
        /// 반환 성공은 실행 요청 수락이지 실제 설비 완료가 아닙니다.</remarks>
        public void SetHandlers(MmsControlValidator validator, MmsControlHandler handler)
        {
            RequireIdle();
            if (validator == null) throw new ArgumentNullException("validator");
            if (handler == null) throw new ArgumentNullException("handler");
            enabled = false; ClearSelection();
            this.validator = validator; this.handler = handler;
        }
        private void RequireIdle()
        {
            if (active || pendingOwner != null) throw new InvalidOperationException("A control operation is active.");
        }
        private bool Enhanced { get { return Model == MmsControlModel.DirectEnhanced || Model == MmsControlModel.SboEnhanced; } }
        private void ClearSelection() { owner = null; selection = null; }
        private MmsServerVariable Raw(string name, byte[] type, byte[] value)
        { return new MmsServerVariable(Domain, name, type, value); }
        private MmsServerVariable CommandVariable(string name, bool operate)
        {
            var fields = new MmsServerVariable[] {
                ValueKind == MmsControlValueKind.AnalogFloat32 ?
                    new MmsServerVariable(Domain, "ctlVal", new MmsServerVariable[] { new MmsServerVariable(Domain, "f", 0f) }) :
                    new MmsServerVariable(Domain, "ctlVal", false),
                new MmsServerVariable(Domain, "origin", new MmsServerVariable[] {
                    Raw("orCat", MmsEncoding.Int(0x85, 8), MmsEncoding.Int(0x85, 0)),
                    Raw("orIdent", MmsEncoding.Int(0x89, -64), MmsEncoding.Tlv(0x89)) }),
                Raw("ctlNum", MmsEncoding.Int(0x86, 8), MmsEncoding.Int(0x86, 0)),
                Raw("T", MmsEncoding.Tlv(0x91), MmsEncoding.Tlv(0x91, new byte[8])),
                new MmsServerVariable(Domain, "Test", false),
                Raw("Check", MmsEncoding.Int(0x84, -2), MmsEncoding.Tlv(0x84, new byte[] { 6, 0 }))
            };
            if (!operate)
            {
                var cancel = new MmsServerVariable[5];
                Array.Copy(fields, cancel, 5); fields = cancel;
            }
            return new MmsServerVariable(Domain, name, fields);
        }
        internal byte[] Select(object session, long now)
        {
            RequireIdle();
            if (owner != null && now >= expires) ClearSelection();
            string reference = "";
            if (enabled && Model == MmsControlModel.SboNormal && (owner == null || owner == session))
            {
                owner = session; expires = now + selectionTimeout;
                reference = Domain + "/" + Item;
            }
            byte[] ascii = new byte[reference.Length];
            for (int i = 0; i < ascii.Length; i++) ascii[i] = (byte)reference[i];
            return MmsEncoding.Tlv(0x8a, ascii);
        }
        internal void Release(object session)
        {
            if (owner == session) ClearSelection();
            if (pendingOwner == session) { pendingOwner = null; pendingRequest = null; pendingCommand = null; }
        }
        internal bool PendingFor(object session) { return pendingOwner == session; }
        internal int Apply(object session, long now, BerElement encoded, bool operate, bool connectionBusy = false,
            bool selectWithValue = false)
        {
            if (active) throw new InvalidOperationException("A control operation is active.");
            failureCause = -1; failureError = 0;
            active = true;
            try { return ApplyCommand(session, now, encoded, operate, connectionBusy, selectWithValue); }
            finally { active = false; }
        }
        private int ApplyCommand(object session, long now, BerElement encoded, bool operate, bool connectionBusy, bool selectWithValue)
        {
            if (encoded.Tag != 0xa2) return 7;
            var fields = encoded.Children();
            bool requested = false; float analog = 0;
            if (ValueKind == MmsControlValueKind.AnalogFloat32)
            {
                if (!fields.More) return 7;
                BerElement value = fields.Read();
                if (value.Tag != 0xa2) return 7;
                var members = value.Children();
                BerElement real = members.Read(); members.End();
                if (real.Tag != 0x87 || real.Length != 5 || real.Buffer[real.Offset] != 8) return 7;
                uint bits = 0;
                for (int i = 1; i < 5; i++) bits = (bits << 8) | real.Buffer[real.Offset + i];
                if ((bits & 0x7f800000U) == 0x7f800000U) return 11;
                analog = RealBits.Decode(bits);
            }
            else
            {
                BerElement value = fields.Read();
                if (value.Tag != 0x83) return 7;
                requested = BerEncoding.ReadBoolean(value.Buffer, value.Offset, value.Length);
            }
            var origin = fields.Read(0xa2).Children();
            long category = origin.Read(0x85).Integer();
            BerElement identifier = origin.Read(0x89); origin.End();
            long number = fields.Read(0x86).Integer();
            BerElement time = fields.Read(0x91);
            BerElement testField = fields.Read(0x83);
            bool test = BerEncoding.ReadBoolean(testField.Buffer, testField.Offset, testField.Length);
            bool check = false;
            if (operate || selectWithValue)
            {
                BerElement bits = fields.Read(0x84);
                if (bits.Length != 2 || bits.Buffer[bits.Offset] != 6 || (bits.Buffer[bits.Offset + 1] & 63) != 0)
                    throw new MmsProtocolException("Invalid control Check bit string.");
                check = bits.Buffer[bits.Offset + 1] != 0;
            }
            fields.End();
            if (category < 0 || category > 8 || identifier.Length > 64 || number < 0 || number > 255 || time.Length != 8) return 11;
            if (owner != null && now >= expires) ClearSelection();
            if (!enabled) return Refuse(3, 8);
            if (check) return Refuse(3, 1);
            if (pendingOwner != null || connectionBusy) return Refuse(3, 12);
            if (selectWithValue)
            {
                if (Model != MmsControlModel.SboEnhanced || handler == null) return Refuse(3, 1);
                if (owner != null) return Refuse(3, owner == session ? 19 : 27);
                var selectedRequest = new MmsControlRequest(Domain, Item, requested, test, (int)category,
                    (byte)number, identifier, time, ValueKind, analog);
                int code = Result(validator(selectedRequest));
                if (code != -1) return Refuse(code, 3, 1);
                owner = session; selection = selectedRequest; expires = now + selectionTimeout;
                return -1;
            }
            bool sbo = Model == MmsControlModel.SboNormal || Model == MmsControlModel.SboEnhanced;
            if (sbo && owner != session)
                return Refuse(3, owner == null ? 18 : 27);
            if (Model == MmsControlModel.SboEnhanced &&
                !selection.MatchesSelection(requested, test, (int)category, (byte)number, identifier, ValueKind, analog))
                return Refuse(3, 26);
            if (!operate)
            {
                if (!sbo) return Refuse(3, 1);
                ClearSelection(); return -1;
            }
            ClearSelection();
            if (Enhanced && handler == null) return Refuse(3, 1);
            MmsControlRequest request = null;
            if (handler != null)
            {
                request = new MmsControlRequest(Domain, Item, requested, test, (int)category,
                    (byte)number, identifier, time, ValueKind, analog);
                int code = Result(validator(request));
                if (code != -1) return Refuse(code, 0, 1);
                if (!test)
                {
                    code = Result(handler(request));
                    if (code != -1) return Refuse(code, 0, 1);
                }
                if (Enhanced)
                {
                    pendingCommand = MmsEncoding.Tlv(0xa2, encoded.Copy());
                    pendingRequest = request; pendingOwner = session;
                    completionDeadline = now + completionTimeout;
                    completionReady = test; completionSuccess = true; completionCause = 0;
                    return -1;
                }
            }
            if (!test) SetState(requested, analog);
            return -1;
        }
        private static int Result(int code)
        {
            if (code < -1 || code > 11) throw new InvalidOperationException("Invalid control callback result.");
            return code;
        }
        private void SetState(bool value, float analog)
        {
            if (ValueKind == MmsControlValueKind.AnalogFloat32)
            {
                analogValue = analog; analogState.SetFloatingPoint(analog); analogStateComponent.SetFloatingPoint(analog);
            }
            else
            {
                booleanValue = value;
                if (ValueKind == MmsControlValueKind.DoublePoint) state.SetDoublePoint(value ? IecDoublePoint.On : IecDoublePoint.Off);
                else state.SetBoolean(value);
            }
        }
        private int Refuse(int code, int cause, int error = 0)
        {
            failureCause = cause; failureError = error;
            return code;
        }
        internal byte[] ErrorReport(BerElement command, bool operate, bool selectWithValue = false)
        {
            if ((!reportControlErrors && !Enhanced) || failureCause < 0) return null;
            return Report(command, operate, failureError, failureCause, false, selectWithValue);
        }
        /// <summary>수락된 enhanced 요청의 완료 결과를 예약합니다. 송신은 소유 세션의 ProcessControlCompletion이 수행합니다.</summary>
        /// <param name="request">실행 핸들러가 받은 동일한 요청 객체입니다. 이전/다른 요청은 거절합니다.</param>
        /// <param name="success">실제 애플리케이션 작업 성공 여부입니다.</param>
        /// <param name="additionalCause">성공은 0, 실패는 1~27(25 제외)의 IEC ControlAddCause입니다.</param>
        /// <exception cref="InvalidOperationException">실행 처리 중이거나 동일 요청의 완료 대기가 없거나 이미 완료 결과가 예약되었습니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">성공 여부와 추가 사유 코드가 일치하지 않거나 사유가 지원 범위를 벗어납니다.</exception>
        /// <remarks>실행 처리기가 반환한 뒤 세션 처리와 같은 실행 흐름에서 호출하십시오. 스레드 안전성을 제공하지 않습니다.
        /// 기한이 지난 뒤에는 완료 처리에서 시간 초과를 우선합니다. 외부 동작을 되돌리거나 취소하지 않습니다.</remarks>
        public void Complete(MmsControlRequest request, bool success, int additionalCause = 0)
        {
            if (active || pendingOwner == null || request == null || pendingRequest != request || completionReady)
                throw new InvalidOperationException("No matching pending control operation.");
            if ((success && additionalCause != 0) || (!success && (additionalCause < 1 || additionalCause > 27 || additionalCause == 25)))
                throw new ArgumentOutOfRangeException("additionalCause");
            completionSuccess = success; completionCause = additionalCause; completionReady = true;
        }
        internal byte[] CompletionReport(object session, long now)
        {
            if (pendingOwner != session) return null;
            bool expired = now >= completionDeadline;
            if (!expired && !completionReady) return null;
            if (expired) { completionSuccess = false; completionCause = 16; }
            var command = MmsEncoding.Single(pendingCommand, 0, pendingCommand.Length, 0xa2);
            byte[] report = Report(command, true, completionSuccess ? 0 : 1, completionCause, true);
            if (completionSuccess && !pendingRequest.Test)
                SetState(ValueKind == MmsControlValueKind.AnalogFloat32 ? false : pendingRequest.Value,
                    ValueKind == MmsControlValueKind.AnalogFloat32 ? pendingRequest.AnalogValue : 0);
            return report;
        }
        private byte[] Report(BerElement command, bool operate, int error, int cause, bool termination, bool selectWithValue = false)
        {
            var fields = command.Children();
            fields.Read();
            BerElement origin = fields.Read(0xa2), number = fields.Read(0x86);
            string reference = Domain + "/" + Item + (selectWithValue ? "$SBOw" : operate ? "$Oper" : "$Cancel");
            byte[] text = new byte[reference.Length];
            for (int i = 0; i < text.Length; i++) text[i] = (byte)reference[i];
            byte[] identifier = MmsEncoding.Identifier("LastApplError");
            identifier[0] = 0x80;
            byte[] diagnosticName = MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0, identifier));
            byte[] detail = MmsEncoding.Tlv(0xa2,
                    MmsEncoding.Tlv(0x8a, text), MmsEncoding.Int(0x85, error),
                    MmsEncoding.Tlv(0xa2, origin.Copy()), MmsEncoding.Int(0x86, number.Integer()),
                    MmsEncoding.Int(0x85, cause));
            byte[] controlName = !termination ? null : MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0,
                MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier(Domain),
                    MmsEncoding.Identifier(Item + "$Oper"))));
            byte[] completed = !termination ? null : MmsEncoding.Tlv(0xa2, command.Copy());
            bool negative = error != 0 || (cause != 0 && cause != 25);
            return MmsEncoding.Tlv(0xa3, MmsEncoding.Tlv(0xa0,
                MmsEncoding.Tlv(0xa0, termination ? negative ? MmsEncoding.Join(diagnosticName, controlName) : controlName : diagnosticName),
                MmsEncoding.Tlv(0xa0, termination ? negative ? MmsEncoding.Join(detail, completed) : completed : detail)));
        }
    }
}
