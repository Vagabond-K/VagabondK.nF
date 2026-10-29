using System;
using System.IO;

namespace VagabondK.nF.MMS.Server
{
    /// <summary>한 연결에서 제한된 Association·Read/Write·이름 탐색·복합 형식·명명 변수 목록 조회를 처리합니다.</summary>
    /// <remarks>소켓 수락·수명·동시 연결 제한은 호출자 책임입니다. 모든 접근을 직렬화하십시오.
    /// IEC 61850 제어·RCB 의미 또는 인증을 제공하지 않습니다. 일반 InformationReport·정상 종료를 지원합니다.
    /// 정상 접근/서비스 오류는 연결을 유지합니다. Write 응답 유실 시 이미 메모리/외부 실행 요청에 적용되었을 수 있습니다.
    /// Association 한 번과 각 요청의 수신·응답에는 각각 생성자에 지정한 양수 제한 시간(ms)을 적용합니다.
    /// 통신·프로토콜·처리기 오류 뒤에는 세션을 다시 사용하지 말고 호출자 소유 채널을 해제하십시오.</remarks>
    public partial class MmsServerSession
    {
        internal readonly IMmsChannel channel;
        internal readonly MmsServerVariable[] variables;

        private readonly int maximumPdu, timeout, maximumNames;
        private readonly byte[] packet = new byte[1028];
        private readonly byte[] tsdu;
        private int tpdu = 128, nesting = 8;
        private long deadline;
        internal bool active; private bool closed;
        /// <summary>협상 완료 여부입니다.</summary>
        public bool Associated { get; internal set; }
        /// <summary>연결을 폐기해야 하는 실패 상태입니다.</summary>
        public bool Faulted { get; internal set; }
        /// <summary>협상된 MMS PDU 상한입니다.</summary>
        public int NegotiatedPduSize { get; internal set; }

        /// <summary>명명 변수 목록 없이 기본 제한으로 세션을 생성합니다.</summary>
        /// <param name="channel">호출자 소유 연결입니다.</param>
        /// <param name="variables">중복 이름이 없는 변수입니다. 배열을 복사하되 변수 값은 공유합니다.</param>
        public MmsServerSession(IMmsChannel channel, MmsServerVariable[] variables)
            : this(channel, variables, 4096, 10000, 16, null) { }

        /// <summary>명명 변수 목록 없이 PDU 상한을 지정하여 세션을 생성합니다.</summary>
        /// <param name="channel">호출자 소유 연결입니다.</param>
        /// <param name="variables">중복 이름이 없는 변수입니다. 배열을 복사하되 변수 값은 공유합니다.</param>
        /// <param name="maximumPduSize">1024~32768바이트입니다.</param>
        public MmsServerSession(IMmsChannel channel, MmsServerVariable[] variables, int maximumPduSize)
            : this(channel, variables, maximumPduSize, 10000, 16, null) { }

        /// <summary>명명 변수 목록 없이 PDU 상한과 기한을 지정하여 세션을 생성합니다.</summary>
        /// <param name="channel">호출자 소유 연결입니다.</param>
        /// <param name="variables">중복 이름이 없는 변수입니다. 배열을 복사하되 변수 값은 공유합니다.</param>
        /// <param name="maximumPduSize">1024~32768바이트입니다.</param>
        /// <param name="timeoutMilliseconds">Association 한 번 또는 요청 하나의 수신·응답 전체에 적용할 제한 시간(ms)입니다.</param>
        public MmsServerSession(IMmsChannel channel, MmsServerVariable[] variables,
            int maximumPduSize, int timeoutMilliseconds)
            : this(channel, variables, maximumPduSize, timeoutMilliseconds, 16, null) { }

        /// <summary>명명 변수 목록 없이 PDU 상한·기한·이름 목록 페이지 크기를 지정하여 세션을 생성합니다.</summary>
        /// <param name="channel">호출자 소유 연결입니다.</param>
        /// <param name="variables">중복 이름이 없는 변수입니다. 배열을 복사하되 변수 값은 공유합니다.</param>
        /// <param name="maximumPduSize">1024~32768바이트입니다.</param>
        /// <param name="timeoutMilliseconds">Association 한 번 또는 요청 하나의 수신·응답 전체에 적용할 제한 시간(ms)입니다.</param>
        /// <param name="maximumNamesPerPage">이름 목록 응답의 최대 항목 수(1~128)입니다. PDU 상한에 따라 더 작아질 수 있습니다.</param>
        public MmsServerSession(IMmsChannel channel, MmsServerVariable[] variables,
            int maximumPduSize, int timeoutMilliseconds, int maximumNamesPerPage)
            : this(channel, variables, maximumPduSize, timeoutMilliseconds, maximumNamesPerPage, null) { }

        /// <summary>연결된 채널과 최대 128개의 고정 이름 변수를 지정합니다.</summary>
        /// <param name="channel">호출자 소유 연결입니다.</param>
        /// <param name="variables">중복 이름이 없는 변수입니다. 배열을 복사하되 변수 값은 공유합니다.</param>
        /// <param name="maximumPduSize">1024~32768바이트입니다.</param>
        /// <param name="timeoutMilliseconds">Association 한 번 또는 요청 하나의 수신·응답 전체에 적용할 제한 시간(ms)입니다.</param>
        /// <param name="maximumNamesPerPage">이름 목록 응답의 최대 항목 수(1~128)입니다. PDU 상한에 따라 더 작아질 수 있습니다.</param>
        /// <param name="namedVariableLists">동일 변수 인스턴스를 참조하는 최대 8개 정적 명명 변수 목록입니다.</param>
        /// <exception cref="ArgumentNullException">채널 또는 변수 배열이 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">PDU 크기·양수 제한 시간(ms)·변수 수·페이지 크기·목록 수가 지원 범위를 벗어납니다.</exception>
        /// <exception cref="ArgumentException">변수/목록 이름 중복, null 멤버 또는 미등록 변수 참조가 있습니다.</exception>
        public MmsServerSession(IMmsChannel channel, MmsServerVariable[] variables,
            int maximumPduSize = 4096, int timeoutMilliseconds = 10000, int maximumNamesPerPage = 16,
            MmsServerNamedVariableList[] namedVariableLists = null)
        {
            if (channel == null || variables == null) throw new ArgumentNullException("channel/variables");
            if (maximumPduSize < 1024 || maximumPduSize > 32768 || timeoutMilliseconds < 1 ||
                variables.Length < 1 || variables.Length > 128 || maximumNamesPerPage < 1 || maximumNamesPerPage > 128)
                throw new ArgumentOutOfRangeException("limits");
            this.variables = new MmsServerVariable[variables.Length];
            for (int i = 0; i < variables.Length; i++)
            {
                if (variables[i] == null) throw new ArgumentException("Null MMS variable.");
                for (int j = 0; j < i; j++)
                    if (variables[j].Domain == variables[i].Domain && variables[j].Item == variables[i].Item)
                        throw new ArgumentException("Duplicate MMS variable.");
                this.variables[i] = variables[i];
            }
            for (int i = 1; i < this.variables.Length; i++)
            {
                MmsServerVariable value = this.variables[i];
                int j = i;
                while (j > 0 && CompareVariables(this.variables[j - 1], value) > 0)
                { this.variables[j] = this.variables[j - 1]; j--; }
                this.variables[j] = value;
            }
            InitializeDataServices(namedVariableLists);
            this.channel = channel;
            maximumPdu = maximumPduSize;
            timeout = timeoutMilliseconds;
            maximumNames = maximumNamesPerPage;
            tsdu = new byte[maximumPduSize + 1024];
        }
        internal void Begin(bool associated)
        {
            if (active || closed || Faulted || Associated != associated) throw new InvalidOperationException("Invalid MMS Server session state.");
            active = true;
            deadline = channel.Milliseconds + timeout;
        }
        internal int Remaining()
        {
            long left = deadline - channel.Milliseconds;
            if (left <= 0) throw new TimeoutException("MMS Server operation deadline expired.");
            return (int)left;
        }
        private bool Receive(int offset, int count, bool allowEof)
        {
            while (count > 0)
            {
                int n = channel.Read(packet, offset, count, Remaining());
                if (n == 0 && allowEof) { Remaining(); return false; }
                if (n <= 0 || n > count) throw new IOException("MMS Server truncated input or invalid channel read.");
                Remaining();
                allowEof = false;
                offset += n;
                count -= n;
            }
            return true;
        }
        private int Packet(bool allowEof)
        {
            if (!Receive(0, 4, allowEof)) return 0;
            int length = (packet[2] << 8) | packet[3];
            if (packet[0] != 3 || packet[1] != 0 || length < 7 || length > tpdu + 4)
                throw new MmsProtocolException("Invalid server TPKT or TPDU limit.");
            Receive(4, length - 4, false);
            return length;
        }
        private void Write(byte[] bytes)
        {
            Write(bytes, bytes.Length);
        }
        private void Write(byte[] bytes, int count)
        {
            int p = 0;
            while (p < count)
            {
                int n = channel.Write(bytes, p, count - p, Remaining());
                if (n <= 0 || n > count - p) throw new IOException("Invalid server channel write progress.");
                Remaining();
                p += n;
            }
        }
        private int ReceiveTsdu(bool allowEof)
        {
            int used = 0;
            while (true)
            {
                int length = Packet(allowEof && used == 0);
                if (length == 0) return 0;
                bool last;
                int offset = CotpData.Read(packet, length, out last);
                int count = length - offset;
                if (count == 0 || count > tsdu.Length - used) throw new MmsProtocolException("Server TSDU capacity exceeded.");
                Array.Copy(packet, offset, tsdu, used, count);
                used += count;
                if (last) return used;
            }
        }
        internal void SendTsdu(byte[] bytes)
        {
            if (bytes.Length > tsdu.Length) throw new MmsProtocolException("Server outgoing TSDU capacity exceeded.");
            for (int p = 0; p < bytes.Length;)
            {
                int count = bytes.Length - p;
                if (count > tpdu - 3) count = tpdu - 3;
                byte[] frame = new byte[count + 7];
                CotpData.Write(frame, 0, bytes, p, count, p + count == bytes.Length);
                Write(frame);
                p += count;
            }
        }
        /// <summary>COTP CR와 제한된 Session/Presentation/ACSE·MMS Initiate를 수락합니다.</summary>
        /// <remarks>TSAP/S-selector=0001, P-selector=00000001, context=1/3만 지원합니다.
        /// AP-title 등 식별 필드는 인증에 사용하지 않습니다. 실패 시 채널을 폐기하십시오.</remarks>
        public void Associate()
        {
            Begin(false);
            try
            {
                int length = Packet(false);
                if (length < 11 || packet[4] + 5 != length || packet[5] != 0xe0 ||
                    packet[6] != 0 || packet[7] != 0 || (packet[8] == 0 && packet[9] == 0) || packet[10] != 0)
                    throw new MmsProtocolException("Invalid COTP CR.");
                byte referenceHigh = packet[8], referenceLow = packet[9];
                int p = 11, seen = 0;
                int exponent = 7;
                while (p < length)
                {
                    if (length - p < 2) throw new MmsProtocolException("Truncated CR parameter.");
                    int tag = packet[p++], count = packet[p++], mask;
                    if (count > length - p) throw new MmsProtocolException("Truncated CR content.");
                    if (tag == 0xc0 && count == 1 && packet[p] >= 7 && packet[p] <= 16)
                    {
                        mask = 1;
                        exponent = packet[p] > 10 ? 10 : packet[p];
                    }
                    else if ((tag == 0xc1 || tag == 0xc2) && count == 2 && packet[p] == 0 && packet[p + 1] == 1)
                        mask = tag == 0xc1 ? 2 : 4;
                    else throw new MmsProtocolException("Unsupported COTP CR option.");
                    if ((seen & mask) != 0) throw new MmsProtocolException("Duplicate CR option.");
                    seen |= mask;
                    p += count;
                }
                tpdu = 1 << exponent;
                Write(new byte[] { 3, 0, 0, 22, 17, 0xd0, referenceHigh, referenceLow, 0, 1, 0,
                    0xc0, 1, (byte)exponent, 0xc2, 2, 0, 1, 0xc1, 2, 0, 1 });
                ParseAssociation(ReceiveTsdu(false));
                SendTsdu(Accept());
                Associated = true;
            }
            catch { Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }
        private int SessionLength(ref int p, int end)
        {
            if (p >= end) throw new MmsProtocolException("Truncated server Session length.");
            int n = tsdu[p++];
            if (n == 255)
            {
                if (end - p < 2) throw new MmsProtocolException("Truncated server Session extended length.");
                n = (tsdu[p] << 8) | tsdu[p + 1];
                p += 2;
            }
            if (n > end - p) throw new MmsProtocolException("Server Session boundary exceeded.");
            return n;
        }
        private void ParseAssociation(int length)
        {
            int p = 0;
            if (length < 2 || tsdu[p++] != 0x0d) throw new MmsProtocolException("Expected Session connect.");
            if (SessionLength(ref p, length) != length - p) throw new MmsProtocolException("Trailing Session connect bytes.");
            BerElement presentation = null;
            int seen = 0;
            while (p < length)
            {
                int code = tsdu[p++], n = SessionLength(ref p, length), mask;
                if (code == 0xc1) { mask = 1; presentation = MmsEncoding.Single(tsdu, p, n, 0x31); }
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
                        throw new MmsProtocolException("Unsupported Session selector.");
                }
                else throw new MmsProtocolException("Unsupported Session parameter.");
                if ((seen & mask) != 0) throw new MmsProtocolException("Duplicate Session parameter.");
                seen |= mask;
                p += n;
            }
            if (presentation == null || (seen & 6) != 6) throw new MmsProtocolException("Missing Session fields.");
            var cp = presentation.Children();
            var mode = cp.Read(0xa0).Children();
            if (mode.Read(0x80).Integer() != 1) throw new MmsProtocolException("Unsupported presentation mode.");
            mode.End();
            var normal = cp.Read(0xa2).Children();
            cp.End();
            if (!normal.Read(0x81).Matches(new byte[] { 0, 0, 0, 1 }) ||
                !normal.Read(0x82).Matches(new byte[] { 0, 0, 0, 1 }))
                throw new MmsProtocolException("Unsupported presentation selector.");
            var contexts = normal.Read(0xa4).Children();
            for (int i = 0; i < 2; i++)
            {
                var context = contexts.Read(0x30).Children();
                if (context.Read(2).Integer() != (i == 0 ? 1 : 3) ||
                    !context.Read(6).Matches(i == 0 ? MmsEncoding.AcseSyntax : MmsEncoding.MmsSyntax))
                    throw new MmsProtocolException("Unsupported presentation context.");
                var syntax = context.Read(0x30).Children();
                if (!syntax.Read(6).Matches(MmsEncoding.BerSyntax)) throw new MmsProtocolException("BER syntax required.");
                syntax.End(); context.End();
            }
            contexts.End();
            BerElement user = MmsEncoding.UnwrapPdv(normal.Read(0x61), 1);
            normal.End();
            var aarq = MmsEncoding.Single(user.Buffer, user.Offset, user.Length, 0x60).Children();
            var application = aarq.Read(0xa1).Children();
            if (!application.Read(6).Matches(MmsEncoding.ApplicationContext)) throw new MmsProtocolException("Unsupported ACSE context.");
            application.End();
            int identities = 0;
            BerElement field = aarq.Read();
            while (field.Tag != 0xbe)
            {
                int mask;
                var identity = field.Children();
                if (field.Tag == 0xa2 || field.Tag == 0xa6)
                {
                    mask = field.Tag == 0xa2 ? 1 : 4;
                    BerElement oid = identity.Read(6);
                    if (oid.Length < 1 || oid.Length > 32) throw new MmsProtocolException("Invalid ACSE identity OID.");
                }
                else if (field.Tag == 0xa3 || field.Tag == 0xa7)
                {
                    mask = field.Tag == 0xa3 ? 2 : 8;
                    if (identity.Read(2).Integer() < 0) throw new MmsProtocolException("Invalid ACSE qualifier.");
                }
                else throw new MmsProtocolException("Unsupported ACSE option or authentication.");
                identity.End();
                if ((identities & mask) != 0) throw new MmsProtocolException("Duplicate ACSE identity.");
                identities |= mask;
                field = aarq.Read();
            }
            aarq.End();
            var info = field.Children();
            var external = info.Read(0x28).Children();
            info.End();
            if (external.Read(2).Integer() != 3) throw new MmsProtocolException("Invalid MMS external context.");
            BerElement data = external.Read(0xa0);
            external.End();
            var initiate = MmsEncoding.Single(data.Buffer, data.Offset, data.Length, 0xa8).Children();
            BerElement first = initiate.Read();
            long peerPdu = maximumPdu;
            if (first.Tag == 0x80) { peerPdu = first.Integer(); first = initiate.Read(); }
            first.Expect(0x81);
            long calling = first.Integer(), called = initiate.Read(0x82).Integer();
            if (peerPdu < 64 || calling < 1 || calling > 32767 || called < 1 || called > 32767)
                throw new MmsProtocolException("Invalid Initiate limits.");
            BerElement detail = initiate.Read();
            if (detail.Tag == 0x83)
            {
                if (detail.Integer() < 1 || detail.Integer() > 127) throw new MmsProtocolException("Invalid nesting limit.");
                nesting = detail.Integer() < 8 ? (int)detail.Integer() : 8;
                detail = initiate.Read();
            }
            detail.Expect(0xa4); initiate.End();
            var d = detail.Children();
            if (d.Read(0x80).Integer() != 1) throw new MmsProtocolException("MMS version 1 required.");
            BerElement parameters = d.Read(0x81), services = d.Read(0x82);
            if (parameters.Length != 3 || parameters.Buffer[parameters.Offset] != 5 ||
                (parameters.Buffer[parameters.Offset + 2] & 31) != 0 ||
                services.Length != 12 || services.Buffer[services.Offset] != 3 ||
                (services.Buffer[services.Offset + 1] & 8) == 0 || (services.Buffer[services.Offset + 11] & 7) != 0)
                throw new MmsProtocolException("Invalid Initiate bit strings or Read not proposed.");
            d.End();
            NegotiatedPduSize = peerPdu < maximumPdu ? (int)peerPdu : maximumPdu;
        }
        private byte[] Accept()
        {
            byte[] initiate = MmsEncoding.Initiate(NegotiatedPduSize, nesting, namedVariableLists != null && namedVariableLists.Length != 0);
            initiate[0] = 0xa9;
            byte[] aare = MmsEncoding.Tlv(0x61, MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(6, MmsEncoding.ApplicationContext)),
                MmsEncoding.Tlv(0xa2, MmsEncoding.Int(2, 0)),
                MmsEncoding.Tlv(0xa3, MmsEncoding.Tlv(0xa1, MmsEncoding.Int(2, 0))),
                MmsEncoding.Tlv(0xbe, MmsEncoding.Tlv(0x28, MmsEncoding.Int(2, 3), MmsEncoding.Tlv(0xa0, initiate))));
            byte[] accepted = MmsEncoding.Tlv(0x30, MmsEncoding.Int(0x80, 0), MmsEncoding.Tlv(0x81, MmsEncoding.BerSyntax));
            byte[] presentation = MmsEncoding.Tlv(0x31, MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x80, 1)),
                MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0x83, new byte[] { 0, 0, 0, 1 }),
                    MmsEncoding.Tlv(0xa5, accepted, accepted), MmsEncoding.Pdv(1, aare)));
            return MmsEncoding.SessionParameter(0x0e, MmsEncoding.Join(new byte[] {
                5, 6, 0x13, 1, 0, 0x16, 1, 2, 0x14, 2, 0, 2, 0x34, 2, 0, 1 },
                MmsEncoding.SessionParameter(0xc1, presentation)));
        }
        /// <summary>호환용 이름입니다. ProcessRequest와 동일하게 지원하는 한 요청을 처리합니다.</summary>
        /// <returns>응답을 송신하면 true, 다음 프레임 전의 정상 TCP EOF이면 false입니다.</returns>
        public bool ProcessRead()
        {
            return ProcessRequest();
        }
        /// <summary>Read·스칼라 Write·GetNameList·GetVariableAccessAttributes 중 요청 하나를 처리합니다.</summary>
        /// <returns>정상 응답 또는 ConfirmedError를 송신하면 true, 프레임 전 정상 TCP EOF이면 false입니다.</returns>
        public bool ProcessRequest()
        {
            Begin(true);
            try
            {
                int length = ReceiveTsdu(true);
                if (length == 0) { ReleaseProfile(); Associated = false; closed = true; return false; }
                if (tsdu[0] == 9) return ReleaseAssociation(length);
                if (length < 5 || tsdu[0] != 1 || tsdu[1] != 0 || tsdu[2] != 1 || tsdu[3] != 0)
                    throw new MmsProtocolException("Invalid Session data prefix.");
                BerElement data = MmsEncoding.UnwrapPdv(MmsEncoding.Single(tsdu, 4, length - 4, 0x61), 3);
                if (data.Length > NegotiatedPduSize) throw new MmsProtocolException("Read exceeds negotiated PDU.");
                var top = new BerCursor(data.Buffer, data.Offset, data.Length);
                BerElement pdu = top.Read(); top.End();
                if (pdu.Tag == 0x8b)
                {
                    if (pdu.Length != 0) throw new MmsProtocolException("Invalid Conclude request.");
                    ReleaseProfile();
                    SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, MmsEncoding.Tlv(0x8c))));
                    return ReleaseAssociation(ReceiveTsdu(false));
                }
                pdu.Expect(0xa0);
                var request = pdu.Children();
                long invoke = request.Read(2).Integer();
                if (invoke < 0 || invoke > uint.MaxValue) throw new MmsProtocolException("Invalid invoke ID.");
                BerElement service = request.Read();
                request.End();
                if (service.Tag == 0xa1) { NameList(invoke, service); return true; }
                if (service.Tag == 0xa6) { Attributes(invoke, service); return true; }
                if (service.Tag == 0xa5) { WriteVariable(invoke, service); return true; }
                if (service.Tag == 0xac) { NamedVariableListDirectory(invoke, service); return true; }
                if (service.Tag != 0xa4)
                {
                    SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3,
                        MmsEncoding.Tlv(0xa4, MmsEncoding.Int(0x80, invoke), MmsEncoding.Int(0x81, 1)))));
                    return true;
                }
                var read = service.Children();
                BerElement specification = read.Read();
                bool withResult = false;
                if (specification.Tag == 0x80)
                {
                    withResult = BerEncoding.ReadBoolean(specification.Buffer, specification.Offset, specification.Length);
                    specification = read.Read();
                }
                specification.Expect(0xa1); read.End();
                ReadVariables(invoke, specification, withResult);
                return true;
            }
            catch { ReleaseProfile(); Faulted = true; Associated = false; throw; }
            finally { active = false; }
        }
        private bool ReleaseAssociation(int length)
        {
            MmsEncoding.ValidateRelease(tsdu, length, false);
            ReleaseProfile();
            SendTsdu(MmsEncoding.Release(true));
            Associated = false; closed = true;
            return false;
        }
        private void ReadSingle(long invoke, BerElement domain, BerElement item)
        {
                if (ReadProfile(invoke, domain, item)) return;
                MmsServerVariable value = null;
                for (int i = 0; i < variables.Length; i++)
                    if (Matches(domain, variables[i].Domain) && Matches(item, variables[i].Item)) { value = variables[i]; break; }
                SendRead(invoke, value);
        }
        /// <summary>이 세션을 종료 상태로 전환하고 소유한 SBO 선택을 해제합니다. 채널과 소켓은 닫지 않습니다.</summary>
        /// <remarks>ProcessRequest와 동시에 호출하지 마십시오. 호출자가 먼저 종료하는 경우 finally에서 호출하십시오.</remarks>
        public void Close()
        {
            if (active) throw new InvalidOperationException("An MMS operation is active.");
            ReleaseProfile(); Associated = false; closed = true;
        }
        private static int Compare(string a, string b)
        {
            int count = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < count; i++) if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            return a.Length == b.Length ? 0 : a.Length < b.Length ? -1 : 1;
        }
        private static int CompareVariables(MmsServerVariable a, MmsServerVariable b)
        {
            int domain = Compare(a.Domain, b.Domain);
            return domain == 0 ? Compare(a.Item, b.Item) : domain;
        }
        private static string Name(BerElement field)
        {
            ValidateIdentifier(field);
            char[] chars = new char[field.Length];
            for (int i = 0; i < chars.Length; i++) chars[i] = (char)field.Buffer[field.Offset + i];
            return new string(chars);
        }
        internal void Confirm(long invoke, byte[] service)
        {
            byte[] response = MmsEncoding.Tlv(0xa1, MmsEncoding.Int(2, invoke), service);
            if (response.Length > NegotiatedPduSize) throw new MmsProtocolException("Discovery response exceeds PDU.");
            SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, response)));
        }
        private void ServiceError(long invoke, int errorClass, int code)
        {
            byte[] response = MmsEncoding.Tlv(0xa2, MmsEncoding.Int(0x80, invoke),
                MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0xa0, MmsEncoding.Int((byte)(0x80 + errorClass), code))));
            if (response.Length > NegotiatedPduSize) throw new MmsProtocolException("ServiceError exceeds PDU.");
            SendTsdu(MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, response)));
        }
        private void NameList(long invoke, BerElement service)
        {
            var fields = service.Children();
            var objectClass = fields.Read(0xa0).Children();
            long type = objectClass.Read(0x80).Integer(); objectClass.End();
            var scope = fields.Read(0xa1).Children();
            BerElement scopeValue = scope.Read(); scope.End();
            string domain = null;
            bool validScope = (type == 9 && scopeValue.Tag == 0x80 && scopeValue.Length == 0) ||
                ((type == 0 || type == 2) && scopeValue.Tag == 0x81);
            if (scopeValue.Tag == 0x81) domain = Name(scopeValue);
            string continuation = fields.More ? Name(fields.Read(0x82)) : null;
            fields.End();
            if (!validScope) { ServiceError(invoke, 4, 0); return; }
            string[] candidates = new string[variables.Length + (namedVariableLists == null ? 0 : namedVariableLists.Length)];
            int count = 0;
            for (int i = 0; i < variables.Length; i++)
            {
                if (domain == null)
                {
                    string name = variables[i].Domain;
                    if (count == 0 || candidates[count - 1] != name) candidates[count++] = name;
                }
                else if (type == 0 && variables[i].Domain == domain) candidates[count++] = variables[i].Item;
            }
            if (type == 2 && namedVariableLists != null)
                for (int i = 0; i < namedVariableLists.Length; i++) if (namedVariableLists[i].Domain == domain) candidates[count++] = namedVariableLists[i].Item;
            if (type == 2)
                for (int i = 1; i < count; i++)
                {
                    string value = candidates[i]; int j = i;
                    while (j > 0 && Compare(candidates[j - 1], value) > 0) { candidates[j] = candidates[j - 1]; j--; }
                    candidates[j] = value;
                }
            if (domain != null && count == 0) { ServiceError(invoke, 7, 2); return; }
            int start = 0;
            if (continuation != null)
            {
                while (start < count && candidates[start] != continuation) start++;
                if (start == count) { ServiceError(invoke, 7, 2); return; }
                start++;
            }
            byte[] names = new byte[0], result = MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0), MmsEncoding.Tlv(0x81, new byte[] { 0 }));
            int taken = 0;
            while (start + taken < count && taken < maximumNames)
            {
                byte[] next = MmsEncoding.Join(names, MmsEncoding.Identifier(candidates[start + taken]));
                byte[] proposed = MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, next),
                    MmsEncoding.Tlv(0x81, new byte[] { start + taken + 1 < count ? (byte)1 : (byte)0 }));
                if (MmsEncoding.Tlv(0xa1, MmsEncoding.Int(2, invoke), proposed).Length > NegotiatedPduSize) break;
                names = next; result = proposed; taken++;
            }
            if (taken == 0 && start < count) { ServiceError(invoke, 3, 0); return; }
            Confirm(invoke, result);
        }
        private void Attributes(long invoke, BerElement service)
        {
            var fields = service.Children();
            var named = fields.Read(0xa0).Children(); fields.End();
            var domainSpecific = named.Read(0xa1).Children(); named.End();
            BerElement domain = domainSpecific.Read(0x1a), item = domainSpecific.Read(0x1a);
            domainSpecific.End(); ValidateIdentifier(domain); ValidateIdentifier(item);
            MmsServerVariable value = null;
            for (int i = 0; i < variables.Length; i++)
                if (Matches(domain, variables[i].Domain) && Matches(item, variables[i].Item)) { value = variables[i]; break; }
            if (value == null) { ServiceError(invoke, 7, 2); return; }
            byte[] specification = value.TypeDefinition();
            Confirm(invoke, MmsEncoding.Tlv(0xa6, MmsEncoding.Tlv(0x80, new byte[] { 0 }), MmsEncoding.Tlv(0xa2, specification)));
        }
        private void WriteVariable(long invoke, BerElement service)
        {
            var fields = service.Children();
            var list = fields.Read(0xa0).Children();
            var variable = list.Read(0x30).Children(); list.End();
            var name = variable.Read(0xa0).Children(); variable.End();
            var pair = name.Read(0xa1).Children(); name.End();
            BerElement domain = pair.Read(0x1a), item = pair.Read(0x1a); pair.End();
            ValidateIdentifier(domain); ValidateIdentifier(item);
            var data = fields.Read(0xa0).Children(); fields.End();
            BerElement encoded = data.Read(); data.End();
            if (WriteProfile(invoke, domain, item, encoded)) return;
            MmsServerVariable target = null;
            for (int i = 0; i < variables.Length; i++)
                if (Matches(domain, variables[i].Domain) && Matches(item, variables[i].Item)) { target = variables[i]; break; }
            Remaining();
            int code = target == null ? 10 : target.ApplyWrite(encoded);
            Confirm(invoke, MmsEncoding.Tlv(0xa5, code == -1 ? MmsEncoding.Tlv(0x81) : MmsEncoding.Int(0x80, code)));
        }
        private void SendRead(long invoke, MmsServerVariable value)
        {
            if (value != null && (value.IsComposite || value.IsRaw))
            {
                Confirm(invoke, MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, value.EncodeValue())));
                return;
            }
            // 이 스칼라 전용 응답은 최소 크기 TPDU 하나에 들어가므로 처리가 끝난 수신 패킷을 재사용합니다.
            int valueLength = value == null ? 3 : value.EncodedLength;
            int p = 22 + BerEncoding.WriteInteger(packet, 22, 2, invoke);
            int length = p + 4 + valueLength;
            if (length - 20 > NegotiatedPduSize) throw new MmsProtocolException("Response exceeds negotiated PDU.");
            packet[0] = 3; packet[1] = 0; packet[2] = 0; packet[3] = (byte)length;
            packet[4] = 2; packet[5] = 0xf0; packet[6] = 0x80;
            packet[7] = 1; packet[8] = 0; packet[9] = 1; packet[10] = 0;
            packet[11] = 0x61; packet[12] = (byte)(length - 13);
            packet[13] = 0x30; packet[14] = (byte)(length - 15);
            packet[15] = 2; packet[16] = 1; packet[17] = 3;
            packet[18] = 0xa0; packet[19] = (byte)(length - 20);
            packet[20] = 0xa1; packet[21] = (byte)(length - 22);
            packet[p++] = 0xa4; packet[p++] = (byte)(valueLength + 2);
            packet[p++] = 0xa1; packet[p++] = (byte)valueLength;
            if (value == null) { packet[p] = 0x80; packet[p + 1] = 1; packet[p + 2] = 10; }
            else Array.Copy(value.Encoded, 0, packet, p, valueLength);
            Write(packet, length);
        }
        private static void ValidateIdentifier(BerElement element)
        {
            if (element.Length < 1 || element.Length > 64) throw new MmsProtocolException("Invalid MMS identifier size.");
            for (int i = 0; i < element.Length; i++)
            {
                byte b = element.Buffer[element.Offset + i];
                if (b < 33 || b > 126) throw new MmsProtocolException("Invalid MMS identifier character.");
            }
        }
        internal static bool Matches(BerElement element, string name)
        {
            if (element.Length != name.Length) return false;
            for (int i = 0; i < name.Length; i++)
                if (element.Buffer[element.Offset + i] != name[i]) return false;
            return true;
        }
    }
}
