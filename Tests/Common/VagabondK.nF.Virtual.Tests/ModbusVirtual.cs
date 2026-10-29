using System;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.ASCII;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Server;

namespace VagabondK.nF.Virtual.Tests
{
    /// <summary>UART 없이 RTU·ASCII 프레임, 최대 수량, 손상 입력 후 복구를 nanoCLR에서 확인합니다.</summary>
    internal static class ModbusVirtual
    {
        internal static void Run()
        {
            Vectors();
            Session(false);
            Session(true);
            Recovery(false);
            Recovery(true);
            Fuzz(false);
            Fuzz(true);
        }

        private static void Vectors()
        {
            var frame = new byte[] { 1, 3, 0, 0, 0, 1 };
            Program.Check(RtuFrame.ComputeCrc(frame, 0, 6) == 0x0A84, "RTU CRC vector");
            var pdu = new byte[253];
            var ascii = new byte[] { (byte)':', (byte)'0', (byte)'1', (byte)'0', (byte)'3', (byte)'0', (byte)'0', (byte)'0', (byte)'0',
                (byte)'0', (byte)'0', (byte)'0', (byte)'1', (byte)'F', (byte)'B', 13, 10 };
            byte unit;
            int n = AsciiFrame.Decode(ascii, ascii.Length, pdu, out unit);
            Program.Check(unit == 1 && n == 5 && pdu[0] == 3 && pdu[4] == 1, "ASCII LRC vector");
            var rtu = new byte[RtuFrame.MaxLength];
            n = RtuFrame.Encode(1, new byte[] { 3, 0, 0, 0, 1 }, 5, rtu);
            Program.Check(n == 8 && rtu[6] == 0x84 && rtu[7] == 0x0A, "RTU CRC byte order");
            rtu[7] ^= 1;
            Program.Throws(() => RtuFrame.Decode(rtu, 8, pdu, out unit), "RTU CRC mismatch rejected");
        }

        // 같은 저장소·요청을 두 인코딩에 적용해 전송 형식 외의 동작이 일치하는지 비교합니다.
        private static void Session(bool asciiMode)
        {
            string mode = asciiMode ? "ASCII " : "RTU ";
            var store = new ArrayDataStore(4000, 4000, 300, 300);
            var link = new WireLink(asciiMode);
            link.Server = new ModbusServer(1, new ServerSide(link), store);
            var client = new ModbusClient(new ClientSide(link));

            var coils = new bool[2000];
            for (int i = 0; i < 1968; i++)
                coils[i] = (i * 7 % 3) == 0;
            client.WriteMultipleCoils(1, 5, 1968, coils, 0);
            var readCoils = new bool[2000];
            client.ReadCoils(1, 5, 2000, readCoils, 0);
            bool same = true;
            for (int i = 0; i < 1968; i++)
                same &= readCoils[i] == coils[i];
            Program.Check(same && !readCoils[1999], mode + "1968 coils round trip");

            var registers = new ushort[125];
            for (int i = 0; i < 123; i++)
                registers[i] = (ushort)(i * 0x0203 + 0x8000);
            registers[0] = 0xFFFF;
            registers[1] = 0x8000;
            registers[2] = 0x7FFF;
            client.WriteMultipleRegisters(1, 10, 123, registers, 0);
            client.WriteSingleRegister(1, 133, 0x8001);
            client.WriteSingleRegister(1, 134, 0xFFFE);
            var readRegisters = new ushort[125];
            client.ReadHoldingRegisters(1, 10, 125, readRegisters, 0);
            same = true;
            for (int i = 0; i < 123; i++)
                same &= readRegisters[i] == registers[i];
            Program.Check(same && readRegisters[123] == 0x8001 && readRegisters[124] == 0xFFFE, mode + "125 registers with sign bit");
            int promoted = (short)readRegisters[1];
            Program.Check(promoted == -32768 && (int)readRegisters[1] == 32768, mode + "ushort/short promotion");

            client.WriteSingleCoil(1, 3999, true);
            var last = new bool[1];
            client.ReadCoils(1, 3999, 1, last, 0);
            Program.Check(last[0], mode + "single coil at end of table");

            var inputs = new bool[2000];
            for (int i = 0; i < inputs.Length; i++)
                inputs[i] = (i & 5) == 1;
            store.SetDiscreteInputs(2000, 2000, inputs);
            var readInputs = new bool[2000];
            client.ReadDiscreteInputs(1, 2000, 2000, readInputs, 0);
            same = true;
            for (int i = 0; i < inputs.Length; i++)
                same &= readInputs[i] == inputs[i];
            Program.Check(same, mode + "2000 discrete inputs");

            var inputRegisters = new ushort[125];
            for (int i = 0; i < inputRegisters.Length; i++)
                inputRegisters[i] = (ushort)(0xFFFF - i * 3);
            store.SetInputRegisters(175, 125, inputRegisters);
            var readInputRegisters = new ushort[125];
            client.ReadInputRegisters(1, 175, 125, readInputRegisters, 0);
            same = true;
            for (int i = 0; i < inputRegisters.Length; i++)
                same &= readInputRegisters[i] == inputRegisters[i];
            Program.Check(same, mode + "125 input registers at table end");

            ExpectException(client, 299, 2, ExceptionCode.IllegalDataAddress, mode + "out of range exception");
            ExpectException(client, 0, 126, ExceptionCode.IllegalDataValue, mode + "oversized count exception");
        }

        private static void ExpectException(ModbusClient client, ushort address, ushort count, ExceptionCode expected, string message)
        {
            var values = new ushort[200];
            ExceptionCode code = 0;
            try
            {
                client.ReadHoldingRegisters(1, address, count, values, 0);
            }
            catch (ModbusException ex)
            {
                code = ex.Code;
            }
            catch (ArgumentException)
            {
                code = ExceptionCode.IllegalDataValue;
            }
            Program.Check(code == expected, message);
        }

        private static void Recovery(bool asciiMode)
        {
            string mode = asciiMode ? "ASCII" : "RTU";
            var store = new ArrayDataStore(1, 1, 1, 1);
            var link = new WireLink(asciiMode);
            link.Server = new ModbusServer(1, new ServerSide(link), store);
            var client = new ModbusClient(new ClientSide(link));
            var values = new ushort[1];
            client.WriteSingleRegister(1, 0, 123);
            for (int kind = 0; kind < 4; kind++)
            {
                int length = link.Encode(1, new byte[] { 6, 0, 0, 0x7f, 0xff }, 5, link.Request);
                if (kind == 0)
                    link.Request[asciiMode ? length - 3 : length - 1] ^= 1;
                else if (kind == 1)
                    length--;
                else if (kind == 2)
                    length = asciiMode ? 3 : 2;
                else if (asciiMode)
                    link.Request[3] = (byte)'G';
                else
                    link.Request[3] ^= 0x80;
                link.RequestLength = length;
                link.ReplyLength = 0;
                Program.Rejects(() => link.Server.ProcessNextRequest(), typeof(ModbusProtocolException),
                    mode + " malformed write rejected " + kind);
                store.ReadRegisters(false, 0, 1, values);
                Program.Check(values[0] == 123 && link.ReplyLength == 0,
                    mode + " rejected write preserves value and sends no reply " + kind);
                client.ReadHoldingRegisters(1, 0, 1, values, 0);
                Program.Check(values[0] == 123, mode + " GET after rejection " + kind);
                client.WriteSingleRegister(1, 0, 456);
                client.ReadHoldingRegisters(1, 0, 1, values, 0);
                Program.Check(values[0] == 456, mode + " SET/GET after rejection " + kind);
                client.WriteSingleRegister(1, 0, 123);
            }
        }

        private static void Fuzz(bool asciiMode)
        {
            var store = new ArrayDataStore(100, 100, 100, 100);
            var link = new WireLink(asciiMode);
            link.Server = new ModbusServer(1, new ServerSide(link), store);
            var random = new Lcg(502);
            var pdu = new byte[253];
            var reply = new byte[253];
            bool valid = true;
            for (int run = 0; run < 2000; run++)
            {
                int length = 1 + random.Next(asciiMode ? 252 : 252);
                for (int i = 0; i < length; i++)
                    pdu[i] = (byte)random.Next(256);
                if ((run & 3) == 0)
                    pdu[0] = (byte)(1 + random.Next(16));
                link.RequestLength = link.Encode(1, pdu, length, link.Request);
                link.ReplyLength = 0;
                link.Server.ProcessNextRequest();
                if (link.ReplyLength == 0)
                {
                    valid = false;
                    continue;
                }
                byte unit;
                int n = link.Decode(link.Reply, link.ReplyLength, reply, out unit);
                bool exception = reply[0] == (pdu[0] | 0x80) && n == 2 && reply[1] >= 1 && reply[1] <= 4;
                valid &= unit == 1 && n >= 2 && n <= 253 && (exception || reply[0] == pdu[0]);
            }
            Program.Check(valid, (asciiMode ? "ASCII" : "RTU") + " server answers random PDUs with valid replies");
        }

        private sealed class WireLink
        {
            private readonly bool _ascii;
            internal readonly byte[] Request, Reply;
            internal int RequestLength, ReplyLength;
            internal ModbusServer Server;

            internal WireLink(bool ascii)
            {
                _ascii = ascii;
                int size = ascii ? AsciiFrame.MaxLength : RtuFrame.MaxLength;
                Request = new byte[size];
                Reply = new byte[size];
            }

            internal int Encode(byte unit, byte[] pdu, int count, byte[] frame)
            {
                return _ascii ? AsciiFrame.Encode(unit, pdu, count, frame) : RtuFrame.Encode(unit, pdu, count, frame);
            }

            internal int Decode(byte[] frame, int count, byte[] pdu, out byte unit)
            {
                return _ascii ? AsciiFrame.Decode(frame, count, pdu, out unit) : RtuFrame.Decode(frame, count, pdu, out unit);
            }
        }

        private sealed class ClientSide : IModbusTransport
        {
            private readonly WireLink _link;

            internal ClientSide(WireLink link)
            {
                _link = link;
            }

            public bool IsSerial { get { return true; } }

            public void Send(FrameContext context, byte[] pdu, int count)
            {
                _link.RequestLength = _link.Encode(context.UnitId, pdu, count, _link.Request);
                _link.ReplyLength = 0;
            }

            public int Receive(byte[] pdu, out FrameContext context)
            {
                _link.Server.ProcessNextRequest();
                if (_link.ReplyLength == 0)
                    throw new Exception("No reply.");
                byte unit;
                int n = _link.Decode(_link.Reply, _link.ReplyLength, pdu, out unit);
                context = new FrameContext(unit, 0);
                return n;
            }
        }

        private sealed class ServerSide : IModbusTransport
        {
            private readonly WireLink _link;

            internal ServerSide(WireLink link)
            {
                _link = link;
            }

            public bool IsSerial { get { return true; } }

            public void Send(FrameContext context, byte[] pdu, int count)
            {
                _link.ReplyLength = _link.Encode(context.UnitId, pdu, count, _link.Reply);
            }

            public int Receive(byte[] pdu, out FrameContext context)
            {
                byte unit;
                int n = _link.Decode(_link.Request, _link.RequestLength, pdu, out unit);
                context = new FrameContext(unit, 0);
                return n;
            }
        }
    }

    internal sealed class Lcg
    {
        private uint _state;

        internal Lcg(uint seed)
        {
            _state = seed;
        }

        internal int Next(int bound)
        {
            _state = _state * 1664525 + 1013904223;
            return (int)((_state >> 8) % (uint)bound);
        }
    }
}
