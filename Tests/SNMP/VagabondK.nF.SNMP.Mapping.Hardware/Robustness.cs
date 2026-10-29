using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.SNMP;
using VagabondK.nF.SNMP.UDP;

namespace VagabondK.nF.SNMP.Mapping.Sample
{
    public partial class DeviceData
    {
        internal void ChangeLocal(int slot)
        {
            switch (slot)
            {
                case 1: Setpoint = -111; break;
                case 2: Level = 54321; break;
                case 3: Profile = new uint[] { 2, 39, 77 }; break;
                default: throw new ArgumentOutOfRangeException("slot");
            }
        }

        internal void CheckOwnership()
        {
            var sync = new object();
            var store = new SnmpStore(this, sync);
            var original = SnmpValue.OctetString(new byte[] { 0, 255 });
            store.QueueWrite(1, original);
            byte[] copy = store.Read(1).GetBytes();
            copy[0] = 7;
            if (Label[0] != 0) throw new InvalidOperationException("Read ownership.");
            Label[0] = 3;
            if (original.GetBytes()[0] != 0) throw new InvalidOperationException("Write ownership.");
            var oid = SnmpValue.ObjectIdentifier(new uint[] { 2, 39, 17 });
            store.QueueWrite(7, oid);
            uint[] arcs = store.Read(7).GetObjectIdentifier();
            arcs[2] = 99;
            if (Profile[2] != 17) throw new InvalidOperationException("OID read ownership.");
            Profile[2] = 18;
            if (oid.GetObjectIdentifier()[2] != 17) throw new InvalidOperationException("OID write ownership.");
            Label = new byte[32];
            bool finished = false;
            Exception failure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    for (int n = 0; n < 800; n++)
                    {
                        lock (sync)
                            for (int i = 0; i < Label.Length; i++) Label[i] = (byte)n;
                        if ((n & 31) == 0) Thread.Sleep(1);
                    }
                }
                catch (Exception error) { failure = error; }
                finally { lock (sync) finished = true; }
            });
            writer.Start();
            int reads = 0;
            while (true)
            {
                copy = store.Read(1).GetBytes();
                for (int i = 1; i < copy.Length; i++)
                    if (copy[i] != copy[0]) throw new InvalidOperationException("Torn array snapshot.");
                reads++;
                bool done;
                lock (sync) done = finished;
                if (done && reads >= 800) break;
                if ((reads & 31) == 0) Thread.Sleep(1);
            }
            if (failure != null) throw failure;
        }
    }

    // 스냅샷/배열 소유권을 먼저 검사한 뒤 외부 피어를 받습니다. 진단 UDP 1162는 격리 시험 전용이며 인증이 없습니다.
    internal static class Robustness
    {
        public static void Main()
        {
            MappingSnapshotChecks.Checks.Run();
            new DeviceData().CheckOwnership();
            var model = new DeviceData();
            var sync = new object();
            var store = new DeviceData.SnmpStore(model, sync);
            bool ready = false;
            long deadline = Environment.TickCount64 + 90000;
            while (!ready)
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                    if (!string.IsNullOrEmpty(network.IPv4Address) && network.IPv4Address != "0.0.0.0" &&
                        network.IPv4Address != "127.0.0.1") ready = true;
                if (Environment.TickCount64 > deadline) throw new TimeoutException("Saved Wi-Fi required.");
                if (!ready) Thread.Sleep(1000);
            }
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            using (var diagnostic = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, 1161));
                diagnostic.Bind(new IPEndPoint(IPAddress.Any, 1162));
                var session = new SnmpUdpSession(socket, new SnmpAgent(store,
                    new SnmpSystem("SNMP static mapping evaluation", "mapping-node"), "public", "private"));
                byte[] request = new byte[6], response = new byte[12];
                while (true)
                {
                    try { session.ProcessNext(1); }
                    catch (SnmpProtocolException error) { System.Diagnostics.Debug.WriteLine(error.Message); }
                    if (!diagnostic.Poll(0, SelectMode.SelectRead)) continue;
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int length = diagnostic.ReceiveFrom(request, ref remote);
                    if (length != 5 || request[0] != 83 || request[1] != 78 || request[2] != 82 ||
                        request[3] != 49 || request[4] > 4)
                        throw new InvalidOperationException("Invalid SNMP diagnostic request.");
                    if (request[4] > 0 && request[4] < 4)
                        lock (sync) model.ChangeLocal(request[4]);
                    uint heap = nanoFramework.Runtime.Native.GC.Run(true);
                    Array.Copy(request, response, 4);
                    response[4] = request[4]; response[5] = 0x60; response[6] = 0x0d;
                    response[8] = (byte)(heap >> 24); response[9] = (byte)(heap >> 16);
                    response[10] = (byte)(heap >> 8); response[11] = (byte)heap;
                    if (diagnostic.SendTo(response, remote) != response.Length)
                        throw new InvalidOperationException("SNMP diagnostic send.");
                }
            }
        }
    }
}
