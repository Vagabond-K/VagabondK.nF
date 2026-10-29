using System;
using VagabondK.nF.MMS;
#if IEC
using VagabondK.nF.IEC61850.Mapping.Sample;
#if CLIENT_ONLY
using ProtocolClient = VagabondK.nF.IEC61850.Client.Iec61850Client;
#endif
#else
using VagabondK.nF.MMS.Mapping.Sample;
#if CLIENT_ONLY
using ProtocolClient = VagabondK.nF.MMS.Client.MmsClient;
#endif
#endif

internal static class Program
{
    private static void Main()
    {
        var sync = new object();
#if SERVER_ONLY
        var model = new LocalData();
#if IEC
        var binding = new LocalData.Iec61850ServerBinding(model, sync);
#else
        var binding = new LocalData.MmsServerBinding(model, sync);
#endif
        binding.Publish();
        Console.WriteLine(binding.GetVariable(0).Item);
#else
        var model = new RemoteData();
        var client = new ProtocolClient(new NoPeer());
#if IEC
        var binding = new RemoteData.Iec61850ClientBinding(model, sync, client);
        binding.Apply(2, MmsValue.FromBoolean(true));
#else
        var binding = new RemoteData.MmsClientBinding(model, sync, client);
        binding.Apply(0, MmsValue.FromBoolean(true));
#endif
        Console.WriteLine(binding.Count.ToString());
#endif
    }
    private sealed class NoPeer : IMmsChannel
    {
        public long Milliseconds { get { return Environment.TickCount64; } }
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds) { throw new InvalidOperationException("No peer."); }
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds) { throw new InvalidOperationException("No peer."); }
    }
}
