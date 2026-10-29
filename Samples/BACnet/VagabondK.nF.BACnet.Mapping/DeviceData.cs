using VagabondK.nF.BACnet.Mapping;

namespace VagabondK.nF.BACnet.Mapping.Sample
{
    /// <summary>BACnet으로 노출할 장치 모델입니다. 생성기가 중첩 <c>BacnetStore</c> 클래스를 추가합니다.</summary>
    /// <remarks>
    /// 매핑 결과: BV0=Enabled(쓰기), AV100=Setpoint(쓰기), AV0=Temperature(품질은 TemperatureGood), BV1=QualityControl(쓰기).
    /// AV와 BV는 객체 종류가 달라 인스턴스 번호 0을 함께 쓸 수 있습니다. QualityControl에 쓰면 Temperature 품질을 바꿔 Fault 보고를 시험할 수 있습니다.
    /// </remarks>
    [BacnetMap]
    public partial class DeviceData
    {
        [BacnetBinaryValue(0, Writable = true)]
        public bool Enabled { get; private set; } = true;

        [BacnetAnalogValue(100, Writable = true)]
        public float Setpoint { get; private set; } = 800f;

        [BacnetAnalogValue(0, Quality = nameof(TemperatureGood))]
        public float Temperature { get; private set; } = 250f;

        public bool TemperatureGood = true;

        [BacnetBinaryValue(1, Writable = true)]
        public bool QualityControl
        {
            get { return TemperatureGood; }
            private set { TemperatureGood = value; }
        }
    }
}
