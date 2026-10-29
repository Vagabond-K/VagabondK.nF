#include <opendnp3/DNP3Manager.h>
#include <opendnp3/channel/IChannelListener.h>
#include <opendnp3/logging/ILogHandler.h>
#include <opendnp3/master/DefaultMasterApplication.h>
#include <opendnp3/master/ISOEHandler.h>
#include <opendnp3/master/ICommandTaskResult.h>
#include <opendnp3/gen/TimeSyncMode.h>
#include <chrono>
#include <condition_variable>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <sstream>
#include <future>

using namespace opendnp3;

static void Emit(const std::string& line)
{
    static std::mutex output;
    std::lock_guard<std::mutex> lock(output);
    std::cout << line << std::endl;
}

class Logger final : public ILogHandler
{
public:
    void log(ModuleId, const char* id, LogLevel, const char*, const char* message) override
    {
        Emit(std::string("LOG ") + id + ": " + message);
    }
};

class ChannelObserver final : public IChannelListener
{
public:
    void OnStateChange(ChannelState state) override
    {
        Emit(std::string("CHANNEL ") + ChannelStateSpec::to_string(state));
        if (state == ChannelState::OPEN) Emit("CONNECTED");
    }
};

class Samples final : public ISOEHandler
{
public:
    bool full = false;
    bool physical = false;
    template<class T> void Other(const char* name, const ICollection<Indexed<T>>& values)
    {
        values.ForeachItem([this, name](const Indexed<T>& item) {
            std::ostringstream line;
            line << name << " " << item.index << " " << std::setprecision(17) << double(item.value.value) << " "
                 << unsigned(item.value.flags.value) << " " << item.value.time.value;
            Emit(line.str());
        });
    }
    void BeginFragment(const ResponseInfo& info) override
    { if (physical) Emit(info.unsolicited ? "BEGIN UNS" : "BEGIN SOL"); }
    void EndFragment(const ResponseInfo& info) override
    { if (physical) Emit(std::string(info.unsolicited ? "END UNS" : "END SOL") + (info.fin ? " FINAL" : "")); }
    void Process(const HeaderInfo&, const ICollection<Indexed<Binary>>& values) override
    {
        values.ForeachItem([this](const Indexed<Binary>& item) {
            Emit("B " + std::to_string(item.index) + " " + std::to_string(item.value.value) + " " +
                std::to_string(item.value.flags.value) + (full ? " " + std::to_string(item.value.time.value) : ""));
        });
    }
    void Process(const HeaderInfo&, const ICollection<Indexed<Analog>>& values) override
    {
        values.ForeachItem([this](const Indexed<Analog>& item) {
            std::ostringstream line;
            line << "A " << item.index << " " << std::setprecision(17) << item.value.value << " "
                 << unsigned(item.value.flags.value);
            if (full) line << " " << item.value.time.value;
            Emit(line.str());
        });
    }
    void Process(const HeaderInfo&, const ICollection<Indexed<DoubleBitBinary>>& v) override { Other("D", v); }
    void Process(const HeaderInfo&, const ICollection<Indexed<Counter>>& v) override { Other("C", v); }
    void Process(const HeaderInfo&, const ICollection<Indexed<FrozenCounter>>& v) override { Other("F", v); }
    void Process(const HeaderInfo&, const ICollection<Indexed<BinaryOutputStatus>>& v) override { Other("O", v); }
    void Process(const HeaderInfo&, const ICollection<Indexed<AnalogOutputStatus>>& v) override { Other("Q", v); }
    void Process(const HeaderInfo&, const ICollection<Indexed<OctetString>>&) override {}
    void Process(const HeaderInfo&, const ICollection<Indexed<TimeAndInterval>>&) override {}
    void Process(const HeaderInfo&, const ICollection<Indexed<BinaryCommandEvent>>&) override {}
    void Process(const HeaderInfo&, const ICollection<Indexed<AnalogCommandEvent>>&) override {}
    void Process(const HeaderInfo&, const ICollection<DNPTime>&) override {}
};

class Completion final : public ITaskCallback
{
    std::mutex gate;
    std::condition_variable changed;
    bool complete = false;
    bool resultReady = false;
    TaskCompletion result = TaskCompletion::FAILURE_NO_COMMS;
public:
    void OnStart() override {}
    void OnDestroyed() override {}
    void OnComplete(TaskCompletion value) override
    {
        std::lock_guard<std::mutex> lock(gate);
        result = value;
        resultReady = true;
        // 성공 완료 콜백이 OnReceiveIIN보다 먼저 오므로 두 콜백을 모두 받은 뒤에만 DONE을 출력합니다.
        complete = value != TaskCompletion::SUCCESS;
        changed.notify_all();
    }
    void OnResponse()
    {
        std::lock_guard<std::mutex> lock(gate);
        if (resultReady) { complete = true; changed.notify_all(); }
    }
    void Wait()
    {
        std::unique_lock<std::mutex> lock(gate);
        if (!changed.wait_for(lock, std::chrono::seconds(12), [this] { return complete; }))
            throw std::runtime_error("OpenDNP3 task did not complete.");
        Emit(std::string("DONE ") + TaskCompletionSpec::to_string(result));
    }
};

class Application final : public IMasterApplication
{
    std::mutex gate;
    std::shared_ptr<Completion> task;
public:
    UTCTimestamp Now() override { return DefaultMasterApplication::Create()->Now(); }
    void SetTask(std::shared_ptr<Completion> value)
    {
        std::lock_guard<std::mutex> lock(gate);
        task = value;
    }
    void OnReceiveIIN(const IINField& iin) override
    {
        Emit("IIN " + std::to_string(iin.LSB) + " " + std::to_string(iin.MSB));
        std::lock_guard<std::mutex> lock(gate);
        if (task) task->OnResponse();
    }
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
        if (argc < 2 || argc > 4) throw std::runtime_error("Expected port|COM6 [full] or address port full.");
        bool full = (argc == 3 && std::string(argv[2]) == "full") || argc == 4;
        bool serial = std::string(argv[1]) == "COM6";
        bool physical = serial || argc == 4;
        DNP3Manager manager(1, std::make_shared<Logger>());
        std::shared_ptr<IChannel> channel;
        if (serial)
        {
            SerialSettings settings; settings.deviceName = "COM6"; settings.parity = Parity::Even;
            channel = manager.AddSerial("physical", levels::NOTHING | flags::WARN | flags::ERR,
                ChannelRetry::Default(), settings, std::make_shared<ChannelObserver>());
        }
        else
        {
            int port = std::stoi(argv[argc == 4 ? 2 : 1]);
            if (port < 1 || port > 65535) throw std::runtime_error("Invalid port.");
            channel = manager.AddTCPClient("interop", levels::NOTHING | flags::WARN | flags::ERR, ChannelRetry::Default(),
                { IPEndpoint(argc == 4 ? argv[1] : "127.0.0.1", static_cast<uint16_t>(port)) },
                argc == 4 ? "0.0.0.0" : "127.0.0.1", std::make_shared<ChannelObserver>());
        }
        MasterStackConfig config;
        config.link.LocalAddr = 2;
        config.link.RemoteAddr = 1;
        config.master.disableUnsolOnStartup = false;
        config.master.unsolClassMask = ClassField::None();
        config.master.startupIntegrityClassMask = ClassField::None();
        config.master.responseTimeout = TimeDuration::Seconds(physical ? 8 : 2);
        config.master.taskStartTimeout = TimeDuration::Seconds(5);
        if (full) config.master.timeSyncMode = TimeSyncMode::NonLAN;
        auto samples = std::make_shared<Samples>();
        samples->full = full;
        samples->physical = physical;
        auto application = std::make_shared<Application>();
        auto master = channel->AddMaster("validation", samples, application, config);
        master->Enable();
        Emit("READY");
        std::string command;
        while (std::getline(std::cin, command) && command != "quit")
        {
            std::istringstream operation(command);
            std::string verb; operation >> verb;
            if (verb == "relay" || verb == "ao")
            {
                unsigned variation = 0, index, selected; double value;
                if (verb == "ao") operation >> variation;
                if (!(operation >> index >> value >> selected) || index > 65535 || selected > 1)
                    throw std::runtime_error("Command syntax.");
                CommandSet commands;
                if (verb == "relay") commands.Add<ControlRelayOutputBlock>({WithIndex(ControlRelayOutputBlock(static_cast<OperationType>(unsigned(value))), uint16_t(index))});
                else if (variation == 1) commands.Add<AnalogOutputInt32>({WithIndex(AnalogOutputInt32(int32_t(value)), uint16_t(index))});
                else if (variation == 2) commands.Add<AnalogOutputInt16>({WithIndex(AnalogOutputInt16(int16_t(value)), uint16_t(index))});
                else if (variation == 3) commands.Add<AnalogOutputFloat32>({WithIndex(AnalogOutputFloat32(float(value)), uint16_t(index))});
                else if (variation == 4) commands.Add<AnalogOutputDouble64>({WithIndex(AnalogOutputDouble64(value), uint16_t(index))});
                else throw std::runtime_error("Analog variation.");
                auto promise = std::make_shared<std::promise<void>>(); auto future = promise->get_future();
                auto callback = [promise](const ICommandTaskResult& result) {
                    result.ForeachItem([](const CommandPointResult& point) {
                        Emit(std::string("STATUS ") + CommandStatusSpec::to_string(point.status));
                    });
                    Emit(std::string("CMD ") + TaskCompletionSpec::to_string(result.summary));
                    promise->set_value();
                };
                application->SetTask(nullptr);
                if (selected) master->SelectAndOperate(std::move(commands), callback);
                else master->DirectOperate(std::move(commands), callback);
                if (future.wait_for(std::chrono::seconds(12)) != std::future_status::ready) throw std::runtime_error("Command timeout.");
                continue;
            }
            if (verb == "enable" || verb == "disable")
            {
                auto complete = std::make_shared<Completion>(); application->SetTask(complete);
                master->PerformFunction(verb, verb == "enable" ? FunctionCode::ENABLE_UNSOLICITED : FunctionCode::DISABLE_UNSOLICITED,
                    { Header::AllObjects(60, 2), Header::AllObjects(60, 3), Header::AllObjects(60, 4) }, TaskConfig::With(complete));
                complete->Wait(); continue;
            }
            unsigned group, variation;
            std::istringstream input(command);
            if (!(input >> group >> variation) || group > 255 || variation > 255)
                throw std::runtime_error("Expected group variation.");
            auto complete = std::make_shared<Completion>();
            application->SetTask(complete);
            unsigned start, stop;
            if (input >> start >> stop)
            {
                if (start > stop || stop > 65535) throw std::runtime_error("Invalid scan range.");
                master->ScanRange(GroupVariationID(static_cast<uint8_t>(group), static_cast<uint8_t>(variation)),
                    static_cast<uint16_t>(start), static_cast<uint16_t>(stop), samples, TaskConfig::With(complete));
            }
            else
                master->ScanAllObjects(GroupVariationID(static_cast<uint8_t>(group), static_cast<uint8_t>(variation)),
                    samples, TaskConfig::With(complete));
            complete->Wait();
        }
        manager.Shutdown();
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << std::endl;
        return 1;
    }
}
