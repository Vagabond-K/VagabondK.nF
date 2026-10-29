using System;

namespace VagabondK.nF.MMS
{
    /// <summary>완전히 수신한 TPKT를 동기적으로 처리합니다.</summary>
    /// <param name="packet">4바이트 헤더를 포함한 재사용 버퍼입니다. 수정하거나 보관하지 마십시오.</param>
    /// <param name="length">이번 패킷의 전체 길이(바이트)입니다.</param>
    public delegate void TpktReceived(byte[] packet, int length);

    /// <summary>TCP 스트림에서 RFC 1006 TPKT를 고정 용량 버퍼로 분리합니다.</summary>
    /// <remarks>소켓 소유권·시간 제한·COTP 상태는 관리하지 않습니다. 호출을 직렬화하고
    /// 콜백에서 재진입하지 마십시오. 오류 후에는 연결을 폐기하고 Reset을 호출해야 합니다.</remarks>
    public sealed class TpktDecoder
    {
        private readonly byte[] packet;
        private readonly TpktReceived received;
        private int used, expected = 4;
        private bool feeding;

        /// <summary>프로토콜 또는 콜백 처리 실패로 Reset이 필요한 상태입니다.</summary>
        public bool Faulted { get; private set; }

        /// <summary>아직 완성되지 않은 패킷의 수신 바이트 수입니다.</summary>
        public int BufferedBytes { get { return used; } }

        /// <summary>최대 전체 패킷 길이와 수신 콜백을 지정합니다.</summary>
        /// <param name="maximumPacketLength">헤더를 포함한 최대 바이트 수(7~65535)입니다. 버퍼는 생성 시 한 번 할당합니다.</param>
        /// <param name="received">완성 패킷을 동기 처리하는 콜백입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">최대 길이가 범위를 벗어났습니다.</exception>
        /// <exception cref="ArgumentNullException">콜백이 null입니다.</exception>
        public TpktDecoder(int maximumPacketLength, TpktReceived received)
        {
            if (maximumPacketLength < 7 || maximumPacketLength > 65535)
                throw new ArgumentOutOfRangeException("maximumPacketLength");
            if (received == null) throw new ArgumentNullException("received");
            packet = new byte[maximumPacketLength];
            this.received = received;
        }

        /// <summary>이전 연결의 미완성 데이터와 오류 상태를 버립니다.</summary>
        /// <exception cref="InvalidOperationException">콜백 안에서 재진입했습니다.</exception>
        public void Reset()
        {
            if (feeding) throw new InvalidOperationException("Reentrant TPKT decoder access.");
            used = 0;
            expected = 4;
            Faulted = false;
        }

        /// <summary>입력 데이터를 처리하여 완성된 패킷을 콜백에 전달하고, 미완성 패킷의 데이터는 보관합니다.</summary>
        /// <param name="buffer">TCP에서 수신한 데이터입니다. 반환 후 보관하지 않습니다.</param>
        /// <param name="offset">시작 바이트 인덱스입니다.</param>
        /// <param name="count">처리할 바이트 수입니다.</param>
        /// <exception cref="ArgumentNullException">버퍼가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">버퍼 범위가 잘못되었습니다.</exception>
        /// <exception cref="InvalidOperationException">Faulted이거나 재진입했습니다.</exception>
        /// <exception cref="MmsProtocolException">헤더 또는 길이가 잘못되었거나 용량을 초과했습니다.</exception>
        /// <remarks>콜백 예외는 Faulted를 설정한 뒤 전파하며 해당 입력의 나머지를 처리하지 않습니다.
        /// 잘못된 프레임 뒤에서 임의의 헤더를 찾아 동기화하지 않습니다.</remarks>
        public void Feed(byte[] buffer, int offset, int count)
        {
            BufferRange.Check(buffer, offset, count);
            if (feeding || Faulted) throw new InvalidOperationException("Reset required or reentrant TPKT access.");
            feeding = true;
            try
            {
                while (count > 0)
                {
                    int take = expected - used < count ? expected - used : count;
                    Array.Copy(buffer, offset, packet, used, take);
                    used += take;
                    offset += take;
                    count -= take;
                    if (used != expected) continue;
                    if (expected == 4)
                    {
                        int length = (packet[2] << 8) | packet[3];
                        if (packet[0] != 3 || packet[1] != 0 || length < 7 || length > packet.Length)
                            throw new MmsProtocolException("Invalid or oversized TPKT header.");
                        expected = length;
                    }
                    else
                    {
                        received(packet, used);
                        used = 0;
                        expected = 4;
                    }
                }
            }
            catch
            {
                Faulted = true;
                throw;
            }
            finally { feeding = false; }
        }

        /// <summary>TCP EOF에서 미완성 TPKT가 없는지 확인합니다.</summary>
        /// <exception cref="InvalidOperationException">Faulted이거나 재진입했습니다.</exception>
        /// <exception cref="MmsProtocolException">EOF에서 미완성 데이터가 남았습니다.</exception>
        public void CompleteInput()
        {
            if (feeding || Faulted) throw new InvalidOperationException("Reset required or reentrant TPKT access.");
            if (used != 0)
            {
                Faulted = true;
                throw new MmsProtocolException("TCP EOF inside TPKT.");
            }
        }
    }
}
