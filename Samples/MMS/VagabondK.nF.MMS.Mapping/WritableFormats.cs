using VagabondK.nF.MMS.Mapping;

namespace VagabondK.nF.MMS.Mapping.Sample
{
    // 원격 쓰기를 지원하는 세 스칼라 형식만 명시적으로 허용하는 서버/클라이언트 대응 모델입니다.
    [MmsServerMap(Domain = "P")]
    internal partial class WritableScalars
    {
        [MmsPoint("Boolean", Writable = true)] public bool Flag { get; private set; }
        [MmsPoint("Float", Writable = true)] public float Number { get; private set; }
        [MmsPoint("Integer", Writable = true)] public int Count { get; private set; }
    }

    [MmsClientMap(Domain = "P")]
    internal partial class WritableRemote
    {
        [MmsPoint("Boolean", Writable = true)] public bool Flag { get; set; }
        [MmsPoint("Float", Writable = true)] public float Number { get; set; }
        [MmsPoint("Integer", Writable = true)] public int Count { get; set; }
    }
}
