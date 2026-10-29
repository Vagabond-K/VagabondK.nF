using System;
using System.IO;
using System.Runtime.CompilerServices;
using VagabondK.nF.Internal;

[assembly: InternalsVisibleTo("VagabondK.nF.IEC61850.Client")]

namespace VagabondK.nF.MMS.Client
{
    /// <summary>한 연결에서 Association·단일 변수 Read/스칼라 Write·이름 탐색·제한된 복합 형식 조회를 수행하는 평가용 MMS 클라이언트입니다.</summary>
    /// <remarks>호출을 직렬화하십시오. 소켓·접속·TLS는 채널 호출자가 관리하며,
    /// Faulted 상태가 된 뒤에는 연결을 폐기하고 새 클라이언트를 생성해야 합니다. 자동 재시도하지 않습니다.</remarks>
    public partial class MmsClient
    {
        internal readonly IMmsChannel channel;
        internal readonly int timeout; private readonly int maximumPdu, proposedExponent;
        private readonly byte[] packet = new byte[1028];
        private readonly byte[] tsdu;
        private int tpduSize = 1024, invoke;
        internal long deadline;
        internal bool active;
        // 결과 API에서만 기한 만료를 false/-1/null로 상위 호출까지 전달합니다.
        private bool returnTimeout, operationTimedOut;
        /// <summary>협상이 완료되어 지원 서비스를 요청할 수 있는 상태입니다.</summary>
        public bool Associated { get; internal set; }
        /// <summary>통신·형식·미지원 응답 오류로 연결 교체가 필요한 상태입니다.</summary>
        public bool Faulted { get; internal set; }
        /// <summary>협상된 최대 MMS PDU 크기(바이트)입니다. 협상 전에는 0입니다.</summary>
        public int NegotiatedPduSize { get; internal set; }
        /// <summary>서버가 GetNameList를 광고했는지 나타냅니다.</summary>
        public bool SupportsNameList { get; internal set; }
        /// <summary>서버가 GetVariableAccessAttributes를 광고했는지 나타냅니다.</summary>
        public bool SupportsVariableAttributes { get; internal set; }
        /// <summary>피어가 MMS Write를 광고했는지 나타냅니다.</summary>
        public bool SupportsWrite { get; internal set; }

        /// <summary>이미 연결된 채널과 고정 수신 상한을 지정합니다.</summary>
        /// <param name="channel">호출자 소유 연결입니다.</param>
        /// <param name="maximumPduSize">MMS PDU 크기의 상한(1024~32768바이트)입니다.</param>
        /// <param name="timeoutMilliseconds">각 Association 또는 서비스 요청 전체의 제한 시간(ms)입니다.</param>
        /// <param name="maximumTpduSize">제안할 COTP TPDU의 최대 크기(128/256/512/1024바이트)입니다.</param>
        /// <exception cref="ArgumentNullException">채널이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">용량 또는 제한 시간이 범위를 벗어났습니다.</exception>
        public MmsClient(IMmsChannel channel, int maximumPduSize = 4096, int timeoutMilliseconds = 10000,
            int maximumTpduSize = 1024)
        {
            if (channel == null) throw new ArgumentNullException("channel");
            if (maximumPduSize < 1024 || maximumPduSize > 32768 || timeoutMilliseconds < 1)
                throw new ArgumentOutOfRangeException("limits");
            if (maximumTpduSize != 128 && maximumTpduSize != 256 && maximumTpduSize != 512 && maximumTpduSize != 1024)
                throw new ArgumentOutOfRangeException("maximumTpduSize");
            proposedExponent = maximumTpduSize == 128 ? 7 : maximumTpduSize == 256 ? 8 : maximumTpduSize == 512 ? 9 : 10;
            tpduSize = maximumTpduSize;
            this.channel = channel;
            maximumPdu = maximumPduSize;
            timeout = timeoutMilliseconds;
            tsdu = new byte[maximumPduSize + 1024];
        }

        internal void Begin(bool associated)
        {
            if (active || Faulted || released || Associated != associated)
                throw new InvalidOperationException("Invalid MMS Client state or concurrent access.");
            active = true;
            deadline = channel.Milliseconds + timeout;
        }
        internal int Remaining()
        {
            long left = deadline - channel.Milliseconds;
            if (left <= 0)
            {
                if (!returnTimeout) throw new TimeoutException("MMS operation deadline expired.");
                TimeoutResult();
                return 0;
            }
            return (int)left;
        }
        private bool TimeoutResult()
        {
            operationTimedOut = true;
            Faulted = true;
            Associated = false;
            return false;
        }
        internal void CheckTimeoutChannel(bool enabled)
        {
            if (enabled && !(channel is IMmsTimeoutChannel))
                throw new NotSupportedException("Timeout results require IMmsTimeoutChannel.");
        }
        private bool Receive(int offset, int count)
        {
            while (count > 0)
            {
                int remaining = Remaining();
                if (remaining == 0) return false;
                int n;
                if (returnTimeout)
                {
                    if (!((IMmsTimeoutChannel)channel).TryRead(packet, offset, count, remaining, out n))
                        return TimeoutResult();
                }
                else n = channel.Read(packet, offset, count, remaining);
                if (n == 0) throw new IOException("TCP EOF during MMS operation.");
                if (n < 0 || n > count) throw new IOException("Invalid channel read count.");
                if (Remaining() == 0) return false;
                offset += n;
                count -= n;
            }
            return true;
        }
        private bool Write(byte[] bytes)
        {
            int p = 0;
            while (p < bytes.Length)
            {
                int remaining = Remaining();
                if (remaining == 0) return false;
                int n;
                if (returnTimeout)
                {
                    if (!((IMmsTimeoutChannel)channel).TryWrite(bytes, p, bytes.Length - p, remaining, out n))
                        return TimeoutResult();
                }
                else n = channel.Write(bytes, p, bytes.Length - p, remaining);
                if (n <= 0 || n > bytes.Length - p) throw new IOException("Invalid channel write progress.");
                if (Remaining() == 0) return false;
                p += n;
            }
            return true;
        }
        private int Packet()
        {
            if (!Receive(0, 4)) return -1;
            int length = (packet[2] << 8) | packet[3];
            if (packet[0] != 3 || packet[1] != 0 || length < 7 || length > tpduSize + 4)
                throw new MmsProtocolException("Invalid TPKT or negotiated TPDU size exceeded.");
            if (!Receive(4, length - 4)) return -1;
            return length;
        }
        private bool SendTsdu(byte[] bytes)
        {
            if (bytes.Length > tsdu.Length) throw new MmsProtocolException("Outgoing TSDU exceeds local capacity.");
            for (int p = 0; p < bytes.Length;)
            {
                int count = bytes.Length - p;
                if (count > tpduSize - 3) count = tpduSize - 3;
                byte[] frame = new byte[count + 7];
                CotpData.Write(frame, 0, bytes, p, count, p + count == bytes.Length);
                if (!Write(frame)) return false;
                p += count;
            }
            return true;
        }
        private int ReceiveTsdu()
        {
            int used = 0;
            while (true)
            {
                int length = Packet();
                if (length < 0) return -1;
                bool last;
                int offset = CotpData.Read(packet, length, out last);
                int count = length - offset;
                if (count == 0 || count > tsdu.Length - used)
                    throw new MmsProtocolException("Empty COTP segment or TSDU capacity exceeded.");
                Array.Copy(packet, offset, tsdu, used, count);
                used += count;
                if (last) return used;
            }
        }

        /// <summary>COTP Class 0와 제한된 OSI/ACSE·MMS Initiate 협상을 동기 수행합니다.</summary>
        /// <remarks>TSAP=0001, S-selector=0001, P-selector=00000001, 인증 없음,
        /// 동시 요청 1개와 Read 서비스만 제안합니다. 실패 시 Faulted를 설정하고 예외를 전파합니다.</remarks>
        /// <exception cref="InvalidOperationException">이미 협상했거나 Faulted/동시 호출 상태입니다.</exception>
        /// <exception cref="MmsProtocolException">협상이 거절되었거나 미지원 형식입니다.</exception>
        /// <exception cref="TimeoutException">전체 제한 시간이 만료되었습니다.</exception>
        /// <exception cref="IOException">연결이 닫혔거나 채널 계약을 위반했습니다.</exception>
        public void Associate()
        {
            Begin(false);
            try
            {
                Write(new byte[] { 3, 0, 0, 22, 17, 0xe0, 0, 0, 0, 1, 0,
                    0xc0, 1, (byte)proposedExponent, 0xc1, 2, 0, 1, 0xc2, 2, 0, 1 });
                ParseConfirm(Packet());
                SendTsdu(MmsEncoding.Association(maximumPdu));
                ParseAssociation(ReceiveTsdu());
                Associated = true;
            }
            catch { Faulted = true; throw; }
            finally { active = false; }
        }

        private void ParseConfirm(int length)
        {
            if (length < 11 || packet[4] + 5 != length || packet[5] != 0xd0 ||
                packet[6] != 0 || packet[7] != 1 || (packet[8] == 0 && packet[9] == 0) || packet[10] != 0)
                throw new MmsProtocolException("Invalid COTP CC reference or class.");
            bool size = false, source = false, destination = false;
            // TPDU 크기 협상이 없으면 Class 0의 기본값은 128입니다.
            tpduSize = 128;
            int p = 11;
            while (p < length)
            {
                if (length - p < 2) throw new MmsProtocolException("Truncated COTP parameter.");
                int code = packet[p++], count = packet[p++];
                if (count > length - p) throw new MmsProtocolException("Truncated COTP parameter content.");
                if (code == 0xc0 && !size && count == 1 && packet[p] >= 7 && packet[p] <= proposedExponent)
                {
                    size = true;
                    tpduSize = 1 << packet[p];
                }
                else if (code == 0xc1 && !source && count == 2 && packet[p] == 0 && packet[p + 1] == 1) source = true;
                else if (code == 0xc2 && !destination && count == 2 && packet[p] == 0 && packet[p + 1] == 1) destination = true;
                else throw new MmsProtocolException("Unsupported or duplicate COTP parameter.");
                p += count;
            }
        }
        private static int SessionLength(byte[] buffer, ref int p, int end)
        {
            if (p >= end) throw new MmsProtocolException("Truncated Session length.");
            int n = buffer[p++];
            if (n == 255)
            {
                if (end - p < 2) throw new MmsProtocolException("Truncated extended Session length.");
                n = (buffer[p] << 8) | buffer[p + 1];
                p += 2;
            }
            if (n > end - p) throw new MmsProtocolException("Session boundary exceeded.");
            return n;
        }
        private void ParseAssociation(int length)
        {
            int p = 0;
            if (tsdu[p++] != 0x0e) throw new MmsProtocolException("Session connection not accepted.");
            int sessionLength = SessionLength(tsdu, ref p, length);
            if (sessionLength != length - p) throw new MmsProtocolException("Trailing Session bytes.");
            BerElement presentation = null;
            int seen = 0;
            while (p < length)
            {
                int code = tsdu[p++];
                int n = SessionLength(tsdu, ref p, length);
                int mask;
                if (code == 0xc1)
                {
                    mask = 1;
                    presentation = MmsEncoding.Single(tsdu, p, n, 0x31);
                }
                else if (code == 5)
                {
                    mask = 2;
                    if (n != 6 || tsdu[p] != 0x13 || tsdu[p + 1] != 1 || tsdu[p + 2] != 0 ||
                        tsdu[p + 3] != 0x16 || tsdu[p + 4] != 1 || tsdu[p + 5] != 2)
                        throw new MmsProtocolException("Unsupported Session connect options.");
                }
                else if (code == 0x14 || code == 0x33 || code == 0x34)
                {
                    mask = code == 0x14 ? 4 : code == 0x33 ? 8 : 16;
                    if (n != 2 || tsdu[p] != 0 || tsdu[p + 1] != (code == 0x14 ? 2 : 1))
                        throw new MmsProtocolException("Unexpected Session requirements or selector.");
                }
                else throw new MmsProtocolException("Unsupported Session parameter.");
                if ((seen & mask) != 0) throw new MmsProtocolException("Duplicate Session parameter.");
                seen |= mask;
                p += n;
            }
            if (presentation == null || (seen & 6) != 6) throw new MmsProtocolException("Missing Session acceptance fields.");
            var cp = presentation.Children();
            var mode = cp.Read(0xa0).Children();
            if (mode.Read(0x80).Integer() != 1) throw new MmsProtocolException("Non-normal presentation mode.");
            mode.End();
            var normal = cp.Read(0xa2).Children();
            cp.End();
            var selector = normal.Read(0x83);
            if (!selector.Matches(new byte[] { 0, 0, 0, 1 })) throw new MmsProtocolException("Unexpected P-selector.");
            var contexts = normal.Read(0xa5).Children();
            for (int i = 0; i < 2; i++)
            {
                var accepted = contexts.Read(0x30).Children();
                if (accepted.Read(0x80).Integer() != 0 || !accepted.Read(0x81).Matches(MmsEncoding.BerSyntax))
                    throw new MmsProtocolException("Presentation context rejected.");
                accepted.End();
            }
            contexts.End();
            BerElement user = MmsEncoding.UnwrapPdv(normal.Read(0x61), 1);
            normal.End();
            var aare = MmsEncoding.Single(user.Buffer, user.Offset, user.Length, 0x61).Children();
            var context = aare.Read(0xa1).Children();
            if (!context.Read(6).Matches(MmsEncoding.ApplicationContext)) throw new MmsProtocolException("ACSE context mismatch.");
            context.End();
            var result = aare.Read(0xa2).Children();
            if (result.Read(2).Integer() != 0) throw new MmsProtocolException("ACSE association rejected.");
            result.End();
            var diagnostic = aare.Read(0xa3).Children();
            var source = diagnostic.Read(0xa1).Children();
            diagnostic.End();
            if (source.Read(2).Integer() != 0) throw new MmsProtocolException("ACSE diagnostic not accepted.");
            source.End();
            var info = aare.Read(0xbe).Children();
            aare.End();
            var external = info.Read(0x28).Children();
            info.End();
            if (external.Read(2).Integer() != 3) throw new MmsProtocolException("MMS external context mismatch.");
            BerElement data = external.Read(0xa0);
            external.End();
            ParseInitiate(MmsEncoding.Single(data.Buffer, data.Offset, data.Length, 0xa9));
        }
        private void ParseInitiate(BerElement element)
        {
            var fields = element.Children();
            BerElement first = fields.Read();
            long peerPdu = maximumPdu;
            if (first.Tag == 0x80) { peerPdu = first.Integer(); first = fields.Read(); }
            first.Expect(0x81);
            if (peerPdu < 64 || first.Integer() != 1 || fields.Read(0x82).Integer() != 1)
                throw new MmsProtocolException("Invalid MMS Initiate limits.");
            BerElement detail = fields.Read();
            if (detail.Tag == 0x83)
            {
                if (detail.Integer() < 1 || detail.Integer() > 8) throw new MmsProtocolException("Invalid MMS nesting limit.");
                detail = fields.Read();
            }
            detail.Expect(0xa4);
            fields.End();
            var d = detail.Children();
            if (d.Read(0x80).Integer() != 1) throw new MmsProtocolException("Unsupported MMS version.");
            BerElement parameters = d.Read(0x81);
            if (parameters.Length != 3 || parameters.Buffer[parameters.Offset] != 5 ||
                (parameters.Buffer[parameters.Offset + 2] & 31) != 0)
                throw new MmsProtocolException("Unsupported MMS parameter bit string.");
            BerElement services = d.Read(0x82);
            if (services.Length != 12 || services.Buffer[services.Offset] != 3 ||
                (services.Buffer[services.Offset + 1] & 8) == 0 || (services.Buffer[services.Offset + 11] & 7) != 0)
                throw new MmsProtocolException("Server does not support Read or malformed service bit string.");
            d.End();
            SupportsNameList = (services.Buffer[services.Offset + 1] & 0x40) != 0;
            SupportsVariableAttributes = (services.Buffer[services.Offset + 1] & 2) != 0;
            SupportsWrite = (services.Buffer[services.Offset + 1] & 4) != 0;
            SupportsNamedVariableListDirectory = (services.Buffer[services.Offset + 2] & 8) != 0;
            NegotiatedPduSize = peerPdu < maximumPdu ? (int)peerPdu : maximumPdu;
        }

        /// <summary>domain이 null이면 VMD domain 목록, 아니면 해당 domain의 변수 이름 한 페이지를 조회합니다.</summary>
        /// <param name="domain">null 또는 ASCII 1~64자의 domain입니다.</param>
        /// <param name="continueAfter">첫 페이지는 null, 다음은 이전 페이지의 ContinueAfter입니다.</param>
        /// <param name="maximumNames">이 응답에서 허용할 이름 수(1~128)입니다. 서버의 페이지 크기를 지시하지는 않습니다.</param>
        /// <param name="namedVariableLists">true이면 지정 domain의 정적 명명 변수 목록 이름을 조회합니다.</param>
        /// <returns>이름과 이어받기 정보입니다.</returns>
        /// <remarks>응답이 로컬 이름 상한을 넘으면 프로토콜 예외·Faulted입니다. 이름은 ASCII 1~64자만 지원합니다.
        /// 서버의 ConfirmedError는 MmsServiceException이며 연결을 유지합니다.</remarks>
        public MmsNameListPage GetNameList(string domain = null, string continueAfter = null, int maximumNames = 128, bool namedVariableLists = false)
        {
            if (namedVariableLists && domain == null) throw new ArgumentException("named variable list enumeration requires domain.");
            if (maximumNames < 1 || maximumNames > 128) throw new ArgumentOutOfRangeException("maximumNames");
            byte[] domainId = domain == null ? null : MmsEncoding.Identifier(domain);
            byte[] continuation = continueAfter == null ? new byte[0] : MmsEncoding.Identifier(continueAfter);
            if (continuation.Length != 0) continuation[0] = 0x82;
            if (!SupportsNameList) throw new InvalidOperationException("GetNameList was not negotiated.");
            Begin(true);
            try
            {
                byte[] scope = domainId == null ? MmsEncoding.Tlv(0x80) : domainId;
                if (domainId != null) domainId[0] = 0x81;
                byte[] service = MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x80, domain == null ? 9 : namedVariableLists ? 2 : 0)),
                    MmsEncoding.Tlv(0xa1, scope), continuation);
                var response = ExchangeService(service, 0xa1).Children();
                var names = response.Read(0xa0).Children();
                var result = new string[maximumNames];
                int count = 0;
                string previous = continueAfter;
                while (names.More)
                {
                    if (count == maximumNames) throw new MmsProtocolException("Name page exceeds local name limit.");
                    string name = MmsEncoding.ReadIdentifier(names.Read(0x1a));
                    if (previous != null && CompareNames(previous, name) >= 0)
                        throw new MmsProtocolException("Name list is not strictly ordered or made no continuation progress.");
                    result[count++] = name;
                    previous = name;
                }
                bool more = true; // ASN.1 기본값은 TRUE입니다.
                if (response.More)
                {
                    BerElement flag = response.Read(0x81);
                    more = BerEncoding.ReadBoolean(flag.Buffer, flag.Offset, flag.Length);
                }
                response.End();
                if (more && count == 0) throw new MmsProtocolException("Empty continuing name page.");
                var page = new string[count];
                Array.Copy(result, page, count);
                return new MmsNameListPage(page, more);
            }
            catch (MmsServiceException) { throw; }
            catch { Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }

        /// <summary>domain-specific 변수 하나의 스칼라·구조체·배열 형식을 조회합니다.</summary>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <returns>스칼라의 비트 수 또는 구조체 성분·배열 원소의 형식 정보입니다.</returns>
        /// <remarks>중첩 깊이 8·형식 노드 256개·배열 길이 255개로 제한합니다. Packed 형식은 미지원입니다.
        /// 정상 ConfirmedError는 MmsServiceException으로 전파하지만 연결을 유지합니다.</remarks>
        public MmsVariableAttributes GetVariableAccessAttributes(string domain, string item)
        {
            byte[] name = MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier(domain), MmsEncoding.Identifier(item)));
            if (!SupportsVariableAttributes) throw new InvalidOperationException("GetVariableAccessAttributes was not negotiated.");
            Begin(true);
            try
            {
                var response = ExchangeService(MmsEncoding.Tlv(0xa6, name), 0xa6).Children();
                BerElement deletable = response.Read(0x80);
                bool canDelete = BerEncoding.ReadBoolean(deletable.Buffer, deletable.Offset, deletable.Length);
                BerElement field = response.Read();
                if (field.Tag == 0xa1)
                {
                    var address = field.Children();
                    BerElement value = address.Read();
                    if (value.Tag != 0x80 && value.Tag != 0x81 && value.Tag != 0x82)
                        throw new MmsProtocolException("Unsupported variable address.");
                    address.End();
                    field = response.Read();
                }
                field.Expect(0xa2); response.End();
                var type = field.Children();
                BerElement specification = type.Read(); type.End();
                MmsVariableAttributes attributes = MmsVariableAttributes.Decode(specification, canDelete);
                Remaining();
                return attributes;
            }
            catch (MmsServiceException) { throw; }
            catch { Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }

        /// <summary>Boolean 변수 하나에 씁니다. 정상 접근 오류는 false와 오류 코드로 반환하며 자동 재전송하지 않습니다.</summary>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <param name="value">적용할 Boolean 값입니다.</param>
        /// <param name="errorCode">성공/예외 전 초기 값은 -1, 정상 거절은 DataAccessError 0~11입니다.</param>
        /// <returns>성공 응답은 true, 정상 접근 거절은 false입니다.</returns>
        /// <remarks>성공은 설비 동작 완료가 아닙니다. Timeout/EOF 등 통신 실패는 예외와 Faulted이며,
        /// 서버에는 이미 적용되었을 수 있습니다. 임의로 재전송하지 마십시오.
        /// 정상 MmsServiceException은 연결을 유지합니다. SupportsWrite는 권한을 의미하지 않습니다.</remarks>
        /// <exception cref="InvalidOperationException">Write 미협상 또는 사용할 수 없는 세션입니다.</exception>
        public bool TryWrite(string domain, string item, bool value, out int errorCode)
        { return TryWriteEncoded(domain, item, MmsEncoding.Tlv(0x83, new byte[] { value ? (byte)1 : (byte)0 }), out errorCode); }
        /// <summary>Int32 변수 하나에 씁니다. 정상 접근 오류는 false와 오류 코드로 반환하며 자동 재전송하지 않습니다.</summary>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <param name="value">적용할 Int32 값입니다.</param>
        /// <param name="errorCode">성공/예외 전 초기 값은 -1, 정상 거절은 DataAccessError 0~11입니다.</param>
        /// <returns>성공 응답은 true, 정상 접근 거절은 false입니다.</returns>
        /// <remarks>통신 실패 시 이미 적용되었을 수 있습니다. 자동 재전송하지 않으며 설비 동작 완료를 보장하지 않습니다.
        /// 정상 접근/서비스 오류는 연결을 유지하고 통신/형식 오류는 예외와 Faulted입니다.</remarks>
        public bool TryWrite(string domain, string item, int value, out int errorCode)
        { return TryWriteEncoded(domain, item, MmsEncoding.Int(0x85, value), out errorCode); }
        /// <summary>Float32 변수 하나에 씁니다. 정상 접근 오류는 false와 오류 코드로 반환하며 자동 재전송하지 않습니다.</summary>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <param name="value">적용할 IEEE754 Float32 값입니다. 특수 값 허용 여부는 서버 정책입니다.</param>
        /// <param name="errorCode">성공/예외 전 초기 값은 -1, 정상 거절은 DataAccessError 0~11입니다.</param>
        /// <returns>성공 응답은 true, 정상 접근 거절은 false입니다.</returns>
        /// <remarks>통신 실패 시 이미 적용되었을 수 있습니다. 자동 재전송하지 않으며 설비 동작 완료를 보장하지 않습니다.
        /// 정상 접근/서비스 오류는 연결을 유지하고 통신/형식 오류는 예외와 Faulted입니다.</remarks>
        public bool TryWrite(string domain, string item, float value, out int errorCode)
        {
            uint bits = RealBits.Encode(value);
            return TryWriteEncoded(domain, item, MmsEncoding.Tlv(0x87,
                new byte[] { 8, (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits }), out errorCode);
        }
        /// <summary>Boolean 한 변수를 씁니다. 접근 거절은 MmsAccessException입니다.</summary>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <param name="value">적용할 값입니다.</param>
        /// <remarks>연결 수명과 실행 여부가 불확실한 경우의 처리 규칙은 TryWrite와 같습니다. 자동 재전송하지 않습니다.</remarks>
        /// <exception cref="MmsAccessException">정상 DataAccessError를 받았습니다. 연결은 유지합니다.</exception>
        public void Write(string domain, string item, bool value)
        { int code; if (!TryWrite(domain, item, value, out code)) throw new MmsAccessException(code); }
        /// <summary>Int32 한 변수를 씁니다. 접근 거절은 MmsAccessException입니다.</summary>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <param name="value">적용할 값입니다.</param>
        /// <remarks>연결 수명과 실행 여부가 불확실한 경우의 처리 규칙은 TryWrite와 같습니다. 자동 재전송하지 않습니다.</remarks>
        /// <exception cref="MmsAccessException">정상 DataAccessError를 받았습니다. 연결은 유지합니다.</exception>
        public void Write(string domain, string item, int value)
        { int code; if (!TryWrite(domain, item, value, out code)) throw new MmsAccessException(code); }
        /// <summary>Float32 한 변수를 씁니다. 접근 거절은 MmsAccessException입니다.</summary>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <param name="value">적용할 값입니다.</param>
        /// <remarks>연결 수명과 실행 여부가 불확실한 경우의 처리 규칙은 TryWrite와 같습니다. 자동 재전송하지 않습니다.</remarks>
        /// <exception cref="MmsAccessException">정상 DataAccessError를 받았습니다. 연결은 유지합니다.</exception>
        public void Write(string domain, string item, float value)
        { int code; if (!TryWrite(domain, item, value, out code)) throw new MmsAccessException(code); }
        internal bool TryWriteEncoded(string domain, string item, byte[] encoded, out int errorCode)
        {
            bool timedOut;
            return TryWriteEncoded(domain, item, encoded, out errorCode, false, out timedOut);
        }
        internal bool TryWriteEncoded(string domain, string item, byte[] encoded, out int errorCode,
            bool timeoutResult, out bool timedOut)
        {
            errorCode = -1;
            timedOut = false;
            CheckTimeoutChannel(timeoutResult);
            byte[] name = MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa1,
                MmsEncoding.Identifier(domain), MmsEncoding.Identifier(item)));
            if (!SupportsWrite) throw new InvalidOperationException("Write was not negotiated.");
            Begin(true);
            returnTimeout = timeoutResult;
            operationTimedOut = false;
            try
            {
                byte[] access = MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0x30, name));
                BerElement response = ExchangeService(MmsEncoding.Tlv(0xa5, access, MmsEncoding.Tlv(0xa0, encoded)), 0xa5);
                if (response == null) return false;
                var results = response.Children();
                BerElement result = results.Read(); results.End();
                if (result.Tag == 0x81 && result.Length == 0)
                {
                    ValidateWriteSuccess();
                    if (Remaining() == 0) return false;
                    return true;
                }
                result.Expect(0x80);
                long code = result.Integer();
                if (code < 0 || code > 11) throw new MmsProtocolException("Invalid Write DataAccessError.");
                if (Remaining() == 0) return false;
                errorCode = (int)code;
                return false;
            }
            catch (MmsServiceException) { throw; }
            catch { Faulted = true; Associated = false; throw; }
            finally { timedOut = operationTimedOut; returnTimeout = false; active = false; }
        }

        private BerElement ExchangeService(byte[] service, byte expectedTag)
        {
            invoke = invoke == int.MaxValue ? 1 : invoke + 1;
            byte[] request = MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, invoke), service);
            if (request.Length > NegotiatedPduSize) throw new MmsProtocolException("Request exceeds negotiated PDU.");
            if (!SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, request)))) return null;
            BerElement confirmed = ReceiveServiceResponse();
            if (confirmed == null) return null;
            CheckServiceError(confirmed);
            var fields = confirmed.Children();
            confirmed.Expect(0xa1);
            if (fields.Read(2).Integer() != invoke) throw new MmsProtocolException("MMS invoke mismatch.");
            BerElement result = fields.Read(expectedTag); fields.End();
            return result;
        }
        private void CheckServiceError(BerElement confirmed)
        {
            if (confirmed.Tag == 0xa2) ReadServiceError(confirmed.Children());
            if (confirmed.Tag == 0xa4)
            {
                var fields = confirmed.Children();
                if (fields.Read(0x80).Integer() != invoke) throw new MmsProtocolException("Reject invoke mismatch.");
                long reason = fields.Read(0x81).Integer(); fields.End();
                if (reason < 0 || reason > 9) throw new MmsProtocolException("Invalid confirmed-request Reject reason.");
                throw new MmsRejectException((int)reason);
            }
        }
        /// <summary>MMS Conclude와 ACSE Release를 한 전체 기한 안에서 수행합니다. 호출자 소유 채널은 닫지 않습니다.</summary>
        /// <remarks>등록된 일반 보고 수신기는 종료 응답 전까지 호출됩니다. 성공 후 재협상할 수 없으며 새 Client가 필요합니다.
        /// 실패는 예외/Faulted이며 자동 재시도하지 않습니다. 소켓은 호출자가 finally에서 폐기하십시오.</remarks>
        public void Release()
        {
            Begin(true);
            try
            {
                SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, MmsEncoding.Tlv(0x8b))));
                BerElement response = ReceiveNonReport(); response.Expect(0x8c);
                if (response.Length != 0) throw new MmsProtocolException("Invalid Conclude response.");
                SendTsdu(MmsEncoding.Release(false));
                MmsEncoding.ValidateRelease(tsdu, ReceiveTsdu(), true);
                Associated = false; released = true;
            }
            catch { Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }
        private bool released;
        internal BerElement ReceiveServicePdu()
        {
            int length = ReceiveTsdu();
            if (length < 0) return null;
            if (length < 5 || tsdu[0] != 1 || tsdu[1] != 0 || tsdu[2] != 1 || tsdu[3] != 0)
                throw new MmsProtocolException("Invalid Session data prefix.");
            BerElement data = MmsEncoding.UnwrapPdv(MmsEncoding.Single(tsdu, 4, length - 4, 0x61), 3);
            if (data.Length > NegotiatedPduSize) throw new MmsProtocolException("Response exceeds negotiated PDU.");
            var pdu = new BerCursor(data.Buffer, data.Offset, data.Length);
            BerElement confirmed = pdu.Read(); pdu.End();
            return confirmed;
        }
        private void ReadServiceError(BerCursor fields)
        {
            if (fields.Read(0x80).Integer() != invoke) throw new MmsProtocolException("ConfirmedError invoke mismatch.");
            BerElement serviceError = fields.Read();
            if (serviceError.Tag == 0x81)
            {
                long position = serviceError.Integer();
                if (position < 0 || position > uint.MaxValue) throw new MmsProtocolException("Invalid modifier position.");
                serviceError = fields.Read();
            }
            serviceError.Expect(0xa2); fields.End();
            var error = serviceError.Children();
            var errorClass = error.Read(0xa0).Children();
            BerElement code = errorClass.Read(); errorClass.End();
            long number = code.Integer();
            if (code.Tag < 0x80 || code.Tag > 0x8c || number < 0 || number > int.MaxValue)
                throw new MmsProtocolException("Malformed ServiceError.");
            if (error.More)
            {
                BerElement optional = error.Read();
                if (optional.Tag == 0x81)
                {
                    long additional = optional.Integer();
                    if (additional < int.MinValue || additional > int.MaxValue)
                        throw new MmsProtocolException("Invalid additional error code.");
                    if (error.More) optional = error.Read();
                    else optional = null;
                }
                if (optional != null)
                {
                    optional.Expect(0x82);
                    for (int i = 0; i < optional.Length; i++)
                        if (optional.Buffer[optional.Offset + i] < 32 || optional.Buffer[optional.Offset + i] > 126)
                            throw new MmsProtocolException("Invalid error description.");
                }
                error.End();
            }
            throw new MmsServiceException(code.Tag - 0x80, (int)number);
        }
        internal static int CompareNames(string a, string b)
        {
            int count = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < count; i++) if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            return a.Length == b.Length ? 0 : a.Length < b.Length ? -1 : 1;
        }

        /// <summary>한 domain-specific MMS 변수를 읽습니다. 이름은 MMS의 원본 식별자입니다.</summary>
        /// <param name="domain">예: NFTESTLD0. 출력 가능한 ASCII 1~64자입니다.</param>
        /// <param name="item">예: GGIO1$ST$Status$stVal. 출력 가능한 ASCII 1~64자입니다.</param>
        /// <returns>지원하는 스칼라 또는 구조체·배열 트리의 독립된 값입니다.</returns>
        /// <exception cref="ArgumentException">식별자가 범위를 벗어났거나 잘못되었습니다.</exception>
        /// <exception cref="MmsAccessException">서버의 정상적인 DataAccessError입니다. 연결은 계속 사용할 수 있습니다.</exception>
        /// <exception cref="InvalidOperationException">연결 상태 또는 동시 호출이 잘못되었습니다.</exception>
        /// <exception cref="MmsProtocolException">미지원 형식·응답 불일치입니다. Faulted가 설정됩니다.</exception>
        /// <exception cref="TimeoutException">전체 Read 제한 시간이 만료되었습니다.</exception>
        /// <exception cref="IOException">연결이 닫혔거나 채널 계약을 위반했습니다.</exception>
        public MmsValue Read(string domain, string item)
        {
            MmsValue value;
            int errorCode;
            if (!TryRead(domain, item, out value, out errorCode)) throw new MmsAccessException(errorCode);
            return value;
        }

        /// <summary>한 변수를 읽고 정상적인 DataAccessError는 예외 없이 반환합니다.</summary>
        /// <param name="domain">출력 가능한 ASCII 1~64자의 MMS domain 식별자입니다.</param>
        /// <param name="item">출력 가능한 ASCII 1~64자의 MMS item 식별자입니다.</param>
        /// <param name="value">성공한 경우 독립된 값 트리이며, 실패 또는 예외 시 null입니다.</param>
        /// <param name="errorCode">DataAccessError인 경우 0~11이며, 성공 또는 통신·형식 예외 시 -1입니다.</param>
        /// <returns>값을 읽으면 true, 서버의 정상적인 DataAccessError이면 false입니다. false여도 연결은 유지됩니다.</returns>
        /// <remarks>통신·기한·미지원 형식 오류를 false로 숨기지 않습니다.
        /// 기존 Read와 동일하게 예외를 전파하고 Faulted를 설정합니다. 정상 요청에서도 인코딩과 값 객체 할당은 발생합니다.</remarks>
        /// <exception cref="ArgumentException">식별자가 범위를 벗어났거나 잘못되었습니다.</exception>
        /// <exception cref="InvalidOperationException">연결 상태 또는 동시 호출이 잘못되었습니다.</exception>
        /// <exception cref="MmsProtocolException">미지원 형식·응답 불일치입니다. Faulted가 설정됩니다.</exception>
        /// <exception cref="TimeoutException">전체 Read 제한 시간이 만료되었습니다.</exception>
        /// <exception cref="IOException">연결이 닫혔거나 채널 계약을 위반했습니다.</exception>
        public bool TryRead(string domain, string item, out MmsValue value, out int errorCode)
        {
            bool timedOut;
            return TryReadCore(domain, item, out value, out errorCode, false, out timedOut);
        }

        /// <summary>정상 접근 거절과 시간 초과를 구분하여 예외 없이 반환합니다.</summary>
        /// <remarks>IMmsTimeoutChannel이 필요합니다. false/timedOut=true이면 Faulted이며 연결을 폐기하십시오.
        /// EOF·소켓·서비스·프로토콜 오류와 잘못된 호출은 기존처럼 예외입니다. 자동 재시도하지 않습니다.</remarks>
        /// <param name="domain">ASCII 1~64자의 domain입니다.</param>
        /// <param name="item">ASCII 1~64자의 item입니다.</param>
        /// <param name="value">성공한 값이며 거절·시간 초과 시 null입니다.</param>
        /// <param name="errorCode">정상 접근 거절은 0~11, 성공·시간 초과는 -1입니다.</param>
        /// <param name="timedOut">소켓 준비 또는 전체 요청 기한이 만료되면 true입니다.</param>
        /// <returns>값을 읽으면 true, 정상 접근 거절 또는 시간 초과이면 false입니다.</returns>
        public bool TryRead(string domain, string item, out MmsValue value, out int errorCode, out bool timedOut)
        {
            return TryReadCore(domain, item, out value, out errorCode, true, out timedOut);
        }
        private bool TryReadCore(string domain, string item, out MmsValue value, out int errorCode,
            bool timeoutResult, out bool timedOut)
        {
            value = null;
            errorCode = -1;
            timedOut = false;
            CheckTimeoutChannel(timeoutResult);
            byte[] domainId = MmsEncoding.Identifier(domain), itemId = MmsEncoding.Identifier(item);
            Begin(true);
            returnTimeout = timeoutResult;
            operationTimedOut = false;
            try
            {
                invoke = invoke == int.MaxValue ? 1 : invoke + 1;
                byte[] request = MmsEncoding.ReadRequest(invoke, domainId, itemId);
                if (request.Length > NegotiatedPduSize) throw new MmsProtocolException("Read exceeds negotiated MMS PDU.");
                if (!SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, request)))) return false;
                BerElement confirmed = ReceiveNonReport();
                if (confirmed == null) return false;
                CheckServiceError(confirmed); confirmed.Expect(0xa1);
                var response = confirmed.Children();
                if (response.Read(2).Integer() != invoke) throw new MmsProtocolException("MMS invoke ID mismatch.");
                var read = response.Read(0xa4).Children();
                response.End();
                var results = read.Read(0xa1).Children();
                read.End();
                BerElement result = results.Read();
                results.End();
                if (result.Tag == 0x80)
                {
                    long code = result.Integer();
                    if (code < 0 || code > 11) throw new MmsProtocolException("Invalid DataAccessError.");
                    if (Remaining() == 0) return false;
                    errorCode = (int)code;
                    return false;
                }
                MmsValue decoded = MmsValue.Decode(result);
                if (Remaining() == 0) return false;
                value = decoded;
                return true;
            }
            catch (MmsServiceException) { throw; }
            catch { Faulted = true; Associated = false; throw; }
            finally { timedOut = operationTimedOut; returnTimeout = false; active = false; }
        }
    }
}
