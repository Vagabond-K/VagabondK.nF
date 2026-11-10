namespace VagabondK.nF.Modbus.Server
{
    /// <summary>수신 PDU 버퍼를 응답 PDU로 덮어쓰는 동기 처리 계약입니다.</summary>
    /// <remarks>
    /// 버퍼는 호출 중에만 빌려 사용하며 보관하거나 비동기 작업에 전달하면 안 됩니다.
    /// 요청 정보가 필요하면 덮어쓰기 전에 보존해야 합니다. 공유 버퍼의 전체 송수신은 앱이 직렬화합니다.
    /// 재진입 호출은 허용하지 않습니다. 시리얼 브로드캐스트에는 호출되지 않습니다.
    /// </remarks>
    public interface IModbusPduHandler
    {
        /// <summary>요청을 처리하고 동일 버퍼에 정상 또는 예외 응답을 기록합니다.</summary>
        /// <param name="context">원 요청의 Unit ID와 Transaction ID 사본입니다.</param>
        /// <param name="pdu">인덱스 0부터 요청이 담긴 최소 253바이트 버퍼입니다.</param>
        /// <param name="requestLength">요청 길이이며 1~253입니다.</param>
        /// <returns>응답 길이 1~253입니다. 예외 응답은 원 Function에 0x80을 더한 코드와 예외 코드의 2바이트입니다.</returns>
        /// <exception cref="VagabondK.nF.Modbus.ModbusException">Server가 원 Function의 예외 응답으로 변환할 오류입니다.</exception>
        int Process(FrameContext context, byte[] pdu, int requestLength);
    }
}
