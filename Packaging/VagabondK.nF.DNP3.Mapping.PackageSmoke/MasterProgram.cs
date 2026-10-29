using System;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.MasterMapping.Sample;

internal static class Program
{
    public static void Main()
    {
        var data = new ReceivedData();
        var binding = new ReceivedData.Dnp3MasterBinding(data, new object());
        binding.OnMeasurements(new Dnp3Measurement[0], default(Dnp3ResponseInfo));
        if (data.Fragments != 1) throw new InvalidOperationException("Master mapping package.");
        var master = new Dnp3Master(2, 1, binding, maximumMeasurements: 64);
        if (master.ReadClasses(1, 0).Length == 0) throw new InvalidOperationException("Master request package.");
    }
}
