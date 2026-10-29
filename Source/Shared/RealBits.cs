using System;

namespace VagabondK.nF.Internal
{
    // nanoFramework의 무할당 Single 변환을 공유 소스로 제공합니다. NaN 송신은 quiet NaN으로 정규화합니다.
    internal static class RealBits
    {
        public static uint Encode(float value)
        {
            ulong raw = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            uint sign = (uint)(raw >> 32) & 0x80000000U;
            int exponent = (int)((raw >> 52) & 2047);
            ulong fraction = raw & 0x000fffffffffffffUL;
            if (exponent == 2047)
                return sign | (fraction == 0 ? 0x7f800000U : 0x7fc00000U);
            if (exponent == 0)
                return sign;
            // Single을 Double로 정확히 확장한 비트에서 지수 편향과 가수 폭을 다시 줄입니다.
            if (exponent >= 897)
                return sign | ((uint)(exponent - 896) << 23) | (uint)(fraction >> 29);
            return sign | (uint)((fraction | 0x0010000000000000UL) >> (926 - exponent));
        }

        public static float Decode(uint bits)
        {
            ulong sign = (ulong)(bits & 0x80000000U) << 32;
            int exponent = (int)((bits >> 23) & 255);
            uint fraction = bits & 0x007fffffU;
            ulong raw;
            if (exponent == 255)
                raw = sign | 0x7ff0000000000000UL | ((ulong)fraction << 29);
            else if (exponent != 0)
                raw = sign | ((ulong)(exponent + 896) << 52) | ((ulong)fraction << 29);
            else if (fraction == 0)
                raw = sign;
            else
            {
                // Single 비정규수를 정규화한 뒤 Double의 지수와 가수에 배치합니다.
                int shift = 0;
                while ((fraction & 0x00800000U) == 0)
                {
                    fraction <<= 1;
                    shift++;
                }
                raw = sign | ((ulong)(897 - shift) << 52) | ((ulong)(fraction & 0x007fffffU) << 29);
            }
            return (float)BitConverter.Int64BitsToDouble(unchecked((long)raw));
        }
    }
}
