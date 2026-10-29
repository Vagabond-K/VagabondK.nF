using System;
using System.IO;
using System.Net.Sockets;

namespace VagabondK.nF.DNP3.TCP
{
    /// <summary>연결된 TCP 소켓에서 한 DNP3 Endpoint의 송수신과 제한 시간 만료 처리를 진행합니다.</summary>
    /// <remarks>소켓과 Endpoint는 호출자가 소유하고 수명을 관리하며, 이 클래스는 연결을 생성하거나 닫지 않습니다.
    /// 호출자는 모든 호출과 Endpoint 접근을 하나의 실행 흐름으로 직렬화해야 합니다.
    /// 내부 시각은 Environment.TickCount64이므로 직접 시작한 Master 요청도 같은 시각 기준을 사용해야 합니다.</remarks>
    public sealed class Dnp3TcpSession
    {
        private readonly Socket _socket;
        private readonly IDnp3Endpoint _endpoint;
        private readonly Dnp3StreamDecoder _decoder;
        private readonly byte[] _input = new byte[292];
        private readonly int _writeTimeout;

        /// <summary>송수신 또는 Endpoint 처리 중 예외가 발생하여 세션을 더 사용할 수 없으면 true입니다.</summary>
        public bool Faulted { get; private set; }

        /// <summary>이 세션의 Frame Decoder입니다. 진단 카운터 확인 용도로 사용하고 세션과 동시에 조작하지 않습니다.</summary>
        public Dnp3StreamDecoder Decoder
        {
            get
            {
                return _decoder;
            }
        }

        /// <summary>호출자 소유의 연결된 소켓과 Endpoint에 세션을 연결합니다.</summary>
        /// <param name="socket">이미 연결된 TCP 소켓입니다. SendTimeout/ReceiveTimeout은 세션이 변경합니다.</param>
        /// <param name="endpoint">전용으로 사용할 Master 또는 Outstation Endpoint입니다.</param>
        /// <param name="frameTimeoutMilliseconds">부분 Link Frame 수신 제한 시간입니다. 1밀리초 이상입니다.</param>
        /// <param name="writeTimeoutMilliseconds">한 전송 배열 전체를 보내는 제한 시간입니다. 1밀리초 이상입니다.</param>
        /// <exception cref="ArgumentNullException">소켓 또는 Endpoint가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">제한 시간이 1밀리초 미만입니다.</exception>
        public Dnp3TcpSession(Socket socket, IDnp3Endpoint endpoint, int frameTimeoutMilliseconds = 1000,
            int writeTimeoutMilliseconds = 1000)
        {
            if (socket == null || endpoint == null)
                throw new ArgumentNullException("dependency");
            if (writeTimeoutMilliseconds < 1)
                throw new ArgumentOutOfRangeException("writeTimeoutMilliseconds");
            _socket = socket;
            _endpoint = endpoint;
            _writeTimeout = writeTimeoutMilliseconds;
            _decoder = new Dnp3StreamDecoder(frameTimeoutMilliseconds);
        }

        private void Healthy()
        {
            if (Faulted)
                throw new InvalidOperationException("Replace a faulted DNP3 TCP session and reset its endpoint before reconnecting.");
        }

        private void Write(byte[] bytes)
        {
            if (bytes == null)
                return;
            long deadline = Environment.TickCount64 + _writeTimeout;
            for (int p = 0; p < bytes.Length;)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    throw new TimeoutException("DNP3 TCP send deadline.");
                _socket.SendTimeout = (int)remaining;
                int sent = _socket.Send(bytes, p, bytes.Length - p, SocketFlags.None);
                if (sent == 0)
                    throw new IOException("DNP3 TCP send made no progress.");
                p += sent;
            }
        }

        /// <summary>Endpoint가 반환한 Frame 배열 전체를 순서대로 전송합니다.</summary>
        /// <param name="frames">전송할 Frame들의 연결된 배열입니다. null이면 전송하지 않습니다.</param>
        /// <remarks>바이트의 DNP3 형식은 검사하지 않습니다. 전송 실패 시 Faulted가 설정되며 일부 바이트가 이미 전송되었을 수 있습니다.</remarks>
        /// <exception cref="InvalidOperationException">세션이 이미 Faulted 상태입니다.</exception>
        /// <exception cref="TimeoutException">전체 전송 제한 시간이 만료되었습니다.</exception>
        /// <exception cref="IOException">소켓 전송이 진행되지 않습니다.</exception>
        /// <exception cref="SocketException">소켓 송신이 실패했습니다.</exception>
        public void Send(byte[] frames)
        {
            Healthy();
            try
            {
                Write(frames);
            }
            catch
            {
                Faulted = true;
                throw;
            }
        }

        private void Drain(byte[] frame)
        {
            while (frame != null)
            {
                Write(_endpoint.Process(frame, Environment.TickCount64));
                frame = _decoder.Take(Environment.TickCount64);
            }
        }

        /// <summary>한 번의 수신 확인, Frame 처리 및 Endpoint Poll을 수행합니다.</summary>
        /// <param name="waitMilliseconds">소켓 읽기 준비를 기다리는 시간입니다. 0~1000밀리초입니다.</param>
        /// <remarks>대기 시간은 전체 메서드 실행 시간의 상한이 아닙니다. 이후 수신에는 1000밀리초의 제한 시간을 설정하며,
        /// 송신 및 처리기 실행 시간이 추가됩니다. 인수 검사 이후 처리 중 예외는 Faulted를 설정한 뒤 전파합니다.
        /// Faulted 세션은 교체하고 Endpoint를 ResetConnection한 후 다시 연결해야 합니다.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">대기 시간이 0~1000 범위를 벗어납니다.</exception>
        /// <exception cref="InvalidOperationException">세션이 이미 Faulted 상태입니다.</exception>
        /// <exception cref="IOException">상대가 연결을 닫았거나 송신이 진행되지 않습니다.</exception>
        /// <exception cref="SocketException">소켓 송수신이 실패했습니다.</exception>
        /// <exception cref="TimeoutException">송신 또는 Endpoint의 통신 Timeout이 발생했습니다.</exception>
        /// <exception cref="Dnp3ProtocolException">Endpoint가 수신 메시지를 거부했습니다.</exception>
        public void RunOnce(int waitMilliseconds = 20)
        {
            Healthy();
            if (waitMilliseconds < 0 || waitMilliseconds > 1000)
                throw new ArgumentOutOfRangeException("waitMilliseconds");
            try
            {
                if (_socket.Poll(waitMilliseconds * 1000, SelectMode.SelectRead))
                {
                    _socket.ReceiveTimeout = 1000;
                    int read = _socket.Receive(_input, 0, _input.Length, SocketFlags.None);
                    if (read == 0)
                        throw new IOException("DNP3 TCP peer closed.");
                    for (int i = 0; i < read; i++)
                        Drain(_decoder.Push(_input[i], Environment.TickCount64));
                }
                Drain(_decoder.Take(Environment.TickCount64));
                Write(_endpoint.Poll(Environment.TickCount64));
            }
            catch
            {
                Faulted = true;
                throw;
            }
        }
    }
}
