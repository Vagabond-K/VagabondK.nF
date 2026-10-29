using System;
using System.Text;
using VagabondK.nF.Internal;

namespace VagabondK.nF.MQTT.Sparkplug
{
    public sealed partial class SparkplugPayload
    {
        /// <summary>스칼라 메트릭을 Sparkplug B protobuf 페이로드로 인코딩합니다.</summary>
        /// <remarks>일반적인 NBIRTH/DDATA 등은 순서 번호를 자동 관리하는 <see cref="SparkplugSession"/>을 사용하십시오.</remarks>
        /// <param name="timestamp">페이로드 타임스탬프(UTC 밀리초)입니다.</param>
        /// <param name="sequence">공유 순서 번호 seq(0..255)입니다. -1이면 seq 필드를 생략합니다.</param>
        /// <param name="metrics">최대 <see cref="MaximumMetrics"/>개의 메트릭입니다. 빈 배열도 허용됩니다.</param>
        /// <param name="includeTimestamp"><see langword="false"/>이면 타임스탬프 필드를 생략합니다.</param>
        /// <returns><see cref="MaximumBytes"/> 이하의 인코딩된 페이로드입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="metrics"/>가 <see langword="null"/>인 경우.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequence"/>가 범위를 벗어났거나 메트릭이 너무 많은 경우.</exception>
        /// <exception cref="ArgumentException">타임스탬프·seq·메트릭이 모두 없는 빈 페이로드이거나, 메트릭이 <see langword="null"/>·형식 불일치이거나, 크기 한도를 넘는 경우.</exception>
        public static byte[] Encode(ulong timestamp, int sequence, SparkplugMetric[] metrics, bool includeTimestamp = true)
        {
            if (sequence < -1 || sequence > 255) throw new ArgumentOutOfRangeException("sequence");
            if (metrics == null) throw new ArgumentNullException("metrics");
            if (metrics.Length > MaximumMetrics) throw new ArgumentOutOfRangeException("metrics");
            if (!includeTimestamp && sequence == -1 && metrics.Length == 0) throw new ArgumentException("Empty payload.");
            var message = new Writer();
            if (includeTimestamp) message.Number(1, timestamp);
            if (sequence >= 0) message.Number(3, (ulong)sequence);
            foreach (SparkplugMetric value in metrics)
            {
                if (value == null || !ValidType(value)) throw new ArgumentException("Unsupported/null metric.");
                var metric = new Writer();
                if (value.Name != null) metric.Text(1, value.Name);
                if (value.HasAlias) metric.Number(2, value.Alias);
                if (value.HasTimestamp) metric.Number(3, value.Timestamp);
                if (value.DataType != SparkplugDataType.Unspecified) metric.Number(4, (ulong)value.DataType);
                if (value.ValueField == 12) metric.Fixed(12, RealBits.Encode((float)value.Real), 4);
                else if (value.ValueField == 13) metric.Fixed(13, unchecked((ulong)BitConverter.DoubleToInt64Bits(value.Real)), 8);
                else metric.Number(value.ValueField, value.Integer);
                message.Child(metric);
            }
            return message.Finish();
        }
        private sealed class Writer
        {
            private readonly byte[] bytes = new byte[MaximumBytes];
            private int count;
            private void Byte(byte value)
            { if (count == bytes.Length) throw new ArgumentException("Sparkplug payload bound."); bytes[count++] = value; }
            private void Varint(ulong value)
            {
                do { byte digit = (byte)(value & 127); value >>= 7; Byte((byte)(digit | (value != 0 ? 128 : 0))); } while (value != 0);
            }
            internal void Number(int field, ulong value) { Varint((ulong)(field << 3)); Varint(value); }
            internal void Text(int field, string text)
            {
                byte[] value = Encoding.UTF8.GetBytes(text);
                Varint((ulong)((field << 3) | 2)); Varint((ulong)value.Length);
                foreach (byte b in value) Byte(b);
            }
            internal void Child(Writer metric)
            {
                Varint(18); Varint((ulong)metric.count);
                for (int i = 0; i < metric.count; i++) Byte(metric.bytes[i]);
            }
            internal void Fixed(int field, ulong value, int width)
            {
                Varint((ulong)((field << 3) | (width == 4 ? 5 : 1)));
                for (int i = 0; i < width; i++) { Byte((byte)value); value >>= 8; }
            }
            internal byte[] Finish() { var result = new byte[count]; Array.Copy(bytes, result, count); return result; }
        }
    }
}
