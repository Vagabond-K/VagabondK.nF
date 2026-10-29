using System;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS;

namespace VagabondK.nF.IEC61850.Mapping.Sample
{
    internal static class Program
    {
        // 네트워크 연결 없이 상태·품질·시각과 형식별 제어 바인딩의 정적 생성을 확인합니다.
        private static void Main()
        {
            var formats = new TypedLocal.Iec61850ServerBinding(new TypedLocal(), new object());
            formats.Publish();
            Console.WriteLine("All-format companion: " + formats.Count + " values, " + formats.ControlCount + " controls.");
            var local = new LocalData();
            var binding = new LocalData.Iec61850ServerBinding(local, new object());
            binding.Publish();
            Console.WriteLine(binding.GetVariable(0).Item);
        }
        // 같은 변수 인스턴스로 DataSet과 URCB를 구성합니다. 채널 소유권과 세션 이벤트 루프는 호출자에게 있습니다.
        internal static Iec61850ServerSession Create(LocalData model, LocalData.Iec61850ServerBinding binding, IMmsChannel channel)
        {
            var members = new VagabondK.nF.MMS.Server.MmsServerVariable[binding.Count];
            for (int n = 0; n < members.Length; n++) members[n] = binding.GetVariable(n);
            var set = new MmsServerDataSet("NFTESTLD0", "Values", members);
            var report = new MmsServerReportControl("NFTESTLD0", "GGIO1$RP$Events", "Events", set);
            binding.GetControl(0).Enabled = true;
            return binding.CreateSession(channel, new[] { set }, new[] { report });
        }
        // enhanced 요청 수락과 실제 완료를 구분합니다. 모델·세션 접근과 동시에 실행되지 않도록 결과 통지도 직렬화합니다.
        internal static void ReportOutcome(LocalData model, LocalData.Iec61850ServerBinding binding, Iec61850ServerSession session,
            bool success, int additionalCause = 0)
        {
            if (session.HasPendingControl && !session.ProcessControlCompletion() && model.Pending != null)
            {
                binding.GetControl(0).Complete(model.Pending, success, additionalCause);
                session.ProcessControlCompletion();
            }
        }
        internal static RemoteData Receive(Iec61850Client client)
        {
            var model = new RemoteData();
            var binding = new RemoteData.Iec61850ClientBinding(model, new object(), client);
            binding.ReadAll();
            return model;
        }
    }
}
