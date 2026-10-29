using System;
using System.Text;

namespace VagabondK.nF.BACnet.Tests
{
    /// <summary>손으로 만든 바이트 프레임으로 <see cref="BacnetDevice"/>의 생성자 제한, 프레이밍, Who-Is 범위, PDU 거부, 읽기/쓰기 오류와 경계값을 검사합니다.</summary>
    internal static class BacnetDeviceCases
    {
        private sealed class Store : IBacnetValueStore
        {
            internal BacnetObject[] Objects;
            internal uint[] Bits;
            internal int WriteIndex = -1;
            internal uint WriteBits;
            internal int CountOverride = -2;

            public int Count
            {
                get
                {
                    return CountOverride != -2 ? CountOverride : Objects.Length;
                }
            }

            public BacnetObject GetObject(int index)
            {
                return Objects[index];
            }

            public BacnetSample Read(int index)
            {
                return new BacnetSample(Bits[index], index != 1);
            }

            public void QueueWrite(int index, uint bits)
            {
                WriteIndex = index;
                WriteBits = bits;
            }
        }

        private static void Fails<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                Cases.Check(true);
                return;
            }
            throw new InvalidOperationException("BACnet expected " + typeof(T).Name);
        }

        private static Store Points()
        {
            return new Store
            {
                Objects = new[] { new BacnetObject(1, false, true), new BacnetObject(2, true, true), new BacnetObject(3, false) },
                Bits = new uint[] { 0x3fc00000, 1, 0 }
            };
        }

        private static byte[] Confirmed(byte service, byte[] body, byte pdu = 0, byte max = 5, byte invoke = 7)
        {
            var apdu = new byte[4 + body.Length];
            apdu[0] = pdu;
            apdu[1] = max;
            apdu[2] = invoke;
            apdu[3] = service;
            Array.Copy(body, 0, apdu, 4, body.Length);
            return Cases.Bacnet(apdu);
        }

        private static byte[] Id(uint type, uint instance, params byte[] tail)
        {
            uint id = (type << 22) | instance;
            var body = new byte[5 + tail.Length];
            body[0] = 0x0c;
            body[1] = (byte)(id >> 24);
            body[2] = (byte)(id >> 16);
            body[3] = (byte)(id >> 8);
            body[4] = (byte)id;
            Array.Copy(tail, 0, body, 5, tail.Length);
            return body;
        }

        private static void Error(byte[] reply, byte service, int errorClass, int code)
        {
            Cases.Check(reply.Length == 13 && reply[6] == 0x50 && reply[7] == 7 && reply[8] == service &&
                reply[9] == 0x91 && reply[10] == errorClass && reply[11] == 0x91 && reply[12] == code);
        }

        private static void Rejected(byte[] reply, bool abort, int reason)
        {
            Cases.Check(reply.Length == 9 && reply[6] == (abort ? 0x71 : 0x60) && reply[7] == 7 && reply[8] == reason);
        }

        internal static void Run()
        {
            Constructor();
            Framing();
            Reads();
            Writes();
            Console.WriteLine("BACNET DEVICE PASS: constructor limits, framing, Who-Is range, PDU rejects, read/write errors and boundaries.");
        }

        private static void Constructor()
        {
            Fails<ArgumentNullException>(() => new BacnetDevice(null, 1, 0, "D"));
            Fails<ArgumentException>(() => new BacnetDevice(Points(), 4194303, 0, "D"));
            new BacnetDevice(Points(), 4194302, 0, "D");
            new BacnetDevice(Points(), 0, 65535, "D");
            foreach (string name in new[] { null, "", new string('a', 65), "a\u001f", "\u007f", "\u00e9", "\u3131" })
                Fails<ArgumentException>(() => new BacnetDevice(Points(), 1, 0, name));
            foreach (string name in new[] { "a", " ", "~", new string('z', 64), " !~" })
                Cases.Check(new BacnetDevice(Points(), 1, 0, name) != null);
            Fails<ArgumentException>(() => new BacnetDevice(new Store { Objects = new BacnetObject[0], CountOverride = -1 }, 1, 0, "D"));
            var many = new BacnetObject[65];
            for (int i = 0; i < many.Length; i++)
                many[i] = new BacnetObject((uint)i, false);
            Fails<ArgumentException>(() => new BacnetDevice(new Store { Objects = many }, 1, 0, "D"));
            Cases.Check(new BacnetDevice(new Store { Objects = many, CountOverride = 64 }, 1, 0, "D") != null);
            Cases.Check(new BacnetDevice(new Store { Objects = new BacnetObject[0] }, 1, 0, "D") != null);
            Fails<ArgumentException>(() => new BacnetDevice(new Store { Objects = new BacnetObject[] { null } }, 1, 0, "D"));
            Fails<ArgumentException>(() => new BacnetDevice(new Store
            {
                Objects = new[] { new BacnetObject(5, true), new BacnetObject(5, true) }
            }, 1, 0, "D"));
            Cases.Check(new BacnetDevice(new Store
            {
                Objects = new[] { new BacnetObject(5, true), new BacnetObject(5, false) }
            }, 1, 0, "D") != null);
        }

        internal static void Framing()
        {
            var device = new BacnetDevice(Points(), 1000, 7, "Device");
            Fails<ArgumentNullException>(() => device.Process(null));
            byte[] who = Cases.Bacnet(new byte[] { 0x10, 8 });
            byte[] iam = device.Process(who);
            Cases.Check(iam.Length == 20 && iam[6] == 0x10 && iam[7] == 0 && iam[8] == 0xc4);
            foreach (var mutate in new Action<byte[]>[] { b => b[0] = 0x82, b => b[1] = 0x0c, b => b[3]++, b => b[4] = 2, b => b[5] = 2 })
            {
                var bad = (byte[])who.Clone();
                mutate(bad);
                Fails<BacnetProtocolException>(() => device.Process(bad));
            }
            Cases.Check(device.Process(Bacnet(who, 0x0b)) != null);
            Fails<BacnetProtocolException>(() => device.Process(new byte[7] { 0x81, 0x0a, 0, 7, 1, 4, 0x10 }));
            var big = new byte[513];
            Array.Copy(who, big, who.Length);
            big[2] = 2;
            big[3] = 1;
            Fails<BacnetProtocolException>(() => device.Process(big));
            Who(device, 0, 4194303, true);
            Who(device, 1000, 1000, true);
            Who(device, 999, 999, false);
            Who(device, 1001, 4194303, false);
            Fails<BacnetProtocolException>(() => Who(device, 2000, 1000, false));
            Fails<BacnetProtocolException>(() => Who(device, 0, 4194304, false));
            Fails<BacnetProtocolException>(() => device.Process(Cases.Bacnet(new byte[] { 0x10, 8, 0x09, 0 })));
            Fails<BacnetProtocolException>(() => device.Process(Cases.Bacnet(new byte[] { 0x10, 8, 0x09, 0, 0x19, 1, 0 })));
            Fails<BacnetProtocolException>(() => device.Process(Cases.Bacnet(new byte[] { 0x00, 5 })));
            Fails<BacnetProtocolException>(() => device.Process(Confirmed(12, Id(8, 1000, 0x19, 77), pdu: 0x20)));
            byte[] read = Id(8, 1000, 0x19, 77);
            Rejected(device.Process(Confirmed(12, read, pdu: 0x08)), true, 4);
            Rejected(device.Process(Confirmed(12, read, pdu: 0x04)), true, 4);
            Rejected(device.Process(Confirmed(12, read, pdu: 0x01)), false, 4);
            Rejected(device.Process(Confirmed(12, read, max: 6)), false, 2);
            Rejected(device.Process(Confirmed(12, read, max: 0x85)), false, 2);
            Cases.Check(device.Process(Confirmed(12, read, max: 0x75))[6] == 0x30);
            Rejected(device.Process(Confirmed(14, read)), false, 9);
            Rejected(device.Process(Confirmed(12, new byte[] { 0x0c, 2, 0, 3 })), false, 4);
            Rejected(device.Process(Confirmed(12, Id(8, 1000, 0x19, 77, 0))), false, 4);
            Rejected(device.Process(Confirmed(12, Id(8, 1000, 0x1d, 77, 0, 0, 0, 0))), false, 4);
            Rejected(device.Process(Confirmed(12, Id(8, 1000, 0x18))), false, 4);
        }

        private static byte[] Bacnet(byte[] frame, byte function)
        {
            var copy = (byte[])frame.Clone();
            copy[1] = function;
            return copy;
        }

        private static void Who(BacnetDevice device, uint low, uint high, bool match)
        {
            var apdu = new byte[12];
            apdu[0] = 0x10;
            apdu[1] = 8;
            int p = 2;
            foreach (var pair in new[] { new[] { 0u, low }, new[] { 1u, high } })
            {
                int width = pair[1] <= 255 ? 1 : pair[1] <= 65535 ? 2 : pair[1] <= 16777215 ? 3 : 4;
                apdu[p++] = (byte)((pair[0] << 4) | 8 | (uint)width);
                for (int shift = (width - 1) * 8; shift >= 0; shift -= 8)
                    apdu[p++] = (byte)(pair[1] >> shift);
            }
            var exact = new byte[p];
            Array.Copy(apdu, exact, p);
            byte[] reply = device.Process(Cases.Bacnet(exact));
            Cases.Check(match ? reply != null && reply[6] == 0x10 : reply == null);
        }

        private static void Reads()
        {
            var store = Points();
            var device = new BacnetDevice(store, 1000, 260, "Device");
            byte[] name = device.Process(Confirmed(12, Id(8, 1000, 0x19, 77)));
            byte[] expected = Encoding.ASCII.GetBytes("Device");
            Cases.Check(name[6] == 0x30 && name[7] == 7 && name[8] == 12 && name[16] == 0x3e && name[17] == 0x75 &&
                name[18] == expected.Length + 1 && name[19] == 0 && name[name.Length - 1] == 0x3f);
            for (int i = 0; i < expected.Length; i++)
                Cases.Check(name[20 + i] == expected[i]);
            byte[] vendor = device.Process(Confirmed(12, Id(8, 1000, 0x19, 120)));
            Cases.Check(vendor[17] == 0x22 && vendor[18] == 1 && vendor[19] == 4);
            byte[] count = device.Process(Confirmed(12, Id(8, 1000, 0x19, 76, 0x29, 0)));
            Cases.Check(count[16] == 0x29 && count[17] == 0 && count[18] == 0x3e && count[19] == 0x21 && count[20] == 4);
            byte[] last = device.Process(Confirmed(12, Id(8, 1000, 0x19, 76, 0x29, 4)));
            Cases.Check(last[19] == 0xc4 && last[20] == 0 && last[21] == 0x80 && last[22] == 0 && last[23] == 3);
            byte[] first = device.Process(Confirmed(12, Id(8, 1000, 0x19, 76, 0x29, 1)));
            Cases.Check(first[19] == 0xc4 && first[20] == 2 && first[23] == 0xe8);
            Error(device.Process(Confirmed(12, Id(8, 1000, 0x19, 76, 0x29, 5))), 12, 2, 42);
            Error(device.Process(Confirmed(12, Id(8, 1000, 0x19, 76, 0x2c, 0x80, 0, 0, 0))), 12, 2, 42);
            Error(device.Process(Confirmed(12, Id(8, 1000, 0x19, 77, 0x29, 1))), 12, 2, 50);
            Error(device.Process(Confirmed(12, Id(8, 1001, 0x19, 77))), 12, 1, 31);
            Error(device.Process(Confirmed(12, Id(2, 4, 0x19, 85))), 12, 1, 31);
            Error(device.Process(Confirmed(12, Id(8, 1000, 0x19, 85))), 12, 2, 32);
            Error(device.Process(Confirmed(12, Id(2, 1, 0x19, 200))), 12, 2, 32);
            Error(device.Process(Confirmed(12, Id(5, 2, 0x19, 117))), 12, 2, 32);
            byte[] analog = device.Process(Confirmed(12, Id(2, 1, 0x19, 85)));
            Cases.Check(analog[17] == 0x44 && analog[18] == 0x3f && analog[19] == 0xc0 && analog[20] == 0 && analog[21] == 0);
            byte[] status = device.Process(Confirmed(12, Id(5, 2, 0x19, 111)));
            Cases.Check(status[17] == 0x82 && status[18] == 4 && status[19] == 0x40);
            byte[] reliability = device.Process(Confirmed(12, Id(5, 2, 0x19, 103)));
            Cases.Check(reliability[17] == 0x91 && reliability[18] == 7);
            byte[] binary = device.Process(Confirmed(12, Id(5, 2, 0x19, 85)));
            Cases.Check(binary[17] == 0x91 && binary[18] == 1);
            byte[] objectName = device.Process(Confirmed(12, Id(5, 2, 0x19, 77)));
            Cases.Check(Encoding.ASCII.GetString(objectName, 20, objectName[18] - 1) == "point-5-2");
            var many = new BacnetObject[64];
            for (int i = 0; i < many.Length; i++)
                many[i] = new BacnetObject((uint)i, true);
            var full = new BacnetDevice(new Store { Objects = many }, 1000, 7, "Device");
            Rejected(full.Process(Confirmed(12, Id(8, 1000, 0x19, 76), max: 2)), true, 4);
            byte[] list = full.Process(Confirmed(12, Id(8, 1000, 0x19, 76), max: 3));
            Cases.Check(list.Length == 343 && list[6] == 0x30 && list[342] == 0x3f);
            Cases.Check(device.Process(Confirmed(12, Id(8, 1000, 0x19, 76), max: 0)).Length == 38);
            byte[] fiftyByteLimit = full.Process(Confirmed(12, Id(8, 1000, 0x19, 76, 0x29, 0), max: 0));
            Cases.Check(fiftyByteLimit[6] == 0x30);
            store.Bits[1] = 2;
            Fails<InvalidOperationException>(() => device.Process(Confirmed(12, Id(5, 2, 0x19, 85))));
        }

        private static void Writes()
        {
            var store = Points();
            var device = new BacnetDevice(store, 1000, 7, "Device");
            byte[] ack = device.Process(Confirmed(15, Id(2, 1, 0x19, 85, 0x3e, 0x44, 0x41, 0x20, 0, 0, 0x3f)));
            Cases.Check(ack.Length == 9 && ack[6] == 0x20 && ack[7] == 7 && ack[8] == 15 &&
                store.WriteIndex == 0 && store.WriteBits == 0x41200000);
            ack = device.Process(Confirmed(15, Id(5, 2, 0x19, 85, 0x3e, 0x91, 1, 0x3f)));
            Cases.Check(ack[6] == 0x20 && store.WriteIndex == 1 && store.WriteBits == 1);
            ack = device.Process(Confirmed(15, Id(5, 2, 0x19, 85, 0x3e, 0x91, 0, 0x3f)));
            Cases.Check(ack[6] == 0x20 && store.WriteBits == 0);
            store.WriteIndex = -1;
            Error(device.Process(Confirmed(15, Id(5, 2, 0x19, 85, 0x3e, 0x91, 2, 0x3f))), 15, 2, 37);
            Error(device.Process(Confirmed(15, Id(5, 2, 0x19, 85, 0x3e, 0x91))), 15, 2, 9);
            Rejected(device.Process(Confirmed(15, Id(5, 2, 0x19, 85, 0x3e, 0x91, 1))), false, 4);
            Error(device.Process(Confirmed(15, Id(5, 2, 0x19, 85, 0x3e, 0x44, 0, 0, 0, 0, 0x3f))), 15, 2, 9);
            Error(device.Process(Confirmed(15, Id(2, 1, 0x19, 85, 0x3e, 0x91, 1, 0x3f))), 15, 2, 9);
            Error(device.Process(Confirmed(15, Id(2, 1, 0x19, 85, 0x3e, 0x44, 0x41, 0x20, 0))), 15, 2, 9);
            Rejected(device.Process(Confirmed(15, Id(2, 1, 0x19, 85, 0x3e, 0x44, 0x41, 0x20, 0, 0))), false, 4);
            foreach (uint bits in new uint[] { 0x7f800000, 0xff800000, 0x7fc00000, 0x7f800001 })
                Error(device.Process(Confirmed(15, Id(2, 1, 0x19, 85, 0x3e, 0x44,
                    (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits, 0x3f))), 15, 2, 37);
            Cases.Check(device.Process(Confirmed(15, Id(2, 1, 0x19, 85, 0x3e, 0x44, 0x7f, 0x7f, 0xff, 0xff, 0x3f)))[6] == 0x20);
            Error(device.Process(Confirmed(15, Id(2, 3, 0x19, 85, 0x3e, 0x44, 0, 0, 0, 0, 0x3f))), 15, 2, 40);
            Error(device.Process(Confirmed(15, Id(2, 1, 0x19, 77, 0x3e, 0x44, 0, 0, 0, 0, 0x3f))), 15, 2, 40);
            Error(device.Process(Confirmed(15, Id(8, 1000, 0x19, 85, 0x3e, 0x44, 0, 0, 0, 0, 0x3f))), 15, 2, 40);
            Error(device.Process(Confirmed(15, Id(2, 1, 0x19, 85, 0x3e, 0x44, 0, 0, 0, 0, 0x3f, 0x49, 8))), 15, 2, 40);
            Rejected(device.Process(Confirmed(15, Id(2, 1, 0x19, 85, 0x44, 0, 0, 0, 0, 0x3f))), false, 4);
            Rejected(device.Process(Confirmed(15, Id(2, 1, 0x19, 85))), false, 4);
            Cases.Check(store.WriteIndex == 0 && store.WriteBits == 0x7f7fffff);
        }
    }
}
