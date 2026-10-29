using System;
using VagabondK.nF.Internal;

namespace VagabondK.nF.DNP3
{
    internal static class Dnp3Objects
    {
        internal static byte Group(Dnp3PointType type, bool events)
        {
            switch (type)
            {
                case Dnp3PointType.Binary:
                    return events ? (byte)2 : (byte)1;
                case Dnp3PointType.DoubleBitBinary:
                    return events ? (byte)4 : (byte)3;
                case Dnp3PointType.Counter:
                    return events ? (byte)22 : (byte)20;
                case Dnp3PointType.FrozenCounter:
                    return events ? (byte)23 : (byte)21;
                case Dnp3PointType.Analog:
                    return events ? (byte)32 : (byte)30;
                case Dnp3PointType.BinaryOutputStatus:
                    return events ? (byte)11 : (byte)10;
                default:
                    return events ? (byte)42 : (byte)40;
            }
        }

        internal static bool Type(int group, out Dnp3PointType type, out bool events)
        {
            for (int t = 0; t <= 6; t++)
                for (int e = 0; e < 2; e++)
                    if (Group((Dnp3PointType)t, e == 1) == group)
                    {
                        type = (Dnp3PointType)t;
                        events = e == 1;
                        return true;
                    }
            type = Dnp3PointType.Binary;
            events = false;
            return false;
        }

        internal static byte DefaultVariation(Dnp3PointType type, bool events)
        {
            if (type == Dnp3PointType.Analog)
                return events ? (byte)7 : (byte)5;
            if (type == Dnp3PointType.AnalogOutputStatus)
                return events ? (byte)7 : (byte)3;
            if (type == Dnp3PointType.Counter || type == Dnp3PointType.FrozenCounter)
                return events ? (byte)5 : (byte)1;
            return 2;
        }

        internal static bool Supported(Dnp3PointType type, bool events, int v)
        {
            if (type == Dnp3PointType.Analog)
                return events ? v >= 1 && v <= 8 : v == 1 || v == 2 || v == 5 || v == 6;
            if (type == Dnp3PointType.AnalogOutputStatus)
                return events ? v >= 1 && v <= 8 : v >= 1 && v <= 4;
            if (type == Dnp3PointType.Counter || type == Dnp3PointType.FrozenCounter)
                return events ? v == 1 || v == 2 || v == 5 || v == 6 : v == 1 || v == 2;
            return events ? v == 1 || v == 2 : v == 2;
        }

        internal static int Width(Dnp3PointType type, bool events, int v)
        {
            if (type == Dnp3PointType.Analog || type == Dnp3PointType.AnalogOutputStatus)
            {
                int format = events ? (v <= 4 ? ((v & 1) == 1 ? 1 : 2) : ((v & 1) == 1 ? 3 : 4)) :
                    type == Dnp3PointType.Analog ? (v >= 5 ? v - 2 : v) : v;
                return 1 + (format == 2 ? 2 : format == 4 ? 8 : 4) +
                    (events && (v == 3 || v == 4 || v == 7 || v == 8) ? 6 : 0);
            }
            if (type == Dnp3PointType.Counter || type == Dnp3PointType.FrozenCounter)
                return 1 + ((v & 1) == 1 ? 4 : 2) + (events && v >= 5 ? 6 : 0);
            return 1 + (events && v == 2 ? 6 : 0);
        }

        internal static ulong Read(byte[] bytes, int p, int width)
        {
            ulong value = 0;
            for (int i = 0; i < width; i++)
                value |= (ulong)bytes[p + i] << (8 * i);
            return value;
        }

        internal static double Signed32(ulong raw)
        {
            // 서로 다른 숫자 형식이 섞인 식에서 nanoCLR이 Int32를 Double로 잘못 승격하는 문제를 피합니다.
            return (double)(raw & 0x7fffffffUL) - (double)(raw & 0x80000000UL);
        }

        internal static void Put(byte[] bytes, ref int p, ulong value, int width)
        {
            for (int i = 0; i < width; i++)
            {
                bytes[p++] = (byte)value;
                value >>= 8;
            }
        }

        internal static int Write(byte[] b, int p, Dnp3Record record)
        {
            Dnp3PointType type = record.Point.Type;
            bool events = record.EventId != 0;
            int v = record.Variation;
            b[p++] = Group(type, events);
            b[p++] = (byte)v;
            if (events)
            {
                b[p++] = 0x28;
                Put(b, ref p, 1, 2);
                Put(b, ref p, record.Point.Index, 2);
            }
            else
            {
                b[p++] = 1;
                Put(b, ref p, record.Point.Index, 2);
                Put(b, ref p, record.Point.Index, 2);
            }
            return WriteValue(b, p, type, events, v, record.Value);
        }

        internal static int WriteValue(byte[] b, int p, Dnp3PointType type, bool events, int v, Dnp3Value value)
        {
            int start = p, width = Width(type, events, v);
            byte flags = value.Flags;
            ulong raw = value.Raw;
            if (type == Dnp3PointType.Binary || type == Dnp3PointType.BinaryOutputStatus)
                flags = (byte)((flags & 0x7f) | ((int)raw << 7));
            else if (type == Dnp3PointType.DoubleBitBinary)
                flags = (byte)((flags & 0x3f) | ((int)raw << 6));
            else if (type == Dnp3PointType.Analog || type == Dnp3PointType.AnalogOutputStatus)
            {
                int format = events ? (v <= 4 ? ((v & 1) == 1 ? 1 : 2) : ((v & 1) == 1 ? 3 : 4)) :
                    type == Dnp3PointType.Analog ? (v >= 5 ? v - 2 : v) : v;
                double number = value.AsAnalog();
                if (format == 1 || format == 2)
                {
                    double minimum = format == 1 ? int.MinValue : short.MinValue;
                    double maximum = format == 1 ? int.MaxValue : short.MaxValue;
                    if (number < minimum)
                    {
                        number = minimum;
                        flags |= 0x20;
                    }
                    if (number > maximum)
                    {
                        number = maximum;
                        flags |= 0x20;
                    }
                    raw = unchecked((uint)(int)number);
                }
                else if (format == 3)
                {
                    if (number > float.MaxValue)
                    {
                        number = float.MaxValue;
                        flags |= 0x20;
                    }
                    if (number < -float.MaxValue)
                    {
                        number = -float.MaxValue;
                        flags |= 0x20;
                    }
                    raw = RealBits.Encode((float)number);
                }
            }
            b[p++] = flags;
            bool time = events && ((type == Dnp3PointType.Binary || type == Dnp3PointType.BinaryOutputStatus ||
                type == Dnp3PointType.DoubleBitBinary) ? v == 2 :
                (type == Dnp3PointType.Counter || type == Dnp3PointType.FrozenCounter) ? v >= 5 :
                v == 3 || v == 4 || v == 7 || v == 8);
            int valueWidth = width - 1 - (time ? 6 : 0);
            Put(b, ref p, raw, valueWidth);
            if (time)
                Put(b, ref p, (ulong)value.Timestamp, 6);
            if (p - start != width)
                throw new InvalidOperationException("DNP3 object width mismatch.");
            return p;
        }

        internal static Dnp3Value Decode(byte[] bytes, ref int p, Dnp3PointType type, bool events, int v)
        {
            int width = Width(type, events, v);
            Dnp3Channel.Require(width <= bytes.Length - p);
            byte flags = bytes[p++];
            ulong raw = 0;
            long timestamp = 0;
            if (type == Dnp3PointType.Binary || type == Dnp3PointType.BinaryOutputStatus)
                raw = (ulong)(flags >> 7);
            else if (type == Dnp3PointType.DoubleBitBinary)
                raw = (ulong)(flags >> 6);
            int valueWidth = 0;
            if (type == Dnp3PointType.Counter || type == Dnp3PointType.FrozenCounter)
                valueWidth = (v & 1) == 1 ? 4 : 2;
            if (type == Dnp3PointType.Analog || type == Dnp3PointType.AnalogOutputStatus)
            {
                int format = events ? (v <= 4 ? ((v & 1) == 1 ? 1 : 2) : ((v & 1) == 1 ? 3 : 4)) :
                    type == Dnp3PointType.Analog ? (v >= 5 ? v - 2 : v) : v;
                valueWidth = format == 2 ? 2 : format == 4 ? 8 : 4;
                Dnp3Channel.Require(valueWidth <= bytes.Length - p);
                raw = Read(bytes, p, valueWidth);
                double number = format == 1 ? Signed32(raw) : format == 2 ? unchecked((short)raw) :
                    format == 3 ? RealBits.Decode((uint)raw) : BitConverter.Int64BitsToDouble(unchecked((long)raw));
                raw = unchecked((ulong)BitConverter.DoubleToInt64Bits(number));
            }
            else if (valueWidth != 0)
                raw = Read(bytes, p, valueWidth);
            p += valueWidth;
            if (width - 1 - valueWidth == 6)
            {
                timestamp = (long)Read(bytes, p, 6);
                p += 6;
            }
            if (type == Dnp3PointType.Binary || type == Dnp3PointType.BinaryOutputStatus)
                flags &= 0x7f;
            if (type == Dnp3PointType.DoubleBitBinary)
                flags &= 0x3f;
            return Dnp3Value.Bits(raw, flags, timestamp);
        }
    }
}
