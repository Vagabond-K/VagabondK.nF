using VagabondK.nF.MMS;
using System;

namespace VagabondK.nF.IEC61850
{
    /// <summary>IEC Dbpos의 2비트 상태입니다. 명령의 ctlVal은 Boolean입니다.</summary>
    public enum IecDoublePoint
    {
        /// <summary>중간 상태입니다.</summary>
        Intermediate = 0,
        /// <summary>꺼짐/열림 상태입니다.</summary>
        Off = 1,
        /// <summary>켜짐/닫힘 상태입니다.</summary>
        On = 2,
        /// <summary>유효하지 않은 상태입니다.</summary>
        Bad = 3
    }
    /// <summary>IEC 품질 비트의 논리 인덱스입니다. 전송할 BitString에는 최상위 비트(MSB)부터 기록합니다.</summary>
    [Flags]
    public enum IecQuality
    {
        /// <summary>유효성이 정상(good)인 상태입니다.</summary>
        Good = 0,
        /// <summary>유효성의 예약된 값(reserved)입니다.</summary>
        Reserved = 1,
        /// <summary>유효하지 않은(invalid) 상태입니다.</summary>
        Invalid = 2,
        /// <summary>유효성이 의심스러운(questionable) 상태입니다.</summary>
        Questionable = 3,
        /// <summary>오버플로입니다.</summary>
        Overflow = 4,
        /// <summary>범위 밖입니다.</summary>
        OutOfRange = 8,
        /// <summary>잘못된 참조입니다.</summary>
        BadReference = 16,
        /// <summary>진동 상태입니다.</summary>
        Oscillatory = 32,
        /// <summary>고장입니다.</summary>
        Failure = 64,
        /// <summary>오래된 값입니다.</summary>
        OldData = 128,
        /// <summary>불일치입니다.</summary>
        Inconsistent = 256,
        /// <summary>값이 부정확한 상태입니다.</summary>
        Inaccurate = 512,
        /// <summary>대체된 값을 출처로 사용하는 상태입니다.</summary>
        Substituted = 1024,
        /// <summary>시험 값입니다.</summary>
        Test = 2048,
        /// <summary>운영자 차단입니다.</summary>
        OperatorBlocked = 4096
    }
    /// <summary>13비트 품질과 8바이트 UTC를 의미에 맞는 값으로 변환합니다. 시계 동기화·값의 정확성·안전성을 보장하지 않습니다.</summary>
    public static class Iec61850Data
    {
        /// <summary>Dbpos 2비트 상태를 해석합니다.</summary>
        /// <param name="value">유효 비트가 정확히 2개인 MMS BitString입니다.</param>
        /// <returns>중간·꺼짐·켜짐·유효하지 않음 중 해당 상태입니다.</returns>
        /// <exception cref="ArgumentException">값이 null이거나 2비트 BitString이 아닙니다.</exception>
        public static IecDoublePoint ReadDoublePoint(MmsValue value)
        {
            if (value == null || value.Kind != MmsValueKind.BitString || value.BitCount != 2)
                throw new ArgumentException("Expected 2-bit IEC double point.");
            return (IecDoublePoint)(value.GetByte(0) >> 6);
        }
        /// <summary>13비트 품질 스냅샷을 해석합니다.</summary>
        /// <param name="value">유효 비트가 정확히 13개인 MMS BitString입니다.</param>
        /// <returns>전송 비트를 논리 비트 인덱스로 옮긴 품질 플래그입니다.</returns>
        /// <exception cref="ArgumentException">값이 null이거나 13비트 BitString이 아닙니다.</exception>
        public static IecQuality ReadQuality(MmsValue value)
        {
            if (value == null || value.Kind != MmsValueKind.BitString || value.BitCount != 13)
                throw new ArgumentException("Expected 13-bit IEC quality.");
            int packed = (value.GetByte(0) << 8) | value.GetByte(1), result = 0;
            for (int i = 0; i < 13; i++) if ((packed & (32768 >> i)) != 0) result |= 1 << i;
            return (IecQuality)result;
        }
        /// <summary>13비트 품질을 2바이트에 씁니다. 버퍼는 호출자가 소유합니다.</summary>
        /// <param name="quality">하위 13비트만 사용하는 품질 플래그입니다.</param>
        /// <param name="buffer">2바이트 이상 기록할 수 있는 대상 배열입니다.</param>
        /// <param name="offset">2바이트를 덮어쓸 0 기반 시작 위치입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">품질에 상위 비트가 있거나 버퍼가 null이거나 기록할 공간이 부족합니다.</exception>
        public static void WriteQuality(IecQuality quality, byte[] buffer, int offset)
        {
            if (((int)quality & ~8191) != 0) throw new ArgumentOutOfRangeException("quality");
            if (buffer == null || offset < 0 || offset > buffer.Length - 2) throw new ArgumentOutOfRangeException("buffer");
            buffer[offset] = buffer[offset + 1] = 0;
            for (int i = 0; i < 13; i++) if (((int)quality & (1 << i)) != 0) buffer[offset + i / 8] |= (byte)(128 >> (i % 8));
        }
        /// <summary>UTC를 Unix 기준 시각(epoch)부터의 밀리초로 변환하며 밀리초 미만은 버립니다. 원래 24비트 소수부의 정밀도는 GetBytes로 보존하십시오.</summary>
        /// <param name="value">8바이트 MMS UtcTime 스냅샷입니다.</param>
        /// <returns>1970-01-01 UTC부터의 밀리초입니다. 시간 품질은 해석하지 않습니다.</returns>
        /// <exception cref="ArgumentException">값이 null이거나 UtcTime이 아닙니다.</exception>
        public static long ReadTimestampMilliseconds(MmsValue value)
        {
            Timestamp(value);
            uint seconds = 0, fraction = 0;
            for (int i = 0; i < 4; i++) seconds = (seconds << 8) | value.GetByte(i);
            for (int i = 4; i < 7; i++) fraction = (fraction << 8) | value.GetByte(i);
            return seconds * 1000L + fraction * 1000L / 16777216L;
        }
        /// <summary>시간 품질의 원시 바이트를 반환합니다. 0x80은 leapSecondsKnown, 0x40은 clockFailure, 0x20은 notSynchronized이며 하위 5비트는 정확도입니다.</summary>
        /// <param name="value">8바이트 MMS UtcTime 스냅샷입니다.</param>
        /// <returns>마지막 바이트의 원시 시간 품질 비트입니다.</returns>
        /// <exception cref="ArgumentException">값이 null이거나 UtcTime이 아닙니다.</exception>
        public static byte ReadTimeQuality(MmsValue value) { Timestamp(value); return value.GetByte(7); }
        private static void Timestamp(MmsValue value)
        {
            if (value == null || value.Kind != MmsValueKind.UtcTime) throw new ArgumentException("Expected UTC time.");
        }
        /// <summary>UTC 밀리초 값을 8바이트로 기록합니다. 품질 기본값은 시계 미동기화·정밀도 미지정(0x3f)입니다.</summary>
        /// <param name="milliseconds">Unix epoch 기준 0~4294967295999ms입니다.</param>
        /// <param name="buffer">8바이트를 기록할 호출자 소유 배열입니다.</param>
        /// <param name="offset">8바이트를 덮어쓸 0 기반 시작 위치입니다.</param>
        /// <param name="timeQuality">마지막 바이트에 그대로 기록할 시간 품질 비트입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">시각이 표현 범위를 벗어나거나 버퍼가 null이거나 기록할 공간이 부족합니다.</exception>
        public static void WriteTimestamp(long milliseconds, byte[] buffer, int offset, byte timeQuality = 0x3f)
        {
            if (milliseconds < 0 || milliseconds / 1000 > uint.MaxValue) throw new ArgumentOutOfRangeException("milliseconds");
            if (buffer == null || offset < 0 || offset > buffer.Length - 8) throw new ArgumentOutOfRangeException("buffer");
            uint seconds = (uint)(milliseconds / 1000);
            uint fraction = (uint)((milliseconds % 1000) * 16777216L / 1000);
            for (int i = 3; i >= 0; i--) { buffer[offset + i] = (byte)seconds; seconds >>= 8; }
            for (int i = 6; i >= 4; i--) { buffer[offset + i] = (byte)fraction; fraction >>= 8; }
            buffer[offset + 7] = timeQuality;
        }
    }
}
