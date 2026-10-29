using System;
using VagabondK.nF.DNP3.Mapping;

namespace VagabondK.nF.DNP3.Mapping.Sample
{
    [Dnp3Map]
    internal partial class FloatData
    {
        [AnalogInput(1, StaticVariation = 5, EventVariation = 7)]
        public float Measurement = 12.5f;
        [AnalogOutput(1, StaticVariation = 3, EventVariation = 7)]
        public float Output = -0.5f;

        // 초기값은 이벤트를 만들지 않으며 이후 Publish가 두 변경을 각각 이벤트로 보관하는지 확인합니다.
        internal static void Demonstrate()
        {
            var data = new FloatData();
            var mapped = new Dnp3Binding(data, new object(), 4);
            data.Measurement = 25.25f;
            data.Output = 2.5f;
            if (!mapped.Publish() || mapped.Database.PointCount != 2 || mapped.Database.EventCount != 2)
                throw new InvalidOperationException("Float32 DNP3 publication.");
        }
    }
}
