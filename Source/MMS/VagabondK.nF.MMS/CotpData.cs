namespace VagabondK.nF.MMS
{
    // Class 0 DT만 처리하며 연결 협상과 TSDU 재조립은 상위 계층에서 담당합니다.
    internal static class CotpData
    {
        internal static int Read(byte[] packet, int length, out bool endOfTsdu)
        {
            BufferRange.Check(packet, 0, length);
            if (length < 7 || packet[0] != 3 || packet[1] != 0 ||
                ((packet[2] << 8) | packet[3]) != length || packet[4] != 2 || packet[5] != 0xf0)
                throw new MmsProtocolException("Expected class 0 COTP DT inside TPKT.");
            endOfTsdu = (packet[6] & 128) != 0;
            return 7;
        }

        internal static int Write(byte[] destination, int offset, byte[] source, int start, int count, bool endOfTsdu)
        {
            BufferRange.Check(source, start, count);
            if (count > 65528) throw new System.ArgumentOutOfRangeException("count");
            BufferRange.Check(destination, offset, count + 7);
            // 호출자가 같은 버퍼 앞부분에 헤더를 추가할 수 있도록 먼저 복사합니다.
            System.Array.Copy(source, start, destination, offset + 7, count);
            int length = count + 7;
            destination[offset] = 3;
            destination[offset + 1] = 0;
            destination[offset + 2] = (byte)(length >> 8);
            destination[offset + 3] = (byte)length;
            destination[offset + 4] = 2;
            destination[offset + 5] = 0xf0;
            destination[offset + 6] = (byte)(endOfTsdu ? 128 : 0);
            return length;
        }
    }
}
