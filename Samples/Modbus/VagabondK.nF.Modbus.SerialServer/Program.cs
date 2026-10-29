using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using nanoFramework.Hardware.Esp32;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.ASCII;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Serial;
using VagabondK.nF.Modbus.Server;

namespace VagabondK.nF.Samples.SerialServer
{
    /// <summary>보드 UART에서 Modbus 요청을 처리하고 수신 진단을 Input Register에 게시합니다.</summary>
    /// <remarks>GPIO17/18과 COM2 설정은 보드별로 확인합니다. 이 예제의 DM2472는 방향을 자동 제어하므로 UART RS485 모드를 사용하지 않습니다.
    /// MODBUS_ASCII 선택 시 양쪽을 9600 7E1로 맞춥니다. 저장소는 예제 메모리이며 물리 출력을 구동하지 않습니다.</remarks>
    public static class Program
    {
        private static readonly byte[] DiscardBuffer = new byte[256];

        /// <summary>초기 데이터를 만들고 요청을 반복 처리하며 오류 후 UART의 남은 데이터를 비웁니다.</summary>
        public static void Main()
        {
            Configuration.SetPinFunction(17, DeviceFunction.COM2_TX);
            Configuration.SetPinFunction(18, DeviceFunction.COM2_RX);
#if MODBUS_ASCII
            const int dataBits = 7;
#else
            const int dataBits = 8;
#endif
            using (var port = new SerialPort("COM2", 9600, Parity.Even, dataBits, StopBits.One))
            {
                // DM2472가 버스 방향을 직접 제어하므로 UART RTS/RS485 모드를 켜지 않습니다.
                port.Open();
#if SERIAL_RECEIVE_PROBE
                ProbeReceiveBatches(port);
#else
                IByteChannel channel = new SerialPortChannel(port);
#if SERIAL_DIAGNOSTICS
                channel = new DiagnosticChannel(channel);
#endif
#if MODBUS_ASCII
                IModbusTransport transport = new AsciiTransport(channel, 3000);
                Debug.WriteLine("ASCII 9600 7E1, Unit 1; TX GPIO17, RX GPIO18.");
#else
                IModbusTransport transport = new RtuTransport(channel, 9600,
                    RtuReceiveMode.RequestLength, 11, 3000);
                Debug.WriteLine("RTU 9600 8E1, request-length RX, Unit 1; TX GPIO17, RX GPIO18.");
#endif
                var store = new ArrayDataStore(2000, 2000, 128, 128);
                store.WriteHoldingRegisters(0, 3, new ushort[] { 123, 456, 65535 });
                store.SetInputRegisters(0, 3, new ushort[] { 100, 200, 300 });
                store.SetDiscreteInputs(0, 4, new bool[] { true, false, true, false });
                var server = new ModbusServer(1, transport, store);
                var receiveCounters = new ushort[6];
                while (true)
                {
                    ReceiveStatus status = server.TryProcessNextRequest();
                    if (status == ReceiveStatus.Success)
                        receiveCounters[0]++;
                    else if (status == ReceiveStatus.Idle)
                        receiveCounters[1]++;
                    else
                    {
                        if (status == ReceiveStatus.Timeout)
                            receiveCounters[2]++;
                        else if (status == ReceiveStatus.CrcMismatch || status == ReceiveStatus.LrcMismatch)
                            receiveCounters[3]++;
                        else
                            receiveCounters[4]++;
                        receiveCounters[5] = (ushort)status;
                        Resynchronize(port);
                    }
                    store.SetInputRegisters(120, 6, receiveCounters);
                }
#endif
            }
        }

#if SERIAL_RECEIVE_PROBE
        // 수신 중 USB 로그 출력을 피하도록 먼저 기록하고, 1초간 무통신이 관측된 뒤 출력합니다.
        private static void ProbeReceiveBatches(SerialPort port)
        {
            var counts = new int[32];
            var times = new long[32];
            var bytes = new byte[256];
            port.ReadTimeout = 1000;
            Debug.WriteLine("UART receive probe; no Modbus server or responses. Leave 2s between sends.");
            while (true)
            {
                int batches = 0;
                int total = 0;
                long first = 0;
                long last = 0;
                while (true)
                {
                    int available = port.BytesToRead;
                    long now = Environment.TickCount64;
                    if (available == 0)
                    {
                        if (batches > 0 && now - last >= 1000)
                            break;
                        Thread.Sleep(1);
                        continue;
                    }
                    if (batches == counts.Length)
                        throw new InvalidOperationException("UART receive probe exceeded 32 batches.");
                    int read = port.Read(bytes, 0, available < bytes.Length ? available : bytes.Length);
                    if (read == 0)
                        throw new InvalidOperationException("UART receive probe read no bytes.");
                    if (batches == 0)
                        first = now;
                    counts[batches] = read;
                    times[batches] = now - first;
                    batches++;
                    total += read;
                    last = now;
                }
                Debug.WriteLine("UART probe total=" + total + ", batches=" + batches);
                for (int i = 0; i < batches; i++)
                    Debug.WriteLine("UART batch offsetMs=" + times[i] + ", count=" + counts[i]);
            }
        }

#endif

        // 오류 프레임의 잔여 바이트가 다음 요청으로 해석되지 않도록 무통신 구간이 관측될 때까지 폐기합니다.
        private static void Resynchronize(SerialPort port)
        {
            do
            {
                int count = port.BytesToRead;
                if (count > DiscardBuffer.Length)
                    count = DiscardBuffer.Length;
                if (count > 0)
                    port.Read(DiscardBuffer, 0, count);
                Thread.Sleep(50);
            } while (port.BytesToRead != 0);
        }

#if SERIAL_DIAGNOSTICS
        // USB 로그 출력은 타이밍에 영향을 줍니다. 개별 요청 진단용이며 타이밍 적합성 시험용이 아닙니다.
        private sealed class DiagnosticChannel : IByteChannel
        {
            private readonly IByteChannel _inner;

            public DiagnosticChannel(IByteChannel inner)
            {
                _inner = inner;
            }

            public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
            {
                int read = _inner.Read(buffer, offset, count, timeoutMilliseconds);
                if (read > 0)
                    Log("UART RX", buffer, offset, read);
                return read;
            }

            public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
            {
                _inner.Write(buffer, offset, count, timeoutMilliseconds);
                Log("UART TX", buffer, offset, count);
            }

            private static void Log(string direction, byte[] bytes, int offset, int count)
            {
                const string hex = "0123456789ABCDEF";
                string text = direction + " count=" + count + ":";
                int shown = count > 32 ? 32 : count;
                for (int i = 0; i < shown; i++)
                {
                    byte value = bytes[offset + i];
                    text += " " + hex[value >> 4] + hex[value & 15];
                }
                Debug.WriteLine(text);
            }
        }
#endif
    }
}
