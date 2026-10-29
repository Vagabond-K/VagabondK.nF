using System;

using System.Net;
using System.Net.Sockets;

using VagabondK.nF.SNMP.UDP;

namespace VagabondK.nF.SNMP.Tests
{
    /// <summary>루프백 UDP로 <c>SnmpUdpSession</c>의 인수·시간 제한 범위 검증, 요청 처리, 잘못되거나 큰 데이터그램 거부 후 복구, Trap 전송을 검사합니다.</summary>
    internal static class UdpCases
    {
        internal static void SoakNormal()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                client.ReceiveTimeout = 2000;
                var store = new Store();
                var session = new SnmpUdpSession(server, AgentCases.Agent(store));
                for (int i = 0; i < 100; i++)
                {
                    long started = SoakRunner.StartExchange();
                    client.SendTo(Wire.Message(1, "private", 0xa3, i * 2, 0, 0,
                        Wire.Bind(Store.E + "1.0", Wire.Int(i))), server.LocalEndPoint);
                    Cases.Check(session.ProcessNext(1000));
                    var written = Receive(client);
                    Cases.Check(written.Id == i * 2 && written.Status == 0);
                    client.SendTo(Wire.Message(1, "public", 0xa0, i * 2 + 1, 0, 0,
                        Wire.Bind(Store.E + "1.0")), server.LocalEndPoint);
                    Cases.Check(session.ProcessNext(1000));
                    var read = Receive(client);
                    Cases.Check(read.Id == i * 2 + 1 && read.Status == 0 && read.Bindings[0].Signed == i);
                    SoakRunner.EndExchange(started);
                }
                Cases.Check(store.Writes == 100);
            }
        }

        internal static void Run()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                client.ReceiveTimeout = 2000;
                var store = new Store();
                var agent = AgentCases.Agent(store);
                var session = new SnmpUdpSession(server, agent);
                Cases.Throws<ArgumentNullException>(() => new SnmpUdpSession(null, agent));
                Cases.Throws<ArgumentNullException>(() => new SnmpUdpSession(server, null));
                Cases.Throws<ArgumentOutOfRangeException>(() => session.ProcessNext(-1));
                Cases.Throws<ArgumentOutOfRangeException>(() => session.ProcessNext(60001));
                Cases.Check(!session.ProcessNext(0));

                EndPoint target = server.LocalEndPoint;
                client.SendTo(Wire.Message(1, "public", 0xa0, 42, 0, 0, Wire.Bind(Store.E + "2.0")), target);
                Cases.Check(session.ProcessNext(1000));
                var reply = Receive(client);
                Cases.Check(reply.Id == 42 && reply.Bindings[0].Ascii == "hello");

                client.SendTo(new byte[] { 0x30, 0x03, 0x02, 0x01 }, target);
                Cases.Throws<SnmpProtocolException>(() => session.ProcessNext(1000));
                client.SendTo(new byte[SnmpAgent.MaximumMessage + 1], target);
                Cases.Throws<SnmpProtocolException>(() => session.ProcessNext(1000));
                byte[] exact = null;
                for (int n = 1300; n < 1472 && exact == null; n++)
                {
                    byte[] candidate = Wire.Message(1, "public", 0xa0, 45, 0, 0, Wire.Bind(Store.E + "2.0", Wire.Text(new string('x', n))));
                    if (candidate.Length == SnmpAgent.MaximumMessage)
                        exact = candidate;
                }
                Cases.Check(exact != null);
                client.SendTo(exact, target);
                Cases.Check(session.ProcessNext(1000));
                reply = Receive(client);
                Cases.Check(reply.Id == 45 && reply.Bindings[0].Ascii == "hello");
                var over = new byte[exact.Length + 1];
                Array.Copy(exact, over, exact.Length);
                Cases.Throws<SnmpProtocolException>(() => agent.Process(over));
                client.SendTo(new byte[4000], target);
                Cases.Throws<SnmpProtocolException>(() => session.ProcessNext(1000));
                client.SendTo(new byte[0], target);
                Cases.Throws<SnmpProtocolException>(() => session.ProcessNext(1000));
                client.SendTo(Wire.Message(1, "wrong", 0xa0, 43, 0, 0, Wire.Bind(Store.E + "2.0")), target);
                Cases.Check(session.ProcessNext(1000));
                client.SendTo(Wire.Message(1, "private", 0xa3, 44, 0, 0, Wire.Bind(Store.E + "1.0", Wire.Int(12))), target);
                Cases.Check(session.ProcessNext(1000));
                reply = Receive(client);
                Cases.Check(reply.Id == 44 && reply.Status == 0 && store.Values[0].Integer == 12);
                Cases.Check(client.Available == 0);

                Cases.Throws<ArgumentNullException>(() => session.SendTrap(null, "traps", SnmpOid.Parse(Store.E + "100"), null));
                session.SendTrap(client.LocalEndPoint, "traps", SnmpOid.Parse(Store.E + "100"), new[] { 0 });
                var trap = Receive(client);
                Cases.Check(trap.Pdu == 0xa7 && trap.Community == "traps" && trap.Bindings.Count == 3 && trap.Bindings[2].Signed == 12);
                Cases.Check(server.IsBound);
            }
        }

        private static Wire.Reply Receive(Socket socket)
        {
            var buffer = new byte[2048];
            EndPoint peer = new IPEndPoint(IPAddress.Any, 0);
            int count = socket.ReceiveFrom(buffer, ref peer);
            var message = new byte[count];
            Array.Copy(buffer, message, count);
            return Wire.Parse(message);
        }
    }
}
