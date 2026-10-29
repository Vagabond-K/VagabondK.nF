using System;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Client;

namespace VagabondK.nF.IEC61850.Client
{
    /// <summary>제한형 MMS Client에 IEC 61850 보고·제어 매핑을 추가합니다. 모든 호출을 직렬화하십시오.</summary>
    public sealed partial class Iec61850Client : MmsClient
    {
        /// <summary>호출자 소유 채널과 MMS 용량·기한 상한을 지정합니다.</summary>
        /// <param name="channel">이미 연결된 호출자 소유 채널입니다. 클라이언트는 연결하거나 해제하지 않습니다.</param>
        /// <param name="maximumPduSize">MMS PDU 크기의 상한(1024~32768바이트)입니다.</param>
        /// <param name="timeoutMilliseconds">Association 또는 서비스 요청 전체의 양수 제한 시간(ms)입니다.</param>
        /// <param name="maximumTpduSize">제안할 COTP TPDU의 최대 크기(128/256/512/1024바이트)입니다.</param>
        /// <exception cref="ArgumentNullException">채널이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">PDU·TPDU 용량 또는 제한 시간이 범위를 벗어납니다.</exception>
        public Iec61850Client(IMmsChannel channel, int maximumPduSize = 4096, int timeoutMilliseconds = 10000,
            int maximumTpduSize = 1024) : base(channel, maximumPduSize, timeoutMilliseconds, maximumTpduSize) { }
        private string controlReference;
        private MmsValue controlOrigin;
        private ulong controlNumber;
        internal MmsControlError ControlError { get; private set; }


        internal bool TryWriteControl(string domain, string item, byte[] encoded, out int errorCode)
        {
            bool timedOut;
            return TryWriteControl(domain, item, encoded, out errorCode, false, out timedOut);
        }
        internal bool TryWriteControl(string domain, string item, byte[] encoded, out int errorCode,
            bool timeoutResult, out bool timedOut)
        {
            if (active || controlReference != null) throw new InvalidOperationException("An MMS operation is active.");
            var command = MmsValue.Decode(MmsEncoding.Single(encoded, 0, encoded.Length, 0xa2));
            controlReference = domain + "/" + item;
            controlOrigin = command.GetElement(1);
            controlNumber = command.GetElement(2).Unsigned;
            ControlError = null;
            try { return TryWriteEncoded(domain, item, encoded, out errorCode, timeoutResult, out timedOut); }
            finally { controlReference = null; controlOrigin = null; }
        }
        internal bool OperateAndWait(string domain, string item, byte[] encoded, out bool accepted, out int errorCode)
        {
            accepted = false; errorCode = -1;
            if (active || controlReference != null) throw new InvalidOperationException("An MMS operation is active.");
            var command = MmsValue.Decode(MmsEncoding.Single(encoded, 0, encoded.Length, 0xa2));
            controlReference = domain + "/" + item;
            controlOrigin = command.GetElement(1); controlNumber = command.GetElement(2).Unsigned;
            ControlError = null;
            long end = channel.Milliseconds + timeout;
            try
            {
                if (!TryWriteEncoded(domain, item, encoded, out errorCode)) return false;
                accepted = true;
                Begin(true); deadline = end;
                BerElement report = ReceiveNonReport();
                return ReadTermination(report, domain, item, encoded);
            }
            catch (MmsServiceException) { throw; }
            catch { Faulted = true; Associated = false; throw; }
            finally { active = false; controlReference = null; controlOrigin = null; }
        }
        private bool ReadTermination(BerElement report, string domain, string item, byte[] encoded)
        {
            report.Expect(0xa3);
            var pdu = report.Children();
            var information = pdu.Read(0xa0).Children(); pdu.End();
            var names = information.Read(0xa0).Children();
            var first = names.Read(0x30).Children();
            var firstName = first.Read(0xa0).Children(); first.End();
            BerElement name = firstName.Read(); firstName.End();
            bool negative = name.Tag == 0x80;
            if (negative && MmsEncoding.ReadIdentifier(name) != "LastApplError")
                throw new MmsProtocolException("Unsupported completion report.");
            if (negative)
            {
                var second = names.Read(0x30).Children();
                var secondName = second.Read(0xa0).Children(); second.End();
                name = secondName.Read(); secondName.End();
            }
            names.End();
            name.Expect(0xa1);
            var pair = name.Children();
            if (MmsEncoding.ReadIdentifier(pair.Read(0x1a)) != domain || MmsEncoding.ReadIdentifier(pair.Read(0x1a)) != item)
                throw new MmsProtocolException("Completion object mismatch.");
            pair.End();
            var values = information.Read(0xa0).Children(); information.End();
            if (negative) DecodeControlError(MmsValue.Decode(values.Read(0xa2)));
            BerElement completed = values.Read(0xa2); values.End();
            BerElement expected = MmsEncoding.Single(encoded, 0, encoded.Length, 0xa2);
            if (!completed.Matches(expected.Copy()))
                throw new MmsProtocolException("Completion command mismatch.");
            if (negative && ControlError.Error == 0 &&
                (ControlError.AdditionalCause == 0 || ControlError.AdditionalCause == 25))
                throw new MmsProtocolException("Negative completion lacks failure diagnostic.");
            return !negative;
        }
        internal void InvalidControlResponse(string message)
        {
            Faulted = true; Associated = false;
            throw new MmsProtocolException(message);
        }


        private void ReadControlError(BerElement report)
        {
            var pdu = report.Children();
            var information = pdu.Read(0xa0).Children(); pdu.End();
            var names = information.Read(0xa0).Children();
            var variable = names.Read(0x30).Children(); names.End();
            var name = variable.Read(0xa0).Children(); variable.End();
            BerElement identifier = name.Read(0x80); name.End();
            if (MmsEncoding.ReadIdentifier(identifier) != "LastApplError") throw new MmsProtocolException("Unsupported information report.");
            var values = information.Read(0xa0).Children(); information.End();
            MmsValue detail = MmsValue.Decode(values.Read(0xa2)); values.End();
            DecodeControlError(detail);
        }
        private void DecodeControlError(MmsValue detail)
        {
            if (detail.Count != 5 || detail.GetElement(0).Kind != MmsValueKind.VisibleString ||
                detail.GetElement(1).Kind != MmsValueKind.Integer || detail.GetElement(2).Kind != MmsValueKind.Structure ||
                detail.GetElement(3).Kind != MmsValueKind.Unsigned || detail.GetElement(4).Kind != MmsValueKind.Integer ||
                detail.GetElement(0).VisibleString != controlReference ||
                detail.GetElement(1).Integer < 0 || detail.GetElement(1).Integer > 3 ||
                detail.GetElement(2).Count != 2 || detail.GetElement(2).GetElement(0).Kind != MmsValueKind.Integer ||
                detail.GetElement(2).GetElement(1).Kind != MmsValueKind.OctetString ||
                detail.GetElement(2).GetElement(0).Integer != controlOrigin.GetElement(0).Integer ||
                detail.GetElement(3).Unsigned != controlNumber || detail.GetElement(4).Integer < 0 || detail.GetElement(4).Integer > 27)
                throw new MmsProtocolException("Uncorrelated or invalid LastApplError.");
            byte[] expected = controlOrigin.GetElement(1).GetBytes(), actual = detail.GetElement(2).GetElement(1).GetBytes();
            if (expected.Length != actual.Length) throw new MmsProtocolException("LastApplError origin mismatch.");
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i]) throw new MmsProtocolException("LastApplError origin mismatch.");
            ControlError = new MmsControlError(controlReference, (int)detail.GetElement(1).Integer, (int)controlNumber,
                (int)detail.GetElement(4).Integer);
        }

        internal override void ValidateWriteSuccess()
        {
            if (controlReference != null && ControlError != null)
                throw new MmsProtocolException("Control diagnostic followed by success.");
        }
        internal override BerElement ReceiveServiceResponse()
        {
            BerElement response = ReceiveNonReport();
            if (response == null) return null;
            if (response.Tag == 0xa3 && controlReference != null)
            {
                ReadControlError(response);
                response = ReceiveNonReport();
            }
            return response;
        }
    }
}
