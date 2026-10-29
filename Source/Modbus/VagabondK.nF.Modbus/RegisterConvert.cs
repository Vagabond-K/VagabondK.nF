using System;
using VagabondK.nF.Internal;

namespace VagabondK.nF.Modbus
{
    /// <summary>Modbus Register 배열과 정수, IEEE 754 실수 및 ASCII 문자열을 변환합니다.</summary>
    /// <remarks>
    /// 모든 시작 위치와 개수는 바이트가 아닌 레지스터 단위입니다. 정수와 실수 변환은 임시 배열을 할당하지 않습니다.
    /// 워드·바이트 순서는 장치의 레지스터 맵에 따라 선택하며 CPU의 바이트 순서에 의존하지 않습니다.
    /// 배열 접근을 동기화하지 않으므로 여러 실행 흐름에서 동시에 읽거나 갱신하는 경우 호출자가 동기화해야 합니다.
    /// 모든 메서드는 null 배열에 ArgumentNullException, 잘못된 배열 범위 또는 순서 값에
    /// ArgumentOutOfRangeException을 발생시킵니다. 쓰기는 인수 검증 뒤 시작하며 통신을 수행하지 않습니다.
    /// </remarks>
    public static class RegisterConvert
    {
        /// <summary>Register 하나를 부호 없는 16비트 정수로 읽습니다.</summary>
        /// <param name="registers">읽을 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. Register 하나가 필요합니다.</param>
        /// <param name="byteOrder">Register 내부 Byte 순서입니다.</param>
        /// <returns>해석한 UInt16 값입니다.</returns>
        public static ushort ReadUInt16(ushort[] registers, int offset,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            return (ushort)Read(registers, offset, 1, RegisterWordOrder.HighWordFirst, byteOrder);
        }

        /// <summary>Register 하나를 2의 보수 16비트 정수로 읽습니다.</summary>
        /// <param name="registers">읽을 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. Register 하나가 필요합니다.</param>
        /// <param name="byteOrder">Register 내부 Byte 순서입니다.</param>
        /// <returns>해석한 Int16 값입니다.</returns>
        public static short ReadInt16(ushort[] registers, int offset,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            return unchecked((short)ReadUInt16(registers, offset, byteOrder));
        }

        /// <summary>Register 두 개를 부호 없는 32비트 정수로 읽습니다.</summary>
        /// <param name="registers">읽을 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 두 개가 필요합니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 Word 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        /// <returns>해석한 UInt32 값입니다.</returns>
        public static uint ReadUInt32(ushort[] registers, int offset,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            return (uint)Read(registers, offset, 2, wordOrder, byteOrder);
        }

        /// <summary>Register 두 개를 2의 보수 32비트 정수로 읽습니다.</summary>
        /// <param name="registers">읽을 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 두 개가 필요합니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 Word 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        /// <returns>해석한 Int32 값입니다.</returns>
        public static int ReadInt32(ushort[] registers, int offset,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            return unchecked((int)ReadUInt32(registers, offset, wordOrder, byteOrder));
        }

        /// <summary>Register 네 개를 부호 없는 64비트 정수로 읽습니다.</summary>
        /// <param name="registers">읽을 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 네 개가 필요합니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 네 Word의 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        /// <returns>해석한 UInt64 값입니다.</returns>
        public static ulong ReadUInt64(ushort[] registers, int offset,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            return Read(registers, offset, 4, wordOrder, byteOrder);
        }

        /// <summary>Register 네 개를 2의 보수 64비트 정수로 읽습니다.</summary>
        /// <param name="registers">읽을 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 네 개가 필요합니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 네 Word의 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        /// <returns>해석한 Int64 값입니다.</returns>
        public static long ReadInt64(ushort[] registers, int offset,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            return unchecked((long)ReadUInt64(registers, offset, wordOrder, byteOrder));
        }

        /// <summary>Register 두 개를 IEEE 754 binary32 실수로 읽습니다.</summary>
        /// <param name="registers">읽을 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 두 개가 필요합니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 Word 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        /// <returns>해석한 Single 값입니다. NaN과 Infinity도 허용합니다.</returns>
        /// <remarks>NaN의 페이로드 보존은 보장하지 않습니다.</remarks>
        public static float ReadSingle(ushort[] registers, int offset,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            return RealBits.Decode(ReadUInt32(registers, offset, wordOrder, byteOrder));
        }

        /// <summary>Register 네 개를 IEEE 754 binary64 실수로 읽습니다.</summary>
        /// <param name="registers">읽을 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 네 개가 필요합니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 네 Word의 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        /// <returns>해석한 Double 값입니다. NaN과 Infinity도 허용합니다.</returns>
        /// <remarks>NaN의 페이로드 보존 여부는 실행 환경에 따라 달라질 수 있습니다.</remarks>
        public static double ReadDouble(ushort[] registers, int offset,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            return BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64(registers, offset, wordOrder, byteOrder)));
        }

        /// <summary>부호 없는 16비트 정수를 Register 하나에 기록합니다.</summary>
        /// <param name="registers">수정할 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. Register 하나가 필요합니다.</param>
        /// <param name="value">기록할 값입니다.</param>
        /// <param name="byteOrder">Register 내부 Byte 순서입니다.</param>
        public static void WriteUInt16(ushort[] registers, int offset, ushort value,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            Write(registers, offset, 1, value, RegisterWordOrder.HighWordFirst, byteOrder);
        }

        /// <summary>16비트 정수를 2의 보수 표현으로 Register 하나에 기록합니다.</summary>
        /// <param name="registers">수정할 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. Register 하나가 필요합니다.</param>
        /// <param name="value">기록할 값입니다.</param>
        /// <param name="byteOrder">Register 내부 Byte 순서입니다.</param>
        public static void WriteInt16(ushort[] registers, int offset, short value,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            WriteUInt16(registers, offset, unchecked((ushort)value), byteOrder);
        }

        /// <summary>부호 없는 32비트 정수를 Register 두 개에 기록합니다.</summary>
        /// <param name="registers">수정할 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 두 개가 필요합니다.</param>
        /// <param name="value">기록할 값입니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 Word 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        public static void WriteUInt32(ushort[] registers, int offset, uint value,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            Write(registers, offset, 2, value, wordOrder, byteOrder);
        }

        /// <summary>32비트 정수를 2의 보수 표현으로 Register 두 개에 기록합니다.</summary>
        /// <param name="registers">수정할 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 두 개가 필요합니다.</param>
        /// <param name="value">기록할 값입니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 Word 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        public static void WriteInt32(ushort[] registers, int offset, int value,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            WriteUInt32(registers, offset, unchecked((uint)value), wordOrder, byteOrder);
        }

        /// <summary>부호 없는 64비트 정수를 Register 네 개에 기록합니다.</summary>
        /// <param name="registers">수정할 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 네 개가 필요합니다.</param>
        /// <param name="value">기록할 값입니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 네 Word의 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        public static void WriteUInt64(ushort[] registers, int offset, ulong value,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            Write(registers, offset, 4, value, wordOrder, byteOrder);
        }

        /// <summary>64비트 정수를 2의 보수 표현으로 Register 네 개에 기록합니다.</summary>
        /// <param name="registers">수정할 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 네 개가 필요합니다.</param>
        /// <param name="value">기록할 값입니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 네 Word의 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        public static void WriteInt64(ushort[] registers, int offset, long value,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            WriteUInt64(registers, offset, unchecked((ulong)value), wordOrder, byteOrder);
        }

        /// <summary>Single 값을 IEEE 754 binary32 표현으로 Register 두 개에 기록합니다.</summary>
        /// <param name="registers">수정할 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 두 개가 필요합니다.</param>
        /// <param name="value">기록할 값입니다. NaN과 Infinity도 허용합니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 Word 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        /// <remarks>NaN은 부호를 유지한 Quiet NaN으로 정규화하며 페이로드는 보존하지 않습니다.</remarks>
        public static void WriteSingle(ushort[] registers, int offset, float value,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            WriteUInt32(registers, offset, RealBits.Encode(value), wordOrder, byteOrder);
        }

        /// <summary>Double 값을 IEEE 754 binary64 표현으로 Register 네 개에 기록합니다.</summary>
        /// <param name="registers">수정할 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다. 연속된 Register 네 개가 필요합니다.</param>
        /// <param name="value">기록할 값입니다. NaN과 Infinity도 허용합니다.</param>
        /// <param name="wordOrder">숫자를 구성하는 네 Word의 순서입니다.</param>
        /// <param name="byteOrder">각 Register 내부 Byte 순서입니다.</param>
        public static void WriteDouble(ushort[] registers, int offset, double value,
            RegisterWordOrder wordOrder = RegisterWordOrder.HighWordFirst,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            WriteUInt64(registers, offset, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)), wordOrder, byteOrder);
        }

        /// <summary>Register당 두 문자인 고정 길이 ASCII 필드를 읽습니다.</summary>
        /// <param name="registers">읽을 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다.</param>
        /// <param name="registerCount">읽을 Register 수입니다. 0도 허용합니다.</param>
        /// <param name="stopAtNull">true이면 첫 NUL 전까지만 반환합니다. false이면 NUL도 문자열에 포함합니다.</param>
        /// <param name="byteOrder">각 Register에서 문자를 읽는 순서입니다. Register 자체는 주소 순서대로 읽습니다.</param>
        /// <returns>새 ASCII 문자열입니다. 공백은 제거하지 않습니다.</returns>
        /// <exception cref="ArgumentException">필드에 ASCII 범위(0~127) 밖의 바이트가 있습니다. 첫 NUL 뒤를 포함한 필드 전체를 검증합니다.</exception>
        public static string ReadAscii(ushort[] registers, int offset, int registerCount, bool stopAtNull = true,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            Validate(registers, offset, registerCount, RegisterWordOrder.HighWordFirst, byteOrder);
            if (registerCount > int.MaxValue / 2)
                throw new ArgumentOutOfRangeException("registerCount");
            int length = registerCount * 2;
            bool terminated = false;
            for (int i = 0; i < registerCount; i++)
            {
                ushort word = registers[offset + i];
                if ((word & 0x8080) != 0)
                    throw new ArgumentException("Register field contains non-ASCII bytes.");
                word = OrderBytes(word, byteOrder);
                if (stopAtNull && !terminated)
                {
                    if ((word >> 8) == 0)
                    {
                        length = i * 2;
                        terminated = true;
                    }
                    else if ((word & 255) == 0)
                    {
                        length = i * 2 + 1;
                        terminated = true;
                    }
                }
            }
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                ushort word = OrderBytes(registers[offset + i / 2], byteOrder);
                chars[i] = (char)((i & 1) == 0 ? word >> 8 : word & 255);
            }
            return new string(chars);
        }

        /// <summary>ASCII 문자열을 고정 길이 레지스터 필드에 쓰고 남는 바이트를 NUL로 채웁니다.</summary>
        /// <param name="registers">수정할 Register 배열입니다.</param>
        /// <param name="offset">0 기반 시작 Register 인덱스입니다.</param>
        /// <param name="registerCount">출력 필드의 Register 수입니다. 0이면 빈 문자열만 허용합니다.</param>
        /// <param name="value">기록할 ASCII 문자열입니다. NUL도 허용하며 문자열 끝에 종료 문자를 위한 별도 공간을 요구하지 않습니다.</param>
        /// <param name="byteOrder">각 Register에 문자를 기록하는 순서입니다. Register 자체는 주소 순서대로 기록합니다.</param>
        /// <exception cref="ArgumentNullException">배열 또는 문자열이 null입니다.</exception>
        /// <exception cref="ArgumentException">문자열이 필드보다 길거나 ASCII 범위 밖의 문자를 포함합니다.</exception>
        /// <remarks>인수와 문자 전체를 검증한 뒤 기록합니다. 입력 오류가 있으면 배열을 변경하지 않습니다.</remarks>
        public static void WriteAscii(ushort[] registers, int offset, int registerCount, string value,
            RegisterByteOrder byteOrder = RegisterByteOrder.HighByteFirst)
        {
            Validate(registers, offset, registerCount, RegisterWordOrder.HighWordFirst, byteOrder);
            if (value == null)
                throw new ArgumentNullException("value");
            if ((long)value.Length > (long)registerCount * 2)
                throw new ArgumentException("String exceeds the register field.");
            for (int i = 0; i < value.Length; i++)
                if (value[i] > 127)
                    throw new ArgumentException("String contains non-ASCII characters.");
            for (int i = 0; i < registerCount; i++)
            {
                long position = (long)i * 2;
                int high = position < value.Length ? value[(int)position] : 0;
                int low = position + 1 < value.Length ? value[(int)position + 1] : 0;
                registers[offset + i] = OrderBytes((ushort)((high << 8) | low), byteOrder);
            }
        }

        private static ushort OrderBytes(ushort value, RegisterByteOrder byteOrder)
        {
            return byteOrder == RegisterByteOrder.HighByteFirst
                ? value : (ushort)(((uint)value << 8) | ((uint)value >> 8));
        }

        private static void Validate(ushort[] registers, int offset, int count,
            RegisterWordOrder wordOrder, RegisterByteOrder byteOrder)
        {
            if (registers == null)
                throw new ArgumentNullException("registers");
            if (offset < 0 || count < 0 || offset > registers.Length - count)
                throw new ArgumentOutOfRangeException("offset");
            if (wordOrder != RegisterWordOrder.HighWordFirst && wordOrder != RegisterWordOrder.LowWordFirst)
                throw new ArgumentOutOfRangeException("wordOrder");
            if (byteOrder != RegisterByteOrder.HighByteFirst && byteOrder != RegisterByteOrder.LowByteFirst)
                throw new ArgumentOutOfRangeException("byteOrder");
        }

        private static ulong Read(ushort[] registers, int offset, int count,
            RegisterWordOrder wordOrder, RegisterByteOrder byteOrder)
        {
            Validate(registers, offset, count, wordOrder, byteOrder);
            ulong value = 0;
            for (int i = 0; i < count; i++)
            {
                int index = wordOrder == RegisterWordOrder.HighWordFirst ? i : count - i - 1;
                value = (value << 16) | OrderBytes(registers[offset + index], byteOrder);
            }
            return value;
        }

        private static void Write(ushort[] registers, int offset, int count, ulong value,
            RegisterWordOrder wordOrder, RegisterByteOrder byteOrder)
        {
            Validate(registers, offset, count, wordOrder, byteOrder);
            for (int i = 0; i < count; i++)
            {
                int shift = (wordOrder == RegisterWordOrder.HighWordFirst ? count - i - 1 : i) * 16;
                registers[offset + i] = OrderBytes((ushort)(value >> shift), byteOrder);
            }
        }
    }
}
