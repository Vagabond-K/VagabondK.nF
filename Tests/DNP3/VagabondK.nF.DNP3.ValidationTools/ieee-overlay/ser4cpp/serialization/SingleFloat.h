#ifndef SER4CPP_SINGLE_FLOAT_H
#define SER4CPP_SINGLE_FLOAT_H
#include "ser4cpp/serialization/IeeeFloat.h"
namespace ser4cpp
{
class SingleFloat : public IeeeFloat<float, uint32_t>
{
public:
    static float to_float32(uint32_t bits) { return Decode(bits); }
    static uint32_t to_uint32(float value) { return Encode(value); }
};
}
#endif
