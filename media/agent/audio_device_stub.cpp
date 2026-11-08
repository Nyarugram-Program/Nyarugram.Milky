// The agent always passes a FakeAudioDeviceModule through the engine
// descriptor, so the platform-default ADM factory is never used. WebRTC still
// requires the symbol to exist at link time; return null instead of pulling the
// ALSA/PulseAudio stack into the agent.

#include "modules/audio_device/include/audio_device.h"

namespace webrtc {

rtc::scoped_refptr<AudioDeviceModule> AudioDeviceModule::Create(
    AudioLayer audio_layer,
    TaskQueueFactory *task_queue_factory) {
    return nullptr;
}

rtc::scoped_refptr<AudioDeviceModuleForTest> AudioDeviceModule::CreateForTest(
    AudioLayer audio_layer,
    TaskQueueFactory *task_queue_factory) {
    return nullptr;
}

}  // namespace webrtc
