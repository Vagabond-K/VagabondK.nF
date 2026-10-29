using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace VagabondK.nF.SNMP.UDP
{
    /// <summary>호출자가 제공한 UDP 소켓으로 SNMP Agent 요청 처리와 Trap 전송을 수행합니다.</summary>
    /// <remarks>소켓은 호출자 소유이며 이 클래스는 소켓을 닫지 않습니다. 세션 호출과 공유 Agent에 대한 접근은 호출자가 직렬화해야 합니다.</remarks>
    public sealed class SnmpUdpSession
    {
        private readonly Socket _socket;
        private readonly SnmpAgent _agent;
        private readonly byte[] _buffer = new byte[SnmpAgent.MaximumMessage + 1];

        /// <summary>기존 UDP 소켓과 SNMP Agent를 연결합니다.</summary>
        /// <param name="socket">호출자가 로컬 주소에 바인딩하고 사용 중 열어 둘 IPv4 UDP 소켓입니다. Agent는 일반적으로 포트 161을 사용합니다.</param>
        /// <param name="agent">메시지를 처리하고 Trap을 생성할 Agent입니다.</param>
        /// <exception cref="ArgumentNullException">소켓 또는 Agent가 <see langword="null"/>입니다.</exception>
        public SnmpUdpSession(Socket socket, SnmpAgent agent)
        {
            if (socket == null || agent == null)
                throw new ArgumentNullException("dependency");
            _socket = socket;
            _agent = agent;
        }

        /// <summary>데이터그램 하나를 수신하여 처리하고 응답이 있으면 송신자에게 전송합니다.</summary>
        /// <param name="timeoutMilliseconds">수신 준비를 기다릴 제한 시간입니다. 범위는 0~60000밀리초이며, 0이면 즉시 검사합니다.</param>
        /// <returns>폐기 대상 메시지를 포함하여 데이터그램을 처리하면 <see langword="true"/>, 수신 준비를 기다리는 중 제한 시간이 만료되면 <see langword="false"/>입니다.</returns>
        /// <exception cref="ArgumentOutOfRangeException">제한 시간이 허용 범위를 벗어납니다.</exception>
        /// <exception cref="SnmpProtocolException">이미 수신한 데이터그램이 최대 크기를 초과하거나 형식이 유효하지 않습니다.</exception>
        /// <exception cref="InvalidOperationException">저장소가 정의와 다른 형식 또는 null 값을 반환했습니다.</exception>
        /// <exception cref="IOException">응답 전체를 전송하지 못했습니다.</exception>
        /// <exception cref="SocketException">소켓 수신 또는 전송에 실패했습니다.</exception>
        /// <remarks>수신 버퍼는 최대 메시지 길이보다 1바이트 큽니다. 지정한 제한 시간은 전체 처리 시간의 상한이 아닙니다. 호출을 직렬화해야 합니다.</remarks>
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
                throw new SnmpProtocolException();
            }
            if (count > SnmpAgent.MaximumMessage)
                throw new SnmpProtocolException();
            var request = new byte[count];
            Array.Copy(_buffer, request, count);
            byte[] reply = _agent.Process(request);
            if (reply != null)
                Send(reply, peer);
            return true;
        }

        /// <summary>확인 응답을 기다리지 않는 SNMPv2c Trap을 Manager에 전송합니다.</summary>
        /// <param name="manager">전송 대상 EndPoint입니다. UDP Trap 수신에는 일반적으로 포트 162를 사용합니다.</param>
        /// <param name="community">공백 없는 ASCII 33~126의 1~32자 community입니다.</param>
        /// <param name="trapOid">snmpTrapOID.0 값으로 사용할 유효한 OID입니다.</param>
        /// <param name="objectIndexes">추가할 저장소 객체의 인덱스 목록(최대 256개)입니다. null이면 추가 객체가 없습니다.</param>
        /// <exception cref="ArgumentNullException">manager, community 또는 trapOid가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">community/OID가 유효하지 않거나 Trap 크기 또는 객체 수가 제한을 초과합니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">객체 인덱스가 저장소 범위를 벗어납니다.</exception>
        /// <exception cref="InvalidOperationException">저장소가 정의와 다른 형식 또는 null 값을 반환했습니다.</exception>
        /// <exception cref="IOException">Trap 전체를 전송하지 못했습니다.</exception>
        /// <exception cref="SocketException">소켓 전송에 실패했습니다.</exception>
        /// <remarks>다른 세션 호출과 직렬화해야 합니다. 전송 성공은 Manager의 수신이나 처리를 보장하지 않으며 자동 재전송은 하지 않습니다.</remarks>
        public void SendTrap(EndPoint manager, string community, uint[] trapOid, int[] objectIndexes)
        {
            if (manager == null)
                throw new ArgumentNullException("manager");
            Send(_agent.CreateTrap(community, trapOid, objectIndexes), manager);
        }

        private void Send(byte[] datagram, EndPoint peer)
        {
            if (_socket.SendTo(datagram, peer) != datagram.Length)
                throw new IOException("Incomplete SNMP datagram send.");
        }
    }
}
