using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace VagabondK.nF.BACnet.IP
{
    /// <summary>호출자가 제공한 UDP 소켓으로 BACnet/IP 요청과 응답을 처리합니다.</summary>
    /// <remarks>소켓의 소유권은 호출자에게 있으며 이 클래스는 소켓을 닫지 않습니다. 호출자는 ProcessNext 호출과 공유 장치에 대한 접근을 직렬화해야 합니다.</remarks>
    public sealed class BacnetIpSession
    {
        private readonly Socket _socket;
        private readonly BacnetDevice _device;
        private readonly byte[] _buffer = new byte[BacnetDevice.MaximumDatagram + 1];

        /// <summary>기존 UDP 소켓과 BACnet 장치를 연결합니다.</summary>
        /// <param name="socket">호출자가 로컬 주소에 바인딩하고 세션 사용 동안 열어 둘 IPv4 UDP 소켓입니다.</param>
        /// <param name="device">요청을 처리할 장치입니다.</param>
        /// <exception cref="ArgumentNullException">소켓 또는 장치가 <see langword="null"/>입니다.</exception>
        public BacnetIpSession(Socket socket, BacnetDevice device)
        {
            if (socket == null || device == null)
                throw new ArgumentNullException("dependency");
            _socket = socket;
            _device = device;
        }

        /// <summary>데이터그램 하나를 수신하고 응답이 있으면 송신자에게 전송합니다.</summary>
        /// <param name="timeoutMilliseconds">수신 준비를 기다릴 제한 시간입니다. 범위는 0~60000밀리초이며, 0이면 즉시 검사합니다.</param>
        /// <returns>데이터그램을 처리하면 <see langword="true"/>, 수신 준비를 기다리는 중 제한 시간이 만료되면 <see langword="false"/>입니다.</returns>
        /// <exception cref="ArgumentOutOfRangeException">제한 시간이 허용 범위를 벗어납니다.</exception>
        /// <exception cref="BacnetProtocolException">수신한 데이터그램이 너무 크거나 지원하지 않는 형식입니다.</exception>
        /// <exception cref="IOException">응답 전체를 전송하지 못했습니다.</exception>
        /// <exception cref="SocketException">소켓 수신 또는 전송에 실패했습니다.</exception>
        /// <remarks>내부 수신 버퍼는 최대 데이터그램 길이보다 1바이트 큽니다. 잘못된 데이터그램을 이미 수신한 뒤 예외가 발생할 수 있습니다. 지정한 제한 시간은 전체 처리 시간의 상한이 아닙니다.</remarks>
        public bool ProcessNext(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 0 || timeoutMilliseconds > 60000)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            if (!_socket.Poll(timeoutMilliseconds * 1000, SelectMode.SelectRead))
                return false;
            EndPoint peer = new IPEndPoint(IPAddress.Any, 0);
            int count;
            try
            {
                count = _socket.ReceiveFrom(_buffer, ref peer);
            }
            catch (SocketException error) when (error.ErrorCode == (int)SocketError.MessageSize)
            {
                throw new BacnetProtocolException();
            }
            var request = new byte[count];
            Array.Copy(_buffer, request, count);
            byte[] reply = _device.Process(request);
            if (reply != null && _socket.SendTo(reply, peer) != reply.Length)
                throw new IOException("Incomplete BACnet datagram send.");
            return true;
        }
    }
}
