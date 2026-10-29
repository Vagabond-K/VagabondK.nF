using System;

namespace VagabondK.nF.DNP3.PackageSmoke
{
    public static class Program
    {
        // 패키지 연결만 확인하며 네트워크·핀·UART에 접근하지 않습니다.
        public static void Main()
        {
            if (typeof(Dnp3Channel) == null || typeof(Dnp3Value) == null)
                throw new InvalidOperationException();
#if MASTER
            if (typeof(Dnp3Master) == null || typeof(IDnp3MasterHandler) == null)
                throw new InvalidOperationException();
#endif
#if OUTSTATION
            if (typeof(Dnp3Station) == null || typeof(Dnp3Database) == null)
                throw new InvalidOperationException();
#endif
#if TCP
            if (typeof(TCP.Dnp3TcpSession) == null)
                throw new InvalidOperationException();
#endif
#if SERIAL
            if (typeof(Serial.Dnp3SerialSession) == null || typeof(Serial.SerialPortChannel) == null)
                throw new InvalidOperationException();
#endif
        }
    }
}
