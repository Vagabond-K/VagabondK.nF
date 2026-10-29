using Microsoft.CodeAnalysis;

namespace VagabondK.nF.MMS.Mapping.Generator
{
    /// <summary>일반 MMS 모델의 Client/Server 직접 접근 코드를 생성합니다.</summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class MmsMappingGenerator : IIncrementalGenerator
    {
        /// <summary>MMS 매핑 특성을 등록하고 선언된 모델의 증분 소스 생성을 준비합니다.</summary>
        /// <inheritdoc/>
        /// <param name="context">매핑 특성과 모델별 소스 출력을 등록할 Roslyn 초기화 컨텍스트입니다.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        { new VagabondK.nF.Mapping.Generator.StaticMappingGenerator(false).Initialize(context); }
    }
}
