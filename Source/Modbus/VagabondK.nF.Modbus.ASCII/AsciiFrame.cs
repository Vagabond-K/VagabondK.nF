using System;

namespace VagabondK.nF.Modbus.ASCII
{
    /// <summary>대문자 16진수 문자와 LRC를 사용하는 Modbus ASCII 프레임 변환을 제공합니다.</summary>
    public static class AsciiFrame
    {
        /// <summary>시작 문자와 CRLF를 포함한 최대 Frame 길이이며 513바이트입니다.</summary>
        public const int MaxLength = 513;

        private static byte Hex(int value)
        {
            return (byte)(value < 10 ? '0' + value : 'A' + value - 10);
        }

        private static int Nibble(byte value)
        {
            if (value >= '0' && value <= '9')
                return value - '0';
            if (value >= 'A' && value <= 'F')
                return value - 'A' + 10;
            return -1;
        }

        private static void Put(byte[] buffer, int offset, byte value)
        {
            buffer[offset] = Hex(value >> 4);
            buffer[offset + 1] = Hex(value & 15);
        }

        private static byte Get(byte[] buffer, int offset)
        {
            return (byte)((Nibble(buffer[offset]) << 4) | Nibble(buffer[offset + 1]));
        }

        /// <summary>Unit ID와 PDU를 ASCII HEX로 변환하고 LRC 및 CRLF를 붙입니다.</summary>
        /// <param name="unitId">0~247의 Serial Unit ID입니다. 0은 Broadcast입니다.</param>
        /// <param name="pdu">인덱스 0부터 읽을 PDU입니다.</param>
        /// <param name="count">PDU 길이이며 범위는 1~253바이트입니다.</param>
        /// <param name="frame">최소 (count + 2) * 2 + 3바이트의 출력 버퍼입니다.</param>
        /// <returns>인덱스 0부터 기록한 ASCII Frame의 바이트 수입니다.</returns>
        /// <exception cref="ArgumentNullException">입력 또는 출력 버퍼가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Unit ID, PDU 길이 또는 버퍼 범위가 유효하지 않습니다.</exception>
        public static int Encode(byte unitId, byte[] pdu, int count, byte[] frame)
        {
            Protocol.ValidatePdu(pdu, count);
            int length = (count + 2) * 2 + 3;
            Protocol.ValidateBuffer(frame, 0, length);
            if (unitId > 247)
                throw new ArgumentOutOfRangeException("unitId");
            frame[0] = (byte)':';
            Put(frame, 1, unitId);
            int sum = unitId;
            for (int i = 0; i < count; i++)
            {
                Put(frame, 3 + i * 2, pdu[i]);
                sum += pdu[i];
            }
            Put(frame, length - 4, unchecked((byte)-sum));
            frame[length - 2] = 13;
            frame[length - 1] = 10;
            return length;
        }

        /// <summary>ASCII Frame의 형식, LRC 및 주소를 검증하고 PDU를 복사합니다.</summary>
        /// <param name="frame">인덱스 0부터 읽을 Frame입니다. HEX 문자는 0~9와 A~F만 허용합니다.</param>
        /// <param name="count">Frame 길이이며 9~513 범위의 홀수 바이트 수입니다.</param>
        /// <param name="pdu">최소 (count - 3) / 2 - 2바이트의 출력 버퍼입니다.</param>
        /// <param name="unitId">검증한 Serial Unit ID입니다.</param>
        /// <returns>PDU의 바이트 수입니다.</returns>
        /// <exception cref="ModbusProtocolException">Frame 형식, 문자, LRC 또는 Unit ID가 유효하지 않습니다.</exception>
        public static int Decode(byte[] frame, int count, byte[] pdu, out byte unitId)
        {
            int length;
            ReceiveStatus status = TryDecode(frame, count, pdu, out unitId, out length);
            if (status == ReceiveStatus.InvalidLength)
                throw new ModbusProtocolException("Invalid ASCII frame.");
            if (status == ReceiveStatus.InvalidCharacter)
                throw new ModbusProtocolException("Invalid ASCII hexadecimal character.");
            if (status == ReceiveStatus.LrcMismatch)
                throw new ModbusProtocolException("ASCII LRC mismatch.");
            if (status == ReceiveStatus.InvalidUnitId)
                throw new ModbusProtocolException("Invalid serial unit ID.");
            return length;
        }

        /// <summary>ASCII 프레임을 해석하고 예상 가능한 프레임 오류는 상태로 반환합니다.</summary>
        /// <param name="frame">인덱스 0부터 읽을 Frame입니다.</param>
        /// <param name="count">검사할 바이트 수입니다.</param>
        /// <param name="pdu">유효한 Frame의 PDU를 인덱스 0부터 저장할 버퍼입니다.</param>
        /// <param name="unitId">성공하면 수신 Unit ID이며, 실패하면 0입니다.</param>
        /// <param name="length">성공하면 PDU 길이이며, 실패하면 0입니다.</param>
        /// <returns>Success, InvalidLength, InvalidCharacter, LrcMismatch 또는 InvalidUnitId입니다.</returns>
        /// <remarks>버퍼가 null이거나 범위가 잘못된 경우에는 인수 예외를 발생시킵니다.</remarks>
        public static ReceiveStatus TryDecode(byte[] frame, int count, byte[] pdu,
            out byte unitId, out int length)
        {
            Protocol.ValidateBuffer(frame, 0, count);
            Protocol.ValidateBuffer(pdu, 0, 0);
            unitId = 0;
            length = 0;
            if (count < 9 || count > MaxLength || (count & 1) == 0 ||
                frame[0] != ':' || frame[count - 2] != 13 || frame[count - 1] != 10)
                return ReceiveStatus.InvalidLength;
            int pduLength = (count - 3) / 2 - 2;
            Protocol.ValidateBuffer(pdu, 0, pduLength);
            int sum = 0;
            for (int offset = 1; offset < count - 2; offset += 2)
            {
                if (Nibble(frame[offset]) < 0 || Nibble(frame[offset + 1]) < 0)
                    return ReceiveStatus.InvalidCharacter;
                sum += Get(frame, offset);
            }
            if ((sum & 255) != 0)
                return ReceiveStatus.LrcMismatch;
            byte decodedUnit = Get(frame, 1);
            if (decodedUnit > 247)
                return ReceiveStatus.InvalidUnitId;
            for (int i = 0; i < pduLength; i++)
                pdu[i] = Get(frame, 3 + i * 2);
            unitId = decodedUnit;
            length = pduLength;
            return ReceiveStatus.Success;
        }
    }
}
