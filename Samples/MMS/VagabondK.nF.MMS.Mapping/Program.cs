using System;
using VagabondK.nF.MMS.Client;

namespace VagabondK.nF.MMS.Mapping.Sample
{
    internal static class Program
    {
        // 통신 없이 생성된 바인딩의 초기화와 로컬 값 게시를 확인하는 진입점입니다.
        private static void Main()
        {
            var formats = new WritableScalars.MmsServerBinding(new WritableScalars(), new object());
            formats.Publish();
            Console.WriteLine("All writable scalar formats: " + formats.Count);
            var local = new LocalData();
            var binding = new LocalData.MmsServerBinding(local, new object());
            local.Contact = false;
            binding.Publish();
            Console.WriteLine(binding.GetVariable(0).Item);
        }
        // Association을 맺은 호출자 소유 클라이언트로 전체 값을 읽고 쓰기가 허용된 Count만 반영합니다.
        internal static void Receive(MmsClient client)
        {
            var remote = new RemoteData();
            var binding = new RemoteData.MmsClientBinding(remote, new object(), client);
            binding.ReadAll();
            remote.Count = 100;
            binding.Write(1);
        }
    }
}
