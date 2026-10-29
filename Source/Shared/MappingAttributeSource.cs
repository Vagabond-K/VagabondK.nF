namespace VagabondK.nF.Mapping.Generator
{
    internal static class MappingAttributeSource
    {
        // 선언용 특성만 MCU PE에서 제외합니다. 생성 바인딩과 실행용 델리게이트는 그대로 남깁니다.
        internal static string ForBuild(string declarations)
        {
            return declarations.Replace("[global::System.AttributeUsage",
                "\n#if NANOFRAMEWORK || NANOFRAMEWORK_1_0\n[global::System.Runtime.CompilerServices.ExcludeType]\n#endif\n[global::System.AttributeUsage");
        }
    }
}
