#if DNP3_MASTER_MAPPING_SERIAL_SAMPLE
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using VagabondK.nF.DNP3.Serial;
using nanoFramework.Hardware.Esp32;

namespace VagabondK.nF.DNP3.MasterMapping.Sample
{
    // DNP3_MASTER_MAPPING_SERIAL_SAMPLE용 진입점입니다. GPIO17/18 및 COM2 9600 8E1이 보드와 통신 상대에 맞는지 확인합니다.
    // DM2472의 자동 방향 제어를 전제로 Normal UART를 사용하며 네이티브 RS485 모드를 켜지 않습니다.
    internal static class SerialProgram
    {
        // Master 응답 대기와 Link 송신 큐가 모두 비었을 때만 다음 읽기를 시작합니다.
        public static void Main()
        {
            Configuration.SetPinFunction(17, DeviceFunction.COM2_TX);
            Configuration.SetPinFunction(18, DeviceFunction.COM2_RX);
            var data = new ReceivedData();
            var sync = new object();
            var floatData = new FloatData();
            data.Observer = new FloatData.Dnp3MasterBinding(floatData, sync);
            var master = new Dnp3Master(2, 1, new ReceivedData.Dnp3MasterBinding(data, sync),
                responseTimeout: 10000, transportTimeout: 10000, maximumMeasurements: 64);
            while (true)
            {
                try
                {
                    using (var port = new SerialPort("COM2", 9600, Parity.Even, 8, StopBits.One))
                    {
                        // DM2472가 방향을 제어하므로 UART는 Normal 모드를 유지합니다.
                        port.Open();
                        var channel = new SerialPortChannel(port);
                        var session = new Dnp3SerialSession(channel, master, ackTimeoutMilliseconds: 1000);
                        session.Reset();
                        long next = 0;
                        while (true)
                        {
                            long now = channel.Milliseconds;
                            if (!master.Busy && session.TransmitIdle && now >= next)
                            {
                                if (master.Result != Dnp3MasterResult.Idle && master.Result != Dnp3MasterResult.Success)
                                    throw new IOException("Serial mapping request failed: " + master.Result);
                                session.Send(master.ReadClasses(15, now));
                                next = now + 3000;
                            }
                            session.RunOnce(10);
                        }
                    }
                }
                catch (IOException error) { Debug.WriteLine("Serial mapping I/O: " + error.Message); }
                catch (TimeoutException error) { Debug.WriteLine("Serial mapping timeout: " + error.Message); }
                catch (Dnp3ProtocolException error) { Debug.WriteLine("Serial mapping protocol: " + error.Message); }
                Thread.Sleep(3000);
            }
        }
    }
}
#endif
