using System.Diagnostics;
using System.IO.Ports;
using System.Threading;

using VagabondK.nF.DNP3.Serial;
using nanoFramework.Hardware.Esp32;

namespace VagabondK.nF.DNP3.SerialHost
{
    /// <summary>실제 UART와 PC 피어를 사용해 DNP3 Master/Outstation을 검증하는 보드 진입점입니다.</summary>
    /// <remarks>DNP3_HARDWARE_TEST가 없으면 통신을 시작하지 않습니다. GPIO17/18, COM2 9600 8E1과 배선을 확인합니다.
    /// DM2472 자동 방향 제어를 전제로 Normal UART를 사용합니다. 제어 명령은 전용 테스트 데이터에만 적용합니다.</remarks>
    public static class Program
    {
        /// <summary>컴파일 조건에 따라 역할과 일반/수치 시나리오를 선택하여 Serial 세션을 반복 처리합니다.</summary>
        public static void Main()
        {
#if DNP3_HARDWARE_TEST
            Configuration.SetPinFunction(17, DeviceFunction.COM2_TX);
            Configuration.SetPinFunction(18, DeviceFunction.COM2_RX);
#if DNP3_NUMERIC_TEST
            var validation = new NumericValidation();
#else
            var validation = new SerialValidation();
#endif
            using (var port = new SerialPort("COM2", 9600, Parity.Even, 8, StopBits.One))
            {
                // DM2472가 방향을 자동 제어하므로 RTS/RS485 모드가 아닌 일반 UART 모드를 사용합니다.
                port.Open();
                var channel = new SerialPortChannel(port);
#if DNP3_MASTER_TEST
                var master = new Dnp3Master(2, 1, validation, responseTimeout: 30000, transportTimeout: 30000);
                var session = new Dnp3SerialSession(channel, master, ackTimeoutMilliseconds: 3000, frameTimeoutMilliseconds: 3000);
                Thread.Sleep(2000);
#if DNP3_NUMERIC_TEST
                validation.RunMaster(master, new ValidationSerialSession(channel, session));
                Debug.WriteLine("DNP3 MCU NUMERIC MASTER PASS: measurements=" + validation.Measurements +
                    "; retries=" + session.Retransmissions + "; CRCerrors=" + session.Decoder.CrcErrors +
                    "; frameTimeouts=" + session.Decoder.FrameTimeouts);
#else
                validation.RunMaster(channel, master, session, 100);
                Debug.WriteLine("DNP3 MCU MASTER PASS: checks=" + validation.Checks +
                    "; retries=" + session.Retransmissions + "; CRCerrors=" + session.Decoder.CrcErrors +
                    "; frameTimeouts=" + session.Decoder.FrameTimeouts);
#endif
                while (true)
                    session.RunOnce(10);
#else
#if DNP3_NUMERIC_TEST
                var station = new Dnp3Station(validation.Database, 1, 2, validation,
#else
                var station = new Dnp3Station(validation.Database, 1, 2, validation, validation,
#endif
                    fragmentSize: 256, confirmationTimeout: 30000, transportTimeout: 30000);
                var session = new Dnp3SerialSession(channel, station, ackTimeoutMilliseconds: 3000, frameTimeoutMilliseconds: 3000);
#if DNP3_NUMERIC_TEST
                while (!validation.Completed || !session.TransmitIdle)
                    session.RunOnce(10);
                Debug.WriteLine(
#if DNP3_NUMERIC_SOAK
                    "DNP3 MCU NUMERIC OUTSTATION SOAK PASS: operations=" +
#else
                    "DNP3 MCU NUMERIC OUTSTATION PASS: operations=" +
#endif
                    validation.Operations +
                    "; retries=" + session.Retransmissions + "; CRCerrors=" + session.Decoder.CrcErrors +
                    "; frameTimeouts=" + session.Decoder.FrameTimeouts);
                while (true)
                    session.RunOnce(10);
#else
                while (true)
                    session.RunOnce(10);
#endif
#endif
            }
#else
            Debug.WriteLine("DNP3 serial disabled. Hardware validation requires Dnp3HardwareTest=true and confirmed wiring.");
            Thread.Sleep(Timeout.Infinite);
#endif
        }
    }
}
