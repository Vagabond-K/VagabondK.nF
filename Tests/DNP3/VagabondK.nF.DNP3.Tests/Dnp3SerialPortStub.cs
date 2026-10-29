using System.Collections.Generic;

// 시험용 nanoFramework UART 계약 대역이며 OS 시리얼 API나 하드웨어를 사용하지 않습니다.
// 포트 열림·모드와 전체 수량 읽기, 강제 시간 초과·쓰기 실패를 재현하며 물리 포트를 열지 않습니다.
namespace System.IO.Ports
{
    public enum SerialMode
    {
        Normal, RS485
    }

    public enum Handshake
    {
        None, RequestToSend
    }

    public sealed class SerialPort
    {
        public bool IsOpen { get; set; }

        public SerialMode Mode { get; set; }

        public Handshake Handshake { get; set; }

        public int ReadTimeout { get; set; }

        public int WriteTimeout { get; set; }
        public readonly Queue<byte> Bytes = new Queue<byte>();
        public int LastRead, LastWrite;
        public bool ReadTimeoutFailure, WriteFailure;

        public int BytesToRead
        {
            get
            {
                if (!IsOpen)
                    throw new InvalidOperationException("Closed.");
                return Bytes.Count;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            LastRead = count;
            if (ReadTimeoutFailure)
                throw new TimeoutException();
            if (count > Bytes.Count)
                throw new InvalidOperationException("Requested unavailable UART bytes.");
            for (int i = 0; i < count; i++)
                buffer[offset + i] = Bytes.Dequeue();
            return count;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (!IsOpen || WriteFailure)
                throw new InvalidOperationException("UART write failed.");
            LastWrite = count;
        }
    }
}
