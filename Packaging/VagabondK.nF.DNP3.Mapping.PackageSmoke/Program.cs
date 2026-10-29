using System;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.Mapping.Sample;

internal static class Program
{
    public static void Main()
    {
        var map = new DeviceData.Dnp3Binding(new DeviceData(), new object(), 8);
        if (!map.Publish() || map.Database.PointCount != 7) throw new InvalidOperationException("DNP3 mapping package.");
        if (map.Operate(Dnp3Command.Analog(0, 2.5, 4), false) != Dnp3CommandStatus.Success || !map.Publish())
            throw new InvalidOperationException("DNP3 command package.");
        if (!map.Publish(Dnp3PointType.AnalogOutputStatus, 0))
            throw new InvalidOperationException("DNP3 selective package.");
        var station = new Dnp3Station(map.Database, 1, 2, map);
        station.Poll(0);
    }
}
