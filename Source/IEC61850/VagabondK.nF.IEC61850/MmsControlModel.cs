namespace VagabondK.nF.IEC61850
{
    /// <summary>평가용 제어 값 프로파일입니다. 실제 CDC/설비 상태 모델 전체를 뜻하지 않습니다.</summary>
    public enum MmsControlValueKind
    {
        /// <summary>SPC Boolean ctlVal/stVal입니다.</summary>
        Boolean,
        /// <summary>DPC Boolean ctlVal과 Dbpos stVal입니다.</summary>
        DoublePoint,
        /// <summary>APC AnalogueValue.f Float32 ctlVal/mxVal입니다.</summary>
        AnalogFloat32
    }
    /// <summary>지원하는 즉시 제어 모델입니다. 전송 보안을 뜻하지 않습니다.</summary>
    public enum MmsControlModel
    {
        /// <summary>선택 없이 즉시 실행합니다.</summary>
        DirectNormal = 1,
        /// <summary>같은 연결에서 선택한 뒤 실행합니다.</summary>
        SboNormal = 2,
        /// <summary>선택 없이 실행 요청 후 CommandTermination으로 완료를 확인합니다.</summary>
        DirectEnhanced = 3,
        /// <summary>값을 지정하여 선택한 뒤 실행하고 CommandTermination으로 완료를 확인합니다.</summary>
        SboEnhanced = 4
    }
}
