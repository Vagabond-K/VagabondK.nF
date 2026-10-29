using System;

namespace VagabondK.nF.Modbus.RTU
{
    /// <summary>Modbus RTU Frame과 PDU 사이의 변환 및 CRC16 검증을 제공합니다.</summary>
    public static class RtuFrame
    {
        /// <summary>Unit ID와 CRC를 포함한 최대 Frame 길이이며 256바이트입니다.</summary>
        public const int MaxLength = 256;

        /// <summary>지정한 바이트 범위의 Modbus CRC16을 계산합니다.</summary>
        /// <param name="buffer">계산할 버퍼입니다.</param>
        /// <param name="offset">계산을 시작할 인덱스입니다.</param>
        /// <param name="count">계산할 바이트 수입니다. 0이면 초기값을 반환합니다.</param>
        /// <returns>CRC16 값입니다. Frame에는 하위 바이트부터 기록합니다.</returns>
        public static ushort ComputeCrc(byte[] buffer, int offset, int count)
        {
            Protocol.ValidateBuffer(buffer, offset, count);
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= buffer[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
            return crc;
        }

        /// <summary>Unit ID, PDU 및 CRC16을 출력 버퍼의 인덱스 0부터 기록합니다.</summary>
        /// <param name="unitId">0~247의 Serial Unit ID입니다. 0은 Broadcast입니다.</param>
        /// <param name="pdu">인덱스 0부터 읽을 PDU입니다.</param>
        /// <param name="count">PDU 길이이며 범위는 1~253바이트입니다.</param>
        /// <param name="frame">최소 count + 3바이트의 출력 버퍼입니다.</param>
        /// <returns>기록한 RTU Frame의 바이트 수입니다.</returns>
        /// <exception cref="ArgumentNullException">입력 또는 출력 버퍼가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Unit ID, PDU 길이 또는 버퍼 범위가 유효하지 않습니다.</exception>
        public static int Encode(byte unitId, byte[] pdu, int count, byte[] frame)
        {
            Protocol.ValidatePdu(pdu, count);
            Protocol.ValidateBuffer(frame, 0, count + 3);
            if (unitId > 247)
                throw new ArgumentOutOfRangeException("unitId");
            frame[0] = unitId;
            Array.Copy(pdu, 0, frame, 1, count);
            ushort crc = ComputeCrc(frame, 0, count + 1);
            frame[count + 1] = (byte)crc;
            frame[count + 2] = (byte)(crc >> 8);
            return count + 3;
        }

        /// <summary>RTU Frame의 길이, CRC 및 Unit ID를 검증하고 PDU를 복사합니다.</summary>
        /// <param name="frame">인덱스 0부터 읽을 Frame입니다.</param>
        /// <param name="count">Frame 길이이며 범위는 4~256바이트입니다.</param>
        /// <param name="pdu">최소 count - 3바이트의 출력 버퍼입니다.</param>
        /// <param name="unitId">검증한 Serial Unit ID입니다.</param>
        /// <returns>PDU의 바이트 수입니다.</returns>
        /// <exception cref="ModbusProtocolException">Frame 길이, CRC 또는 Unit ID가 유효하지 않습니다.</exception>
        public static int Decode(byte[] frame, int count, byte[] pdu, out byte unitId)
        {
            int length;
            ReceiveStatus status = TryDecode(frame, count, pdu, out unitId, out length);
            if (status == ReceiveStatus.InvalidLength)
                throw new ModbusProtocolException("Invalid RTU frame length.");
            if (status == ReceiveStatus.CrcMismatch)
                throw new ModbusProtocolException("RTU CRC mismatch.");
            if (status == ReceiveStatus.InvalidUnitId)
                throw new ModbusProtocolException("Invalid serial unit ID.");
            return length;
        }

        /// <summary>RTU 프레임을 해석하고 예상 가능한 프레임 오류는 상태로 반환합니다.</summary>
        /// <param name="frame">인덱스 0부터 읽을 Frame입니다.</param>
        /// <param name="count">검사할 바이트 수입니다.</param>
        /// <param name="pdu">검증한 PDU를 저장할 버퍼입니다. 유효한 Frame에 대해 count - 3바이트가 필요합니다.</param>
        /// <param name="unitId">성공하면 수신 Unit ID이며, 실패하면 0입니다.</param>
        /// <param name="length">성공하면 PDU 길이이며, 실패하면 0입니다.</param>
        /// <returns>Success, InvalidLength, CrcMismatch 또는 InvalidUnitId입니다.</returns>
        /// <remarks>버퍼가 null이거나 범위가 잘못된 경우에는 상태 대신 인수 예외를 발생시킵니다.</remarks>
        public static ReceiveStatus TryDecode(byte[] frame, int count, byte[] pdu,
            out byte unitId, out int length)
        {
            Protocol.ValidateBuffer(frame, 0, count);
            Protocol.ValidateBuffer(pdu, 0, 0);
            unitId = 0;
            length = 0;
            if (count < 4 || count > MaxLength)
                return ReceiveStatus.InvalidLength;
            Protocol.ValidateBuffer(pdu, 0, count - 3);
            ushort crc = ComputeCrc(frame, 0, count - 2);
            if (frame[count - 2] != (byte)crc || frame[count - 1] != (byte)(crc >> 8))
                return ReceiveStatus.CrcMismatch;
            if (frame[0] > 247)
                return ReceiveStatus.InvalidUnitId;
            Array.Copy(frame, 1, pdu, 0, count - 3);
            unitId = frame[0];
            length = count - 3;
            return ReceiveStatus.Success;
        }
    }
}
