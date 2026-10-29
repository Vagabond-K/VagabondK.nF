using System;
using System.IO.Ports;
using System.Net.Sockets;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.ASCII;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Serial;
using VagabondK.nF.Modbus.Server;
using VagabondK.nF.Modbus.TCP;

namespace VagabondK.nF.PackageSmoke
{
    public static class Program
    {
        // 컴파일·연결 확인용 시험기입니다. 하드웨어를 열지 않도록 설계했으며 배포하지 마십시오.
        public static void Main()
        {
            var port = new SerialPort("COM2", 9600, Parity.Even, 8, StopBits.One);
            var channel = new SerialPortChannel(port);
            var rtu = new RtuTransport(channel, 9600, RtuReceiveMode.ResponseLength);
            var ascii = new AsciiTransport(channel);
            var store = new ArrayDataStore(8, 8, 8, 8);
            var server = new ModbusServer(1, ascii, store);
            var client = new ModbusClient(rtu);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var tcp = new TcpTransport(new SocketChannel(socket));
            var registers = new ushort[4];
            RegisterConvert.WriteSingle(registers, 0, -1.25f, RegisterWordOrder.LowWordFirst);
            if (RegisterConvert.ReadSingle(registers, 0, RegisterWordOrder.LowWordFirst) != -1.25f)
                throw new InvalidOperationException("Register numeric conversion failed.");
            RegisterConvert.WriteDouble(registers, 0, -2.0);
            if (RegisterConvert.ReadDouble(registers, 0) != -2.0)
                throw new InvalidOperationException("Register double conversion failed.");
            RegisterConvert.WriteAscii(registers, 0, 4, "ABC");
            if (RegisterConvert.ReadAscii(registers, 0, 4) != "ABC")
                throw new InvalidOperationException("Register ASCII conversion failed.");
            byte[] pdu = new byte[253];
            byte[] frame = new byte[513];
            pdu[0] = 3;
            int count = AsciiFrame.Encode(1, pdu, 5, frame);
            byte unit;
            int length;
            if (AsciiFrame.TryDecode(frame, count, pdu, out unit, out length) != ReceiveStatus.Success ||
                server == null || client == null || tcp == null)
                throw new InvalidOperationException("Package smoke fixture failed.");
            socket.Close();
            port.Dispose();
        }
    }
}
