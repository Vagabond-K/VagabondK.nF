namespace VagabondK.nF.DNP3.Interop
{
    // 부호 있는 0·비정규 수·최대 유한 Single 벡터 뒤에 재현 가능한 xorshift 비트를 사용하며 NaN/Infinity는 제외합니다.
    internal static class Fixtures
    {
        private static readonly uint[] RealBoundaries = {
            0, 0x80000000, 1, 0x80000001, 0x007fffff, 0x807fffff,
            0x00800000, 0x80800000, 0x7f7fffff, 0xff7fffff,
            0x3f800000, 0xbf800000, 0x3eaaaaab, 0x4b800000
        };

        public static uint NextFiniteBits(ref uint state, int iteration)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            uint bits = iteration < RealBoundaries.Length ? RealBoundaries[iteration] : state;
            return (bits & 0x7f800000) == 0x7f800000 ? bits ^ 0x00800000 : bits;
        }
    }
}
