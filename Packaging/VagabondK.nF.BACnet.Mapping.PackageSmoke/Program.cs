using System.Threading;
using VagabondK.nF.BACnet.Mapping;

namespace VagabondK.nF.BACnet.MappingPackageSmoke
{
    [BacnetMap]
    public partial class DeviceData
    {
        [BacnetPoint(0, Writable = true)]
        public bool Enabled { get; private set; }
#if !BINARY_ONLY
        [BacnetPoint(0, Writable = true)]
        public float Temperature { get; private set; }
#endif
    }

    public static class Program
    {
        public static void Main()
        {
            var store = new DeviceData.BacnetStore(new DeviceData(), new object());
#if BINARY_ONLY
            store.QueueWrite(0, 1);
            if (store.Read(0).Bits != 1) throw new System.InvalidOperationException("BV package mapping.");
#else
            store.QueueWrite(0, 0x80000001);
            store.QueueWrite(1, 1);
            if (store.Read(0).Bits != 0x80000001 || store.Read(1).Bits != 1)
                throw new System.InvalidOperationException("AV/BV package mapping.");
#endif
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
