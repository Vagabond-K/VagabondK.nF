using System;
using VagabondK.nF.Modbus;

// 하드웨어 없이 Register 변환을 검증합니다. 알려진 배치와 모든 워드·바이트 순서 조합, 실수 및 ASCII를 확인합니다.
internal static class Program
{
    private static int _checks;

    private static void Check(bool condition)
    {
        _checks++;
        if (!condition)
            throw new InvalidOperationException("Register conversion assertion " + _checks);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        _checks++;
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static int Main()
    {
        try
        {
            KnownLayouts();
            foreach (RegisterWordOrder words in Enum.GetValues(typeof(RegisterWordOrder)))
                foreach (RegisterByteOrder bytes in Enum.GetValues(typeof(RegisterByteOrder)))
                {
                    Integers(words, bytes);
                    Reals(words, bytes);
                }
            Strings();
            InvalidArguments();
            Console.WriteLine("REGISTER DATA PASS: " + _checks + " assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static ushort[] Buffer()
    {
        return new ushort[] { 0xDEAD, 0, 0, 0, 0, 0xBEEF };
    }

    private static void Guards(ushort[] registers, int width)
    {
        Check(registers[0] == 0xDEAD && registers[5] == 0xBEEF);
        for (int i = width + 1; i < 5; i++)
            Check(registers[i] == 0);
    }

    // 독립적인 기대값 계산: 표준 빅엔디언 바이트로 직렬화한 뒤 실제 레지스터 배치에 맞게 넣습니다.
    private static ushort[] Layout(ulong value, int width, RegisterWordOrder words, RegisterByteOrder bytes)
    {
        var canonical = new byte[width * 2];
        for (int i = canonical.Length - 1; i >= 0; i--)
        {
            canonical[i] = (byte)value;
            value >>= 8;
        }
        var result = Buffer();
        for (int i = 0; i < width; i++)
        {
            int position = words == RegisterWordOrder.HighWordFirst ? i : width - 1 - i;
            byte first = canonical[position * 2];
            byte second = canonical[position * 2 + 1];
            result[i + 1] = bytes == RegisterByteOrder.HighByteFirst
                ? (ushort)(first * 256 + second) : (ushort)(second * 256 + first);
        }
        return result;
    }

    private static void Equal(ushort[] expected, ushort[] actual)
    {
        for (int i = 0; i < expected.Length; i++)
            Check(expected[i] == actual[i]);
    }

    private static void KnownLayouts()
    {
        var registers = new ushort[4];
        RegisterConvert.WriteUInt32(registers, 0, 0x41424344);
        Check(registers[0] == 0x4142 && registers[1] == 0x4344);
        RegisterConvert.WriteUInt32(registers, 0, 0x41424344, byteOrder: RegisterByteOrder.LowByteFirst);
        Check(registers[0] == 0x4241 && registers[1] == 0x4443);
        RegisterConvert.WriteUInt32(registers, 0, 0x41424344, RegisterWordOrder.LowWordFirst);
        Check(registers[0] == 0x4344 && registers[1] == 0x4142);
        RegisterConvert.WriteUInt32(registers, 0, 0x41424344, RegisterWordOrder.LowWordFirst, RegisterByteOrder.LowByteFirst);
        Check(registers[0] == 0x4443 && registers[1] == 0x4241);
        RegisterConvert.WriteUInt64(registers, 0, 0x0102030405060708, RegisterWordOrder.LowWordFirst);
        Equal(new ushort[] { 0x0708, 0x0506, 0x0304, 0x0102 }, registers);
        RegisterConvert.WriteSingle(registers, 0, 1.0f);
        Check(registers[0] == 0x3F80 && registers[1] == 0);
        RegisterConvert.WriteDouble(registers, 0, -2.0);
        Equal(new ushort[] { 0xC000, 0, 0, 0 }, registers);
        Check(RegisterConvert.ReadInt16(new ushort[] { 0x8000 }, 0) == short.MinValue);
        Check(RegisterConvert.ReadInt32(new ushort[] { 0x8000, 0 }, 0) == int.MinValue);
        Check(RegisterConvert.ReadInt64(new ushort[] { 0x8000, 0, 0, 0 }, 0) == long.MinValue);
    }

    private static void Integers(RegisterWordOrder words, RegisterByteOrder bytes)
    {
        for (int i = 0; i <= ushort.MaxValue; i++)
        {
            var expected = Layout((ushort)i, 1, RegisterWordOrder.HighWordFirst, bytes);
            Check(RegisterConvert.ReadUInt16(expected, 1, bytes) == (ushort)i);
            Check(RegisterConvert.ReadInt16(expected, 1, bytes) == unchecked((short)i));
            var actual = Buffer();
            RegisterConvert.WriteInt16(actual, 1, unchecked((short)i), bytes);
            Equal(expected, actual);
            RegisterConvert.WriteUInt16(actual, 1, (ushort)i, bytes);
            Equal(expected, actual);
        }
        ulong[] values = { 0, 1, 0x7FFF, 0x8000, 0xFFFF, 0x7FFFFFFF, 0x80000000,
            0xFFFFFFFF, 0x7FFFFFFFFFFFFFFF, 0x8000000000000000, ulong.MaxValue, 0x123456789ABCDEF0 };
        foreach (ulong raw in values)
        {
            var expected32 = Layout((uint)raw, 2, words, bytes);
            Check(RegisterConvert.ReadUInt32(expected32, 1, words, bytes) == (uint)raw);
            Check(RegisterConvert.ReadInt32(expected32, 1, words, bytes) == unchecked((int)raw));
            var actual = Buffer();
            RegisterConvert.WriteUInt32(actual, 1, (uint)raw, words, bytes);
            Equal(expected32, actual);
            RegisterConvert.WriteInt32(actual, 1, unchecked((int)raw), words, bytes);
            Equal(expected32, actual);
            Guards(actual, 2);
            var expected64 = Layout(raw, 4, words, bytes);
            Check(RegisterConvert.ReadUInt64(expected64, 1, words, bytes) == raw);
            Check(RegisterConvert.ReadInt64(expected64, 1, words, bytes) == unchecked((long)raw));
            actual = Buffer();
            RegisterConvert.WriteUInt64(actual, 1, raw, words, bytes);
            Equal(expected64, actual);
            RegisterConvert.WriteInt64(actual, 1, unchecked((long)raw), words, bytes);
            Equal(expected64, actual);
            Guards(actual, 4);
        }
    }

    private static void Reals(RegisterWordOrder words, RegisterByteOrder bytes)
    {
        uint[] singles = { 0, 0x80000000, 1, 0x007FFFFF, 0x00800000, 0x3F800000, 0xBF800000,
            0x3EAAAAAB, 0x7F7FFFFF, 0xFF7FFFFF, 0x7F800000, 0xFF800000, 0x7FC00000,
            0x7FA00001, 0xFFC12345, 0x80000001 };
        foreach (uint raw in singles)
            Single(raw, words, bytes);
        var random = new Random(1347);
        var bits = new byte[8];
        for (int i = 0; i < 4096; i++)
        {
            random.NextBytes(bits);
            Single(BitConverter.ToUInt32(bits, 0), words, bytes);
            Double(BitConverter.ToUInt64(bits, 0), words, bytes);
        }
        ulong[] doubles = { 0, 0x8000000000000000, 1, 0x000FFFFFFFFFFFFF, 0x0010000000000000,
            0x3FF0000000000000, 0xBFF0000000000000, 0x7FEFFFFFFFFFFFFF, 0xFFEFFFFFFFFFFFFF,
            0x7FF0000000000000, 0xFFF0000000000000, 0x7FF8000000000000, 0x7FF0000000000001 };
        foreach (ulong raw in doubles)
            Double(raw, words, bytes);
    }

    private static void Single(uint raw, RegisterWordOrder words, RegisterByteOrder bytes)
    {
        float expected = BitConverter.Int32BitsToSingle(unchecked((int)raw));
        float actual = RegisterConvert.ReadSingle(Layout(raw, 2, words, bytes), 1, words, bytes);
        var output = Buffer();
        RegisterConvert.WriteSingle(output, 1, expected, words, bytes);
        if (float.IsNaN(expected))
        {
            Check(float.IsNaN(actual));
            uint canonical = unchecked((uint)BitConverter.SingleToInt32Bits(expected)) & 0x80000000U;
            Equal(Layout(canonical | 0x7FC00000U, 2, words, bytes), output);
        }
        else
        {
            Check(BitConverter.SingleToInt32Bits(actual) == unchecked((int)raw));
            Equal(Layout(raw, 2, words, bytes), output);
        }
        Guards(output, 2);
    }

    private static void Double(ulong raw, RegisterWordOrder words, RegisterByteOrder bytes)
    {
        double expected = BitConverter.Int64BitsToDouble(unchecked((long)raw));
        double actual = RegisterConvert.ReadDouble(Layout(raw, 4, words, bytes), 1, words, bytes);
        var output = Buffer();
        RegisterConvert.WriteDouble(output, 1, expected, words, bytes);
        if (double.IsNaN(expected))
            Check(double.IsNaN(actual));
        else
            Check(BitConverter.DoubleToInt64Bits(actual) == unchecked((long)raw));
        Equal(Layout(unchecked((ulong)BitConverter.DoubleToInt64Bits(expected)), 4, words, bytes), output);
        Guards(output, 4);
    }

    private static void Strings()
    {
        foreach (RegisterByteOrder bytes in Enum.GetValues(typeof(RegisterByteOrder)))
        {
            var registers = Buffer();
            RegisterConvert.WriteAscii(registers, 1, 4, "ABC", bytes);
            Check(RegisterConvert.ReadAscii(registers, 1, 4, byteOrder: bytes) == "ABC");
            Check(RegisterConvert.ReadAscii(registers, 1, 4, false, bytes) == "ABC\0\0\0\0\0");
            Guards(registers, 4);
            Check(registers[1] == (bytes == RegisterByteOrder.HighByteFirst ? 0x4142 : 0x4241));
            RegisterConvert.WriteAscii(registers, 1, 4, "ABCDEFGH", bytes);
            Check(RegisterConvert.ReadAscii(registers, 1, 4, byteOrder: bytes) == "ABCDEFGH");
            RegisterConvert.WriteAscii(registers, 1, 4, " A\0Z ", bytes);
            Check(RegisterConvert.ReadAscii(registers, 1, 4, byteOrder: bytes) == " A");
            Check(RegisterConvert.ReadAscii(registers, 1, 4, false, bytes) == " A\0Z \0\0\0");
            RegisterConvert.WriteAscii(registers, 1, 4, "", bytes);
            Check(RegisterConvert.ReadAscii(registers, 1, 4, byteOrder: bytes) == "");
            Check(RegisterConvert.ReadAscii(registers, registers.Length, 0, byteOrder: bytes) == "");
            RegisterConvert.WriteAscii(registers, registers.Length, 0, "", bytes);
            for (int i = 0; i <= 127; i++)
            {
                string text = new string((char)i, 2);
                RegisterConvert.WriteAscii(registers, 1, 1, text, bytes);
                Check(RegisterConvert.ReadAscii(registers, 1, 1, false, bytes) == text);
            }
        }
        var invalid = new ushort[] { 0, 0xFF };
        Throws<ArgumentException>(() => RegisterConvert.ReadAscii(invalid, 0, 2));
        invalid[0] = 0x8000;
        Throws<ArgumentException>(() => RegisterConvert.ReadAscii(invalid, 0, 1));
    }

    // null, 범위/순서 오류 및 잘못된 문자열을 거부하고 실패한 쓰기가 기존 배열을 변경하지 않는지 확인합니다.
    private static void InvalidArguments()
    {
        var registers = new ushort[] { 1, 2, 3, 4 };
        var before = (ushort[])registers.Clone();
        var badWord = (RegisterWordOrder)99;
        var badByte = (RegisterByteOrder)99;
        Throws<ArgumentNullException>(() => RegisterConvert.ReadUInt16(null, 0));
        Throws<ArgumentNullException>(() => RegisterConvert.WriteUInt64(null, 0, 0));
        Throws<ArgumentNullException>(() => RegisterConvert.ReadAscii(null, 0, 0));
        Throws<ArgumentNullException>(() => RegisterConvert.WriteAscii(registers, 0, 4, null));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.ReadUInt16(registers, -1));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.ReadUInt16(registers, 4));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.ReadUInt32(registers, 3));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.ReadUInt64(registers, 1));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.ReadDouble(registers, int.MaxValue));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.ReadUInt32(registers, 0, badWord));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.ReadInt16(registers, 0, badByte));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.WriteUInt64(registers, 1, 0));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.WriteUInt32(registers, 0, 0, badWord));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.WriteUInt16(registers, 0, 0, badByte));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.ReadAscii(registers, 0, int.MaxValue));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.WriteAscii(registers, 0, -1, ""));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.WriteAscii(registers, int.MaxValue, 0, ""));
        Throws<ArgumentOutOfRangeException>(() => RegisterConvert.WriteAscii(registers, 0, 4, "", badByte));
        Throws<ArgumentException>(() => RegisterConvert.WriteAscii(registers, 0, 1, "ABC"));
        Throws<ArgumentException>(() => RegisterConvert.WriteAscii(registers, 0, 4, "AB\u0080"));
        Throws<ArgumentException>(() => RegisterConvert.WriteAscii(registers, 4, 0, "A"));
        Equal(before, registers);
    }
}
