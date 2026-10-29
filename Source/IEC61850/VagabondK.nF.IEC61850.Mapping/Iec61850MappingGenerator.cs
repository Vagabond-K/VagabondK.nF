using Microsoft.CodeAnalysis;

namespace VagabondK.nF.IEC61850.Mapping.Generator
{
    /// <summary>IEC 상태·품질·시각·보고·제어의 직접 접근 코드를 생성합니다.</summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class Iec61850MappingGenerator : IIncrementalGenerator
    {
        /// <summary>IEC 61850 매핑 특성을 등록하고 상태·보고·제어 모델의 증분 소스 생성을 준비합니다.</summary>
        /// <inheritdoc/>
        /// <param name="context">IEC 61850 매핑 특성과 모델별 소스 출력을 등록할 Roslyn 초기화 컨텍스트입니다.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        { new VagabondK.nF.Mapping.Generator.StaticMappingGenerator(true).Initialize(context); }
    }
}
