// In-process P2P audio self-test for the linked tgcalls engine.
//
// Two real tgcalls instances are created exactly like the Android client does,
// exchange signaling through an in-memory bridge, and negotiate a genuine
// WebRTC/ICE path over loopback. The caller feeds a 440 Hz tone and the callee
// measures the decoded audio. This proves the engine, codecs, encryption and
// transport are live; it is not a substitute for a real device call.

#include "FakeAudioDeviceModule.h"
#include "tgcalls/Instance.h"
#include "tgcalls/v2/InstanceV2Impl.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <random>
#include <string>
#include <thread>
#include <vector>

namespace {

constexpr uint32_t kSampleRate = 48000;
constexpr uint32_t kChannels = 1;
constexpr uint32_t kFrameSamples = 480;  // 10 ms

double NowSeconds() {
    return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

class ToneRecorder final : public tgcalls::FakeAudioDeviceModule::Recorder {
public:
    explicit ToneRecorder(double frequency) : frequency_(frequency) {
        buffer_.resize(kFrameSamples * kChannels);
    }

    tgcalls::AudioFrame Record() override {
        for (uint32_t i = 0; i < kFrameSamples; ++i) {
            double t = static_cast<double>(phase_) / kSampleRate;
            int16_t sample = static_cast<int16_t>(3000.0 * std::sin(2.0 * M_PI * frequency_ * t));
            for (uint32_t ch = 0; ch < kChannels; ++ch) {
                buffer_[i * kChannels + ch] = sample;
            }
            ++phase_;
        }
        tgcalls::AudioFrame frame;
        frame.audio_samples = buffer_.data();
        frame.num_samples = kFrameSamples;
        frame.bytes_per_sample = sizeof(int16_t);
        frame.num_channels = kChannels;
        frame.samples_per_sec = kSampleRate;
        frame.elapsed_time_ms = 0;
        frame.ntp_time_ms = 0;
        return frame;
    }

    int32_t WaitForUs() override { return 10000; }

private:
    double frequency_;
    std::vector<int16_t> buffer_;
    uint64_t phase_ = 0;
};

class MeasuringRenderer final : public tgcalls::FakeAudioDeviceModule::Renderer {
public:
    bool Render(const tgcalls::AudioFrame &frame) override {
        if (!frame.audio_samples || frame.num_samples == 0 || frame.bytes_per_sample != 2) {
            return true;
        }
        const int16_t *samples = reinterpret_cast<const int16_t *>(frame.audio_samples);
        const size_t count = static_cast<size_t>(frame.num_samples) * frame.num_channels;
        double sum = 0;
        int lastSign = 0;
        for (size_t i = 0; i < count; ++i) {
            double value = samples[i];
            sum += value * value;
            int sign = samples[i] >= 0 ? 1 : -1;
            if (lastSign != 0 && sign != lastSign) {
                ++zeroCrossings_;
            }
            lastSign = sign;
        }
        frames_.fetch_add(1);
        energy_.fetch_add(static_cast<uint64_t>(sum));
        samples_.fetch_add(static_cast<uint64_t>(count));
        return true;
    }

    int32_t WaitForUs() override { return 10000; }

    uint64_t frames() const { return frames_.load(); }
    double rms() const {
        uint64_t n = samples_.load();
        return n == 0 ? 0.0 : std::sqrt(static_cast<double>(energy_.load()) / static_cast<double>(n));
    }
    uint64_t zeroCrossings() const { return zeroCrossings_.load(); }

private:
    std::atomic<uint64_t> frames_{0};
    std::atomic<uint64_t> zeroCrossings_{0};
    std::atomic<uint64_t> energy_{0};
    std::atomic<uint64_t> samples_{0};
};

struct Bridge {
    std::mutex mutex;
    std::shared_ptr<tgcalls::Instance> caller;
    std::shared_ptr<tgcalls::Instance> callee;

    void deliver(const std::vector<uint8_t> &data, bool toCallee) {
        std::lock_guard<std::mutex> lock(mutex);
        auto target = toCallee ? callee : caller;
        if (target) {
            target->receiveSignalingData(data);
        }
    }
};

std::string StateName(tgcalls::State state) {
    switch (state) {
        case tgcalls::State::WaitInit: return "wait_init";
        case tgcalls::State::WaitInitAck: return "wait_init_ack";
        case tgcalls::State::Established: return "established";
        case tgcalls::State::Failed: return "failed";
        case tgcalls::State::Reconnecting: return "reconnecting";
    }
    return "unknown";
}

}  // namespace

int RunSelfTest(int seconds) {
    std::array<uint8_t, 256> key{};
    std::random_device rd;
    for (auto &byte : key) {
        byte = static_cast<uint8_t>(rd());
    }

    auto callerRenderer = std::make_shared<MeasuringRenderer>();
    auto calleeRenderer = std::make_shared<MeasuringRenderer>();
    auto callerRecorder = std::make_shared<ToneRecorder>(440.0);
    auto calleeRecorder = std::make_shared<ToneRecorder>(880.0);
    auto bridge = std::make_shared<Bridge>();

    std::atomic<tgcalls::State> callerState{tgcalls::State::WaitInit};
    std::atomic<tgcalls::State> calleeState{tgcalls::State::WaitInit};
    std::atomic<int> remoteMediaStates{0};

    auto callerDesc = (tgcalls::Descriptor){
        .version = "13.0.0",
        .config = {
            .initializationTimeout = 10.0,
            .receiveTimeout = 10.0,
            .enableP2P = true,
            .customParameters = "",
        },
        .encryptionKey = tgcalls::EncryptionKey(std::make_shared<std::array<uint8_t, 256>>(key), true),
        .stateUpdated = [&callerState](tgcalls::State state) { callerState.store(state); },
        .remoteMediaStateUpdated = [&remoteMediaStates](tgcalls::AudioState, tgcalls::VideoState) {
            remoteMediaStates.fetch_add(1);
        },
        .signalingDataEmitted = [bridge](const std::vector<uint8_t> &data) {
            bridge->deliver(data, true);
        },
        .createAudioDeviceModule = tgcalls::FakeAudioDeviceModule::Creator(
            callerRenderer, callerRecorder,
            tgcalls::FakeAudioDeviceModule::Options{.samples_per_sec = kSampleRate, .num_channels = kChannels}),
    };

    auto calleeDesc = (tgcalls::Descriptor){
        .version = "13.0.0",
        .config = {
            .initializationTimeout = 10.0,
            .receiveTimeout = 10.0,
            .enableP2P = true,
            .customParameters = "",
        },
        .encryptionKey = tgcalls::EncryptionKey(std::make_shared<std::array<uint8_t, 256>>(key), false),
        .stateUpdated = [&calleeState](tgcalls::State state) { calleeState.store(state); },
        .remoteMediaStateUpdated = [&remoteMediaStates](tgcalls::AudioState, tgcalls::VideoState) {
            remoteMediaStates.fetch_add(1);
        },
        .signalingDataEmitted = [bridge](const std::vector<uint8_t> &data) {
            bridge->deliver(data, false);
        },
        .createAudioDeviceModule = tgcalls::FakeAudioDeviceModule::Creator(
            calleeRenderer, calleeRecorder,
            tgcalls::FakeAudioDeviceModule::Options{.samples_per_sec = kSampleRate, .num_channels = kChannels}),
    };

    auto caller = std::shared_ptr<tgcalls::Instance>(tgcalls::Meta::Create("13.0.0", std::move(callerDesc)).release());
    auto callee = std::shared_ptr<tgcalls::Instance>(tgcalls::Meta::Create("13.0.0", std::move(calleeDesc)).release());
    if (!caller || !callee) {
        std::fprintf(stderr, "{\"ok\":false,\"error\":\"engine_create_failed\"}\n");
        return 2;
    }
    {
        std::lock_guard<std::mutex> lock(bridge->mutex);
        bridge->caller = caller;
        bridge->callee = callee;
    }

    const double start = NowSeconds();
    const double deadline = start + 8.0;
    while (NowSeconds() < deadline) {
        if (callerState.load() == tgcalls::State::Established &&
            calleeState.load() == tgcalls::State::Established) {
            break;
        }
        if (callerState.load() == tgcalls::State::Failed || calleeState.load() == tgcalls::State::Failed) {
            break;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }
    const double establishedAt = NowSeconds();

    const double measureUntil = establishedAt + seconds;
    while (NowSeconds() < measureUntil) {
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }

    uint64_t callerFrames = callerRenderer->frames();
    uint64_t calleeFrames = calleeRenderer->frames();
    double callerRms = callerRenderer->rms();
    double calleeRms = calleeRenderer->rms();
    uint64_t calleeCrossings = calleeRenderer->zeroCrossings();

    caller->stop([](tgcalls::FinalState) {});
    callee->stop([](tgcalls::FinalState) {});

    bool established = callerState.load() == tgcalls::State::Established &&
                       calleeState.load() == tgcalls::State::Established;
    bool audioFlowing = calleeFrames > 100 && calleeRms > 100.0;

    std::printf(
        "{\"ok\":%s,\"established\":%s,\"caller_state\":\"%s\",\"callee_state\":\"%s\","
        "\"caller_frames\":%llu,\"callee_frames\":%llu,\"caller_rms\":%.2f,\"callee_rms\":%.2f,"
        "\"callee_zero_crossings\":%llu,\"remote_media_state_events\":%d,\"measured_seconds\":%d}\n",
        audioFlowing ? "true" : "false", established ? "true" : "false",
        StateName(callerState.load()).c_str(), StateName(calleeState.load()).c_str(),
        static_cast<unsigned long long>(callerFrames), static_cast<unsigned long long>(calleeFrames),
        callerRms, calleeRms, static_cast<unsigned long long>(calleeCrossings),
        remoteMediaStates.load(), seconds);
    return audioFlowing ? 0 : 1;
}
