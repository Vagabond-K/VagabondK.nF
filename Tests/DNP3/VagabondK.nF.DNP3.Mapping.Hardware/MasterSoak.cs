using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VagabondK.nF.DNP3;

// UDP 20002의 명령으로 반복 수신·오프라인 시간 초과·재시작 후 복구를 수행하는 보드용 장시간 검증기입니다.
// Serial 조건에서는 GPIO17/18의 COM2 9600 8E1을 사용합니다. 제어 포트와 통신 상대는 격리된 테스트망에 둡니다.
internal static class MasterSoak
{
    private static readonly MasterValidation validation = new MasterValidation();
    private static readonly uint[] metrics = new uint[18];
    private static readonly byte[] response = new byte[88];
    private static uint sequence, status = 2;
    private static byte command;
    private static string failure;
    private static long started;
#if DNP3_TCP_RECOVERY
    private static Socket recoveryListener;
#endif

    internal static void Run()
    {
#if DNP3_MASTER_MAPPING_SERIAL
        nanoFramework.Hardware.Esp32.Configuration.SetPinFunction(17, nanoFramework.Hardware.Esp32.DeviceFunction.COM2_TX);
        nanoFramework.Hardware.Esp32.Configuration.SetPinFunction(18, nanoFramework.Hardware.Esp32.DeviceFunction.COM2_RX);
        using (var port = new System.IO.Ports.SerialPort("COM2", 9600, System.IO.Ports.Parity.Even, 8, System.IO.Ports.StopBits.One))
        {
            port.Open();
            validation.InitializeSerial(new VagabondK.nF.DNP3.Serial.SerialPortChannel(port));
            Serve();
        }
#else
#if DNP3_TCP_RECOVERY
        using (recoveryListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            recoveryListener.Bind(new IPEndPoint(IPAddress.Any, 20003));
            recoveryListener.Listen(1);
            Serve();
        }
#else
        Serve();
#endif
#endif
    }
    private static void Serve()
    {
        started = Environment.TickCount64;
        using (var diagnostic = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            diagnostic.Bind(new IPEndPoint(IPAddress.Any, 20002));
            var request = new byte[10];
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int length = diagnostic.ReceiveFrom(request, ref remote);
                if (length != 9 || request[0] != 68 || request[1] != 77 || request[2] != 83 || request[3] != 49)
                {
                    Debug.WriteLine("Rejected soak diagnostic packet.");
                    continue;
                }
                uint incoming = Read(request, 4);
                if (incoming == sequence + 1 && failure == null)
                {
                    sequence = incoming;
                    command = request[8];
                    try
                    {
                        Execute(command, diagnostic, remote);
                        status = 2;
                    }
                    catch (Exception error)
                    {
                        failure = "command " + request[8] + ": " + error +
                            (error is SocketException ? " socket code=" + ((SocketException)error).ErrorCode : "");
                        Debug.WriteLine("DNP3 MASTER SOAK FAILED: " + failure);
                        status = 3;
                    }
                }
                else if (incoming != sequence)
                    Debug.WriteLine("Rejected stale/out-of-order soak command.");
                Send(diagnostic, remote);
            }
        }
    }
    private static void Execute(byte command, Socket diagnostic, EndPoint remote)
    {
        if (command == 0) return;
        if (command == 1)
        {
#if DNP3_MASTER_MAPPING_SERIAL
            validation.RunSerial(8);
#else
            validation.Run("192.168.0.210", 20000, 8);
#endif
            metrics[0]++;
        }
#if DNP3_MASTER_MAPPING_SERIAL
        else if (command == 2 || command == 5)
        {
            validation.PrepareSerialFault();
            status = 1;
            Send(diagnostic, remote);
            metrics[7] = (uint)validation.AwaitSerialTimeout(command == 2);
            metrics[6]++;
        }
        else if (command == 4)
        {
            status = 1;
            Send(diagnostic, remote);
            validation.AwaitSerialPartial();
            metrics[7] = 6;
            metrics[6]++;
        }
        else if (command >= 6 && command <= 11)
        {
            status = 1;
            Send(diagnostic, remote);
            var probe = new SerialCrcValidation(command < 9);
            probe.Run(validation.SerialChannel, (command - 6) % 3);
            metrics[16] = (uint)probe.CrcErrors;
            metrics[17] = (uint)probe.DiscardedBytes;
            metrics[7] = command + 1U;
            metrics[6]++;
        }
#else
        else if (command >= 6 && command <= 13)
        {
#if DNP3_TCP_RECOVERY
            status = 1;
            Send(diagnostic, remote);
            if (!recoveryListener.Poll(15000000, SelectMode.SelectRead))
                throw new TimeoutException("TCP recovery injector connection.");
            using (var socket = recoveryListener.Accept())
            {
                var probe = new SerialCrcValidation(command < 10);
                probe.RunTcp(socket, (command - 6) % 4);
                metrics[14] = (uint)probe.FrameTimeouts;
                metrics[16] = (uint)probe.CrcErrors;
                metrics[17] = (uint)probe.DiscardedBytes;
                metrics[7] = command + 1U;
                metrics[6]++;
            }
#else
            throw new ArgumentOutOfRangeException("command");
#endif
        }
        else if (command == 2)
        {
            validation.OpenFault("192.168.0.210", 20000);
            status = 1;
            Send(diagnostic, remote);
            metrics[7] = (uint)validation.AwaitFault();
            metrics[6]++;
            Debug.WriteLine("Expected injected DNP3 fault: " + metrics[7]);
        }
#endif
        else if (command == 3) validation.PeerRestarted();
        else throw new ArgumentOutOfRangeException("command");
    }
    private static void Send(Socket socket, EndPoint remote)
    {
        metrics[1] = (uint)validation.Checks;
        metrics[2] = (uint)validation.Received;
        metrics[3] = (uint)validation.Events;
        metrics[4] = (uint)validation.Unsolicited;
        metrics[5] = (uint)validation.Generation;
        metrics[8] = nanoFramework.Runtime.Native.GC.Run(true);
        if (metrics[0] >= 2 && status == 2 && command == 1)
        {
            if (metrics[11] == 0) metrics[9] = metrics[10] = metrics[11] = metrics[8];
            if (metrics[8] < metrics[9]) metrics[9] = metrics[8];
            if (metrics[8] > metrics[10]) metrics[10] = metrics[8];
        }
        metrics[12] = (uint)((Environment.TickCount64 - started) / 1000);
        metrics[13] = Debugger.IsAttached ? 1U : 0U;
        metrics[15] = 0x600D;
#if DNP3_MASTER_MAPPING_SERIAL
        metrics[14] = (uint)validation.SerialFrameTimeouts;
        Write(response, 12, (uint)validation.SerialRetries);
#endif
        response[0] = 68; response[1] = 77; response[2] = 83; response[3] = 49;
        Write(response, 4, sequence);
        Write(response, 8, status);
        for (int i = 0; i < metrics.Length; i++) Write(response, 16 + i * 4, metrics[i]);
        byte[] packet = response;
        if (failure != null)
        {
            byte[] text = Encoding.UTF8.GetBytes(failure);
            packet = new byte[response.Length + text.Length];
            Array.Copy(response, packet, response.Length);
            Array.Copy(text, 0, packet, response.Length, text.Length);
        }
        if (socket.SendTo(packet, remote) != packet.Length) throw new InvalidOperationException("Soak diagnostic send.");
    }
    private static uint Read(byte[] bytes, int p)
    {
        return ((uint)bytes[p] << 24) | ((uint)bytes[p + 1] << 16) | ((uint)bytes[p + 2] << 8) | bytes[p + 3];
    }
    private static void Write(byte[] bytes, int p, uint value)
    {
        bytes[p] = (byte)(value >> 24); bytes[p + 1] = (byte)(value >> 16);
        bytes[p + 2] = (byte)(value >> 8); bytes[p + 3] = (byte)value;
    }
}
