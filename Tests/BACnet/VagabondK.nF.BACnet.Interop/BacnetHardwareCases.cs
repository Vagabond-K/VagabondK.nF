using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using static VagabondK.nF.BACnet.Interop.Program;

namespace VagabondK.nF.BACnet.Interop
{
    /// <summary>실제 보드의 BACnet IPHost 샘플(Device 12345, UDP 47808)을 독립 클라이언트로 검사합니다.</summary>
    /// <remarks>I-Am 검색, Object_List·품질 읽기, 쓰기 후 복구, 잘못된 프레임 이후 정상 응답 회복을 확인합니다.
    /// 보드가 같은 IPv4 LAN에 있어야 하며 PC 방화벽에서 UDP를 허용해야 합니다. 샘플 값이 바뀌므로 운영 장비에는 사용하지 마십시오.</remarks>
    internal static class BacnetHardwareCases
    {
        internal static void Run(string host, int iterations)
        {
            Check(iterations >= 14 && iterations <= 1000000, "Hardware iterations must be 14..1000000.");
            IPAddress remote = IPAddress.Parse(host);
            Check(remote.AddressFamily == AddressFamily.InterNetwork, "BACnet hardware requires an IPv4 address.");
            string local;
            using (var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                route.Connect(new IPEndPoint(remote, 47808));
                local = ((IPEndPoint)route.LocalEndPoint).Address.ToString();
            }
            using var client = new BacnetClient(new BacnetIpUdpProtocolTransport(0, true, false, 1472, localEndpointIp: local), 2000, 0);
            using var iam = new ManualResetEventSlim();
            client.OnIam += (sender, peer, instance, maxApdu, segmentation, vendor) =>
            {
                if (instance == 12345 && maxApdu == 480 && segmentation == BacnetSegmentations.SEGMENTATION_NONE && vendor == 0)
                    iam.Set();
            };
            int errorClass = -1, errorCode = -1;
            client.OnError += (sender, peer, pdu, service, invoke, cls, code, bytes, offset, length) =>
            {
                errorClass = (int)cls;
                errorCode = (int)code;
            };
            client.Start();
            var address = new BacnetAddress(BacnetAddressTypes.IP, host + ":47808");
            var device = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 12345);
            var level = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 0);
            var limit = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 100);
            var enable = new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, 0);
            IList<BacnetValue> Read(BacnetObjectId obj, BacnetPropertyIds property, uint array = uint.MaxValue)
            {
                Check(client.ReadPropertyRequest(address, obj, property, out IList<BacnetValue> values, arrayIndex: array),
                    "MCU BACnet read failed: " + obj + "/" + property);
                return values;
            }
            void WriteError(BacnetObjectId obj, BacnetValue value, int code, byte? priority = null)
            {
                errorClass = errorCode = -1;
                using var result = (BacnetAsyncResult)client.BeginWritePropertyRequest(address, obj,
                    BacnetPropertyIds.PROP_PRESENT_VALUE, new[] { value }, true, priority: priority);
                Check(result.WaitForDone(4000), "MCU write error timeout.");
                client.EndWritePropertyRequest(result, out Exception error);
                Check(error != null && errorClass == 2 && errorCode == code, "MCU BACnet error mismatch.");
            }
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!iam.IsSet && DateTime.UtcNow < deadline)
            {
                client.WhoIs(12345, 12345, address);
                iam.Wait(1000);
            }
            Check(iam.IsSet, "MCU independent discovery timeout.");
            var objects = Read(device, BacnetPropertyIds.PROP_OBJECT_LIST);
            Check(objects.Count == 4, "MCU object list shape.");
            Check((float)Read(level, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value == 250f, "MCU source value.");
            var flags = (BacnetBitString)Read(level, BacnetPropertyIds.PROP_STATUS_FLAGS)[0].Value;
            Check(flags.bits_used == 4 && !flags.GetBit(1), "MCU source quality.");
            WriteError(level, new BacnetValue(1f), 40);
            WriteError(limit, new BacnetValue(float.NaN), 37);
            WriteError(limit, new BacnetValue(float.PositiveInfinity), 37);
            WriteError(limit, new BacnetValue(true), 9);
            WriteError(limit, new BacnetValue(1f), 40, 8);
            WriteError(enable, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 2U), 37);
            MalformedWriteRecovery(host, client, address, limit, enable);
            uint random = 20261024;
            for (int i = 0; i < iterations; i++)
            {
                uint bits = Fixtures.NextFiniteBits(ref random, i);
                float number = BitConverter.UInt32BitsToSingle(bits);
                Check(client.WritePropertyRequest(address, limit, BacnetPropertyIds.PROP_PRESENT_VALUE,
                    new[] { new BacnetValue(number) }), "MCU analog write.");
                Check(BitConverter.SingleToUInt32Bits((float)Read(limit, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value) == bits,
                    "MCU float32 bit mismatch: iteration=" + i + " bits=" + bits.ToString("X8"));
                uint bit = (uint)i & 1;
                Check(client.WritePropertyRequest(address, enable, BacnetPropertyIds.PROP_PRESENT_VALUE,
                    new[] { new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, bit) }), "MCU binary write.");
                Check(Convert.ToUInt32(Read(enable, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value) == bit, "MCU binary value mismatch.");
            }

            static void MalformedWriteRecovery(string host, BacnetClient client, BacnetAddress address,
                BacnetObjectId limit, BacnetObjectId enable)
            {
                using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                raw.Bind(new IPEndPoint(IPAddress.Any, 0));
                raw.ReceiveTimeout = 10000;
                var remote = new IPEndPoint(IPAddress.Parse(host), 47808);
                byte[] analog = {
                    0x81, 10, 0, 24, 1, 4, 0, 5, 0, 15, 0x0c, 0, 0x80, 0, 100, 0x19, 85,
                    0x3e, 0x44, 0x43, 0xc8, 0, 0, 0x3f
                };
                byte[] binary = {
                    0x81, 10, 0, 21, 1, 4, 0, 5, 0, 15, 0x0c, 1, 0x40, 0, 0, 0x19, 85,
                    0x3e, 0x91, 1, 0x3f
                };
                var cases = new List<(string Name, byte[] Frame, byte Pdu, byte Code)>();
                foreach (var source in new[] { analog, binary })
                    for (int length = 10; length < source.Length; length++)
                    {
                        var cut = new byte[length];
                        Array.Copy(source, cut, length);
                        cut[3] = (byte)length;
                        byte pdu = source == analog && length >= 18 && length <= 22 ||
                            source == binary && length >= 18 && length <= 19 ? (byte)0x50 : (byte)0x60;
                        cases.Add(((source == analog ? "analog" : "binary") + "-cut-" + length, cut, pdu,
                            pdu == 0x50 ? (byte)9 : (byte)4));
                    }
                foreach (string kind in new[] { "object-tag", "property-tag", "opening-tag", "value-tag",
                    "closing-tag", "trailing", "segmented" })
                {
                    var bad = new byte[analog.Length + (kind == "trailing" ? 1 : 0)];
                    Array.Copy(analog, bad, analog.Length);
                    bad[3] = (byte)bad.Length;
                    byte pdu = 0x60, code = 4;
                    if (kind == "object-tag") bad[10] = 0x0d;
                    else if (kind == "property-tag") bad[15] = 0x29;
                    else if (kind == "opening-tag") bad[17] = 0x2e;
                    else if (kind == "value-tag") { bad[18] = 0x91; pdu = 0x50; code = 9; }
                    else if (kind == "closing-tag") bad[23] = 0x2f;
                    else if (kind == "trailing") { pdu = 0x50; code = 40; }
                    else { bad[6] = 8; pdu = 0x71; }
                    cases.Add((kind, bad, pdu, code));
                }
                uint ReadBits(BacnetObjectId obj)
                {
                    Check(client.ReadPropertyRequest(address, obj, BacnetPropertyIds.PROP_PRESENT_VALUE,
                        out IList<BacnetValue> values), "Recovery read failed: " + obj);
                    return obj.Equals(limit) ? BitConverter.SingleToUInt32Bits((float)values[0].Value) :
                        Convert.ToUInt32(values[0].Value);
                }
                for (int i = 0; i < cases.Count; i++)
                {
                    var test = cases[i];
                    Check(client.WritePropertyRequest(address, limit, BacnetPropertyIds.PROP_PRESENT_VALUE,
                        new[] { new BacnetValue(123f) }), "Recovery baseline analog write.");
                    Check(client.WritePropertyRequest(address, enable, BacnetPropertyIds.PROP_PRESENT_VALUE,
                        new[] { new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 0U) }),
                        "Recovery baseline binary write.");
                    test.Frame[8] = (byte)(100 + i);
                    Check(raw.SendTo(test.Frame, remote) == test.Frame.Length, "Complete malformed Write send.");
                    var reply = new byte[512];
                    EndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                    int count = raw.ReceiveFrom(reply, ref peer);
                    Check(peer.Equals(remote) && count >= 9 && reply[0] == 0x81 && reply[1] == 10 &&
                        ((reply[2] << 8) | reply[3]) == count && reply[4] == 1 && reply[5] == 0 &&
                        reply[6] == test.Pdu && reply[7] == test.Frame[8],
                        "Device rejection identity/type mismatch: " + test.Name);
                    if (test.Pdu == 0x50)
                        Check(count == 13 && reply[8] == 15 && reply[9] == 0x91 && reply[10] == 2 &&
                            reply[11] == 0x91 && reply[12] == test.Code, "Write Error mismatch: " + test.Name);
                    else
                        Check(count == 9 && reply[8] == test.Code, "Reject/Abort reason mismatch: " + test.Name);
                    Check(!raw.Poll(100000, SelectMode.SelectRead), "Unexpected extra rejection response.");
                    Check(ReadBits(limit) == BitConverter.SingleToUInt32Bits(123f) && ReadBits(enable) == 0,
                        "Malformed Write changed values: " + test.Name);
                    float next = -500f - i;
                    Check(client.WritePropertyRequest(address, limit, BacnetPropertyIds.PROP_PRESENT_VALUE,
                        new[] { new BacnetValue(next) }), "Normal analog Write after rejection.");
                    Check(client.WritePropertyRequest(address, enable, BacnetPropertyIds.PROP_PRESENT_VALUE,
                        new[] { new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 1U) }),
                        "Normal binary Write after rejection.");
                    Check(ReadBits(limit) == BitConverter.SingleToUInt32Bits(next) && ReadBits(enable) == 1,
                        "Normal Read after rejection: " + test.Name);
                    Console.WriteLine("BACNET WRITE RECOVERY PASS: " + test.Name +
                        "; device Reject/Error/Abort observed; both values preserved; subsequent analog/binary Write/Read PASS.");
                }
                Console.WriteLine("BACNET WRITE RECOVERY COMPLETE: " + cases.Count +
                    " cases; raw malformed APDUs, independent BACnet 4.0.0 normal Write/Read.");
            }
            using (var malformed = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                foreach (byte[] frame in new[] { new byte[0], new byte[] { 0x81, 0x0a, 0, 4 }, new byte[513], new byte[1472] })
                {
                    malformed.SendTo(frame, new IPEndPoint(IPAddress.Parse(host), 47808));
                    Check(Read(device, BacnetPropertyIds.PROP_OBJECT_NAME).Count == 1,
                        "MCU malformed datagram recovery: length=" + frame.Length);
                }
            Console.WriteLine("BACNET MCU INDEPENDENT PASS: BACnet 4.0.0; direct UDP47808; discovery/Object_List/quality; " +
                iterations + " exact finite float32 + binary write/read cycles; write rejection and malformed recovery; no PLC/Modbus/physical outputs.");
        }
    }
}
