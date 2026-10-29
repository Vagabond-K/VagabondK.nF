using VagabondK.nF.Modbus.Server;

namespace VagabondK.nF.Modbus.Mapping.Sample
{
    // 동기식 PDU 처리만 보여 주며 Serial/TCP 전송 계층은 아닙니다.
    // Send 안에서 Server를 즉시 실행하는 예제용 채널입니다. 프레임 경계, CRC, 실제 I/O 시간 제한은 검증하지 않습니다.
    internal sealed class SampleLoopback : IModbusTransport
    {
        private readonly ServerSide _wire = new ServerSide();
        private readonly ModbusServer _server;

        internal SampleLoopback(IModbusDataStore store)
        {
            _server = new ModbusServer(1, _wire, store);
        }

        public bool IsSerial { get { return false; } }

        public void Send(FrameContext context, byte[] pdu, int count)
        {
            _wire.Context = context;
            _wire.RequestLength = count;
            System.Array.Copy(pdu, _wire.Request, count);
            _server.ProcessNextRequest();
        }

        public int Receive(byte[] pdu, out FrameContext context)
        {
            context = _wire.Context;
            System.Array.Copy(_wire.Response, pdu, _wire.ResponseLength);
            return _wire.ResponseLength;
        }

        private sealed class ServerSide : IModbusTransport
        {
            internal readonly byte[] Request = new byte[253];
            internal readonly byte[] Response = new byte[253];
            internal FrameContext Context;
            internal int RequestLength;
            internal int ResponseLength;

            public bool IsSerial { get { return false; } }

            public void Send(FrameContext context, byte[] pdu, int count)
            {
                Context = context;
                System.Array.Copy(pdu, Response, count);
                ResponseLength = count;
            }

            public int Receive(byte[] pdu, out FrameContext context)
            {
                context = Context;
                System.Array.Copy(Request, pdu, RequestLength);
                return RequestLength;
            }
        }
    }
}
