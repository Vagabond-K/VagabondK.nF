#ifndef VAGABONDK_NF_TEST_IEEE_FLOAT_H
#define VAGABONDK_NF_TEST_IEEE_FLOAT_H
#include "ser4cpp/container/SequenceTypes.h"
#include <cstring>
#include <limits>
#include <cstdint>

namespace ser4cpp
{
// 명시적으로 활성화하는 시험 전용 대체 구현이며 고정 버전의 원본 소스는 수정하지 않습니다.
template<class Float, class Integer> class IeeeFloat
{
public:
    static_assert(sizeof(Float) == sizeof(Integer) && std::numeric_limits<Float>::is_iec559,
                  "The patched validation tool requires native IEEE754 binary32/binary64.");
    using type_t = Float;
    static constexpr std::size_t size = sizeof(Float);
    static constexpr Float max_value = std::numeric_limits<Float>::max();
    static constexpr Float min_value = -std::numeric_limits<Float>::max();
    static Float Decode(Integer bits)
    {
        Float value; std::memcpy(&value, &bits, size); return value;
    }
    static Integer Encode(Float value)
    {
        Integer bits; std::memcpy(&bits, &value, size); return bits;
    }
    static bool read_from(rseq_t& input, Float& value)
    {
        if (input.length() < size) return false;
        Integer bits = 0;
        for (std::size_t i = 0; i < size; ++i) bits |= Integer(input[i]) << (8 * i);
        value = Decode(bits); input.advance(size); return true;
    }
    static bool write_to(wseq_t& output, Float value)
    {
        if (output.length() < size) return false;
        Integer bits = Encode(value);
        for (std::size_t i = 0; i < size; ++i) { output[i] = uint8_t(bits); bits >>= 8; }
        output.advance(size); return true;
    }
};
}
#endif
