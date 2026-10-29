using System.Collections.Generic;

namespace System.IO.Ports
{
    // 요청 수량 전체가 버퍼에 있어야 읽을 수 있는 nanoCLR UART를 모사합니다.
    // 요청 수량 전체가 없으면 시간 초과를 발생시켜 채널이 BytesToRead 이내로만 읽는지 확인하는 테스트 대역입니다.
    public sealed class SerialPort
    {
        public readonly Queue<byte> Incoming = new Queue<byte>();

        public int ReadTimeout { get; set; }

        public int WriteTimeout { get; set; }

        public int BytesToRead
        {
            get
            {
                return Incoming.Count;
            }
        }
        public Exception ReadError;
        public int LastReadCount;

        public int Read(byte[] buffer, int offset, int count)
        {
            LastReadCount = count;
            if (ReadError != null)
                throw ReadError;
            if (Incoming.Count < count)
                throw new TimeoutException();
            for (int i = 0; i < count; i++)
                buffer[offset + i] = Incoming.Dequeue();
            return count;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
