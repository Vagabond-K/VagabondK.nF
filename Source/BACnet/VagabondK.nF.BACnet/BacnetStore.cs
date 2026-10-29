using System;

namespace VagabondK.nF.BACnet
{
    /// <summary>BACnet Analog Value 또는 Binary Value 객체의 변경 불가능한 정의입니다.</summary>
    public sealed class BacnetObject
    {
        /// <summary>0~4194302 범위의 객체 인스턴스 번호를 가져옵니다.</summary>
        public uint Instance { get; private set; }

        /// <summary>Binary Value 객체이면 <see langword="true"/>, Analog Value 객체이면 <see langword="false"/>입니다.</summary>
        public bool Binary { get; private set; }

        /// <summary>Present_Value 쓰기를 허용하는지 가져옵니다.</summary>
        public bool Writable { get; private set; }

        /// <summary>객체의 인스턴스 번호, 종류 및 쓰기 허용 여부를 지정합니다.</summary>
        /// <param name="instance">0~4194302 범위의 인스턴스 번호입니다.</param>
        /// <param name="binary">Binary Value이면 <see langword="true"/>, Analog Value이면 <see langword="false"/>입니다.</param>
        /// <param name="writable">Present_Value 쓰기를 허용할지 여부입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="instance"/>가 4194302보다 큽니다.</exception>
        public BacnetObject(uint instance, bool binary, bool writable = false)
        {
            if (instance > 4194302)
                throw new ArgumentOutOfRangeException("instance");
            Instance = instance;
            Binary = binary;
            Writable = writable;
        }
    }

    /// <summary>BACnet 객체의 값과 품질을 함께 전달하는 스냅샷입니다.</summary>
    public struct BacnetSample
    {
        /// <summary>Binary Value의 0 또는 1, 또는 Analog Value의 IEEE 754 float32 원시 비트를 가져옵니다.</summary>
        public uint Bits { get; private set; }

        /// <summary>정상 품질이면 <see langword="true"/>입니다. 비정상 품질은 Fault와 신뢰성 오류로 보고됩니다.</summary>
        public bool Good { get; private set; }

        /// <summary>값의 원시 비트와 품질로 스냅샷을 생성합니다. 비트의 유효성은 여기서 검사하지 않습니다.</summary>
        /// <param name="bits">Binary Value의 0/1 또는 Analog Value의 IEEE 754 float32 비트입니다.</param>
        /// <param name="good">정상 품질인지 여부입니다.</param>
        public BacnetSample(uint bits, bool good)
        {
            Bits = bits;
            Good = good;
        }
    }

    /// <summary>BACnet 객체 정의, 값의 스냅샷 및 쓰기 요청을 제공하는 저장소 계약입니다.</summary>
    /// <remarks>사용 중 객체 수와 정의를 변경하지 않아야 합니다. 구현은 각 Read에서 값과 품질을 원자적으로 읽어야 합니다.</remarks>
    public interface IBacnetValueStore
    {
        /// <summary>객체 수를 가져옵니다. BacnetDevice는 0~64개를 허용합니다.</summary>
        int Count { get; }

        /// <summary>지정한 객체의 변경 불가능한 정의를 가져옵니다.</summary>
        /// <param name="index">0 이상 Count 미만인 저장소 인덱스입니다.</param>
        /// <returns><see langword="null"/>이 아닌 객체 정의입니다.</returns>
        BacnetObject GetObject(int index);

        /// <summary>객체의 값과 품질을 하나의 원자적 스냅샷으로 읽습니다.</summary>
        /// <param name="index">0 이상 Count 미만인 저장소 인덱스입니다.</param>
        /// <returns>Binary Value는 0/1, Analog Value는 float32 원시 비트를 담은 스냅샷입니다.</returns>
        BacnetSample Read(int index);

        /// <summary>검증된 Present_Value 쓰기 요청을 저장소에 전달합니다.</summary>
        /// <param name="index">0 이상 Count 미만인 쓰기 가능한 객체의 인덱스입니다.</param>
        /// <param name="bits">Binary Value는 0/1, Analog Value는 유한한 float32 값의 원시 비트입니다.</param>
        /// <remarks>반영 시점은 구현이 정합니다. 프로토콜 ACK는 이 호출의 반환을 뜻하며 실제 장치 반영 완료를 보장하지 않습니다.</remarks>
        void QueueWrite(int index, uint bits);
    }
}
