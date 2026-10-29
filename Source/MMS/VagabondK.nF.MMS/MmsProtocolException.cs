using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("VagabondK.nF.MMS.Server")]
[assembly: InternalsVisibleTo("VagabondK.nF.MMS.Client")]
[assembly: InternalsVisibleTo("VagabondK.nF.IEC61850")]
[assembly: InternalsVisibleTo("VagabondK.nF.IEC61850.Client")]
[assembly: InternalsVisibleTo("VagabondK.nF.IEC61850.Server")]

namespace VagabondK.nF.MMS
{
    /// <summary>잘못되었거나 제한형 MMS/OSI 프로파일 범위 밖의 데이터를 나타냅니다.</summary>
    public sealed class MmsProtocolException : Exception
    {
        /// <summary>형식 또는 지원 범위 오류의 원인을 지정합니다.</summary>
        /// <param name="message">거부한 프레임 또는 프로토콜 조건의 설명입니다.</param>
        public MmsProtocolException(string message) : base(message) { }
    }
}
