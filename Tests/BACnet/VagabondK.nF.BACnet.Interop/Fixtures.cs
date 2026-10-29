using System;


namespace VagabondK.nF.BACnet.Interop
{
    // 애플리케이션 쪽 BACnet 값입니다. 호스트가 다음 주기에 반영하는 것처럼 쓰기는 Commit 전까지 보류됩니다.
    internal sealed class TestBacnetStore : IBacnetValueStore
    {
        private readonly BacnetObject[] _objects;
        private readonly int[] _slots;
        private readonly uint[] _bits;
        private readonly bool[] _good;
        private readonly uint[] _pending;
        private readonly bool[] _queued;

        public int Count
        {
            get
            {
                return _objects.Length;
            }
        }

        public TestBacnetStore(BacnetObject[] objects, int[] slots, uint[] initial, bool[] good)
        {
            _objects = objects;
            _slots = slots;
            _bits = initial;
            _good = good;
            _pending = new uint[initial.Length];
            _queued = new bool[initial.Length];
        }

        public BacnetObject GetObject(int index)
        {
            return _objects[index];
        }

        public BacnetSample Read(int index)
        {
            lock (this)
            {
                int slot = _slots[index];
                return new BacnetSample(_bits[slot], _good[slot]);
            }
        }

        public void QueueWrite(int index, uint bits)
        {
            if (!_objects[index].Writable)
                throw new InvalidOperationException("Read-only BACnet object.");
            lock (this)
            {
                int slot = _slots[index];
                _pending[slot] = bits;
                _queued[slot] = true;
            }
        }

        public void Set(int slot, uint bits, bool good)
        {
            lock (this)
            {
                _bits[slot] = bits;
                _good[slot] = good;
            }
        }

        public void SetQuality(int slot, bool good)
        {
            lock (this)
                _good[slot] = good;
        }

        public uint Get(int slot)
        {
            lock (this)
                return _bits[slot];
        }

        public void Commit()
        {
            lock (this)
                for (int i = 0; i < _bits.Length; i++)
                    if (_queued[i])
                    {
                        _bits[i] = _pending[i];
                        _queued[i] = false;
                    }
        }
    }

    internal static class Fixtures
    {
        public const int Level = 0, Limit = 1, Enable = 2;
        private static readonly uint[] RealBoundaries = {
            0, 0x80000000, 1, 0x80000001, 0x007fffff, 0x807fffff,
            0x00800000, 0x80800000, 0x7f7fffff, 0xff7fffff,
            0x3f800000, 0xbf800000, 0x3eaaaaab, 0x4b800000
        };

        public static uint NextFiniteBits(ref uint state, int iteration)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            uint bits = iteration < RealBoundaries.Length ? RealBoundaries[iteration] : state;
            return (bits & 0x7f800000) == 0x7f800000 ? bits ^ 0x00800000 : bits;
        }

        // 슬롯: Level(REAL 입력, 초기 NoData), Limit(REAL 설정값 800), Enable(BOOL 설정값 true).
        public static TestBacnetStore CreateBacnet(int count)
        {
            var objects = new BacnetObject[count];
            var slots = new int[count];
            objects[0] = new BacnetObject(0, false);
            slots[0] = Level;
            if (count > 1)
            {
                objects[1] = new BacnetObject(100, false, true);
                slots[1] = Limit;
            }
            if (count > 2)
            {
                objects[2] = new BacnetObject(0, true, true);
                slots[2] = Enable;
            }
            for (int i = 3; i < count; i++)
            {
                objects[i] = new BacnetObject((uint)i, false);
                slots[i] = Level;
            }
            return new TestBacnetStore(objects, slots,
                new uint[] { 0, BitConverter.SingleToUInt32Bits(800f), 1 }, new[] { false, true, true });
        }
    }
}
