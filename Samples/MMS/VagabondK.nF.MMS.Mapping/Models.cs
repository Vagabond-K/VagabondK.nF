using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Mapping;

namespace VagabondK.nF.MMS.Mapping.Sample
{
#if !CLIENT_ONLY
    // 일반 속성 쓰기는 설비 제어가 아닙니다. Count 검증기는 모델 반영 전에 0~1000만 허용합니다.
    [MmsServerMap(Domain = "Plant")]
    internal partial class LocalData
    {
        [MmsPoint("Contact")]
        public bool Contact = true;
        [MmsPoint("Count", Writable = true, Validate = nameof(CheckCount))]
        public int Count { get; private set; } = 42;
        [MmsPoint("Temperature")]
        public float Temperature = 12.5f;
        private int CheckCount(MmsValue value) { return value.Integer < 0 || value.Integer > 1000 ? 11 : -1; }
    }
#endif
#if !SERVER_ONLY
    [MmsClientMap(Domain = "Plant")]
    internal partial class RemoteData
    {
        [MmsPoint("Contact")]
        public bool Contact { get; private set; }
        [MmsPoint("Count", Writable = true)]
        public int Count { get; set; }
        [MmsPoint("Temperature")]
        public float Temperature { get; private set; }
    }
#endif
}
