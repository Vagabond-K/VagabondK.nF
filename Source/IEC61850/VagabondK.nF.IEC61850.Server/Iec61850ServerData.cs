using System;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Server;
namespace VagabondK.nF.IEC61850.Server
{
    /// <summary>IEC 61850 상태·품질·UTC를 MMS 말단 변수로 매핑합니다.</summary>
    /// <remarks>변수 갱신과 세션 처리를 같은 잠금 또는 이벤트 루프로 직렬화하십시오.
    /// Set 계열은 항상 변경 버전을 증가시키며 Update 계열은 전송 값이 달라질 때만 증가시킵니다.</remarks>
    public static class Iec61850ServerData
    {
        /// <summary>원격 쓰기를 허용하지 않는 Dbpos 2비트 상태의 말단 변수를 생성합니다.</summary>
        /// <param name="domain">공백 없는 ASCII 1~64자의 MMS domain입니다.</param>
        /// <param name="item">공백 없는 ASCII 1~64자의 MMS item입니다.</param>
        /// <param name="value">초기 상태(0~3)입니다.</param>
        /// <returns>독립 버퍼를 소유하는 2비트 상태 변수입니다.</returns>
        /// <exception cref="ArgumentException">식별자가 잘못되었거나 초기 상태가 0~3 범위를 벗어납니다.</exception>
        public static MmsServerVariable CreateDoublePoint(string domain, string item, IecDoublePoint value = IecDoublePoint.Intermediate)
        {
            var result = new MmsServerVariable(domain, item, MmsEncoding.Int(0x84, 2), new byte[] { 0x84, 2, 6, 0 });
            result.SetDoublePoint(value); return result;
        }
        /// <summary>Dbpos 상태를 추가 할당 없이 변경합니다. 세션 처리와 직렬화하십시오.</summary>
        /// <param name="variable">CreateDoublePoint로 만든 상태 변수입니다.</param>
        /// <param name="value">기록할 상태(0~3)입니다.</param>
        /// <exception cref="InvalidOperationException">변수가 지원하는 Dbpos 인코딩이 아닙니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">상태 값이 0~3 범위를 벗어납니다.</exception>
        public static void SetDoublePoint(this MmsServerVariable variable, IecDoublePoint value)
        {
            if (variable.rawType == null || variable.Encoded[0] != 0x84 || variable.EncodedLength != 4) throw new InvalidOperationException("Not double point.");
            if ((int)value < 0 || (int)value > 3) throw new ArgumentOutOfRangeException("value");
            variable.Encoded[3] = (byte)((int)value << 6); variable.version++;
        }
        /// <summary>13비트 품질의 말단 변수를 생성합니다. 원격 쓰기는 허용하지 않습니다.</summary>
        /// <param name="domain">공백 없는 ASCII 1~64자의 MMS domain입니다.</param>
        /// <param name="item">공백 없는 ASCII 1~64자의 MMS item입니다.</param>
        /// <param name="quality">하위 13비트만 사용하는 초기 품질 플래그입니다.</param>
        /// <returns>독립 버퍼를 소유하는 13비트 품질 변수입니다.</returns>
        /// <exception cref="ArgumentException">식별자가 잘못되었거나 품질에 하위 13비트 이외의 비트가 있습니다.</exception>
        public static MmsServerVariable CreateQuality(string domain, string item, IecQuality quality = IecQuality.Good)
        {
            var value = new MmsServerVariable(domain, item, MmsEncoding.Int(0x84, 13), new byte[] { 0x84, 3, 3, 0, 0 });
            value.SetQuality(quality); return value;
        }
        /// <summary>UTC 말단 변수를 생성합니다. 시각 동기화는 애플리케이션 책임입니다.</summary>
        /// <param name="domain">공백 없는 ASCII 1~64자의 MMS domain입니다.</param>
        /// <param name="item">공백 없는 ASCII 1~64자의 MMS item입니다.</param>
        /// <param name="milliseconds">Unix epoch 기준 초기 UTC 시각(0~4294967295999ms)입니다.</param>
        /// <param name="timeQuality">원시 시간 품질 바이트입니다. 기본값 0x3f는 시계 미동기화·정밀도 미지정을 뜻합니다.</param>
        /// <returns>독립된 8바이트 UTC 버퍼를 소유하며 원격 쓰기를 허용하지 않는 변수입니다.</returns>
        /// <exception cref="ArgumentException">식별자가 잘못되었거나 시각이 MMS UTC 범위를 벗어납니다.</exception>
        public static MmsServerVariable CreateTimestamp(string domain, string item, long milliseconds = 0, byte timeQuality = 0x3f)
        {
            var value = new MmsServerVariable(domain, item, MmsEncoding.Tlv(0x91), new byte[10]);
            value.Encoded[0] = 0x91; value.Encoded[1] = 8; value.SetTimestamp(milliseconds, timeQuality); return value;
        }
        /// <summary>품질 값을 추가 할당 없이 변경합니다. 세션 처리와 직렬화하십시오.</summary>
        /// <param name="variable">CreateQuality로 만든 품질 변수입니다.</param>
        /// <param name="quality">하위 13비트만 사용하는 품질 플래그입니다.</param>
        /// <exception cref="InvalidOperationException">변수가 지원하는 품질 인코딩이 아닙니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">품질에 하위 13비트 이외의 비트가 있습니다.</exception>
        public static void SetQuality(this MmsServerVariable variable, IecQuality quality)
        {
            if (variable.rawType == null || variable.Encoded[0] != 0x84 || variable.EncodedLength != 5) throw new InvalidOperationException("Not quality.");
            Iec61850Data.WriteQuality(quality, variable.Encoded, 3);
            variable.version++;
        }
        /// <summary>UTC 값을 추가 할당 없이 변경합니다. 기본 품질은 시계 미동기화·정밀도 미지정입니다.</summary>
        /// <param name="variable">CreateTimestamp로 만든 UTC 변수입니다.</param>
        /// <param name="milliseconds">Unix epoch 기준 UTC 시각(0~4294967295999ms)입니다.</param>
        /// <param name="timeQuality">마지막 바이트에 기록할 원시 시간 품질입니다.</param>
        /// <exception cref="InvalidOperationException">변수가 지원하는 UTC 인코딩이 아닙니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">시각이 MMS UTC 범위를 벗어납니다.</exception>
        public static void SetTimestamp(this MmsServerVariable variable, long milliseconds, byte timeQuality = 0x3f)
        {
            if (variable.rawType == null || variable.Encoded[0] != 0x91) throw new InvalidOperationException("Not timestamp.");
            Iec61850Data.WriteTimestamp(milliseconds, variable.Encoded, 2, timeQuality);
            variable.version++;
        }
        /// <summary>Dbpos 전송 값이 다를 때만 버퍼와 변경 버전을 갱신합니다. 세션 처리와 직렬화하십시오.</summary>
        /// <param name="variable">CreateDoublePoint로 만든 상태 변수입니다.</param>
        /// <param name="value">비교하여 반영할 상태(0~3)입니다.</param>
        /// <exception cref="InvalidOperationException">변수가 지원하는 Dbpos 인코딩이 아닙니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">상태 값이 0~3 범위를 벗어납니다.</exception>
        public static void UpdateDoublePoint(this MmsServerVariable variable, IecDoublePoint value)
        {
            if (variable.rawType == null || variable.Encoded[0] != 0x84 || variable.EncodedLength != 4) throw new InvalidOperationException("Not double point.");
            if ((int)value < 0 || (int)value > 3) throw new ArgumentOutOfRangeException("value");
            if (variable.Encoded[3] != (byte)((int)value << 6)) variable.SetDoublePoint(value);
        }
        /// <summary>품질 전송 값이 다를 때만 버퍼와 변경 버전을 갱신합니다.</summary>
        /// <param name="variable">CreateQuality로 만든 품질 변수입니다.</param>
        /// <param name="quality">비교하여 반영할 하위 13비트 품질 플래그입니다.</param>
        /// <exception cref="InvalidOperationException">변수가 지원하는 품질 인코딩이 아닙니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">품질에 하위 13비트 이외의 비트가 있습니다.</exception>
        public static void UpdateQuality(this MmsServerVariable variable, IecQuality quality)
        {
            if (variable.rawType == null || variable.Encoded[0] != 0x84 || variable.EncodedLength != 5) throw new InvalidOperationException("Not quality.");
            if (((int)quality & ~8191) != 0) throw new ArgumentOutOfRangeException("quality");
            int packed = (variable.Encoded[3] << 8) | variable.Encoded[4], current = 0;
            for (int i = 0; i < 13; i++) if ((packed & (32768 >> i)) != 0) current |= 1 << i;
            if (current != (int)quality) variable.SetQuality(quality);
        }
        /// <summary>UTC 전송 값·시간 품질이 다를 때만 버퍼와 변경 버전을 갱신합니다.</summary>
        /// <param name="variable">CreateTimestamp로 만든 UTC 변수입니다.</param>
        /// <param name="milliseconds">Unix epoch 기준 UTC 시각(0~4294967295999ms)입니다.</param>
        /// <param name="timeQuality">시각과 함께 비교할 원시 시간 품질 바이트입니다.</param>
        /// <exception cref="InvalidOperationException">변수가 지원하는 UTC 인코딩이 아닙니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">시각이 MMS UTC 범위를 벗어납니다.</exception>
        public static void UpdateTimestamp(this MmsServerVariable variable, long milliseconds, byte timeQuality = 0x3f)
        {
            if (variable.rawType == null || variable.Encoded[0] != 0x91) throw new InvalidOperationException("Not timestamp.");
            if (milliseconds < 0 || milliseconds / 1000 > uint.MaxValue) throw new ArgumentOutOfRangeException("milliseconds");
            uint seconds = (uint)(milliseconds / 1000);
            uint fraction = (uint)((milliseconds % 1000) * 16777216L / 1000);
            for (int i = 0; i < 4; i++)
                if (variable.Encoded[2 + i] != (byte)(seconds >> ((3 - i) * 8)))
                { variable.SetTimestamp(milliseconds, timeQuality); return; }
            for (int i = 0; i < 3; i++)
                if (variable.Encoded[6 + i] != (byte)(fraction >> ((2 - i) * 8)))
                { variable.SetTimestamp(milliseconds, timeQuality); return; }
            if (variable.Encoded[9] != timeQuality) variable.SetTimestamp(milliseconds, timeQuality);
        }
    }
}
