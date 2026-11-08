// Minimal Linux platform layer for the tgcalls engine.
//
// The Android client implements this layer against JNI and the Android audio
// stack. The media agent only needs audio, so the platform provides stock
// software video codec factories (required unconditionally by the engine) and
// no capture source. Application audio never goes through this layer: the
// agent supplies a FakeAudioDeviceModule to the engine descriptor.

#include "api/video_codecs/builtin_video_decoder_factory.h"
#include "api/video_codecs/builtin_video_encoder_factory.h"
#include "platform/PlatformInterface.h"
#include "VideoCapturerInterface.h"

namespace tgcalls {

class LinuxPlatformInterface final : public PlatformInterface {
public:
    std::unique_ptr<webrtc::VideoEncoderFactory> makeVideoEncoderFactory(
        bool preferHardwareEncoding, bool isScreencast) override {
        return webrtc::CreateBuiltinVideoEncoderFactory();
    }

    std::unique_ptr<webrtc::VideoDecoderFactory> makeVideoDecoderFactory() override {
        return webrtc::CreateBuiltinVideoDecoderFactory();
    }

    bool supportsEncoding(const std::string &codecName) override {
        return codecName == "VP8" || codecName == "VP9";
    }

    webrtc::scoped_refptr<webrtc::VideoTrackSourceInterface> makeVideoSource(
        rtc::Thread *signalingThread, rtc::Thread *workerThread, bool screencapture) override {
        return nullptr;
    }

    void adaptVideoSource(webrtc::scoped_refptr<webrtc::VideoTrackSourceInterface> videoSource,
                          int width, int height, int fps) override {
    }

    std::unique_ptr<VideoCapturerInterface> makeVideoCapturer(
        webrtc::scoped_refptr<webrtc::VideoTrackSourceInterface> source,
        std::string deviceId,
        std::function<void(VideoState)> stateUpdated,
        std::function<void(PlatformCaptureInfo)> captureInfoUpdated,
        std::shared_ptr<PlatformContext> platformContext,
        std::pair<int, int> &outResolution) override {
        return nullptr;
    }
};

std::unique_ptr<PlatformInterface> CreatePlatformInterface() {
    return std::make_unique<LinuxPlatformInterface>();
}

}  // namespace tgcalls
