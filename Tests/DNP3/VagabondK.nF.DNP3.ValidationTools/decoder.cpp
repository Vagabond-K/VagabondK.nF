#include <ser4cpp/serialization/SingleFloat.h>
#include <ser4cpp/serialization/DoubleFloat.h>
#include <iostream>
#include <stdexcept>
#include <cmath>
#include <cstring>
#include <string>

template<class F, class U, class Codec> void Check(U raw, F decoded, F arithmetic)
{
    F native; std::memcpy(&native, &raw, sizeof(F));
    U decodedBits; std::memcpy(&decodedBits, &decoded, sizeof(F));
    if (decodedBits != raw || Codec::Encode(native) != raw ||
        decoded != arithmetic || std::signbit(decoded) != std::signbit(arithmetic))
        throw std::runtime_error("IEEE conversion mismatch, bits=" + std::to_string(raw));
    uint8_t bytes[sizeof(F)];
    ser4cpp::wseq_t output(bytes, sizeof(F));
    if (!Codec::write_to(output, native) || output.length() != 0) throw std::runtime_error("Encode size.");
    for (std::size_t i = 0; i < sizeof(F); ++i)
        if (bytes[i] != uint8_t(raw >> (8 * i))) throw std::runtime_error("Little endian bytes.");
    ser4cpp::rseq_t input(bytes, sizeof(F)); F result;
    if (!Codec::read_from(input, result) || Codec::Encode(result) != raw || input.length() != 0)
        throw std::runtime_error("Read bytes.");
    for (std::size_t i = 0; i < sizeof(F); ++i)
    {
        ser4cpp::rseq_t shortInput(bytes, i); F unchanged = F(123);
        ser4cpp::wseq_t shortOutput(bytes, i);
        if (Codec::read_from(shortInput, unchanged) || unchanged != F(123) || shortInput.length() != i ||
            Codec::write_to(shortOutput, native) || shortOutput.length() != i)
            throw std::runtime_error("Truncated input/output mutated.");
    }
}
static void Single(uint32_t raw)
{
    unsigned exp = (raw >> 23) & 255;
    if (exp == 255) return;
    uint32_t mantissa = raw & 0x7fffff;
    float arithmetic = exp == 0 ? std::ldexp(float(mantissa), -149) :
        std::ldexp(float(0x800000 | mantissa), int(exp) - 150);
    if (raw >> 31) arithmetic = -arithmetic;
    Check<float, uint32_t, ser4cpp::SingleFloat>(raw, ser4cpp::SingleFloat::to_float32(raw), arithmetic);
}
static void Double(uint64_t raw)
{
    unsigned exp = unsigned((raw >> 52) & 2047);
    if (exp == 2047) return;
    uint64_t mantissa = raw & 0xfffffffffffffULL;
    double arithmetic = exp == 0 ? std::ldexp(double(mantissa), -1074) :
        std::ldexp(double(0x10000000000000ULL | mantissa), int(exp) - 1075);
    if (raw >> 63) arithmetic = -arithmetic;
    Check<double, uint64_t, ser4cpp::DoubleFloat>(raw, ser4cpp::DoubleFloat::to_double(raw), arithmetic);
}
int main()
{
    try
    {
        for (uint32_t bits = 0; bits <= 0x800000; ++bits) { Single(bits); Single(bits | 0x80000000); }
        for (int exp = 0; exp < 2047; ++exp)
            for (uint64_t mantissa : {0ULL, 1ULL, 0x7ffffffffffffULL, 0xfffffffffffffULL})
            {
                uint64_t bits = (uint64_t(exp) << 52) | mantissa;
                Double(bits); Double(bits | 0x8000000000000000ULL);
            }
        uint64_t state = 20261023;
        for (int i = 0; i < 1000000; ++i)
        {
            state ^= state << 13; state ^= state >> 7; state ^= state << 17;
            Single(uint32_t(state)); Double(state);
            Double(state & 0x800fffffffffffffULL);
        }
        std::cout << "PATCHED IEEE PASS: all signed binary32 subnormals/zeros, binary64 exponent boundaries, "
                     "1,000,000 random patterns per corpus, native bit-copy and independent ldexp arithmetic, "
                     "little-endian encode/decode and truncation rejection. Not a second DNP3 stack." << std::endl;
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << std::endl; return 1; }
}
