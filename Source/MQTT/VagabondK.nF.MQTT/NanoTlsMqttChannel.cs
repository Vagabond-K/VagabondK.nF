#if NANOFRAMEWORK
using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace VagabondK.nF.MQTT
{
    /// <summary>서버 인증서·호스트 검증을 강제하고 명시적 신뢰 루트를 쓰는 nanoFramework TLS(1.2/1.3) MQTT 채널입니다.</summary>
    /// <remarks>
    /// <para>인수 검증 후 TLS 스트림 생성에 성공하면 전달받은 소켓의 소유권을 가져갑니다.
    /// 이후 초기화 또는 인증이 실패하면 스트림과 소켓을 닫으며, 성공한 채널은 <see cref="Dispose"/>로 함께 닫습니다.</para>
    /// <para>인증서 유효 기간 검사를 위해 인증 전에 장치의 UTC 시계를 맞춰야 합니다. 클라이언트 인증서를 주면 상호 TLS를 사용합니다.
    /// TLS 1.3·상호 인증의 실제 가용성은 펌웨어와 서버에 의존합니다.
    /// nanoFramework 빌드(<c>NANOFRAMEWORK</c>)에서만 제공됩니다.</para>
    /// </remarks>
    public sealed class NanoTlsMqttChannel : IMqttSegmentChannel, IDisposable
    {
        private readonly Socket socket;
        private readonly SslStream stream;
        private readonly StreamMqttChannel channel;
        private bool disposed;
        /// <summary>연결된 소켓 위에서 TLS 클라이언트 인증을 수행합니다. 인증서 검증을 건너뛰는 옵션은 없습니다.</summary>
        /// <param name="socket">브로커에 연결된 TCP 소켓입니다. TLS 스트림 생성에 성공한 뒤에는 이 채널이 소유합니다.
        /// 인수 검증 또는 TLS 스트림 생성 자체가 실패하면 호출자가 정리해야 합니다.</param>
        /// <param name="targetHost">서버 인증서와 비교할 호스트 이름입니다.</param>
        /// <param name="authority">서버 인증서를 검증할 신뢰 루트(CA) 인증서입니다.</param>
        /// <param name="clientCertificate">상호 TLS용 클라이언트 인증서입니다. 없으면 <see langword="null"/>입니다.</param>
        /// <param name="timeoutMilliseconds">핸드셰이크와 기본 읽기/쓰기 제한 시간(밀리초), 1..60000입니다.</param>
        /// <param name="protocols">허용할 TLS 버전입니다. TLS 1.2와 1.3만 지정할 수 있습니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="socket"/> 또는 <paramref name="authority"/>가 <see langword="null"/>인 경우.</exception>
        /// <exception cref="ArgumentException"><paramref name="targetHost"/>가 비었거나 허용되지 않는 프로토콜을 지정한 경우.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMilliseconds"/>가 범위를 벗어난 경우.</exception>
        public NanoTlsMqttChannel(Socket socket, string targetHost, X509Certificate authority,
            X509Certificate clientCertificate = null, int timeoutMilliseconds = 10000,
            SslProtocols protocols = SslProtocols.Tls12)
        {
            if (socket == null || authority == null) throw new ArgumentNullException("socket/authority");
            if (targetHost == null || targetHost.Length == 0) throw new ArgumentException("targetHost");
            if (timeoutMilliseconds < 1 || timeoutMilliseconds > 60000) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            if (protocols == SslProtocols.None || (protocols & ~(SslProtocols.Tls12 | SslProtocols.Tls13)) != 0)
                throw new ArgumentException("Only TLS 1.2/1.3 are permitted.");
            this.socket = socket;
            stream = new SslStream(socket);
            bool success = false;
            try
            {
                stream.ReadTimeout = timeoutMilliseconds; stream.WriteTimeout = timeoutMilliseconds;
                // nanoFramework의 mbedTLS는 이 값을 비트 마스크가 아닌 단일 값으로 비교합니다.
                stream.SslVerification = SslVerification.CertificateRequired;
                stream.AuthenticateAsClient(targetHost, clientCertificate, authority, protocols);
                channel = new StreamMqttChannel(stream, Ready);
                success = true;
            }
            finally { if (!success) stream.Dispose(); }
        }
        private bool Ready(int timeoutMilliseconds)
        {
            if (disposed) throw new ObjectDisposedException("NanoTlsMqttChannel");
            return stream.DataAvailable || socket.Poll(timeoutMilliseconds * 1000, SelectMode.SelectRead);
        }
        /// <inheritdoc />
        public long Milliseconds { get { return Environment.TickCount64; } }
        /// <inheritdoc />
        public bool TryRead(byte[] bytes, int offset, int count, int timeoutMilliseconds, out int transferred)
        { return channel.TryRead(bytes, offset, count, timeoutMilliseconds, out transferred); }
        /// <inheritdoc />
        public void Write(byte[] bytes, int count, int timeoutMilliseconds)
        { channel.Write(bytes, count, timeoutMilliseconds); }
        /// <inheritdoc />
        public void Write(byte[] bytes, int offset, int count, int timeoutMilliseconds)
        { channel.Write(bytes, offset, count, timeoutMilliseconds); }
        /// <summary>소유한 TLS 스트림과 소켓을 닫습니다. 여러 번 호출해도 안전하지만, 클라이언트 작업과 동시에 호출하면 안 됩니다.</summary>
        public void Dispose()
        { if (!disposed) { disposed = true; stream.Dispose(); } }
    }
}
#endif
