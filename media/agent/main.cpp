// Entry point for the Nyarugram media agent.
//
// Without arguments it lists the compiled tgcalls protocol versions. The media
// agent control protocol is implemented in agent.cpp; "--selftest-p2p" runs the
// in-process P2P audio verification used by the build checks.

#include "tgcalls/Instance.h"
#include "tgcalls/v2/InstanceV2Impl.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>

int RunSelfTest(int seconds);
int RunMediaAgent();

int main(int argc, char **argv) {
    // The Android JNI layer activates protocol versions through these explicit
    // template instantiations; a static library would otherwise drop the object.
    tgcalls::Register<tgcalls::InstanceV2Impl>();

    if (argc >= 2 && std::strcmp(argv[1], "--agent") == 0) {
        return RunMediaAgent();
    }
    if (argc >= 2 && std::strcmp(argv[1], "--selftest-p2p") == 0) {
        int seconds = argc >= 3 ? std::atoi(argv[2]) : 5;
        return RunSelfTest(seconds);
    }

    for (const auto &version : tgcalls::Meta::Versions()) {
        std::printf("%s\n", version.c_str());
    }
    return 0;
}
