using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("VagabondK.nF.DNP3.Master")]
[assembly: InternalsVisibleTo("VagabondK.nF.DNP3.Outstation")]
[assembly: InternalsVisibleTo("VagabondK.nF.DNP3.Serial")]

namespace VagabondK.nF.DNP3
{
    /// <summary>잘못되었거나 지원하지 않는 DNP3 Frame 또는 메시지를 나타냅니다.</summary>
    public sealed class Dnp3ProtocolException : Exception
    {
        /// <summary>기본 프로토콜 오류 메시지로 예외를 생성합니다.</summary>
        public Dnp3ProtocolException() : base("Malformed or unsupported DNP3 frame.") { }
    }

    /// <summary>한 상대 장치와 통신하는 DNP3 Master 또는 Outstation의 처리 계약입니다.</summary>
    /// <remarks>호출자는 모든 접근을 직렬화하고 반환된 프레임들을 순서대로 전송해야 합니다.
    /// <c>now</c>는 연결 내에서 동일한 기준을 사용하는 단조 증가 밀리초 시각이어야 합니다.</remarks>
    public interface IDnp3Endpoint
    {
        /// <summary>로컬 장치의 Link 주소입니다.</summary>
        ushort LocalAddress { get; }

        /// <summary>상대 장치의 Link 주소입니다.</summary>
        ushort RemoteAddress { get; }

        /// <summary>Master이면 <see langword="true"/>, Outstation이면 <see langword="false"/>입니다.</summary>
        bool IsMaster { get; }

        /// <summary>하나의 완전한 Link Frame을 처리합니다.</summary>
        /// <param name="frame">CRC를 포함하는 완전한 Link Frame입니다.</param>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>전송할 Frame들의 연결된 바이트 배열이며, 전송할 데이터가 없으면 <see langword="null"/>입니다.</returns>
        /// <exception cref="Dnp3ProtocolException">Frame 또는 메시지가 잘못되었거나 지원 범위를 벗어납니다.</exception>
        /// <exception cref="TimeoutException">진행 중인 메시지의 처리 제한 시간이 만료되었습니다.</exception>
        byte[] Process(byte[] frame, long now);

        /// <summary>제한 시간 만료를 확인하고 재전송 및 자발적 전송을 진행합니다.</summary>
        /// <param name="now">단조 증가 밀리초 시각입니다.</param>
        /// <returns>전송할 Frame들의 연결된 배열이며, 없으면 <see langword="null"/>입니다.</returns>
        /// <exception cref="TimeoutException">진행 중인 통신의 제한 시간이 만료되었거나 재시도 한도를 소진했습니다.</exception>
        byte[] Poll(long now);

        /// <summary>새 연결을 시작할 수 있도록 연결별 프로토콜 상태를 초기화합니다.</summary>
        /// <remarks>소켓이나 SerialPort를 열거나 닫지 않습니다.</remarks>
        void ResetConnection();
    }

    /// <summary>DNP3 Link Frame 길이와 CRC 계산 기능을 제공합니다.</summary>
    public static class Dnp3Link
    {
        /// <summary>지정된 바이트 범위의 DNP3 CRC를 계산합니다.</summary>
        /// <param name="bytes">원본 바이트 배열입니다.</param>
        /// <param name="offset">계산을 시작할 0 기반 바이트 위치입니다.</param>
        /// <param name="count">계산할 바이트 수입니다. 0도 허용합니다.</param>
        /// <returns>최종 반전이 적용된 16비트 CRC입니다.</returns>
        /// <exception cref="ArgumentException">배열이 null이거나 범위가 배열을 벗어납니다.</exception>
        public static ushort Crc(byte[] bytes, int offset, int count)
        {
            if (bytes == null || offset < 0 || count < 0 || offset > bytes.Length - count)
                throw new ArgumentException("CRC range.");
            ushort crc = 0;
            for (int i = 0; i < count; i++)
            {
                crc ^= bytes[offset + i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0xa6bc : 0));
            }
            return (ushort)~crc;
        }

        /// <summary>Link 헤더의 길이 필드로 전체 Frame 바이트 수를 계산합니다.</summary>
        /// <param name="header">최소 3바이트이며 05 64로 시작하는 헤더입니다.</param>
        /// <returns>10바이트 헤더와 각 데이터 블록의 CRC를 포함한 길이입니다.</returns>
        /// <remarks>헤더 및 데이터의 CRC나 실제 배열의 전체 Frame 포함 여부는 검사하지 않습니다.</remarks>
        /// <exception cref="ArgumentException">배열이 null이거나 3바이트 미만입니다.</exception>
        /// <exception cref="Dnp3ProtocolException">시작 바이트 또는 길이 필드가 잘못되었습니다.</exception>
        public static int FrameLength(byte[] header)
        {
            if (header == null || header.Length < 3)
                throw new ArgumentException("DNP3 header.");
            Dnp3Channel.Require(header[0] == 5 && header[1] == 0x64 && header[2] >= 5);
            int data = header[2] - 5;
            return 10 + data + 2 * ((data + 15) / 16);
        }
    }
}
