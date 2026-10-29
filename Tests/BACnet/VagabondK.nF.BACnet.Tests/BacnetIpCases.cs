using System;
using System.Net;
using System.Net.Sockets;

using VagabondK.nF.BACnet.IP;

namespace VagabondK.nF.BACnet.Tests
{
    /// <summary>루프백 UDP로 <see cref="BacnetIpSession"/>의 소켓 소유권이 호출자에게 유지되는지 확인하고 유휴 타임아웃, 0바이트·짧은·길이 초과 데이터그램 처리와 거부 후 복구를 검사합니다.</summary>
    internal static class BacnetIpCases
    {
        private sealed class Values : IBacnetValueStore
        {
            public int Count
            {
                get
                {
                    return 1;
                }
            }

            public BacnetObject GetObject(int index)
            {
                return new BacnetObject(1, false, true);
            }

            public BacnetSample Read(int index)
            {
                return new BacnetSample(0x80000000, true);
            }

            public void QueueWrite(int index, uint bits)
            {
                throw new InvalidOperationException("No test writes.");
            }
        }

        internal static void SoakNormal()
        {
            var device = new BacnetDevice(new Values(), 123, 0, "Independent");
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            peer.ReceiveTimeout = 2000;
            var session = new BacnetIpSession(socket, device);
            byte[] who = Cases.Bacnet(new byte[] { 0x10, 8 });
            var bytes = new byte[512];
            for (int i = 0; i < 100; i++)
            {
                long started = SoakRunner.StartExchange();
                peer.SendTo(who, socket.LocalEndPoint);
                Cases.Check(session.ProcessNext(1000));
                int count = peer.Receive(bytes);
                Cases.Check(count == 20 && bytes[6] == 0x10 && bytes[7] == 0);
                SoakRunner.EndExchange(started);
            }
        }

        internal static void Run()
        {
            var device = new BacnetDevice(new Values(), 123, 0, "Independent");
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            peer.ReceiveTimeout = 1000;
            var session = new BacnetIpSession(socket, device);
            Cases.Check(!session.ProcessNext(0));
            foreach (int size in new[] { 0, 4, 513, 600, 2048 })
            {
                peer.SendTo(new byte[size], socket.LocalEndPoint);
                bool rejected = false;
                try
                {
                    session.ProcessNext(1000);
                }
                catch (BacnetProtocolException)
                {
                    rejected = true;
                }
                Cases.Check(rejected);
                byte[] who = Cases.Bacnet(new byte[] { 0x10, 8 });
                peer.SendTo(who, socket.LocalEndPoint);
                Cases.Check(session.ProcessNext(1000));
                var bytes = new byte[512];
                int count = peer.Receive(bytes);
                Cases.Check(count == 20 && bytes[6] == 0x10 && bytes[7] == 0);
            }
            bool invalid = false;
            try
            {
                session.ProcessNext(60001);
            }
            catch (ArgumentOutOfRangeException)
            {
                invalid = true;
            }
            Cases.Check(invalid);
            Console.WriteLine("BACNET IP SESSION PASS: caller-owned UDP, idle timeout, zero/short/oversized datagrams, recovery after every rejection.");
        }
    }
}
