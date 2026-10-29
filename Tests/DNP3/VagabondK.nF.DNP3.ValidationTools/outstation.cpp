#include <opendnp3/DNP3Manager.h>
#include <opendnp3/logging/ILogHandler.h>
#include <opendnp3/outstation/DefaultOutstationApplication.h>
#include <opendnp3/outstation/SimpleCommandHandler.h>
#include <opendnp3/outstation/UpdateBuilder.h>
#include <iostream>
#include <sstream>
#include <mutex>
#include <cstring>
#include <cmath>

using namespace opendnp3;
static void Emit(const std::string& text)
{
    static std::mutex gate; std::lock_guard<std::mutex> lock(gate);
    std::cout << text << std::endl;
}
class Logger final : public ILogHandler
{
    void log(ModuleId, const char*, LogLevel, const char*, const char* message) override { Emit(std::string("LOG ") + message); }
};
class Application final : public DefaultOutstationApplication
{
public:
    bool physical = false;
    uint64_t sampleTime = 1700000000000ULL;
    DNPTime Now() override { return physical ? DNPTime(sampleTime) : DefaultOutstationApplication::Now(); }
    bool WriteAbsoluteTime(const UTCTimestamp& timestamp) override
    {
        Emit("TIME " + std::to_string(timestamp.msSinceEpoch)); return DefaultOutstationApplication::WriteAbsoluteTime(timestamp);
    }
};
static void PhysicalValues(IUpdateHandler& update, unsigned generation, Application* application = nullptr, bool boundaries = true)
{
    auto mode = generation == 0 ? EventMode::Suppress : EventMode::Force;
    DNPTime time(1700000000000ULL + generation);
    for (uint16_t i = 0; i < 8; ++i)
    {
        if (boundaries && generation >= 32 && generation <= 35)
        {
            unsigned scenario = generation - 32;
            const uint64_t times[] = {0, 1, 65535, 65536, 4294967295ULL, 4294967296ULL, 281474976710654ULL, 281474976710655ULL};
            const uint8_t bits[] = {0, 128, 2, 4, 8, 16, 32, 64};
            const double numbers[] = {-2147483648.0, -32768, -1.25, 0, 1.25, 32767, 2147483520.0, 16777216};
            const uint32_t counters[] = {0, 1, 65535, 65536, 2147483647, 2147483648U, 4294967294U, 4294967295U};
            DNPTime timestamp(scenario >= 2 ? 1 : times[scenario == 0 ? i : 7 - i]);
            uint8_t flags = bits[i] ^ (scenario == 0 ? 0 : scenario == 1 ? 255 : scenario == 2 ? 85 : 170);
            if (!application) throw std::runtime_error("Boundary freeze clock missing.");
            application->sampleTime = timestamp.value;
            update.Update(Binary((i + scenario) % 2 != 0, Flags(flags & 127), timestamp), i, mode);
            update.Update(DoubleBitBinary(static_cast<DoubleBit>((i + scenario) % 4), Flags(flags & 63), timestamp), i, mode);
            update.Update(Counter(counters[(i + scenario) % 8], Flags(flags), timestamp), i, mode);
            update.FreezeCounter(i, false, mode);
            update.Update(Analog(numbers[(i + scenario) % 8], Flags(flags), timestamp), i, mode);
            update.Update(BinaryOutputStatus((i + scenario) % 2 != 0, Flags(flags & 127), timestamp), i, mode);
            update.Update(AnalogOutputStatus(numbers[(i + scenario) % 8], Flags(flags), timestamp), i, mode);
            continue;
        }
        double analog = (i % 2 == 0 ? -1.0 : 1.0) * (i + generation + 0.25);
        update.Update(Binary((i + generation) % 2 != 0, Flags(1), time), i, mode);
        update.Update(DoubleBitBinary((i + generation) % 2 ? DoubleBit::DETERMINED_ON : DoubleBit::DETERMINED_OFF, Flags(1), time), i, mode);
        update.Update(Counter(1000 + i + generation, Flags(1), time), i, mode);
        update.FreezeCounter(i, false, mode);
        update.Update(Analog(analog, Flags(1), time), i, mode);
        update.Update(BinaryOutputStatus((i + generation) % 2 != 0, Flags(1), time), i, mode);
        update.Update(AnalogOutputStatus(analog, Flags(1), time), i, mode);
    }
}
class PhysicalControls final : public SimpleCommandHandler
{
    std::shared_ptr<Application> application;
    bool boundaries;
    unsigned generation = 0, operations = 0;
    void Output(double value, uint16_t index, IUpdateHandler& handler)
    {
        handler.Update(AnalogOutputStatus(value, Flags(1)), index);
        ++operations;
        Emit("PHYSICAL OPERATIONS " + std::to_string(operations));
        if (value == 24589) Emit("PHYSICAL COMPLETE");
    }
public:
    explicit PhysicalControls(std::shared_ptr<Application> app, bool boundaryValues) :
        SimpleCommandHandler(CommandStatus::SUCCESS), application(app), boundaries(boundaryValues) {}
    CommandStatus Operate(const ControlRelayOutputBlock&, uint16_t index, IUpdateHandler& handler, OperateType) override
    {
        if (index != 65500) return CommandStatus::NOT_SUPPORTED;
        application->sampleTime = 1700000000000ULL + ++generation;
        PhysicalValues(handler, generation, application.get(), boundaries); ++operations;
        Emit("PHYSICAL OPERATIONS " + std::to_string(operations)); return CommandStatus::SUCCESS;
    }
    CommandStatus Operate(const AnalogOutputInt32& value, uint16_t index, IUpdateHandler& handler, OperateType) override
    { Output(value.value, index, handler); return CommandStatus::SUCCESS; }
    CommandStatus Operate(const AnalogOutputInt16& value, uint16_t index, IUpdateHandler& handler, OperateType) override
    { Output(value.value, index, handler); return CommandStatus::SUCCESS; }
    CommandStatus Operate(const AnalogOutputFloat32& value, uint16_t index, IUpdateHandler& handler, OperateType) override
    { Output(value.value, index, handler); return CommandStatus::SUCCESS; }
    CommandStatus Operate(const AnalogOutputDouble64& value, uint16_t index, IUpdateHandler& handler, OperateType) override
    { Output(value.value, index, handler); return CommandStatus::SUCCESS; }
};
class Controls final : public SimpleCommandHandler
{
public:
    Controls() : SimpleCommandHandler(CommandStatus::SUCCESS) {}
    void DoOperate(const ControlRelayOutputBlock&, uint16_t index, OperateType type) override
    { Emit("OPERATE R " + std::to_string(index) + " " + std::to_string(unsigned(type))); }
    void DoOperate(const AnalogOutputInt16&, uint16_t index, OperateType) override { Emit("OPERATE A2 " + std::to_string(index)); }
    void DoOperate(const AnalogOutputInt32&, uint16_t index, OperateType) override { Emit("OPERATE A1 " + std::to_string(index)); }
    void DoOperate(const AnalogOutputFloat32&, uint16_t index, OperateType) override { Emit("OPERATE A3 " + std::to_string(index)); }
    void DoOperate(const AnalogOutputDouble64&, uint16_t index, OperateType) override { Emit("OPERATE A4 " + std::to_string(index)); }
};
int main(int argc, char** argv)
{
    try
    {
        if (argc == 2 && std::string(argv[1]) == "--decoder-identity")
        {
#ifdef VAGABONDK_NF_PATCHED_IEEE
            Emit("DECODER PATCHED_IEEE");
#else
            Emit("DECODER ORIGINAL");
#endif
            return 0;
        }
        if (argc != 2 && argc != 3 && argc != 4) throw std::runtime_error("Expected TCP port|COM6 [physical|soak [bind-address]].");
        bool soak = argc >= 3 && std::string(argv[2]) == "soak";
        bool physical = soak || (argc >= 3 && std::string(argv[2]) == "physical");
        bool serial = std::string(argv[1]) == "COM6";
        DNP3Manager manager(1, std::make_shared<Logger>());
        std::shared_ptr<IChannel> channel;
        if (serial)
        {
            SerialSettings settings; settings.deviceName = "COM6"; settings.parity = Parity::Even;
            channel = manager.AddSerial("physical", levels::NOTHING | flags::WARN | flags::ERR,
                ChannelRetry::Default(), settings, nullptr);
        }
        else
        {
            int port = std::stoi(argv[1]); if (port < 1 || port > 65535) throw std::runtime_error("Port range.");
            channel = manager.AddTCPServer("external", levels::NOTHING | flags::WARN | flags::ERR,
                ServerAcceptMode::CloseExisting, IPEndpoint(argc == 4 ? argv[3] : "127.0.0.1", uint16_t(port)), nullptr);
        }
        DatabaseConfig database;
        for (uint16_t i = 0; i < (physical ? 8 : 70); ++i)
        {
            database.binary_input[i].svariation = StaticBinaryVariation::Group1Var2;
            database.binary_input[i].evariation = EventBinaryVariation::Group2Var2;
            database.double_binary[i].svariation = StaticDoubleBinaryVariation::Group3Var2;
            database.double_binary[i].evariation = EventDoubleBinaryVariation::Group4Var2;
            database.counter[i].evariation = EventCounterVariation::Group22Var5;
            database.frozen_counter[i].evariation = EventFrozenCounterVariation::Group23Var5;
            database.analog_input[i].svariation = StaticAnalogVariation::Group30Var5;
            database.analog_input[i].evariation = EventAnalogVariation::Group32Var7;
            database.binary_output_status[i].svariation = StaticBinaryOutputStatusVariation::Group10Var2;
            database.binary_output_status[i].evariation = EventBinaryOutputStatusVariation::Group11Var2;
            database.analog_output_status[i].svariation = StaticAnalogOutputStatusVariation::Group40Var3;
            database.analog_output_status[i].evariation = EventAnalogOutputStatusVariation::Group42Var7;
        }
        OutstationStackConfig config(database);
        config.link.LocalAddr = 1; config.link.RemoteAddr = 2;
        config.outstation.params.allowUnsolicited = true;
        config.outstation.params.maxTxFragSize = 256;
        config.outstation.eventBufferConfig = EventBufferConfig::AllTypes(512);
        auto application = std::make_shared<Application>(); application->physical = physical;
        std::shared_ptr<ICommandHandler> controls = physical ?
            std::static_pointer_cast<ICommandHandler>(std::make_shared<PhysicalControls>(application, !soak)) :
            std::static_pointer_cast<ICommandHandler>(std::make_shared<Controls>());
        auto station = channel->AddOutstation("validation", controls, application, config);
        if (physical)
        {
            UpdateBuilder initial; PhysicalValues(initial, 0); station->Apply(initial.Build());
        }
        station->Enable(); Emit("READY");
        std::string line;
        while (std::getline(std::cin, line) && line != "quit")
        {
            std::istringstream input(line); std::string command; unsigned generation;
            input >> command;
            if (command == "number")
            {
                unsigned width; uint64_t bits;
                if (!(input >> width >> std::hex >> bits) || (width != 32 && width != 64))
                    throw std::runtime_error("Expected number 32|64 hexBits.");
                double number;
                if (width == 32) { uint32_t raw = uint32_t(bits); float value; std::memcpy(&value, &raw, 4); number = value; }
                else std::memcpy(&number, &bits, 8);
                if (!std::isfinite(number)) throw std::runtime_error("Expected finite number.");
                UpdateBuilder update; update.Update(Analog(number, Flags(1), DNPTime(1700000000000ULL)), 0, EventMode::Suppress);
                station->Apply(update.Build()); Emit("NUMBER UPDATED"); continue;
            }
            if (!(input >> generation) || command != "update") throw std::runtime_error("Expected update generation.");
            UpdateBuilder update; auto mode = generation == 0 ? EventMode::Suppress : EventMode::Force;
            DNPTime time(1700000000000ULL + generation);
            for (uint16_t i = 0; i < 70; ++i)
            {
                update.Update(Binary((i + generation) % 2 != 0, Flags(1), time), i, mode);
                update.Update(DoubleBitBinary((i + generation) % 2 ? DoubleBit::DETERMINED_ON : DoubleBit::DETERMINED_OFF, Flags(1), time), i, mode);
                update.Update(Counter(1000 + i + generation, Flags(1), time), i, mode);
                update.FreezeCounter(i, false, mode);
                update.Update(Analog(i + generation + 0.25, Flags(1), time), i, mode);
                update.Update(BinaryOutputStatus((i + generation) % 2 != 0, Flags(1), time), i, mode);
                update.Update(AnalogOutputStatus(i + generation + 0.5, Flags(1), time), i, mode);
            }
            station->Apply(update.Build()); Emit("UPDATED " + std::to_string(generation));
        }
        manager.Shutdown(); return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << std::endl; return 1; }
}
