#ifndef SER4CPP_DOUBLE_FLOAT_H
#define SER4CPP_DOUBLE_FLOAT_H
#include "ser4cpp/serialization/IeeeFloat.h"
namespace ser4cpp
{
class DoubleFloat : public IeeeFloat<double, uint64_t>
{
public:
    static double to_double(uint64_t bits) { return Decode(bits); }
    static uint64_t to_uint64(double value) { return Encode(value); }
};
}
#endif
