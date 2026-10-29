using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

internal static class Program
{
    // 보드의 네트워크가 준비된 격리 시험망에서 사용합니다. 인증 없는 18102 포트를 외부에 노출하지 마십시오.
    // 연결별로 요청·값 갱신·보고 송신을 한 루프에서 직렬화하며 URCB는 단절 중 이벤트를 저장하지 않습니다.
    public static void Main()
    {
        const string domain = "NFTESTLD0";
        var count = new MmsServerVariable(domain, "GGIO1$ST$Count$stVal", 0);
        var quality = Iec61850ServerData.CreateQuality(domain, "GGIO1$ST$Count$q");
        var time = Iec61850ServerData.CreateTimestamp(domain, "GGIO1$ST$Count$t");
        var variables = new MmsServerVariable[] { count, quality, time };
        var set = new MmsServerDataSet(domain, "GGIO1$Values", variables);
        var report = new MmsServerReportControl(domain, "GGIO1$RP$Events", "NFREPORT", set,
            integrityMilliseconds: 1000);
        int value = 0;
        using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            listener.Bind(new IPEndPoint(IPAddress.Any, 18102)); listener.Listen(1);
            while (true)
            {
                using (Socket socket = listener.Accept())
                {
                    var session = new Iec61850ServerSession(new MmsSocketChannel(socket), variables,
                        controls: new MmsServerControl[0], dataSets: new MmsServerDataSet[] { set },
                        reports: new MmsServerReportControl[] { report });
                    try
                    {
                        session.Associate();
                        long next = 0;
                        while (session.Associated)
                        {
                            long now = Environment.TickCount64;
                            if (now >= next)
                            {
                                count.SetInteger(++value);
                                quality.SetQuality(IecQuality.Good);
                                time.SetTimestamp((DateTime.UtcNow.Ticks - 621355968000000000L) / 10000);
                                next = now + 500;
                            }
                            if (socket.Poll(1000, SelectMode.SelectRead) && !session.ProcessRequest()) break;
                            session.ProcessReports();
                            Thread.Sleep(1);
                        }
                    }
                    catch (IOException error) { Debug.WriteLine("MMS transport: " + error.Message); }
                    catch (SocketException error) { Debug.WriteLine("MMS socket: " + error.ErrorCode); }
                    catch (TimeoutException error) { Debug.WriteLine("MMS timeout: " + error.Message); }
                    catch (MmsProtocolException error) { Debug.WriteLine("MMS protocol: " + error.Message); }
                    finally { session.Close(); }
                }
            }
        }
    }
}
