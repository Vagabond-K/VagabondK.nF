using System;
using System.IO.Ports;
using System.Threading;

namespace VagabondK.nF.DNP3.Serial
{
    /// <summary>nanoFramework SerialPort를 DNP3 바이트 채널로 사용하는 어댑터입니다.</summary>
    /// <remarks>호출자가 핀 매핑, 통신 속도·패리티·정지 비트 설정, 포트 열기 및 해제를 담당합니다.
    /// 채널은 SerialPort를 소유하거나 닫지 않습니다. 호출자는 모든 접근을 직렬화해야 합니다.</remarks>
    public sealed class SerialPortChannel : IDnp3ByteChannel
    {
        private readonly SerialPort _port;
        private readonly bool _rs485;

        /// <summary>Environment.TickCount64 기반의 단조 증가 밀리초 시각입니다.</summary>
        public long Milliseconds
        {
            get
            {
                return Environment.TickCount64;
            }
        }

        /// <summary>지정한 UART 모드를 검사하여 SerialPort 어댑터를 생성합니다.</summary>
        /// <param name="port">호출자가 수명을 관리하는 SerialPort입니다. 실제 Read/Write 전에 열려 있어야 합니다.</param>
        /// <param name="rs485">true이면 네이티브 RS485 모드, false이면 Normal 모드를 요구합니다. 모두 Handshake.None이어야 합니다.</param>
        /// <exception cref="ArgumentNullException">SerialPort가 null입니다.</exception>
        /// <exception cref="InvalidOperationException">SerialPort의 모드 또는 흐름 제어 설정이 요구 조건과 일치하지 않습니다.</exception>
        public SerialPortChannel(SerialPort port, bool rs485 = false)
        {
            if (port == null)
                throw new ArgumentNullException("port");
            _port = port;
            _rs485 = rs485;
            ValidateMode();
        }

        /// <summary>닫힌 SerialPort에 네이티브 RS485 모드와 Handshake.None을 설정합니다.</summary>
        /// <param name="port">아직 열지 않은 SerialPort입니다.</param>
        /// <remarks>포트를 열거나 핀 및 통신 속도를 설정하지 않습니다. 하드웨어와 펌웨어의 네이티브 RS485 지원이 필요합니다.</remarks>
        /// <exception cref="ArgumentNullException">SerialPort가 null입니다.</exception>
        /// <exception cref="InvalidOperationException">SerialPort가 이미 열려 있습니다.</exception>
        public static void ConfigureRs485(SerialPort port)
        {
            if (port == null)
                throw new ArgumentNullException("port");
            if (port.IsOpen)
                throw new InvalidOperationException("Configure native RS485 before opening the UART.");
            port.Handshake = Handshake.None;
            port.Mode = SerialMode.RS485;
        }

        private void ValidateMode()
        {
            if (_port.Mode != (_rs485 ? SerialMode.RS485 : SerialMode.Normal) || _port.Handshake != Handshake.None)
                throw new InvalidOperationException("DNP3 serial requires the selected native UART mode without flow control.");
        }

        private static void Validate(byte[] buffer, int offset, int count, int timeout)
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 1 || offset > buffer.Length - count || timeout < 1)
                throw new ArgumentOutOfRangeException("buffer range / timeout");
        }

        /// <summary>UART에 도착한 바이트를 지정된 버퍼 범위로 읽습니다.</summary>
        /// <param name="buffer">수신할 null이 아닌 배열입니다.</param>
        /// <param name="offset">기록할 0 기반 바이트 위치입니다.</param>
        /// <param name="count">최대 수신 바이트 수입니다. 1 이상이며 배열 범위 안이어야 합니다.</param>
        /// <param name="timeoutMilliseconds">대기 제한 시간입니다. 1밀리초 이상입니다.</param>
        /// <returns>실제로 읽은 바이트 수입니다. 제한 시간 내 수신하지 못하면 0입니다.</returns>
        /// <remarks>버퍼를 모두 채울 때까지 기다리지 않고 현재 사용 가능한 바이트만 요청합니다.
        /// SerialPort의 ReadTimeout을 변경하며 UART의 TimeoutException은 0으로 변환합니다.</remarks>
        /// <exception cref="ArgumentNullException">버퍼가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">버퍼 범위 또는 Timeout이 잘못되었습니다.</exception>
        /// <exception cref="InvalidOperationException">UART 모드·흐름 제어 설정이 요구 조건과 맞지 않거나 포트가 열려 있지 않습니다.</exception>
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            Validate(buffer, offset, count, timeoutMilliseconds);
            ValidateMode();
            long started = Milliseconds;
            while (true)
            {
                long remaining = timeoutMilliseconds - (Milliseconds - started);
                if (remaining <= 0)
                    return 0;
                int available = _port.BytesToRead;
                if (available > 0)
                {
                    _port.ReadTimeout = (int)remaining;
                    try
                    {
                        return _port.Read(buffer, offset, available < count ? available : count);
                    }
                    catch (TimeoutException)
                    {
                        return 0;
                    }
                }
                Thread.Sleep(1);
            }
        }

        /// <summary>지정된 바이트 범위를 SerialPort로 전송합니다.</summary>
        /// <param name="buffer">전송할 null이 아닌 배열입니다.</param>
        /// <param name="offset">전송할 0 기반 바이트 위치입니다.</param>
        /// <param name="count">전송할 바이트 수입니다. 1 이상이며 배열 범위 안이어야 합니다.</param>
        /// <param name="timeoutMilliseconds">SerialPort 쓰기 제한 시간입니다. 1밀리초 이상입니다.</param>
        /// <remarks>SerialPort의 WriteTimeout을 변경합니다. RS485 송신 방향과 마지막 정지 비트 처리는
        /// 네이티브 UART 드라이버에 맡기며 GPIO 지연으로 대체하지 않습니다.</remarks>
        /// <exception cref="ArgumentNullException">버퍼가 null입니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">버퍼 범위 또는 Timeout이 잘못되었습니다.</exception>
        /// <exception cref="InvalidOperationException">UART 모드·흐름 제어 설정이 요구 조건과 맞지 않거나 포트가 열려 있지 않습니다.</exception>
        /// <exception cref="TimeoutException">SerialPort 쓰기 제한 시간이 만료되었습니다.</exception>
        public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            Validate(buffer, offset, count, timeoutMilliseconds);
            ValidateMode();
            _port.WriteTimeout = timeoutMilliseconds;
            // 네이티브 RS485 드라이버가 마지막 정지 비트까지 RTS를 제어하므로 통신 속도로 계산한 GPIO 대기 시간으로 대신하지 않습니다.
            _port.Write(buffer, offset, count);
        }
    }
}
