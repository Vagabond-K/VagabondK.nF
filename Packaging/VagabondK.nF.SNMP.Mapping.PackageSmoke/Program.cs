using System.Threading;
using VagabondK.nF.SNMP;
using VagabondK.nF.SNMP.Mapping;

namespace VagabondK.nF.SNMP.MappingPackageSmoke
{
    [SnmpMap]
    public partial class DeviceData
    {
        [SnmpScalar("1.3.6.1.4.1.32473.10.1.0", SnmpType.Integer32, Writable = true)]
        public int Setpoint { get; private set; }
    }

    public static class Program
    {
        public static void Main()
        {
            var store = new DeviceData.SnmpStore(new DeviceData(), new object());
            store.QueueWrite(0, SnmpValue.Integer32(123));
            if (store.Read(0).Integer != 123)
                throw new System.InvalidOperationException("Package mapping failed.");
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
