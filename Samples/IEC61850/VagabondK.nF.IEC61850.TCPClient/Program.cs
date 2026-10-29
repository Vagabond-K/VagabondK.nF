using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.IEC61850;
using VagabondK.nF.MMS.TCP;

internal static class Program
{
    // 저장된 보드 네트워크 설정과 격리된 시험 피어가 필요합니다. 주소·포트·모델 이름을 환경에 맞추십시오.
    // 연결만 재시도하며 결과가 불확실한 제어 명령은 재전송하지 않습니다.
    public static void Main()
    {
        const string host = "192.168.0.210", domain = "NFTESTLD0";
        // 격리된 Boolean 메모리 제어 시험에만 지정하십시오. null이면 읽기와 보고만 수행합니다.
        const string controlItem = null;
        var monitor = new MonitorSession(delegate(string message) { Debug.WriteLine(message); });
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(new IPEndPoint(IPAddress.Parse(host), 18102));
                    monitor.Run(new MmsSocketChannel(socket), domain, "GGIO1$Measurements", "GGIO1$RP$Events", controlItem);
                    return;
                }
            }
            catch (IOException error) { Debug.WriteLine("MMS transport: " + error.Message); }
            catch (SocketException error) { Debug.WriteLine("MMS socket: " + error.ErrorCode); }
            catch (TimeoutException error) { Debug.WriteLine("MMS timeout: " + error.Message); }
            catch (MmsProtocolException error) { Debug.WriteLine("MMS protocol: " + error.Message); }
            if (monitor.ControlAttempted) Debug.WriteLine("Control outcome may be unknown; command will not be replayed.");
            if (attempt == 4) throw new IOException("MMS sample exhausted five connection attempts.");
            Thread.Sleep(1000 << (attempt < 3 ? attempt : 3));
        }
    }
}
