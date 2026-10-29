using System;
using System.Threading;
using VagabondK.nF.Modbus.Mapping;
using VagabondK.nF.BACnet.Mapping;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.Mapping;

namespace MappingSnapshotChecks
{
    [ModbusMap(ModbusMapRole.Server)]
    internal partial class ModbusSnapshot
    {
        [ModbusPoint(ModbusArea.HoldingRegister, 0)]
        internal long Value = 0x1111111111111111L;
    }

    [BacnetMap]
    internal partial class BacnetSnapshot
    {
        [BacnetPoint(0, Quality = nameof(Good))]
        internal float Value;
        internal bool Good;
    }

    [Dnp3Map]
    internal partial class Dnp3Snapshot
    {
        [Dnp3Point(Dnp3PointType.Binary, 0, EventClass = 0, Flags = nameof(Flags), Timestamp = nameof(Time))]
        internal bool Value;
        internal byte Flags = 4;
        internal long Time;
    }

    // 같은 앱 잠금을 공유할 때 개별 값/Point가 찢어지지 않는지 검사합니다. 요청 전체나 여러 Point의 원자성 시험은 아닙니다.
    internal static class Checks
    {
        internal static void Run()
        {
            RunPair(new ModbusProbe());
            RunPair(new BacnetProbe());
            RunPair(new Dnp3Probe());
        }

        private interface IProbe
        {
            object Gate { get; }
            void Update(int n);
            void Verify();
        }

        private sealed class ModbusProbe : IProbe
        {
            private readonly object gate = new object();
            private readonly ModbusSnapshot model = new ModbusSnapshot();
            private readonly ModbusSnapshot.ModbusStore store;
            private readonly ushort[] registers = new ushort[4];
            internal ModbusProbe() { store = new ModbusSnapshot.ModbusStore(model, gate); }
            public object Gate { get { return gate; } }
            public void Update(int n) { model.Value = (n & 1) == 0 ? 0x1111111111111111L : 0x6666666666666666L; }
            public void Verify()
            {
                store.ReadRegisters(false, 0, 4, registers);
                if (registers[0] != 0x1111 && registers[0] != 0x6666)
                    throw new InvalidOperationException("Modbus snapshot value.");
                for (int i = 1; i < registers.Length; i++)
                    if (registers[i] != registers[0]) throw new InvalidOperationException("Modbus torn multiword scalar.");
            }
        }

        private sealed class BacnetProbe : IProbe
        {
            private readonly object gate = new object();
            private readonly BacnetSnapshot model = new BacnetSnapshot();
            private readonly BacnetSnapshot.BacnetStore store;
            internal BacnetProbe() { store = new BacnetSnapshot.BacnetStore(model, gate); }
            public object Gate { get { return gate; } }
            public void Update(int n)
            {
                model.Value = (n & 1) == 0 ? 0f : 1f;
                model.Good = model.Value == 1f;
            }
            public void Verify()
            {
                var sample = store.Read(0);
                if (sample.Good != (sample.Bits == 0x3f800000))
                    throw new InvalidOperationException("BACnet torn value/quality snapshot.");
            }
        }

        private sealed class Dnp3Probe : IProbe
        {
            private readonly object gate = new object();
            private readonly Dnp3Snapshot model = new Dnp3Snapshot();
            private readonly Dnp3Snapshot.Dnp3Binding binding;
            internal Dnp3Probe() { binding = new Dnp3Snapshot.Dnp3Binding(model, gate); }
            public object Gate { get { return gate; } }
            public void Update(int n)
            {
                model.Value = (n & 1) != 0;
                model.Flags = model.Value ? (byte)1 : (byte)4;
                model.Time = model.Value ? 1 : 0;
            }
            public void Verify()
            {
                if (!binding.Publish(Dnp3PointType.Binary, 0)) throw new InvalidOperationException("Class zero overflow.");
                var value = binding.Database.Read(Dnp3PointType.Binary, 0);
                if (value.Raw == 1 ? value.Flags != 1 || value.Timestamp != 1 :
                    value.Raw != 0 || value.Flags != 4 || value.Timestamp != 0)
                    throw new InvalidOperationException("DNP3 torn value/flags/time snapshot.");
            }
        }

        private static void RunPair(IProbe probe)
        {
            object gate = probe.Gate;
            bool done = false;
            Exception failure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 800; i++)
                    {
                        lock (gate) probe.Update(i);
                        if ((i & 31) == 0) Thread.Sleep(1);
                    }
                }
                catch (Exception error) { failure = error; }
                finally { lock (gate) done = true; }
            });
            writer.Start();
            int reads = 0;
            long deadline = Environment.TickCount64 + 30000;
            while (true)
            {
                probe.Verify();
                reads++;
                bool finished;
                lock (gate) finished = done;
                if (finished && reads >= 800) break;
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Concurrent mapping snapshot.");
                if ((reads & 31) == 0) Thread.Sleep(1);
            }
            if (failure != null) throw failure;
        }
    }
}
