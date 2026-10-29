using System;
using System.IO.Ports;
using System.Threading;

namespace VagabondK.nF.Modbus.Serial
{
    /// <summary>RTU와 ASCII에서 공통으로 사용할 수 있는 UART 바이트 스트림 어댑터입니다.</summary>
    /// <remarks>
    /// 애플리케이션이 SerialPort의 통신 설정, 포트 열기, RS-485 방향 제어 및 해제를 담당합니다.
    /// 이 어댑터는 포트를 닫지 않으며 ReadTimeout과 WriteTimeout을 변경합니다. 정밀한 프레임 경계 검출은 제공하지 않습니다.
    /// </remarks>
    public sealed class SerialPortChannel : IByteChannel
    {
        private readonly SerialPort _port;

        /// <summary>호출자가 관리하는 SerialPort를 연결합니다.</summary>
        /// <param name="port">송수신 전에 설정 및 Open을 완료할 SerialPort입니다.</param>
        /// <exception cref="ArgumentNullException">포트가 null입니다.</exception>
        public SerialPortChannel(SerialPort port)
        {
            if (port == null)
                throw new ArgumentNullException("port");
            _port = port;
        }

        /// <inheritdoc/>
        /// <remarks>BytesToRead를 확인하여 현재 버퍼에 있는 바이트 수만큼만 요청합니다. 읽기 제한 시간이 만료되면 0을 반환합니다.</remarks>
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            Validate(buffer, offset, count, timeoutMilliseconds);
            var deadline = new OperationDeadline(timeoutMilliseconds);
            try
            {
                int available;
                while ((available = _port.BytesToRead) == 0)
                {
                    int remaining;
                    if (!deadline.TryGetRemainingMilliseconds(out remaining))
                        return 0;
                    Thread.Sleep(remaining > 1 ? 1 : remaining);
                }
                // 일부 nanoCLR UART 드라이버는 요청한 바이트 수를 모두 채울 때까지 기다립니다.
                int readTimeout;
                if (!deadline.TryGetRemainingMilliseconds(out readTimeout))
                    return 0;
                _port.ReadTimeout = readTimeout;
                return _port.Read(buffer, offset, available < count ? available : count);
            }
            catch (TimeoutException)
            {
                return 0; // 유효한 Modbus 결과가 아니라 채널의 수신 제한 시간 만료를 알립니다.
            }
        }

        /// <inheritdoc/>
        /// <remarks>포트의 WriteTimeout을 설정하고 지정한 범위를 씁니다. 송수신 방향 전환은 포트 설정 또는 외부 회로가 담당합니다.</remarks>
        public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            Validate(buffer, offset, count, timeoutMilliseconds);
            _port.WriteTimeout = timeoutMilliseconds;
            _port.Write(buffer, offset, count);
        }

        private static void Validate(byte[] buffer, int offset, int count, int timeout)
        {
            Protocol.ValidateBuffer(buffer, offset, count);
            if (count == 0 || timeout < 1)
                throw new ArgumentOutOfRangeException("count");
        }
    }
}
