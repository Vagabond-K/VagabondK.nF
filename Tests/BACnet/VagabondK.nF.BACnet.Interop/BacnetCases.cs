using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using static VagabondK.nF.BACnet.Interop.Program;

namespace VagabondK.nF.BACnet.Interop
{
    /// <summary>루프백 UDP에서 독립 BACnet 클라이언트로 검색, REAL/BOOL 읽기·쓰기, 오류 코드, 배열, 품질, APDU 제한을 검증합니다.</summary>
    /// <remarks>장치 쪽은 요청마다 <c>Serve</c>로 데이터그램 하나를 처리하며, 쓰기는 <see cref="TestBacnetStore.Commit"/> 후(다음 스캔)에 반영되는지 확인합니다.</remarks>
    internal static class BacnetCases
    {
        private static void Serve(Socket socket, BacnetDevice device)
        {
            var buffer = new byte[BacnetDevice.MaximumDatagram + 1];
            EndPoint peer = new IPEndPoint(IPAddress.Any, 0);
            int count = socket.ReceiveFrom(buffer, ref peer);
            var request = new byte[count];
            Array.Copy(buffer, request, count);
            byte[] reply = device.Process(request);
            if (reply != null && socket.SendTo(reply, peer) != reply.Length)
                throw new InvalidOperationException("Incomplete BACnet datagram send.");
        }

        private static IList<BacnetValue> Read(BacnetClient client, BacnetAddress peer,
            BacnetObjectId obj, BacnetPropertyIds property, uint array = uint.MaxValue)
        {
            Check(client.ReadPropertyRequest(peer, obj, property, out IList<BacnetValue> values, arrayIndex: array),
                "Independent BACnet read failed: " + obj + "/" + property);
            return values;
        }

        public static void Run(int iterations, uint seed)
        {
            var store = Fixtures.CreateBacnet(64);
            var device = new BacnetDevice(store, 12345, 0, "Evaluation");
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            udp.ReceiveTimeout = 100;
            var endpoint = (IPEndPoint)udp.LocalEndPoint;
            using var stop = new CancellationTokenSource();
            Exception workerError = null;
            int malformed = 0;
            var worker = new Thread(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            Serve(udp, device);
                        }
                        catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut) { }
                        catch (BacnetProtocolException)
                        {
                            Interlocked.Increment(ref malformed);
                        }
                    }
                }
                catch (Exception error)
                {
                    workerError = error;
                }
            });
            worker.Start();
            try
            {
                using var client = new BacnetClient(new BacnetIpUdpProtocolTransport(0, true, false, 1472, "127.0.0.1"), 1000, 0);
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
                int reject = -1;
                client.OnReject += (sender, peer, pdu, invoke, reason, bytes, offset, length) => reject = (int)reason;
                client.Start();
                var address = new BacnetAddress(BacnetAddressTypes.IP, "127.0.0.1:" + endpoint.Port);
                var level = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 0);
                var limit = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 100);
                var enabled = new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, 0);
                var dev = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 12345);
                client.WhoIs(12345, 12345, address);
                Check(iam.Wait(3000), "BACnet independent Who-Is/I-Am decode.");
                client.WhoIs(-1, -1, address);

                void ReadError(BacnetObjectId obj, BacnetPropertyIds property, int cls, int code, uint array = uint.MaxValue)
                {
                    errorClass = errorCode = -1;
                    using var result = (BacnetAsyncResult)client.BeginReadPropertyRequest(address, obj, property, true, arrayIndex: array);
                    Check(result.WaitForDone(2000), "BACnet expected error timed out.");
                    client.EndReadPropertyRequest(result, out _, out Exception error);
                    Check(error != null && errorClass == cls && errorCode == code, "Incorrect BACnet read error: " + errorClass + "/" + errorCode);
                }
                void WriteError(BacnetObjectId obj, BacnetValue value, int code, byte? priority = null)
                {
                    errorClass = errorCode = -1;
                    using var result = (BacnetAsyncResult)client.BeginWritePropertyRequest(address, obj,
                        BacnetPropertyIds.PROP_PRESENT_VALUE, new[] { value }, true, priority: priority);
                    Check(result.WaitForDone(2000), "BACnet expected write error timed out.");
                    client.EndWritePropertyRequest(result, out Exception error);
                    Check(error != null && errorClass == 2 && errorCode == code, "Incorrect BACnet write error: " + errorClass + "/" + errorCode);
                }
                void Quality(bool fault)
                {
                    var flags = (BacnetBitString)Read(client, address, level, BacnetPropertyIds.PROP_STATUS_FLAGS)[0].Value;
                    Check(flags.bits_used == 4 && !flags.GetBit(0) && flags.GetBit(1) == fault &&
                        !flags.GetBit(2) && !flags.GetBit(3), "BACnet status flags mismatch.");
                    Check(Convert.ToUInt32(Read(client, address, level, BacnetPropertyIds.PROP_RELIABILITY)[0].Value) ==
                        (fault ? 7U : 0U), "BACnet reliability mismatch.");
                }
                Quality(true); // NoData
                store.Set(Fixtures.Level, BitConverter.SingleToUInt32Bits(250f), true);
                Quality(false);
                Check((float)Read(client, address, level, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value == 250f, "BACnet REAL decoding.");
                store.SetQuality(Fixtures.Level, false);
                Quality(true);
                store.Set(Fixtures.Level, BitConverter.SingleToUInt32Bits(-12.5f), false);
                Quality(true);
                Check((float)Read(client, address, level, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value == -12.5f, "BACnet bad-quality value lost.");
                store.Set(Fixtures.Level, BitConverter.SingleToUInt32Bits(3.125f), true);
                Quality(false);

                Check((float)Read(client, address, limit, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value == 800f, "BACnet initial setting.");
                Check(client.WritePropertyRequest(address, limit, BacnetPropertyIds.PROP_PRESENT_VALUE, new[] { new BacnetValue(400f) }), "BACnet setting write.");
                Check((float)Read(client, address, limit, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value == 800f, "BACnet must not prematurely apply pending setting.");
                store.Commit();
                Check((float)Read(client, address, limit, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value == 400f, "BACnet commit did not apply setting.");
                Check(Convert.ToUInt32(Read(client, address, enabled, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value) == 1, "BACnet BOOL enum decode.");
                Check(client.WritePropertyRequest(address, enabled, BacnetPropertyIds.PROP_PRESENT_VALUE,
                    new[] { new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 0U) }), "BACnet BOOL write.");
                store.Commit();
                Check(Convert.ToUInt32(Read(client, address, enabled, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value) == 0, "BACnet BOOL application.");

                WriteError(level, new BacnetValue(1f), 40);
                WriteError(limit, new BacnetValue(true), 9);
                WriteError(limit, new BacnetValue(float.NaN), 37);
                WriteError(limit, new BacnetValue(float.PositiveInfinity), 37);
                WriteError(limit, new BacnetValue(float.NegativeInfinity), 37);
                WriteError(limit, new BacnetValue(1f), 40, 8);
                WriteError(enabled, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 2U), 37);
                WriteError(enabled, new BacnetValue(true), 9);
                store.Commit();
                Check((float)Read(client, address, limit, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value == 400f, "Rejected BACnet write mutated state.");
                Check(Convert.ToUInt32(Read(client, address, enabled, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value) == 0, "Rejected BOOL write mutated state.");
                ReadError(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 200), BacnetPropertyIds.PROP_PRESENT_VALUE, 1, 31);
                ReadError(level, (BacnetPropertyIds)999, 2, 32);
                ReadError(level, BacnetPropertyIds.PROP_PRESENT_VALUE, 2, 50, 0);
                ReadError(dev, BacnetPropertyIds.PROP_OBJECT_LIST, 2, 42, 66);
                var objects = Read(client, address, dev, BacnetPropertyIds.PROP_OBJECT_LIST);
                Check(objects.Count == 65 && (BacnetObjectId)objects[0].Value == dev, "BACnet maximum Object_List.");
                Check(Convert.ToUInt32(Read(client, address, dev, BacnetPropertyIds.PROP_OBJECT_LIST, 0)[0].Value) == 65, "BACnet array count.");
                Check((BacnetObjectId)Read(client, address, dev, BacnetPropertyIds.PROP_OBJECT_LIST, 1)[0].Value == dev, "BACnet array first.");
                Check((BacnetObjectId)Read(client, address, dev, BacnetPropertyIds.PROP_OBJECT_LIST, 65)[0].Value == (BacnetObjectId)objects[64].Value, "BACnet array last.");
                var services = (BacnetBitString)Read(client, address, dev, BacnetPropertyIds.PROP_PROTOCOL_SERVICES_SUPPORTED)[0].Value;
                for (byte i = 0; i < services.bits_used; i++)
                    Check(services.GetBit(i) == (i == 12 || i == 15 || i == 26 || i == 34), "BACnet service advertisement mismatch.");
                var types = (BacnetBitString)Read(client, address, dev, BacnetPropertyIds.PROP_PROTOCOL_OBJECT_TYPES_SUPPORTED)[0].Value;
                for (byte i = 0; i < types.bits_used; i++)
                    Check(types.GetBit(i) == (i == 2 || i == 5 || i == 8), "BACnet object-type advertisement mismatch.");
                Check(!(bool)Read(client, address, level, BacnetPropertyIds.PROP_OUT_OF_SERVICE)[0].Value, "BACnet Out_Of_Service decode.");
                using (var result = (BacnetAsyncResult)client.BeginReadPropertyMultipleRequest(address, level,
                    new[] { new BacnetPropertyReference(BacnetPropertyIds.PROP_PRESENT_VALUE, uint.MaxValue) }, true))
                {
                    Check(result.WaitForDone(2000), "BACnet unsupported-service Reject timed out.");
                    client.EndReadPropertyMultipleRequest(result, out _, out Exception failure);
                    Check(failure != null && reject == 9, "BACnet unrecognized-service Reject decode.");
                }

                using var small = new BacnetClient(new BacnetIpUdpProtocolTransport(0, true, false, 1472, "127.0.0.1",
                    (BacnetMaxAdpu)0), 1000, 0);
                int abort = -1;
                small.OnAbort += (sender, peer, pdu, invoke, reason, bytes, offset, length) => abort = (int)reason;
                small.Start();
                using (var result = (BacnetAsyncResult)small.BeginReadPropertyRequest(address, dev, BacnetPropertyIds.PROP_OBJECT_LIST, true))
                {
                    Check(result.WaitForDone(2000), "BACnet small-APDU abort timed out.");
                    small.EndReadPropertyRequest(result, out _, out Exception failure);
                    Check(failure != null && abort == 4, "BACnet segmentation-not-supported Abort decode.");
                }
                for (uint i = 1; i <= objects.Count; i++)
                    Check((BacnetObjectId)Read(client, address, dev, BacnetPropertyIds.PROP_OBJECT_LIST, i)[0].Value ==
                        (BacnetObjectId)objects[(int)i - 1].Value, "BACnet array element mismatch: " + i);
                uint random = seed;
                for (int i = 0; i < iterations; i++)
                {
                    uint bits = Fixtures.NextFiniteBits(ref random, i);
                    float number = BitConverter.UInt32BitsToSingle(bits);
                    bool bit = (random & 1) != 0;
                    string context = "seed=" + seed + ", iteration=" + i + ", bits=0x" + bits.ToString("X8");
                    Check(client.WritePropertyRequest(address, limit, BacnetPropertyIds.PROP_PRESENT_VALUE,
                        new[] { new BacnetValue(number) }), "BACnet stress REAL write: " + context);
                    Check(client.WritePropertyRequest(address, enabled, BacnetPropertyIds.PROP_PRESENT_VALUE,
                        new[] { new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, bit ? 1U : 0U) }),
                        "BACnet stress BOOL write: " + context);
                    store.Commit();
                    Check(BitConverter.SingleToUInt32Bits((float)Read(client, address, limit, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value) == bits,
                        "BACnet stress REAL bit-exact read: " + context);
                    Check(Convert.ToUInt32(Read(client, address, enabled, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value) == (bit ? 1U : 0U),
                        "BACnet stress BOOL read: " + context);
                    store.Set(Fixtures.Level, bits, i % 2 == 0);
                    Quality(i % 2 != 0);
                    Check(BitConverter.SingleToUInt32Bits((float)Read(client, address, level, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value) == bits,
                        "BACnet stress Input bit-exact read: " + context);
                    if (i % 32 == 0)
                    {
                        WriteError(limit, new BacnetValue(float.NaN), 37);
                        ReadError(level, (BacnetPropertyIds)999, 2, 32);
                    }
                }
                if (iterations > 0)
                    Console.WriteLine("BACNET STRESS PASS: " + iterations + " independent REAL/BOOL write-scan-read and Input/quality cycles, bit-exact finite boundaries/random values, seed=" + seed);
                using (var malformedSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    malformedSocket.SendTo(new byte[] { 0x81, 0x0a, 0, 4 }, endpoint);
                Check(SpinWait.SpinUntil(() => Volatile.Read(ref malformed) == 1, 2000), "BACnet malformed frame rejection.");
                Read(client, address, dev, BacnetPropertyIds.PROP_OBJECT_NAME);
                Console.WriteLine("BACNET INDEPENDENT PASS: BACnet 4.0.0 UDP discovery, REAL/BOOL read-write, next-scan commit, exact errors, 65-item arrays, service/object bits, NoData/Good/Bad/Stale, APDU Abort and malformed-frame recovery.");
            }
            finally
            {
                stop.Cancel();
                Check(worker.Join(3000), "BACnet worker did not terminate.");
                if (workerError != null)
                    throw new InvalidOperationException("BACnet worker failed.", workerError);
            }
        }
    }
}
