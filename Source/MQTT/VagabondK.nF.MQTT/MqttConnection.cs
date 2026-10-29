using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace VagabondK.nF.MQTT
{
    /// <summary><see cref="MqttConnection"/>이 지정된 시간 안에 패킷을 주고받는 데 사용하는 바이트 전송 채널입니다.</summary>
    /// <remarks>소켓·스트림 등 실제 전송 계층의 연결과 해제는 호출자가 관리합니다.
    /// 구현 자체에서 동시 접근을 지원할 필요는 없으며, 연결 객체 또는 호출자가 한 채널에 대한 호출을 차례로 실행해야 합니다.</remarks>
    public interface IMqttChannel
    {
        /// <summary>작업 전체 마감 시간 계산에 쓰는 단조 증가 시계 값(밀리초)입니다.</summary>
        long Milliseconds { get; }
        /// <summary>제한 시간 안에 최대 <paramref name="count"/> 바이트를 읽습니다. 구현은 제한 시간을 반드시 지켜야 합니다.</summary>
        /// <param name="bytes">수신 데이터를 기록할 버퍼입니다.</param>
        /// <param name="offset">기록을 시작할 <paramref name="bytes"/> 내 위치입니다.</param>
        /// <param name="count">읽을 최대 바이트 수입니다. 1 이상입니다.</param>
        /// <param name="timeoutMilliseconds">데이터를 기다릴 최대 시간(밀리초)입니다.</param>
        /// <param name="transferred">실제로 읽은 바이트 수입니다. 상대가 연결을 닫았으면(EOF) 0입니다.</param>
        /// <returns>제한 시간이 지나면 <see langword="false"/>, 데이터를 읽었거나 EOF이면 <see langword="true"/>입니다.</returns>
        bool TryRead(byte[] bytes, int offset, int count, int timeoutMilliseconds, out int transferred);
        /// <summary><paramref name="bytes"/>의 처음 <paramref name="count"/> 바이트를 제한 시간 안에 동기적으로 모두 보내고, 그렇지 못하면 예외를 던집니다.</summary>
        /// <param name="bytes">보낼 데이터입니다. 호출이 반환되면 연결이 버퍼를 재사용하므로 구현이 참조를 보관하면 안 됩니다.</param>
        /// <param name="count">보낼 바이트 수입니다.</param>
        /// <param name="timeoutMilliseconds">전송 완료까지 허용하는 최대 시간(밀리초)입니다.</param>
        void Write(byte[] bytes, int count, int timeoutMilliseconds);
    }

    /// <summary>중간 복사 없이 버퍼의 지정 구간을 바로 보내는 선택적 채널 계약입니다.</summary>
    /// <remarks>연결은 이 인터페이스가 구현되어 있으면 내부 송신 버퍼를 그대로 넘기고, 아니면 별도 버퍼로 복사해
    /// <see cref="IMqttChannel.Write(byte[], int, int)"/>를 호출합니다. 구현은 반환 전에 지정 구간을 모두 소비해야 합니다.</remarks>
    public interface IMqttSegmentChannel : IMqttChannel
    {
        /// <summary><paramref name="offset"/>부터 정확히 <paramref name="count"/> 바이트를 마감 시간 안에 보내고, 그렇지 못하면 예외를 던집니다.</summary>
        /// <param name="bytes">보낼 데이터가 들어 있는 버퍼입니다. 반환 후 재사용되므로 보관하면 안 됩니다.</param>
        /// <param name="offset">전송을 시작할 위치입니다.</param>
        /// <param name="count">보낼 바이트 수입니다.</param>
        /// <param name="timeoutMilliseconds">전송 완료까지 허용하는 최대 시간(밀리초)입니다.</param>
        void Write(byte[] bytes, int offset, int count, int timeoutMilliseconds);
    }

    /// <summary>이미 연결된 호출자 소유 <see cref="Socket"/>을 MQTT 채널로 감쌉니다. 소켓을 연결하거나 닫지 않습니다.</summary>
    /// <remarks>작은 읽기(헤더 바이트 등)는 내부 256바이트 버퍼로 묶어 소켓 호출 수를 줄입니다.
    /// 제한 시간은 1..<c>int.MaxValue / 1000</c> 밀리초 범위여야 합니다.</remarks>
    public sealed class SocketMqttChannel : IMqttSegmentChannel
    {
        private readonly Socket socket;
        private readonly byte[] input = new byte[256];
        private int inputPosition, inputCount;
        /// <summary>소유권을 넘겨받지 않고 연결된 소켓을 감쌉니다.</summary>
        /// <param name="socket">이미 연결된 TCP 소켓입니다. 닫기는 호출자 책임입니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="socket"/>이 <see langword="null"/>인 경우.</exception>
        public SocketMqttChannel(Socket socket) { this.socket = socket ?? throw new ArgumentNullException("socket"); }
        /// <inheritdoc />
        public long Milliseconds { get { return Environment.TickCount64; } }
        /// <inheritdoc />
        /// <exception cref="ArgumentOutOfRangeException">구간이 버퍼를 벗어나거나 <paramref name="count"/>가 0이거나 제한 시간이 범위를 벗어난 경우.</exception>
        public bool TryRead(byte[] bytes, int offset, int count, int timeoutMilliseconds, out int transferred)
        {
            Validate(bytes, offset, count, timeoutMilliseconds);
            if (count == 0) throw new ArgumentOutOfRangeException("count");
            transferred = 0;
            if (inputPosition < inputCount)
            {
                transferred = count < inputCount - inputPosition ? count : inputCount - inputPosition;
                Array.Copy(input, inputPosition, bytes, offset, transferred); inputPosition += transferred;
                return true;
            }
            if (!socket.Poll(timeoutMilliseconds * 1000, SelectMode.SelectRead)) return false;
            socket.ReceiveTimeout = timeoutMilliseconds;
            if (count >= input.Length) transferred = socket.Receive(bytes, offset, count, SocketFlags.None);
            else
            {
                inputCount = socket.Receive(input, 0, input.Length, SocketFlags.None);
                transferred = count < inputCount ? count : inputCount;
                Array.Copy(input, 0, bytes, offset, transferred); inputPosition = transferred;
            }
            return true;
        }
        /// <inheritdoc />
        public void Write(byte[] bytes, int count, int timeoutMilliseconds)
        { Write(bytes, 0, count, timeoutMilliseconds); }
        /// <inheritdoc />
        /// <exception cref="TimeoutException">마감 시간 안에 모든 바이트를 보내지 못한 경우.</exception>
        /// <exception cref="IOException">소켓이 0바이트 전송으로 종료를 알린 경우.</exception>
        public void Write(byte[] bytes, int offset, int count, int timeoutMilliseconds)
        {
            Validate(bytes, offset, count, timeoutMilliseconds);
            long deadline = Milliseconds + timeoutMilliseconds;
            int end = offset + count;
            while (offset < end)
            {
                int remaining = (int)(deadline - Milliseconds);
                if (remaining <= 0 || !socket.Poll(remaining * 1000, SelectMode.SelectWrite)) throw new TimeoutException("MQTT send deadline.");
                remaining = (int)(deadline - Milliseconds);
                if (remaining <= 0) throw new TimeoutException("MQTT send deadline.");
                socket.SendTimeout = remaining;
                int sent = socket.Send(bytes, offset, end - offset, SocketFlags.None);
                if (sent == 0) throw new IOException("MQTT socket send EOF.");
                offset += sent;
            }
        }
        private static void Validate(byte[] bytes, int offset, int count, int timeout)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (offset < 0 || count < 0 || offset > bytes.Length || count > bytes.Length - offset)
                throw new ArgumentOutOfRangeException("offset/count");
            if (timeout < 1 || timeout > int.MaxValue / 1000) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
        }
    }

    /// <summary>수신한 PUBLISH를 처리 중인 스레드에서 동기적으로 전달받는 기본 콜백입니다.</summary>
    /// <remarks>QoS1 중복 수신은 다시 전달되므로 처리 로직은 멱등이어야 합니다. 콜백 안에서 같은 연결의 메서드를 호출(재진입)하면 안 됩니다.
    /// 콜백이 예외를 던지면 QoS1의 PUBACK 또는 QoS2의 PUBCOMP를 보내지 않고 연결이 Faulted 상태가 됩니다.
    /// 콜백 실행 시간도 통신 기한을 소모합니다. 느린 검증·외부 작업은 페이로드를 보관한 뒤 작업 반환 후 수행하십시오.</remarks>
    /// <param name="topic">토픽 별칭이 해석된 전체 토픽 이름입니다.</param>
    /// <param name="payload">콜백이 소유하는 페이로드 복사본입니다.</param>
    /// <param name="retained">브로커가 보관 메시지(retained)로 재전송했는지 여부입니다.</param>
    /// <param name="duplicate">DUP 플래그, 즉 재전송된 패킷인지 여부입니다.</param>
    public delegate void MqttPublication(string topic, byte[] payload, bool retained, bool duplicate);

    /// <summary>UTF-8 토픽, 바이너리 Will, QoS0/1/2, 직렬화된 폴링을 지원하는 MQTT 3.1/3.1.1/5 동기식 클라이언트입니다.</summary>
    /// <remarks>
    /// <para>한 인스턴스는 하나의 채널에서 한 번만 연결할 수 있습니다. 작업 실패(타임아웃, 프로토콜 오류, 콜백 예외)나 <see cref="Abort"/> 후에는
    /// <see cref="Faulted"/>가 되며, 재연결하려면 새 채널과 새 <see cref="MqttConnection"/>을 만들고 같은 <see cref="MqttSessionState"/>를 넘깁니다.</para>
    /// <para>스레드 안전하지 않습니다. 모든 작업은 한 스레드에서 차례로 호출해야 하며, 수신 콜백 안에서의 재진입은
    /// <see cref="InvalidOperationException"/>으로 거부됩니다. 다른 스레드에서 사용하려면 <see cref="MqttWorker"/>를 쓰십시오.</para>
    /// <para>작업은 블로킹이며 <see cref="MqttConnectionOptions.OperationTimeoutMilliseconds"/>로 통신 기한을 정합니다.
    /// 사용자 콜백을 강제로 중단하지 않으므로 콜백 비용과 예외 전파까지 포함한 API 반환 시간의 상한 보장은 아닙니다.</para>
    /// </remarks>
    public sealed partial class MqttConnection
    {
        /// <summary>기본 최대 MQTT 본문(가변 헤더 + 페이로드) 크기(바이트)입니다. 송신과 수신 모두에 적용됩니다.</summary>
        public const int MaximumBody = 1024;
        /// <summary>기본 작업 제한 시간(밀리초)입니다. 요청 전체와 부분 프레임 수신 마감에 적용됩니다.</summary>
        public const int TimeoutMilliseconds = 3000;
        private readonly IMqttChannel channel;
        private readonly byte[] single = new byte[1];
        private readonly Body outgoing;
        private readonly byte[] controlBuffer = new byte[8];
        private byte[] fallbackSendBuffer;
        private readonly byte[] acknowledgment = new byte[3];
        private static readonly byte[] Empty = new byte[0];
        // nanoCLR의 UTF-8 기반 문자열 인덱서로는 UTF-16 서로게이트 쌍을 정확히 읽을 수 없습니다.
#if NANOFRAMEWORK
        private static readonly bool NativeUtf8Strings = true;
#else
        private static readonly bool NativeUtf8Strings = false;
#endif
        private bool started, active, connected, faulted, pingPending;
        private long lastSent, pingSent;
        private int keepAliveMilliseconds;
        private readonly MqttSessionState session;
        private readonly int maximumBody, maximumPayload, maximumNameBytes, maximumFilters;
        private int acknowledgmentRetries;
        private int operationTimeout = TimeoutMilliseconds;
        private bool persistent;
        private bool ownsSession;
        private byte protocolLevel = 4;
        private MqttAuthenticate authenticate;
        private string authenticationMethod;
        private string[] inboundAliases;
        private int inboundPacketSize;
        private uint serverPacketSize = uint.MaxValue;
        private uint sessionExpiry;
        private int inboundReceiveMaximum;
        /// <summary>QoS와 MQTT 5 속성까지 함께 받는 수신 콜백입니다. 설정되어 있으면 <see cref="Received"/>보다 우선합니다.</summary>
        /// <remarks>호출 스레드, 재진입 금지, 예외 처리 규칙은 <see cref="Received"/>와 같습니다.</remarks>
        public MqttMessageReceived MessageReceived { get; set; }
        /// <summary>CONNACK에서 검증된 MQTT 5 속성입니다(최대 QoS, Receive Maximum 등 서버 기능 협상 결과 포함). MQTT 3.x에서는 빈 배열입니다.</summary>
        public MqttProperty[] ConnackProperties { get; private set; } = NoProperties;
        /// <summary>가장 최근에 검증한 확인 패킷(PUBACK/PUBREC/PUBCOMP/SUBACK/UNSUBACK/AUTH)의 MQTT 5 속성입니다. 없으면 빈 배열입니다.</summary>
        public MqttProperty[] AcknowledgmentProperties { get; private set; } = NoProperties;
        /// <summary>가장 최근에 검증한 발행 확인(PUBACK/PUBREC/PUBCOMP) 또는 AUTH 성공의 사유 코드입니다.</summary>
        /// <remarks>성공으로 취급되는 16(No Matching Subscribers) 같은 MQTT 5 정보성 코드를 확인할 때 사용합니다. MQTT 3.x에서는 항상 0입니다.</remarks>
        public byte LastReasonCode { get; private set; }
        /// <summary>CONNACK 수락 후 정상 종료나 전송·프로토콜 오류가 발생하기 전까지 <see langword="true"/>입니다.</summary>
        public bool Connected { get { return connected && !faulted; } }
        /// <summary>작업 실패나 <see cref="Abort"/> 이후 <see langword="true"/>입니다. 이 인스턴스는 더 사용할 수 없으며 새 채널과 연결이 필요합니다.</summary>
        public bool Faulted { get { return faulted; } }
        /// <summary>수신 PUBLISH를 받는 기본 콜백입니다. <see cref="MessageReceived"/>를 설정하지 않았다면 구독 전에 반드시 지정해야 합니다.</summary>
        /// <remarks>콜백은 <see cref="Poll"/> 등 작업을 호출한 스레드에서 실행됩니다. 처리기가 없거나 콜백이 예외를 던지면
        /// QoS1의 PUBACK 또는 QoS2의 PUBCOMP를 보내지 않고 연결이 Faulted 상태가 됩니다.</remarks>
        public MqttPublication Received { get; set; }
        /// <summary>CONNACK의 Session Present 플래그로, 브로커가 이전 세션을 이어받았는지 나타냅니다. MQTT 3.1에는 이 플래그가 없어 항상 <see langword="false"/>입니다.</summary>
        public bool SessionPresent { get; private set; }
        /// <summary>요청한 클라이언트 ID입니다. MQTT 5에서 빈 ID로 연결했다면 브로커가 할당한 ID로 바뀝니다.</summary>
        public string ClientId { get; private set; }
        /// <summary>새 RAM 세션과 기본 용량으로 일회용 클라이언트를 만듭니다.</summary>
        /// <param name="channel">호출자가 소유하는 연결된 채널입니다. 연결은 이를 닫지 않습니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="channel"/>이 <see langword="null"/>인 경우.</exception>
        public MqttConnection(IMqttChannel channel) : this(channel, new MqttSessionState()) { }
        /// <summary>재사용 가능한 RAM 세션과 제한된 버퍼 용량으로 연결을 만듭니다.</summary>
        /// <remarks>각 용량만큼 버퍼를 미리 할당하므로 장치 메모리에 맞게 설정하십시오. 기본값은 본문 1024바이트, 페이로드 512바이트,
        /// 토픽/필터 128바이트, 요청당 필터 16개입니다. <paramref name="session"/>은 연결 중 이 인스턴스가 배타적으로 점유하며,
        /// 연결이 끝나면 해제되어 다음 연결에 다시 넘길 수 있습니다.</remarks>
        /// <param name="channel">호출자가 소유하는 연결된 채널입니다.</param>
        /// <param name="session">미완료 QoS 상태를 보관하는 RAM 세션입니다. 같은 클라이언트 ID·브로커 조합에만 재사용해야 합니다.</param>
        /// <param name="maximumBody">송수신 MQTT 본문 최대 크기(바이트), 256..268435455입니다.</param>
        /// <param name="maximumPayload">PUBLISH/Will 페이로드 최대 크기(바이트), 0..<paramref name="maximumBody"/>-4입니다.</param>
        /// <param name="maximumNameBytes">클라이언트 ID·토픽·필터·사용자 이름의 최대 UTF-8 길이(바이트), 1..65535입니다.</param>
        /// <param name="maximumFilters">SUBSCRIBE/UNSUBSCRIBE 한 번에 보낼 수 있는 최대 필터 수, 1..65535입니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="channel"/> 또는 <paramref name="session"/>이 <see langword="null"/>인 경우.</exception>
        /// <exception cref="ArgumentOutOfRangeException">용량 인수가 허용 범위를 벗어난 경우.</exception>
        public MqttConnection(IMqttChannel channel, MqttSessionState session, int maximumBody = MaximumBody, int maximumPayload = 512,
            int maximumNameBytes = 128, int maximumFilters = 16)
        {
            this.channel = channel ?? throw new ArgumentNullException("channel");
            this.session = session ?? throw new ArgumentNullException("session");
            if (maximumBody < 256 || maximumBody > 268435455 || maximumNameBytes < 1 || maximumNameBytes > 65535 ||
                maximumPayload < 0 || maximumPayload > maximumBody - 4 || maximumFilters < 1 || maximumFilters > 65535)
                throw new ArgumentOutOfRangeException("MQTT bounds");
            this.maximumBody = maximumBody; this.maximumPayload = maximumPayload;
            this.maximumNameBytes = maximumNameBytes; this.maximumFilters = maximumFilters;
            outgoing = new Body(maximumBody);
        }

        /// <summary>Will 없이 MQTT 3.1.1 클린 세션으로 연결합니다.</summary>
        /// <param name="clientId">MQTT UTF-8 클라이언트 ID입니다. 빈 문자열이면 브로커가 ID를 할당할 수 있습니다.</param>
        /// <param name="keepAliveSeconds">keepalive 간격(초)입니다. 0이면 비활성화하며, 송신이 간격의 절반 동안 없으면 PINGREQ를 보냅니다.</param>
        /// <remarks>예외와 실패 시 동작은 <see cref="Connect(MqttConnectionOptions)"/>와 같습니다.</remarks>
        public void Connect(string clientId, ushort keepAliveSeconds = 20)
        { Connect(clientId, null, null, keepAliveSeconds); }

        /// <summary>QoS1·비보관 바이너리 Will을 등록하고 MQTT 3.1.1 클린 세션으로 연결합니다. 인증이나 TLS 협상은 하지 않습니다.</summary>
        /// <param name="clientId">MQTT UTF-8 클라이언트 ID입니다.</param>
        /// <param name="willTopic">Will 토픽입니다. <paramref name="willPayload"/>와 함께 <see langword="null"/>이면 Will을 쓰지 않습니다.</param>
        /// <param name="willPayload">비정상 종료 시 브로커가 발행할 페이로드입니다.</param>
        /// <param name="keepAliveSeconds">keepalive 간격(초)입니다. 0이면 비활성화합니다.</param>
        /// <remarks>예외와 실패 시 동작은 <see cref="Connect(MqttConnectionOptions)"/>와 같습니다.</remarks>
        public void Connect(string clientId, string willTopic, byte[] willPayload, ushort keepAliveSeconds = 20)
        {
            Connect(new MqttConnectionOptions { ClientId = clientId, WillTopic = willTopic, WillPayload = willPayload,
                KeepAliveSeconds = keepAliveSeconds });
        }
        /// <summary>자격 증명, 프로토콜 수준(3.1/3.1.1/5), Will, MQTT 5 속성을 지정해 CONNECT를 보내고 CONNACK을 기다립니다.</summary>
        /// <remarks>
        /// <para>TLS는 전달받은 채널이 담당합니다. 인스턴스당 한 번만 호출할 수 있으며, 실패하면 <see cref="Faulted"/>가 됩니다.</para>
        /// <para>MQTT 5 AUTH 챌린지는 최대 16회까지 <see cref="MqttConnectionOptions.Authenticate"/>로 처리합니다.
        /// 세션에 미완료 QoS 발행이나 배치가 남아 있으면 CONNACK 직후 재전송(DUP)해 완료한 뒤 반환합니다.</para>
        /// </remarks>
        /// <param name="options">연결 설정입니다. 값은 호출 중 동기적으로 검증·인코딩됩니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>인 경우.</exception>
        /// <exception cref="ArgumentException">ID·토픽·속성·재시도·제한 시간이 유효하지 않거나, CONNECT가 본문 한도를 넘거나,
        /// 지속 세션의 클라이언트 ID/프로토콜 수준이 이전과 다른 경우.</exception>
        /// <exception cref="InvalidOperationException">이미 연결을 시도한 인스턴스이거나 세션을 다른 연결이 사용 중인 경우.</exception>
        /// <exception cref="MqttReasonException">MQTT 5 브로커가 사유 코드로 연결을 거부한 경우.</exception>
        /// <exception cref="IOException">CONNACK이 잘못되었거나 MQTT 3.x 연결이 거부된 경우, 또는 전송 오류.</exception>
        /// <exception cref="TimeoutException">작업 제한 시간 안에 응답이 오지 않은 경우.</exception>
        public void Connect(MqttConnectionOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            Text(options.ClientId, options.ProtocolLevel == 5 || options.CleanSession && options.ProtocolLevel == 4, maximumNameBytes);
            bool will = options.WillTopic != null || options.WillPayload != null;
            if (will) Name(options.WillTopic, false, maximumNameBytes);
            if (will && (options.WillPayload == null || options.WillPayload.Length > maximumPayload) ||
                options.WillQos > MqttQos.ExactlyOnce || !will && options.WillRetain ||
                options.ProtocolLevel != 3 && options.ProtocolLevel != 4 && options.ProtocolLevel != 5 ||
                options.ProtocolLevel != 5 && options.Password != null && options.UserName == null || options.AcknowledgmentRetries < 0 ||
                options.AcknowledgmentRetries > 10 || options.OperationTimeoutMilliseconds < 1 ||
                options.OperationTimeoutMilliseconds > 60000) throw new ArgumentException("MQTT CONNECT options.");
            if (options.UserName != null) Text(options.UserName, true, maximumNameBytes);
            if (options.Password != null && options.Password.Length > 65535) throw new ArgumentOutOfRangeException("Password");
            byte[] connectProperties = Empty, willProperties = Empty;
            MqttProperty[] requestedProperties = options.Properties ?? NoProperties;
            if (options.ProtocolLevel == 5)
            {
                if (Number(requestedProperties, MqttPropertyId.ReceiveMaximum, (uint)session.Incoming.Length) > session.Incoming.Length ||
                    Number(requestedProperties, MqttPropertyId.MaximumPacketSize, (uint)(maximumBody + 5)) > maximumBody + 5)
                    throw new ArgumentException("MQTT advertised receive capacity exceeds local bounds.");
                var combined = new MqttProperty[requestedProperties.Length +
                    (Find(requestedProperties, MqttPropertyId.ReceiveMaximum) == null ? 1 : 0) + (Find(requestedProperties, MqttPropertyId.MaximumPacketSize) == null ? 1 : 0)];
                Array.Copy(requestedProperties, combined, requestedProperties.Length);
                int next = requestedProperties.Length;
                if (Find(requestedProperties, MqttPropertyId.ReceiveMaximum) == null) combined[next++] = new MqttProperty(MqttPropertyId.ReceiveMaximum, (uint)session.Incoming.Length);
                if (Find(requestedProperties, MqttPropertyId.MaximumPacketSize) == null) combined[next] = new MqttProperty(MqttPropertyId.MaximumPacketSize, (uint)(maximumBody + 5));
                requestedProperties = combined;
                connectProperties = EncodeProperties(combined, MqttPropertyContext.Connect);
                willProperties = EncodeProperties(options.WillProperties, MqttPropertyContext.Will);
                if (Find(combined, MqttPropertyId.AuthenticationData) != null && Find(combined, MqttPropertyId.AuthenticationMethod) == null)
                    throw new ArgumentException("MQTT Authentication Data requires Method.");
            }
            else if (options.Properties != null || options.WillProperties != null || options.Authenticate != null)
                throw new ArgumentException("MQTT 5 properties require protocol level 5.");
            if (!will && options.WillProperties != null) throw new ArgumentException("Will properties require a Will.");
            int connectSize = (options.ProtocolLevel != 3 ? 10 : 12) + 2 + Utf8Length(options.ClientId) +
                (will ? 4 + Utf8Length(options.WillTopic) + options.WillPayload.Length : 0) +
                (options.UserName != null ? 2 + Utf8Length(options.UserName) : 0) +
                (options.Password != null ? 2 + options.Password.Length : 0) +
                (options.ProtocolLevel == 5 ? VariableSize(connectProperties.Length) + connectProperties.Length +
                    (will ? VariableSize(willProperties.Length) + willProperties.Length : 0) : 0);
            if (connectSize > maximumBody) throw new ArgumentException("CONNECT packet bound.");
            if (!options.CleanSession && session.ClientId != null &&
                (session.ClientId != options.ClientId || session.ProtocolLevel != options.ProtocolLevel))
                throw new ArgumentException("Persistent session identity changed.");
            if (started || active) throw new InvalidOperationException("Use a new connection and socket.");
            started = active = true;
            bool success = false;
            try
            {
                lock (session)
                {
                    if (session.Owner != null) throw new InvalidOperationException("MQTT session already in use.");
                    session.Owner = this; ownsSession = true;
                }
                var body = outgoing; body.Reset();
                protocolLevel = options.ProtocolLevel;
                authenticate = options.Authenticate;
                var method = Find(requestedProperties, MqttPropertyId.AuthenticationMethod); authenticationMethod = method == null ? null : method.Text;
                if (protocolLevel == 5)
                {
                    inboundAliases = new string[(int)Number(requestedProperties, MqttPropertyId.TopicAliasMaximum, 0)];
                    inboundPacketSize = (int)Number(requestedProperties, MqttPropertyId.MaximumPacketSize, (uint)(maximumBody + 5));
                    inboundReceiveMaximum = (int)Number(requestedProperties, MqttPropertyId.ReceiveMaximum, (uint)session.Incoming.Length);
                }
                persistent = protocolLevel == 5 ? Number(requestedProperties, MqttPropertyId.SessionExpiryInterval, 0) != 0 : !options.CleanSession;
                acknowledgmentRetries = options.AcknowledgmentRetries;
                operationTimeout = options.OperationTimeoutMilliseconds;
                if (options.CleanSession || protocolLevel != 5 && !persistent ||
                    session.ExpiresAt != 0 && Environment.TickCount64 >= session.ExpiresAt) session.Clear();
                session.ExpiresAt = 0;
                session.ClientId = options.ClientId; session.ProtocolLevel = protocolLevel;
                ClientId = options.ClientId;
                body.Text(protocolLevel != 3 ? "MQTT" : "MQIsdp"); body.Byte(protocolLevel);
                body.Byte((byte)((options.CleanSession ? 2 : 0) | (will ? 4 | (byte)options.WillQos << 3 | (options.WillRetain ? 32 : 0) : 0) |
                    (options.UserName != null ? 128 : 0) | (options.Password != null ? 64 : 0)));
                body.Word(options.KeepAliveSeconds);
                if (protocolLevel == 5) WriteProperties(connectProperties);
                body.Text(options.ClientId);
                if (will) { if (protocolLevel == 5) WriteProperties(willProperties); body.Text(options.WillTopic); body.Binary(options.WillPayload); }
                if (options.UserName != null) body.Text(options.UserName);
                if (options.Password != null) body.Binary(options.Password);
                long deadline = channel.Milliseconds + operationTimeout;
                Send(0x10, body.Bytes, body.Count, deadline);
                Packet packet = Receive(deadline, false);
                int challenges = 0;
                while (protocolLevel == 5 && packet.Header == 0xf0)
                {
                    if (++challenges > 16) throw new IOException("MQTT authentication challenge bound.");
                    HandleAuthentication(packet, deadline, false);
                    packet = Receive(deadline, false);
                }
                if (packet.Header != 0x20 || packet.Bytes.Length < 2 ||
                    protocolLevel != 5 && packet.Bytes.Length != 2 || packet.Bytes[0] > (!options.CleanSession && protocolLevel != 3 ? 1 : 0))
                    throw new IOException("MQTT CONNACK refused or malformed.");
                if (protocolLevel == 5)
                {
                    int position = 2;
                    ConnackProperties = ParseProperties(packet.Bytes, ref position, MqttPropertyContext.ConnAck);
                    if (position != packet.Bytes.Length) throw new IOException("MQTT CONNACK trailing data.");
                    ValidateAuthentication(ConnackProperties);
                    serverPacketSize = Number(ConnackProperties, MqttPropertyId.MaximumPacketSize, uint.MaxValue);
                }
                if (!ValidReason(MqttPacketType.ConnAck, packet.Bytes[1]) || packet.Bytes[1] != 0 && packet.Bytes[0] != 0)
                    throw new IOException("MQTT CONNACK reason/flags.");
                if (packet.Bytes[1] != 0) throw new MqttReasonException(MqttPacketType.ConnAck, packet.Bytes[1], ConnackProperties);
                if (protocolLevel == 5 && options.ClientId.Length == 0)
                {
                    var assigned = Find(ConnackProperties, MqttPropertyId.AssignedClientIdentifier);
                    if (assigned == null || assigned.Text.Length == 0 || Utf8Length(assigned.Text) > maximumNameBytes)
                        throw new IOException("MQTT missing/oversized assigned client identifier.");
                    ClientId = assigned.Text; session.ClientId = assigned.Text;
                }
                if (protocolLevel == 5)
                {
                    sessionExpiry = Number(ConnackProperties, MqttPropertyId.SessionExpiryInterval, Number(requestedProperties, MqttPropertyId.SessionExpiryInterval, 0));
                    persistent = sessionExpiry != 0;
                }
                SessionPresent = packet.Bytes[0] == 1;
                if (persistent && protocolLevel != 3 && !SessionPresent) session.ClearIncoming();
                keepAliveMilliseconds = (int)Number(ConnackProperties, MqttPropertyId.ServerKeepAlive, options.KeepAliveSeconds) * 1000;
                connected = true;
                if (session.OutgoingStage != 0) ResumePublication();
                if (session.Batch != null) ResumeBatch();
                success = true;
            }
            finally { active = false; if (!success) Fault(); }
        }
        /// <summary>토픽 필터 하나를 구독하고 SUBACK을 기다립니다.</summary>
        /// <param name="filter"><c>+</c>, <c>#</c> 와일드카드를 쓸 수 있는 토픽 필터입니다.</param>
        /// <param name="qos">요청할 최대 QoS입니다.</param>
        /// <returns>브로커가 수락하면 <see langword="true"/>(요청보다 낮은 QoS 승인 포함), 명시적으로 거부하면 <see langword="false"/>입니다.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="qos"/>가 정의되지 않은 값인 경우.</exception>
        /// <remarks>그 밖의 예외는 <see cref="Subscribe(string[], byte[], MqttProperty[])"/>와 같습니다.</remarks>
        public bool Subscribe(string filter, MqttQos qos)
        {
            if (qos > MqttQos.ExactlyOnce) throw new ArgumentOutOfRangeException("qos");
            return Subscribe(new[] { filter }, new[] { (byte)qos })[0] < 128;
        }
        /// <summary>여러 토픽 필터를 한 SUBSCRIBE 요청으로 구독합니다.</summary>
        /// <param name="filters">구독할 필터입니다. 개수는 1..생성자의 maximumFilters(기본 16)입니다.</param>
        /// <param name="qualityOfService">필터별 요청 QoS(0..2)이며 <paramref name="filters"/>와 길이가 같아야 합니다.</param>
        /// <returns>필터별 브로커 승인 값입니다. 0..2는 승인된 QoS, 128 이상은 거부 사유입니다.</returns>
        /// <remarks>예외는 <see cref="Subscribe(string[], byte[], MqttProperty[])"/>와 같습니다.</remarks>
        public byte[] Subscribe(string[] filters, byte[] qualityOfService)
        { return Subscribe(filters, qualityOfService, null); }
        /// <summary>MQTT 5 구독 옵션과 속성을 지정해 여러 필터를 구독하고 SUBACK을 기다립니다.</summary>
        /// <param name="filters">구독할 필터입니다. 개수는 1..생성자의 maximumFilters입니다. <c>$share/</c> 공유 구독도 지원합니다.</param>
        /// <param name="qualityOfService">필터별 구독 옵션 바이트입니다. 하위 2비트는 QoS, 비트 2는 No Local, 비트 3은 Retain As Published,
        /// 비트 4..5는 Retain Handling(0..2)입니다. MQTT 3.x에서는 QoS 값(0..2)만 허용됩니다.</param>
        /// <param name="properties">SUBSCRIBE 속성(Subscription Identifier, User Property)입니다. MQTT 3.x에서는 <see langword="null"/>이어야 합니다.</param>
        /// <returns>필터별 SUBACK 사유 코드입니다. 128 미만은 승인된 QoS, 128 이상은 필터별 거부입니다.</returns>
        /// <exception cref="ArgumentException">배열이 비었거나 길이가 맞지 않거나, 필터·옵션이 잘못되었거나 패킷이 본문 한도를 넘는 경우.</exception>
        /// <exception cref="InvalidOperationException">연결되지 않았거나 Faulted이거나 재진입한 경우, 또는 브로커가 와일드카드·공유 구독·구독 ID를 지원하지 않는 경우.</exception>
        /// <exception cref="IOException">SUBACK이 잘못되었거나 전송 오류가 난 경우. 연결은 Faulted가 됩니다.</exception>
        /// <exception cref="TimeoutException">재시도를 포함해 제한 시간 안에 SUBACK이 오지 않은 경우. 연결은 Faulted가 됩니다.</exception>
        public byte[] Subscribe(string[] filters, byte[] qualityOfService, MqttProperty[] properties)
        {
            if (filters == null || qualityOfService == null || filters.Length == 0 || filters.Length > maximumFilters ||
                filters.Length != qualityOfService.Length) throw new ArgumentException("Subscription arrays.");
            byte[] encoded = PacketProperties(properties, MqttPropertyContext.Subscribe);
            int size = 2 + (protocolLevel == 5 ? encoded.Length + VariableSize(encoded.Length) : 0);
            for (int i = 0; i < filters.Length; i++)
            {
                Name(filters[i], true, maximumNameBytes);
                byte option = qualityOfService[i];
                if (protocolLevel != 5 && option > 2 || (option & 3) > 2 || (option & 192) != 0 ||
                    (option & 48) == 48 || filters[i].IndexOf("$share/") == 0 && (option & 4) != 0)
                    throw new ArgumentOutOfRangeException("qualityOfService");
                if (protocolLevel == 5)
                {
                    if (Number(ConnackProperties, MqttPropertyId.WildcardSubscriptionAvailable, 1) == 0 && (filters[i].IndexOf('+') >= 0 || filters[i].IndexOf('#') >= 0) ||
                        Number(ConnackProperties, MqttPropertyId.SharedSubscriptionAvailable, 1) == 0 && filters[i].IndexOf("$share/") == 0 ||
                        Number(ConnackProperties, MqttPropertyId.SubscriptionIdentifierAvailable, 1) == 0 && Find(properties, MqttPropertyId.SubscriptionIdentifier) != null)
                        throw new InvalidOperationException("MQTT broker does not support requested subscription capability.");
                }
                size += Utf8Length(filters[i]) + 3;
            }
            if (size > maximumBody) throw new ArgumentException("Subscription packet bound.");
            Enter();
            bool success = false;
            try
            {
                ushort id = Identifier();
                var body = outgoing; body.Reset(); body.Word(id);
                if (protocolLevel == 5) WriteProperties(encoded);
                for (int i = 0; i < filters.Length; i++) { body.Text(filters[i]); body.Byte(qualityOfService[i]); }
                long deadline = channel.Milliseconds + operationTimeout;
                Send(0x82, body.Bytes, body.Count, deadline);
                Packet packet = Wait(0x90, id, deadline, CaptureOutgoing(), 0x82);
                int position = AckProperties(packet);
                if (packet.Bytes.Length != position + filters.Length) throw new IOException("MQTT invalid SUBACK length.");
                byte[] grants = new byte[filters.Length];
                for (int i = 0; i < grants.Length; i++)
                {
                    grants[i] = packet.Bytes[i + position];
                    if (grants[i] < 128 && grants[i] > (qualityOfService[i] & 3) || !ValidReason(MqttPacketType.SubAck, grants[i]))
                        throw new IOException("MQTT invalid SUBACK grant.");
                }
                session.Complete(0x90, id, grants);
                success = true;
                return grants;
            }
            finally { active = false; if (!success) Fault(); }
        }
        /// <summary>필터 구독을 해제하고 해당 UNSUBACK을 기다립니다.</summary>
        /// <param name="filters">해제할 필터입니다. 개수는 1..생성자의 maximumFilters(기본 16)입니다.</param>
        /// <remarks>예외는 <see cref="Unsubscribe(string[], MqttProperty[])"/>와 같습니다. MQTT 5의 필터별 사유 코드는 반환하지 않습니다.</remarks>
        public void Unsubscribe(params string[] filters)
        { Unsubscribe(filters, null); }
        /// <summary>MQTT 5 User Property를 붙여 필터 구독을 해제하고 필터별 UNSUBACK 사유를 반환합니다.</summary>
        /// <param name="filters">해제할 필터입니다. 개수는 1..생성자의 maximumFilters입니다.</param>
        /// <param name="properties">UNSUBSCRIBE 속성(User Property만 허용)입니다. MQTT 3.x에서는 <see langword="null"/>이어야 합니다.</param>
        /// <returns>MQTT 5에서는 필터별 사유 코드(0 성공, 17 구독 없음, 128 이상 실패)이고, MQTT 3.x에서는 빈 배열입니다.</returns>
        /// <exception cref="ArgumentException">필터 배열이나 속성이 잘못되었거나 패킷이 본문 한도를 넘는 경우.</exception>
        /// <exception cref="InvalidOperationException">연결되지 않았거나 Faulted이거나 재진입한 경우.</exception>
        /// <exception cref="IOException">UNSUBACK이 잘못되었거나 전송 오류가 난 경우. 연결은 Faulted가 됩니다.</exception>
        /// <exception cref="TimeoutException">제한 시간 안에 UNSUBACK이 오지 않은 경우. 연결은 Faulted가 됩니다.</exception>
        public byte[] Unsubscribe(string[] filters, MqttProperty[] properties)
        {
            if (filters == null || filters.Length == 0 || filters.Length > maximumFilters) throw new ArgumentException("Unsubscribe filters.");
            byte[] encoded = PacketProperties(properties, MqttPropertyContext.Unsubscribe);
            int size = 2 + (protocolLevel == 5 ? encoded.Length + VariableSize(encoded.Length) : 0);
            foreach (string filter in filters) { Name(filter, true, maximumNameBytes); size += Utf8Length(filter) + 2; }
            if (size > maximumBody) throw new ArgumentException("Unsubscribe packet bound.");
            Enter(); bool success = false;
            try
            {
                ushort id = Identifier();
                outgoing.Reset(); outgoing.Word(id);
                if (protocolLevel == 5) WriteProperties(encoded);
                foreach (string filter in filters) outgoing.Text(filter);
                long deadline = channel.Milliseconds + operationTimeout;
                Send(0xa2, outgoing.Bytes, outgoing.Count, deadline);
                Packet packet = Wait(0xb0, id, deadline, CaptureOutgoing(), 0xa2);
                byte[] reasons = Empty;
                if (protocolLevel == 5)
                {
                    int position = AckProperties(packet);
                    if (packet.Bytes.Length - position != filters.Length) throw new IOException("MQTT UNSUBACK reason count.");
                    reasons = new byte[filters.Length];
                    Array.Copy(packet.Bytes, position, reasons, 0, reasons.Length);
                    foreach (byte reason in reasons) if (!ValidReason(MqttPacketType.UnsubAck, reason)) throw new IOException("MQTT UNSUBACK reason.");
                }
                session.Complete(0xb0, id, reasons);
                success = true;
                return reasons;
            }
            finally { active = false; if (!success) Fault(); }
        }
        /// <summary>메시지를 발행하고 QoS에 맞는 확인 절차가 끝날 때까지 기다립니다.</summary>
        /// <param name="topic">와일드카드가 없는 토픽 이름입니다.</param>
        /// <param name="payload">보낼 페이로드입니다. 호출 중 내부 버퍼로 복사됩니다.</param>
        /// <param name="qos">QoS0은 송신만, QoS1은 PUBACK, QoS2는 PUBREC/PUBREL/PUBCOMP까지 기다립니다.</param>
        /// <param name="retain">브로커에 보관 메시지로 남길지 여부입니다.</param>
        /// <remarks>예외와 전달 보장은 <see cref="Publish(string, byte[], MqttQos, bool, MqttProperty[])"/>와 같습니다.</remarks>
        public void Publish(string topic, byte[] payload, MqttQos qos = MqttQos.AtMostOnce, bool retain = false)
        { Publish(topic, payload, qos, retain, null); }
        /// <summary>MQTT 5 속성(콘텐츠 형식, 응답 토픽·상관 데이터, 만료, User Property, 토픽 별칭 등)을 붙여 메시지를 발행합니다.</summary>
        /// <remarks>
        /// <para>토픽 별칭을 지정해도 항상 전체 토픽을 함께 보내므로, 재연결 후 재전송이 이전 연결의 별칭에 의존하지 않습니다.</para>
        /// <para>QoS1/2 확인을 기다리다 실패하면 브로커 전달 여부가 불확실합니다. 미완료 상태는 <see cref="MqttSessionState"/>에 남아,
        /// 지속 세션이면 같은 세션으로 다시 <see cref="Connect(MqttConnectionOptions)"/>할 때 재전송됩니다.</para>
        /// </remarks>
        /// <param name="topic">와일드카드가 없는 토픽 이름입니다.</param>
        /// <param name="payload">보낼 페이로드입니다. 길이는 생성자의 maximumPayload 이하여야 합니다.</param>
        /// <param name="qos">전달 보장 수준입니다. 브로커의 Maximum QoS를 넘을 수 없습니다.</param>
        /// <param name="retain">브로커에 보관 메시지로 남길지 여부입니다. 브로커가 Retain을 지원하지 않으면 거부됩니다.</param>
        /// <param name="properties">PUBLISH 속성입니다. MQTT 3.x에서는 <see langword="null"/>이어야 합니다.</param>
        /// <exception cref="ArgumentException">토픽·페이로드·QoS·속성이 잘못되었거나 패킷이 본문 한도를 넘는 경우.</exception>
        /// <exception cref="InvalidOperationException">연결되지 않았거나 재진입한 경우, 또는 브로커가 협상한 QoS·Retain·토픽 별칭 한도를 넘은 경우.</exception>
        /// <exception cref="MqttReasonException">MQTT 5 브로커가 PUBACK/PUBREC에서 128 이상 사유 코드로 거부한 경우.</exception>
        /// <exception cref="IOException">전송·프로토콜 오류. 연결은 Faulted가 됩니다.</exception>
        /// <exception cref="TimeoutException">재시도를 포함해 제한 시간 안에 확인이 오지 않은 경우. 연결은 Faulted가 됩니다.</exception>
        public void Publish(string topic, byte[] payload, MqttQos qos, bool retain, MqttProperty[] properties)
        {
            Name(topic, false, maximumNameBytes);
            if (payload == null || payload.Length > maximumPayload || qos > MqttQos.ExactlyOnce) throw new ArgumentOutOfRangeException("payload/qos");
            byte[] encoded = PacketProperties(properties, MqttPropertyContext.Publish);
            int size = 2 + Utf8Length(topic) + (qos == MqttQos.AtMostOnce ? 0 : 2) + payload.Length +
                (protocolLevel == 5 ? VariableSize(encoded.Length) + encoded.Length : 0);
            if (size > maximumBody) throw new ArgumentException("MQTT publication body bound.");
            if ((byte)qos > Number(ConnackProperties, MqttPropertyId.MaximumQos, 2) || retain && Number(ConnackProperties, MqttPropertyId.RetainAvailable, 1) == 0 ||
                Find(properties, MqttPropertyId.TopicAlias) != null && Number(properties, MqttPropertyId.TopicAlias, 0) > Number(ConnackProperties, MqttPropertyId.TopicAliasMaximum, 0))
                throw new InvalidOperationException("MQTT broker publication limit.");
            Enter();
            bool success = false;
            try
            {
                ushort id = qos != MqttQos.AtMostOnce ? Identifier() : (ushort)0;
                var body = outgoing; body.Reset(); body.Text(topic); if (qos != MqttQos.AtMostOnce) body.Word(id);
                if (protocolLevel == 5) WriteProperties(encoded);
                body.Raw(payload);
                long deadline = channel.Milliseconds + operationTimeout;
                byte header = (byte)(0x30 | (byte)qos << 1 | (retain ? 1 : 0));
                if (qos != MqttQos.AtMostOnce)
                {
                    session.OutgoingBody = CaptureOutgoing(); session.OutgoingHeader = header;
                    session.OutgoingStage = (byte)qos; session.OutgoingId = id;
                }
                Send(header, body.Bytes, body.Count, deadline);
                if (qos != MqttQos.AtMostOnce) CompletePublication(deadline);
                success = true;
            }
            finally { active = false; if (!success) Fault(); }
        }
        /// <summary>수신 패킷을 최대 하나 처리하고 keepalive(PINGREQ 송신·PINGRESP 마감 확인)를 수행합니다.</summary>
        /// <remarks>수신 PUBLISH 콜백은 이 메서드를 호출한 스레드에서 실행됩니다. keepalive와 수신 처리를 위해 주기적으로 자주 호출해야 합니다.
        /// 오류가 나면 연결은 Faulted가 됩니다.</remarks>
        /// <param name="waitMilliseconds">첫 바이트를 기다릴 시간(밀리초), 1..1000입니다. 패킷이 시작되면 나머지는 작업 제한 시간 안에 읽습니다.</param>
        /// <returns>패킷을 하나 처리했으면 <see langword="true"/>, 대기 시간 동안 수신이 없으면 <see langword="false"/>입니다.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="waitMilliseconds"/>가 범위를 벗어난 경우.</exception>
        /// <exception cref="InvalidOperationException">연결되지 않았거나 재진입한 경우.</exception>
        /// <exception cref="MqttReasonException">MQTT 5 서버가 DISCONNECT를 보낸 경우.</exception>
        /// <exception cref="IOException">프로토콜 위반이나 EOF 등 전송 오류.</exception>
        /// <exception cref="TimeoutException">부분 패킷 또는 PINGRESP 마감 시간을 넘긴 경우.</exception>
        public bool Poll(int waitMilliseconds = 100)
        {
            if (waitMilliseconds < 1 || waitMilliseconds > 1000) throw new ArgumentOutOfRangeException("waitMilliseconds");
            Enter();
            bool success = false;
            try
            {
                KeepAlive();
                Packet packet = Receive(channel.Milliseconds + waitMilliseconds, true);
                if (packet != null) Process(packet, channel.Milliseconds + operationTimeout);
                KeepAlive();
                success = true;
                return packet != null;
            }
            finally { active = false; if (!success) Fault(); }
        }
        /// <summary>즉시 PINGREQ를 보내고(이미 대기 중이면 생략) PINGRESP를 기다립니다. 그 사이 도착한 PUBLISH도 처리합니다.</summary>
        /// <exception cref="InvalidOperationException">연결되지 않았거나 재진입한 경우.</exception>
        /// <exception cref="TimeoutException">작업 제한 시간 안에 PINGRESP가 오지 않은 경우. 연결은 Faulted가 됩니다.</exception>
        /// <exception cref="IOException">전송·프로토콜 오류. 연결은 Faulted가 됩니다.</exception>
        public void Ping()
        {
            Enter(); bool success = false;
            try
            {
                long deadline = channel.Milliseconds + operationTimeout;
                if (!pingPending)
                {
                    Send(0xc0, Empty, 0, deadline);
                    pingPending = true; pingSent = channel.Milliseconds;
                }
                while (pingPending) Process(Receive(deadline, false), deadline);
                success = true;
            }
            finally { active = false; if (!success) Fault(); }
        }
        /// <summary>정상 DISCONNECT를 보내고, 작업 제한 시간 안에 브로커가 연결을 닫을(EOF) 때까지 남은 수신 데이터를 버립니다.</summary>
        /// <remarks>호출자의 전송 계층은 닫지 않습니다. 비지속 세션이면 세션 상태를 비우고, 세션 점유를 해제합니다.
        /// 예외는 <see cref="Disconnect(byte, MqttProperty[])"/>와 같습니다.</remarks>
        public void Disconnect()
        { Disconnect(0, null); }
        /// <summary>MQTT 5 사유 코드와 속성을 지정해 DISCONNECT를 보내고 브로커의 연결 종료를 기다립니다.</summary>
        /// <remarks>DISCONNECT 이후에는 송신이 금지되므로, 닫히기 전 수신한 패킷은 처리나 확인 없이 버립니다.
        /// TCP 리셋으로 마지막 제어 패킷이 유실되지 않도록 EOF까지 읽은 다음 반환합니다.</remarks>
        /// <param name="reasonCode">0은 정상 종료, 4는 Will 발행을 요청하는 종료입니다. 0이 아닌 값은 MQTT 5에서만 허용됩니다.</param>
        /// <param name="properties">Session Expiry Interval, Reason String, User Property입니다. 만료가 0이던 세션을 여기서 연장할 수는 없습니다.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="reasonCode"/>가 허용되지 않는 값인 경우.</exception>
        /// <exception cref="ArgumentException">속성이 잘못되었거나 만료 0 세션의 만료를 늘리려 한 경우.</exception>
        /// <exception cref="InvalidOperationException">연결되지 않았거나 재진입한 경우.</exception>
        /// <exception cref="TimeoutException">제한 시간 안에 브로커가 연결을 닫지 않은 경우. 연결은 Faulted가 됩니다.</exception>
        public void Disconnect(byte reasonCode, MqttProperty[] properties)
        {
            byte[] encoded = PacketProperties(properties, MqttPropertyContext.Disconnect);
            if (reasonCode != 0 && (protocolLevel != 5 || !ValidReason(MqttPacketType.Disconnect, reasonCode)))
                throw new ArgumentOutOfRangeException("reasonCode");
            if (protocolLevel == 5 && sessionExpiry == 0 && Number(properties, MqttPropertyId.SessionExpiryInterval, 0) != 0)
                throw new ArgumentException("Cannot extend a zero-expiry MQTT session at DISCONNECT.");
            Enter();
            bool success = false;
            try
            {
                long deadline = channel.Milliseconds + operationTimeout;
                if (protocolLevel == 5 && (reasonCode != 0 || encoded.Length != 0))
                {
                    outgoing.Reset(); outgoing.Byte(reasonCode); WriteProperties(encoded);
                    Send(0xe0, outgoing.Bytes, outgoing.Count, deadline);
                }
                else Send(0xe0, Empty, 0, deadline);
                // 읽지 않은 데이터가 남은 소켓을 닫으면 TCP 리셋으로 마지막 제어 패킷이 유실될 수 있습니다.
                // DISCONNECT 이후에는 송신이 금지되므로 처리나 확인 응답 없이 남은 데이터를 비웁니다.
                byte[] drain = new byte[MaximumBody];
                while (true)
                {
                    int timeout = (int)(deadline - channel.Milliseconds), count;
                    if (timeout <= 0 || !channel.TryRead(drain, 0, drain.Length, timeout, out count))
                        throw new TimeoutException("MQTT DISCONNECT close deadline.");
                    if (count == 0) break;
                    if (count < 0 || count > drain.Length) throw new IOException("MQTT channel count.");
                }
                if (protocolLevel == 5) { sessionExpiry = Number(properties, MqttPropertyId.SessionExpiryInterval, sessionExpiry); persistent = sessionExpiry != 0; }
                connected = false;
                if (!persistent) session.Clear();
                ReleaseSession(); success = true;
            }
            finally { active = false; if (!success) Fault(); }
        }
        /// <summary>아무것도 송신하지 않고 연결을 포기한 뒤 RAM 세션 점유를 해제합니다.</summary>
        /// <remarks>이후 <see cref="Faulted"/>가 <see langword="true"/>가 되며, 전송 계층은 호출자가 닫아야 합니다.
        /// 지속 세션의 미완료 QoS 핸드셰이크는 세션에 남아 재연결 시 이어서 처리됩니다(비지속 세션은 비웁니다).</remarks>
        /// <exception cref="InvalidOperationException">진행 중인 작업이나 수신 콜백 안에서 호출한 경우.</exception>
        public void Abort()
        {
            if (active) throw new InvalidOperationException("MQTT reentrant abort.");
            started = true; Fault();
        }
        private void Enter()
        {
            if (!Connected || active) throw new InvalidOperationException("MQTT disconnected, faulted or reentrant.");
            active = true;
        }
        private void Fault()
        {
            faulted = true; connected = false;
            if (ownsSession && !persistent) session.Clear();
            ReleaseSession();
        }
        private void ReleaseSession()
        {
            lock (session)
            {
                if (ownsSession)
                {
                    session.Owner = null; ownsSession = false;
                    if (persistent && protocolLevel == 5 && sessionExpiry != uint.MaxValue)
                        session.ExpiresAt = Environment.TickCount64 + (long)sessionExpiry * 1000;
                }
            }
        }
        private ushort Identifier() { session.Identifier++; if (session.Identifier == 0) session.Identifier++; return session.Identifier; }
        private byte[] CaptureOutgoing()
        {
            var result = new byte[outgoing.Count];
            Array.Copy(outgoing.Bytes, 5, result, 0, result.Length);
            return result;
        }
        private void Replay(byte header, byte[] body, long deadline)
        {
            outgoing.Reset(); outgoing.Raw(body);
            Send(header, outgoing.Bytes, outgoing.Count, deadline);
        }
        private void ResumePublication()
        {
            long deadline = channel.Milliseconds + operationTimeout;
            if (session.OutgoingStage == 3) Acknowledge(0x62, session.OutgoingId, deadline);
            else Replay((byte)(session.OutgoingHeader | 8), session.OutgoingBody, deadline);
            CompletePublication(deadline);
        }
        private void CompletePublication(long deadline)
        {
            ushort id = session.OutgoingId;
            if (session.OutgoingStage == 1)
                Wait(0x40, id, deadline, session.OutgoingBody, (byte)(session.OutgoingHeader | 8));
            else
            {
                if (session.OutgoingStage == 2)
                {
                    Wait(0x50, id, deadline, session.OutgoingBody, (byte)(session.OutgoingHeader | 8));
                    session.OutgoingStage = 3;
                    Acknowledge(0x62, id, channel.Milliseconds + operationTimeout);
                }
                Wait(0x70, id, channel.Milliseconds + operationTimeout, new[] { (byte)(id >> 8), (byte)id }, 0x62);
            }
            session.Complete(session.OutgoingStage == 1 ? (byte)0x40 : (byte)0x70, id);
            session.ClearOutgoing();
        }
        private Packet Wait(byte header, ushort id, long deadline, byte[] retryBody = null, byte retryHeader = 0)
        {
            int retries = acknowledgmentRetries;
            while (true)
            {
                Packet packet;
                KeepAlive();
                long firstDeadline = deadline;
                if (keepAliveMilliseconds != 0)
                {
                    long keepAliveDeadline = pingPending ? pingSent + operationTimeout : lastSent + keepAliveMilliseconds / 2;
                    if (keepAliveDeadline < firstDeadline) firstDeadline = keepAliveDeadline;
                }
                try { packet = Receive(firstDeadline, false, deadline); }
                catch (ResponseTimeoutException)
                {
                    if (channel.Milliseconds < deadline) continue;
                    if (retryBody == null || retries-- == 0) throw;
                    deadline = channel.Milliseconds + operationTimeout;
                    Replay(retryHeader, retryBody, deadline);
                    continue;
                }
                if (packet.Header == header)
                {
                    if (packet.Bytes.Length >= 2 && (packet.Bytes[0] << 8 | packet.Bytes[1]) != id &&
                        ProcessCompleted(packet, deadline)) continue;
                    if (packet.Bytes.Length < 2 || (packet.Bytes[0] << 8 | packet.Bytes[1]) != id)
                        throw new IOException("MQTT acknowledgment ID/length mismatch.");
                    if (header != 0x90 && !(header == 0xb0 && protocolLevel == 5)) ValidateAcknowledgment(packet, true);
                    return packet;
                }
                if (header == 0x70 && packet.Header == 0x50 && packet.Bytes.Length >= 2 &&
                    (packet.Bytes[0] << 8 | packet.Bytes[1]) == id)
                { ValidateAcknowledgment(packet, true); Acknowledge(0x62, id, deadline); continue; }
                Process(packet, deadline);
            }
        }
        private void KeepAlive()
        {
            long now = channel.Milliseconds;
            if (pingPending && now - pingSent >= operationTimeout) throw new TimeoutException("MQTT PINGRESP deadline.");
            if (keepAliveMilliseconds != 0 && !pingPending && now - lastSent >= keepAliveMilliseconds / 2)
            {
                Send(0xc0, Empty, 0, now + operationTimeout);
                pingPending = true; pingSent = channel.Milliseconds;
            }
        }
        private void Process(Packet packet, long deadline)
        {
            if (protocolLevel == 5 && packet.Header == 0xe0) ServerDisconnect(packet);
            if (protocolLevel == 5 && packet.Header == 0xf0) { HandleAuthentication(packet, deadline, true); return; }
            if (ProcessCompleted(packet, deadline)) return;
            if (packet.Header == 0xd0)
            {
                if (packet.Bytes.Length != 0 || !pingPending) throw new IOException("MQTT unexpected PINGRESP.");
                pingPending = false; return;
            }
            if (packet.Header == 0x62)
            {
                byte reason = ValidateAcknowledgment(packet, false);
                ushort releaseId = (ushort)(packet.Bytes[0] << 8 | packet.Bytes[1]);
                if (releaseId == 0) throw new IOException("MQTT zero PUBREL identifier.");
                bool found = false;
                for (int i = 0; i < session.Incoming.Length; i++)
                    if (session.Incoming[i] != null && session.Incoming[i].Id == releaseId)
                    {
                        var pending = session.Incoming[i];
                        if (reason < 128) ProcessStored(pending);
                        session.Incoming[i] = null;
                        found = true;
                        break;
                    }
                Acknowledge(0x70, releaseId, deadline, protocolLevel == 5 && !found ? (byte)146 : (byte)0);
                return;
            }
            if ((packet.Header >> 4) != 3) throw new IOException("MQTT unsupported/unexpected packet.");
            int qos = (packet.Header >> 1) & 3;
            if (qos > 2 || qos == 0 && (packet.Header & 8) != 0) throw new IOException("MQTT PUBLISH QoS/flags.");
            byte[] bytes = packet.Bytes;
            if (bytes.Length < 2) throw new IOException("MQTT topic length.");
            int length = bytes[0] << 8 | bytes[1], position = length + 2;
            if (length == 0 && protocolLevel != 5 || length > maximumNameBytes || position > bytes.Length) throw new IOException("MQTT topic bound.");
            if (!ValidUtf8(bytes, 2, length)) throw new IOException("MQTT invalid UTF-8 topic.");
            string topic = Encoding.UTF8.GetString(bytes, 2, length);
            if (topic.IndexOf('+') >= 0 || topic.IndexOf('#') >= 0) throw new IOException("MQTT topic wildcard.");
            ushort id = 0;
            if (qos != 0)
            {
                if (position + 2 > bytes.Length) throw new IOException("MQTT missing packet ID.");
                id = (ushort)(bytes[position] << 8 | bytes[position + 1]); position += 2;
                if (id == 0) throw new IOException("MQTT zero packet ID.");
            }
            MqttProperty[] properties = protocolLevel == 5 ? ParseProperties(bytes, ref position, MqttPropertyContext.Publish) : NoProperties;
            if (protocolLevel == 5)
            {
                var alias = Find(properties, MqttPropertyId.TopicAlias);
                if (alias != null)
                {
                    if (alias.Number > inboundAliases.Length) throw new IOException("MQTT topic alias exceeds advertised maximum.");
                    int slot = (int)alias.Number - 1;
                    if (length != 0) inboundAliases[slot] = topic;
                    else topic = inboundAliases[slot];
                }
                if (topic == null || topic.Length == 0) throw new IOException("MQTT empty/unregistered topic alias.");
            }
            if (bytes.Length - position > maximumPayload) throw new IOException("MQTT payload bound.");
            packet.Topic = topic; packet.PayloadOffset = position; packet.Properties = properties;
            if (qos == 2)
            {
                int slot = -1, occupied = 0;
                for (int i = 0; i < session.Incoming.Length; i++)
                {
                    if (session.Incoming[i] != null && session.Incoming[i].Id == id)
                    {
                        var previous = session.Incoming[i];
                        if ((packet.Header & 8) == 0 || previous.Topic != topic ||
                            previous.Body.Length - previous.PayloadOffset != bytes.Length - position)
                            throw new IOException("MQTT reused QoS2 identifier.");
                        for (int j = 0; j < bytes.Length - position; j++)
                            if (previous.Body[previous.PayloadOffset + j] != bytes[position + j]) throw new IOException("MQTT changed QoS2 duplicate.");
                        // MQTT 5 재전송은 별칭·만료 속성을 바꿀 수 있으므로 보관한 원본 메타데이터를 한 번만 전달합니다.
                        Acknowledge(0x50, id, deadline); return;
                    }
                    if (session.Incoming[i] == null) slot = i;
                    else occupied++;
                }
                if (slot < 0 || protocolLevel == 5 && occupied >= inboundReceiveMaximum) throw new IOException("MQTT inbound QoS2 capacity exceeded.");
                session.Incoming[slot] = new MqttPendingPublication { Id = id, Header = packet.Header, Body = bytes,
                    Topic = topic, PayloadOffset = position, Properties = properties };
                Acknowledge(0x50, id, deadline); return;
            }
            if (protocolLevel == 5 && qos == 1)
            {
                int occupied = 0;
                foreach (var pending in session.Incoming) if (pending != null) occupied++;
                if (occupied >= inboundReceiveMaximum) throw new IOException("MQTT inbound Receive Maximum exceeded.");
            }
            Deliver(packet);
            if (qos == 1) Acknowledge(0x40, id, deadline);
        }
        private bool ProcessCompleted(Packet packet, long deadline)
        {
            byte header = packet.Header;
            if (header != 0x40 && header != 0x50 && header != 0x70 && header != 0x90 && header != 0xb0) return false;
            if (packet.Bytes.Length < 2) return false;
            ushort id = (ushort)(packet.Bytes[0] << 8 | packet.Bytes[1]);
            if (id == 0) return false;
            for (int i = 0; i < session.CompletedIds.Length; i++)
                if (session.CompletedIds[i] == id && (session.CompletedHeaders[i] == header ||
                    header == 0x50 && session.CompletedHeaders[i] == 0x70))
                {
                    if (header == 0x90 || header == 0xb0 && protocolLevel == 5)
                    {
                        int position = AckProperties(packet);
                        if (position == packet.Bytes.Length) throw new IOException("MQTT duplicate acknowledgment missing reasons.");
                        var original = session.CompletedReasons[i];
                        if (original == null || original.Length != packet.Bytes.Length - position)
                            throw new IOException("MQTT duplicate acknowledgment reason count changed.");
                        for (int j = 0; j < original.Length; j++)
                            if (packet.Bytes[position + j] != original[j]) throw new IOException("MQTT duplicate acknowledgment reason changed.");
                        for (; position < packet.Bytes.Length; position++)
                            if (!ValidReason((MqttPacketType)(header >> 4), packet.Bytes[position])) throw new IOException("MQTT duplicate acknowledgment reason.");
                    }
                    else if (ValidateAcknowledgment(packet, false) >= 128) return true;
                    if (header == 0x50) Acknowledge(0x62, id, deadline);
                    return true;
                }
            return false;
        }
        private void ProcessStored(MqttPendingPublication pending)
        {
            Deliver(new Packet { Header = pending.Header, Bytes = pending.Body,
                Topic = pending.Topic, PayloadOffset = pending.PayloadOffset, Properties = pending.Properties });
        }
        private void Deliver(Packet packet)
        {
            byte[] bytes = packet.Bytes;
            int position = packet.PayloadOffset;
            byte[] payload = new byte[bytes.Length - position];
            Array.Copy(bytes, position, payload, 0, payload.Length);
            if (MessageReceived != null)
                MessageReceived(new MqttMessage { Topic = packet.Topic, Payload = payload,
                    Qos = (MqttQos)((packet.Header >> 1) & 3), Retained = (packet.Header & 1) != 0,
                    Duplicate = (packet.Header & 8) != 0, Properties = packet.Properties ?? NoProperties });
            else
            {
                if (Received == null) throw new InvalidOperationException("MQTT publication handler required.");
                Received(packet.Topic, payload, (packet.Header & 1) != 0, (packet.Header & 8) != 0);
            }
        }
        private void Acknowledge(byte header, ushort id, long deadline, byte reason = 0)
        {
            acknowledgment[0] = (byte)(id >> 8); acknowledgment[1] = (byte)id;
            acknowledgment[2] = reason;
            Send(header, acknowledgment, reason == 0 ? 2 : 3, deadline);
        }
        private void Send(byte header, byte[] body, int count, long deadline)
        {
            bool data = body == outgoing.Bytes;
            byte[] packet = data ? body : controlBuffer;
            int headerSize = 1 + VariableSize(count);
            if (count + headerSize > serverPacketSize) throw new IOException("MQTT negotiated server Maximum Packet Size exceeded.");
            int offset = data ? 5 - headerSize : 0;
            packet[offset] = header;
            int remaining = count, position = offset + 1;
            do
            {
                byte digit = (byte)(remaining % 128); remaining /= 128;
                packet[position++] = (byte)(digit | (remaining != 0 ? 128 : 0));
            } while (remaining != 0);
            if (!data) Array.Copy(body, 0, packet, position, count);
            int timeout = (int)(deadline - channel.Milliseconds);
            if (timeout <= 0) throw new TimeoutException("MQTT operation deadline.");
            IMqttSegmentChannel segmented = channel as IMqttSegmentChannel;
            if (segmented != null) segmented.Write(packet, offset, headerSize + count, timeout);
            else
            {
                if (fallbackSendBuffer == null) fallbackSendBuffer = new byte[maximumBody + 5];
                Array.Copy(packet, offset, fallbackSendBuffer, 0, headerSize + count);
                channel.Write(fallbackSendBuffer, headerSize + count, timeout);
            }
            lastSent = channel.Milliseconds;
        }
        private Packet Receive(long deadline, bool idleAllowed, long packetDeadline = 0)
        {
            int timeout = (int)(deadline - channel.Milliseconds), count;
            if (timeout <= 0)
            {
                if (idleAllowed) return null;
                throw new ResponseTimeoutException();
            }
            if (!channel.TryRead(single, 0, 1, timeout, out count))
            {
                if (idleAllowed) return null;
                throw new ResponseTimeoutException();
            }
            if (count != 1) throw new IOException("MQTT TCP EOF.");
            byte header = single[0];
            if (packetDeadline != 0) deadline = packetDeadline;
            else if (idleAllowed) deadline = channel.Milliseconds + operationTimeout;
            int remaining = 0, multiplier = 1, digits = 0;
            byte digit;
            do
            {
                Read(single, 0, 1, deadline); digit = single[0]; digits++;
                remaining += (digit & 127) * multiplier;
                if (remaining > maximumBody || digits == 4 && (digit & 128) != 0) throw new IOException("MQTT remaining length bound.");
                multiplier *= 128;
            } while ((digit & 128) != 0);
            if (digits > 1 && digit == 0) throw new IOException("MQTT nonminimal remaining length.");
            if (protocolLevel == 5 && remaining + digits + 1 > inboundPacketSize)
                throw new IOException("MQTT advertised Maximum Packet Size exceeded.");
            byte[] body = new byte[remaining];
            Read(body, 0, remaining, deadline);
            return new Packet { Header = header, Bytes = body };
        }
        private void Read(byte[] bytes, int offset, int count, long deadline)
        {
            while (count != 0)
            {
                int timeout = (int)(deadline - channel.Milliseconds), received;
                if (timeout <= 0 || !channel.TryRead(bytes, offset, count, timeout, out received))
                    throw new TimeoutException("MQTT partial packet deadline.");
                if (received <= 0 || received > count) throw new IOException("MQTT EOF/channel count.");
                offset += received; count -= received;
            }
        }
        private sealed class Packet { internal byte Header; internal byte[] Bytes; internal string Topic; internal int PayloadOffset; internal MqttProperty[] Properties; }
        private sealed class ResponseTimeoutException : TimeoutException
        { internal ResponseTimeoutException() : base("MQTT response deadline; delivery may be uncertain.") { } }
        private static void Name(string name, bool filter, int limit = 128)
        {
            Text(name, false, limit);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!filter && (c == '+' || c == '#') ||
                    filter && (c == '+' && (i > 0 && name[i - 1] != '/' || i + 1 < name.Length && name[i + 1] != '/') ||
                    c == '#' && (i != name.Length - 1 || i > 0 && name[i - 1] != '/')))
                    throw new ArgumentException("MQTT name/filter.");
            }
        }
        private static void Text(string text, bool emptyAllowed, int limit = 128)
        {
            if (text == null || !emptyAllowed && text.Length == 0 || Utf8Length(text) > limit) throw new ArgumentException("MQTT UTF-8 text length.");
            if (NativeUtf8Strings)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == 0) throw new ArgumentException("MQTT forbidden Unicode.");
                    if (text[i] >= 128)
                    {
                        byte[] encoded = Encoding.UTF8.GetBytes(text);
                        if (!ValidUtf8(encoded, 0, encoded.Length)) throw new ArgumentException("MQTT forbidden Unicode.");
                        return;
                    }
                }
                return;
            }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == 0 || c >= 0xfdd0 && c <= 0xfdef || c >= 0xfffe) throw new ArgumentException("MQTT forbidden Unicode.");
                if (c >= 0xd800 && c <= 0xdbff)
                {
                    if (++i == text.Length || text[i] < 0xdc00 || text[i] > 0xdfff) throw new ArgumentException("MQTT UTF-16 surrogate.");
                    if ((((c - 0xd800) << 10 | text[i] - 0xdc00) & 65535) >= 65534)
                        throw new ArgumentException("MQTT Unicode noncharacter.");
                }
                else if (c >= 0xdc00 && c <= 0xdfff) throw new ArgumentException("MQTT UTF-16 surrogate.");
            }
        }
        private static int Utf8Length(string value)
        {
            int length = 0;
            foreach (char c in value)
            {
                if (NativeUtf8Strings && c >= 128) return Encoding.UTF8.GetBytes(value).Length;
                length += c < 128 ? 1 : c < 2048 ? 2 : c >= 0xdc00 && c <= 0xdfff ? 1 : 3;
            }
            return length;
        }
        private static bool ValidUtf8(byte[] bytes, int offset, int count)
        {
            int end = offset + count;
            while (offset < end)
            {
                int c = bytes[offset++], following, value, minimum;
                if (c < 128) { if (c == 0) return false; continue; }
                if (c >= 194 && c <= 223) { following = 1; value = c & 31; minimum = 128; }
                else if (c >= 224 && c <= 239) { following = 2; value = c & 15; minimum = 2048; }
                else if (c >= 240 && c <= 244) { following = 3; value = c & 7; minimum = 65536; }
                else return false;
                if (end - offset < following) return false;
                while (following-- > 0) { int b = bytes[offset++]; if ((b & 192) != 128) return false; value = value << 6 | b & 63; }
                if (value < minimum || value > 0x10ffff || value >= 0xd800 && value <= 0xdfff ||
                    value >= 0xfdd0 && value <= 0xfdef || (value & 65535) >= 65534) return false;
            }
            return true;
        }
        private sealed class Body
        {
            private readonly byte[] bytes;
            private int count = 5;
            internal Body(int capacity) { bytes = new byte[capacity + 5]; }
            internal byte[] Bytes { get { return bytes; } }
            internal int Count { get { return count - 5; } }
            internal void Reset() { count = 5; }
            internal void Byte(byte value) { if (count == bytes.Length) throw new ArgumentException("MQTT packet bound."); bytes[count++] = value; }
            internal void Word(ushort value) { Byte((byte)(value >> 8)); Byte((byte)value); }
            internal void Variable(int value)
            {
                do { byte digit = (byte)(value & 127); value >>= 7; Byte((byte)(digit | (value != 0 ? 128 : 0))); } while (value != 0);
            }
            internal void Raw(byte[] value)
            {
                if (value.Length > bytes.Length - count) throw new ArgumentException("MQTT packet bound.");
                Array.Copy(value, 0, bytes, count, value.Length); count += value.Length;
            }
            internal void Binary(byte[] value) { Word((ushort)value.Length); Raw(value); }
            internal void Text(string value)
            {
                int length = Utf8Length(value); Word((ushort)length);
                if (length == value.Length) foreach (char c in value) Byte((byte)c);
                else Raw(Encoding.UTF8.GetBytes(value));
            }
        }
    }
}
