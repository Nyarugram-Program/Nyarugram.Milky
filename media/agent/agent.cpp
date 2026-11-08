// Nyarugram media agent: one real tgcalls call in a separate process.
//
// The .NET adapter owns all call state, the backend RPC and the application
// audio contract. This process owns only the native engine instance: it accepts
// a start descriptor, pipes opaque signaling both ways, and exchanges PCM audio
// over the same framed TCP connection.
//
// Wire format (all messages, little-endian not used; lengths are big-endian):
//   4 bytes length | 1 byte kind | payload
//   kind 0x01: UTF-8 JSON control message
//   kind 0x02: PCM S16LE 48 kHz mono, application -> engine
//   kind 0x03: PCM S16LE 48 kHz mono, engine -> application
//
// The agent binds 127.0.0.1:0, prints "READY <port>" to stdout, accepts exactly
// one controller, runs one call, then exits. No secrets or audio are logged.

#include "FakeAudioDeviceModule.h"
#include "tgcalls/Instance.h"
#include "tgcalls/v2/InstanceV2Impl.h"
#include "third-party/json11.hpp"

#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <deque>
#include <future>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

constexpr uint32_t kSampleRate = 48000;
constexpr size_t kFrame10Ms = 480;
constexpr size_t kFrame20Ms = 960;

enum MessageKind : uint8_t {
    kControl = 0x01,
    kAudioToEngine = 0x02,
    kAudioFromEngine = 0x03,
};

struct Message {
    uint8_t kind;
    std::vector<uint8_t> payload;
};

class FramedSocket {
public:
    explicit FramedSocket(int fd) : fd_(fd) {}
    ~FramedSocket() {
        if (fd_ >= 0) {
            ::close(fd_);
        }
    }

    int fd() const { return fd_; }

    bool ReadMessage(Message &message, int timeoutMs) {
        if (!WaitReadable(timeoutMs)) {
            return false;
        }
        uint8_t header[4];
        if (!ReadExact(header, 4)) {
            return false;
        }
        uint32_t length = (static_cast<uint32_t>(header[0]) << 24) |
                          (static_cast<uint32_t>(header[1]) << 16) |
                          (static_cast<uint32_t>(header[2]) << 8) |
                          static_cast<uint32_t>(header[3]);
        if (length == 0 || length > 8u * 1024u * 1024u) {
            return false;
        }
        uint8_t kind = 0;
        if (!ReadExact(&kind, 1)) {
            return false;
        }
        message.kind = kind;
        message.payload.resize(length);
        return ReadExact(message.payload.data(), length);
    }

    bool WriteMessage(const Message &message) {
        std::lock_guard<std::mutex> lock(writeMutex_);
        uint32_t length = static_cast<uint32_t>(message.payload.size());
        uint8_t header[5] = {
            static_cast<uint8_t>((length >> 24) & 0xFF),
            static_cast<uint8_t>((length >> 16) & 0xFF),
            static_cast<uint8_t>((length >> 8) & 0xFF),
            static_cast<uint8_t>(length & 0xFF),
            static_cast<uint8_t>(message.kind),
        };
        return WriteExact(header, 5) &&
               (message.payload.empty() || WriteExact(message.payload.data(), message.payload.size()));
    }

private:
    bool WaitReadable(int timeoutMs) {
        if (timeoutMs < 0) {
            return true;
        }
        struct timeval tv;
        tv.tv_sec = timeoutMs / 1000;
        tv.tv_usec = (timeoutMs % 1000) * 1000;
        fd_set set;
        FD_ZERO(&set);
        FD_SET(fd_, &set);
        int result = ::select(fd_ + 1, &set, nullptr, nullptr, &tv);
        return result > 0;
    }

    bool ReadExact(void *buffer, size_t length) {
        uint8_t *cursor = static_cast<uint8_t *>(buffer);
        while (length > 0) {
            ssize_t read = ::recv(fd_, cursor, length, 0);
            if (read <= 0) {
                return false;
            }
            cursor += read;
            length -= static_cast<size_t>(read);
        }
        return true;
    }

    bool WriteExact(const void *buffer, size_t length) {
        const uint8_t *cursor = static_cast<const uint8_t *>(buffer);
        while (length > 0) {
            ssize_t written = ::send(fd_, cursor, length, 0);
            if (written <= 0) {
                return false;
            }
            cursor += written;
            length -= static_cast<size_t>(written);
        }
        return true;
    }

    int fd_;
    std::mutex writeMutex_;
};

// Serialises every outbound frame on a dedicated thread so engine callbacks never
// block on socket writes. Audio is droppable, control messages are not.
class Sender {
public:
    explicit Sender(FramedSocket &socket) : socket_(socket), thread_([this] { Run(); }) {}

    ~Sender() {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            stopping_ = true;
        }
        condition_.notify_all();
        if (thread_.joinable()) {
            thread_.join();
        }
    }

    void SendControl(std::string json) {
        Message message;
        message.kind = kControl;
        message.payload.assign(json.begin(), json.end());
        Enqueue(std::move(message), false);
    }

    void SendAudio(std::vector<uint8_t> pcm) {
        Message message;
        message.kind = kAudioFromEngine;
        message.payload = std::move(pcm);
        Enqueue(std::move(message), true);
    }

    void Flush() {
        std::unique_lock<std::mutex> lock(mutex_);
        drained_.wait_for(lock, std::chrono::milliseconds(500),
                          [this] { return queue_.empty(); });
    }

private:
    void Enqueue(Message message, bool droppable) {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            if (stopping_) {
                return;
            }
            if (droppable && audioQueued_ > 120) {
                return;  // ~1.2 s backlog: drop instead of growing without bound
            }
            audioQueued_ += droppable ? 1 : 0;
            queue_.push_back(std::move(message));
        }
        condition_.notify_one();
    }

    void Run() {
        while (true) {
            Message message;
            {
                std::unique_lock<std::mutex> lock(mutex_);
                condition_.wait(lock, [this] { return stopping_ || !queue_.empty(); });
                if (queue_.empty()) {
                    if (stopping_) {
                        return;
                    }
                    continue;
                }
                message = std::move(queue_.front());
                queue_.pop_front();
                if (message.kind == kAudioFromEngine) {
                    audioQueued_ -= 1;
                }
            }
            if (!socket_.WriteMessage(message)) {
                {
                    std::lock_guard<std::mutex> lock(mutex_);
                    stopping_ = true;
                    queue_.clear();
                    audioQueued_ = 0;
                }
                drained_.notify_all();
                return;
            }
            {
                std::lock_guard<std::mutex> lock(mutex_);
                if (queue_.empty()) {
                    drained_.notify_all();
                }
            }
        }
    }

    FramedSocket &socket_;
    std::mutex mutex_;
    std::condition_variable condition_;
    std::condition_variable drained_;
    std::deque<Message> queue_;
    size_t audioQueued_ = 0;
    bool stopping_ = false;
    std::thread thread_;
};

class ApplicationAudioInput {
public:
    void Push(const std::vector<uint8_t> &pcm) {
        if (pcm.size() < 2 || pcm.size() % 2 != 0) {
            return;
        }
        const int16_t *samples = reinterpret_cast<const int16_t *>(pcm.data());
        size_t count = pcm.size() / 2;
        std::lock_guard<std::mutex> lock(mutex_);
        for (size_t i = 0; i < count; ++i) {
            samples_.push_back(samples[i]);
        }
        const size_t limit = kSampleRate;  // one second of audio
        while (samples_.size() > limit) {
            samples_.pop_front();
        }
    }

    void Read10ms(int16_t *out) {
        std::lock_guard<std::mutex> lock(mutex_);
        for (size_t i = 0; i < kFrame10Ms; ++i) {
            if (!samples_.empty()) {
                out[i] = samples_.front();
                samples_.pop_front();
            } else {
                out[i] = 0;
            }
        }
    }

private:
    std::mutex mutex_;
    std::deque<int16_t> samples_;
};

class AgentRecorder final : public tgcalls::FakeAudioDeviceModule::Recorder {
public:
    explicit AgentRecorder(std::shared_ptr<ApplicationAudioInput> input) : input_(std::move(input)) {}

    tgcalls::AudioFrame Record() override {
        input_->Read10ms(buffer_.data());
        tgcalls::AudioFrame frame;
        frame.audio_samples = buffer_.data();
        frame.num_samples = kFrame10Ms;
        frame.bytes_per_sample = sizeof(int16_t);
        frame.num_channels = 1;
        frame.samples_per_sec = kSampleRate;
        frame.elapsed_time_ms = 0;
        frame.ntp_time_ms = 0;
        return frame;
    }

    int32_t WaitForUs() override { return 10000; }

private:
    std::shared_ptr<ApplicationAudioInput> input_;
    std::vector<int16_t> buffer_ = std::vector<int16_t>(kFrame10Ms);
};

class AgentRenderer final : public tgcalls::FakeAudioDeviceModule::Renderer {
public:
    explicit AgentRenderer(std::shared_ptr<Sender> sender) : sender_(std::move(sender)) {}

    bool Render(const tgcalls::AudioFrame &frame) override {
        if (!frame.audio_samples || frame.num_samples == 0) {
            return true;
        }
        const size_t channels = frame.num_channels == 0 ? 1 : frame.num_channels;
        const int16_t *samples = frame.audio_samples;
        for (size_t i = 0; i < frame.num_samples; ++i) {
            int32_t mixed = 0;
            for (size_t ch = 0; ch < channels; ++ch) {
                mixed += samples[i * channels + ch];
            }
            pending_.push_back(static_cast<int16_t>(mixed / static_cast<int32_t>(channels)));
        }
        if (pending_.size() >= kFrame20Ms) {
            std::vector<uint8_t> pcm(kFrame20Ms * 2);
            std::memcpy(pcm.data(), pending_.data(), pcm.size());
            pending_.erase(pending_.begin(), pending_.begin() + static_cast<std::ptrdiff_t>(kFrame20Ms));
            sender_->SendAudio(std::move(pcm));
            audioFrames_.fetch_add(1);
        }
        return true;
    }

    int32_t WaitForUs() override { return 10000; }

    uint64_t audioFrames() const { return audioFrames_.load(); }

private:
    std::shared_ptr<Sender> sender_;
    std::vector<int16_t> pending_;
    std::atomic<uint64_t> audioFrames_{0};
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

std::string JsonString(const std::string &value) {
    std::string out = "\"";
    for (char c : value) {
        if (c == '"' || c == '\\') {
            out.push_back('\\');
            out.push_back(c);
        } else if (static_cast<unsigned char>(c) < 0x20) {
            continue;
        } else {
            out.push_back(c);
        }
    }
    out.push_back('"');
    return out;
}

struct StartOptions {
    std::string version = "13.0.0";
    bool caller = true;
    bool p2p = true;
    std::vector<uint8_t> sharedKey;
    std::string customParameters;
    std::vector<tgcalls::RtcServer> rtcServers;
    bool log = false;
};

bool ParseStart(const std::string &json, StartOptions &options, std::string &error) {
    std::string parseError;
    auto parsed = json11::Json::parse(json, parseError);
    if (!parsed.is_object()) {
        error = "invalid_start_json";
        return false;
    }
    if (parsed["version"].is_string()) {
        options.version = parsed["version"].string_value();
    }
    options.caller = parsed["caller"].is_bool() ? parsed["caller"].bool_value() : true;
    options.p2p = parsed["p2p"].is_bool() ? parsed["p2p"].bool_value() : true;
    options.log = parsed["log"].is_bool() && parsed["log"].bool_value();
    if (parsed["custom_parameters"].is_string()) {
        options.customParameters = parsed["custom_parameters"].string_value();
    }
    if (!parsed["shared_key_b64"].is_string()) {
        error = "missing_shared_key";
        return false;
    }
    // base64 decode, standard alphabet with no whitespace.
    std::string encoded = parsed["shared_key_b64"].string_value();
    if (encoded.size() % 4 != 0) {
        error = "invalid_shared_key";
        return false;
    }
    options.sharedKey.clear();
    for (size_t i = 0; i < encoded.size(); i += 4) {
        auto value = [](char c) -> int {
            if (c >= 'A' && c <= 'Z') return c - 'A';
            if (c >= 'a' && c <= 'z') return c - 'a' + 26;
            if (c >= '0' && c <= '9') return c - '0' + 52;
            if (c == '+') return 62;
            if (c == '/') return 63;
            return -1;
        };
        int a = value(encoded[i]);
        int b = value(encoded[i + 1]);
        int c = encoded[i + 2] == '=' ? 0 : value(encoded[i + 2]);
        int d = encoded[i + 3] == '=' ? 0 : value(encoded[i + 3]);
        if (a < 0 || b < 0 || c < 0 || d < 0) {
            error = "invalid_shared_key";
            return false;
        }
        options.sharedKey.push_back(static_cast<uint8_t>((a << 2) | (b >> 4)));
        if (encoded[i + 2] != '=') {
            options.sharedKey.push_back(static_cast<uint8_t>(((b & 0xF) << 4) | (c >> 2)));
        }
        if (encoded[i + 3] != '=') {
            options.sharedKey.push_back(static_cast<uint8_t>(((c & 0x3) << 6) | d));
        }
    }
    if (options.sharedKey.size() != 256) {
        error = "invalid_shared_key_length";
        return false;
    }

    auto servers = parsed["rtc_servers"];
    if (servers.is_array()) {
        uint8_t id = 1;
        for (const auto &entry : servers.array_items()) {
            if (!entry.is_object()) {
                continue;
            }
            tgcalls::RtcServer server;
            server.id = id++;
            server.host = entry["host"].is_string() ? entry["host"].string_value() : "";
            server.port = static_cast<uint16_t>(entry["port"].is_number() ? entry["port"].int_value() : 0);
            server.login = entry["username"].is_string() ? entry["username"].string_value() : "";
            server.password = entry["password"].is_string() ? entry["password"].string_value() : "";
            server.isTurn = entry["turn"].is_bool() && entry["turn"].bool_value();
            server.isTcp = false;
            if (server.host.empty() || server.port == 0) {
                continue;
            }
            options.rtcServers.push_back(server);
        }
    }
    return true;
}

int ListenOnLoopback(uint16_t &port) {
    int fd = ::socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) {
        return -1;
    }
    int reuse = 1;
    ::setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
    if (::bind(fd, reinterpret_cast<sockaddr *>(&address), sizeof(address)) != 0) {
        ::close(fd);
        return -1;
    }
    if (::listen(fd, 1) != 0) {
        ::close(fd);
        return -1;
    }
    socklen_t length = sizeof(address);
    if (::getsockname(fd, reinterpret_cast<sockaddr *>(&address), &length) != 0) {
        ::close(fd);
        return -1;
    }
    port = ntohs(address.sin_port);
    return fd;
}

}  // namespace

int RunMediaAgent() {
    uint16_t port = 0;
    int listenFd = ListenOnLoopback(port);
    if (listenFd < 0) {
        std::fprintf(stderr, "agent_listen_failed\n");
        return 2;
    }
    std::printf("READY %u\n", static_cast<unsigned>(port));
    std::fflush(stdout);

    // Bound the wait for a controller so an orphaned process cannot linger forever.
    struct timeval acceptTimeout{15, 0};
    ::setsockopt(listenFd, SOL_SOCKET, SO_RCVTIMEO, &acceptTimeout, sizeof(acceptTimeout));
    int controlFd = ::accept(listenFd, nullptr, nullptr);
    ::close(listenFd);
    if (controlFd < 0) {
        std::fprintf(stderr, "agent_accept_timeout\n");
        return 3;
    }
    FramedSocket socket(controlFd);

    Message startMessage;
    if (!socket.ReadMessage(startMessage, 15000) || startMessage.kind != kControl) {
        std::fprintf(stderr, "agent_start_missing\n");
        return 4;
    }
    std::string startJson(startMessage.payload.begin(), startMessage.payload.end());
    StartOptions options;
    std::string parseError;
    if (!ParseStart(startJson, options, parseError)) {
        Message reply;
        reply.kind = kControl;
        std::string json = "{\"type\":\"error\",\"code\":" + JsonString(parseError) + "}";
        reply.payload.assign(json.begin(), json.end());
        socket.WriteMessage(reply);
        return 5;
    }
    auto sender = std::make_shared<Sender>(socket);
    auto audioInput = std::make_shared<ApplicationAudioInput>();
    auto recorder = std::make_shared<AgentRecorder>(audioInput);
    auto renderer = std::make_shared<AgentRenderer>(sender);
    std::atomic<tgcalls::State> engineState{tgcalls::State::WaitInit};
    std::atomic<bool> remoteMediaReported{false};
    std::atomic<bool> stopRequested{false};
    std::promise<void> stopped;
    auto stoppedFuture = stopped.get_future().share();

    auto keyArray = std::make_shared<std::array<uint8_t, 256>>();
    std::copy(options.sharedKey.begin(), options.sharedKey.end(), keyArray->begin());

    std::string logPath;
    if (options.log) {
        logPath = "/tmp/milky_media_agent_" + std::to_string(static_cast<long long>(getpid())) + ".log";
    }

    auto descriptor = (tgcalls::Descriptor){
        .version = options.version,
        .config = {
            // Signaling relays through the backend, so each handshake leg costs one
            // synchronization round trip. The engine watchdog must outlast that path;
            // the adapter's own media-ready timeout remains the upper bound.
            .initializationTimeout = 60.0,
            .receiveTimeout = 60.0,
            .enableP2P = options.p2p,
            .logPath = {logPath},
            .maxApiLayer = 92,
            .customParameters = options.customParameters,
        },
        .rtcServers = options.rtcServers,
        .encryptionKey = tgcalls::EncryptionKey(keyArray, options.caller),
        .stateUpdated = [&engineState, sender](tgcalls::State state) {
            engineState.store(state);
            sender->SendControl("{\"type\":\"state\",\"state\":\"" + StateName(state) + "\"}");
        },
        .remoteMediaStateUpdated = [&remoteMediaReported, sender](tgcalls::AudioState audio, tgcalls::VideoState) {
            if (!remoteMediaReported.exchange(true)) {
                sender->SendControl(std::string("{\"type\":\"remote_media_state\",\"audio\":") +
                                            (audio == tgcalls::AudioState::Active ? "true" : "false") + "}");
            }
        },
        .signalingDataEmitted = [sender](const std::vector<uint8_t> &data) {
            // base64 encode for the JSON control channel.
            std::string encoded;
            encoded.reserve((data.size() + 2) / 3 * 4);
            for (size_t i = 0; i < data.size(); i += 3) {
                uint32_t value = static_cast<uint32_t>(data[i]) << 16;
                if (i + 1 < data.size()) value |= static_cast<uint32_t>(data[i + 1]) << 8;
                if (i + 2 < data.size()) value |= static_cast<uint32_t>(data[i + 2]);
                encoded.push_back("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"[static_cast<size_t>((value >> 18) & 0x3F)]);
                encoded.push_back("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"[static_cast<size_t>((value >> 12) & 0x3F)]);
                encoded.push_back(i + 1 < data.size() ? "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"[static_cast<size_t>((value >> 6) & 0x3F)] : '=');
                encoded.push_back(i + 2 < data.size() ? "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"[static_cast<size_t>(value & 0x3F)] : '=');
            }
            sender->SendControl("{\"type\":\"signaling\",\"data\":\"" + encoded + "\"}");
        },
        .createAudioDeviceModule = tgcalls::FakeAudioDeviceModule::Creator(
            renderer, recorder,
            tgcalls::FakeAudioDeviceModule::Options{.samples_per_sec = kSampleRate, .num_channels = 1}),
    };

    auto instance = std::shared_ptr<tgcalls::Instance>(
        tgcalls::Meta::Create(options.version, std::move(descriptor)).release());
    if (!instance) {
        sender->SendControl("{\"type\":\"error\",\"code\":\"unknown_engine_version\"}");
        return 6;
    }
    sender->SendControl("{\"type\":\"started\",\"version\":" + JsonString(options.version) + "}");

    std::thread reader([&] {
        int idleReads = 0;
        while (!stopRequested.load()) {
            Message message;
            if (!socket.ReadMessage(message, 1000)) {
                idleReads += 1;
                if (idleReads >= 600) {
                    // Ten minutes without any controller frame: treat the adapter as gone.
                    // A healthy call may legitimately send no control frames for a long time.
                    stopRequested.store(true);
                    break;
                }
                continue;
            }
            idleReads = 0;
            if (message.kind == kAudioToEngine) {
                audioInput->Push(message.payload);
                continue;
            }
            if (message.kind != kControl) {
                continue;
            }
            std::string json(message.payload.begin(), message.payload.end());
            std::string parse;
            auto parsed = json11::Json::parse(json, parse);
            if (!parsed.is_object()) {
                continue;
            }
            const std::string type = parsed["type"].is_string() ? parsed["type"].string_value() : "";
            if (type == "signaling" && parsed["data"].is_string()) {
                std::string encoded = parsed["data"].string_value();
                // base64 decode
                std::vector<uint8_t> data;
                data.reserve((encoded.size() + 3) / 4 * 3);
                for (size_t i = 0; i + 3 < encoded.size(); i += 4) {
                    auto value = [](char c) -> int {
                        if (c >= 'A' && c <= 'Z') return c - 'A';
                        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
                        if (c >= '0' && c <= '9') return c - '0' + 52;
                        if (c == '+') return 62;
                        if (c == '/') return 63;
                        return -1;
                    };
                    int a = value(encoded[i]);
                    int b = value(encoded[i + 1]);
                    int c = encoded[i + 2] == '=' ? 0 : value(encoded[i + 2]);
                    int d = encoded[i + 3] == '=' ? 0 : value(encoded[i + 3]);
                    if (a < 0 || b < 0 || c < 0 || d < 0) {
                        break;
                    }
                    data.push_back(static_cast<uint8_t>((a << 2) | (b >> 4)));
                    if (encoded[i + 2] != '=') data.push_back(static_cast<uint8_t>(((b & 0xF) << 4) | (c >> 2)));
                    if (encoded[i + 3] != '=') data.push_back(static_cast<uint8_t>(((c & 0x3) << 6) | d));
                }
                instance->receiveSignalingData(data);
            } else if (type == "stop") {
                stopRequested.store(true);
                break;
            }
        }
    });

    while (!stopRequested.load()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        if (engineState.load() == tgcalls::State::Failed) {
            sender->SendControl("{\"type\":\"failed\",\"code\":\"engine_failed\"}");
            stopRequested.store(true);
        }
    }

    instance->stop([&stopped](tgcalls::FinalState) { stopped.set_value(); });
    if (stoppedFuture.wait_for(std::chrono::seconds(3)) != std::future_status::ready) {
        sender->SendControl("{\"type\":\"error\",\"code\":\"engine_stop_timeout\"}");
    }
    sender->SendControl("{\"type\":\"stopped\"}");
    sender->Flush();
    if (reader.joinable()) {
        reader.join();
    }
    instance.reset();
    return 0;
}
