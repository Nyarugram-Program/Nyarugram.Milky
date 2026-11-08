cmake_minimum_required(VERSION 3.16)
project(milky_media_build C CXX)

# The upstream source tree is produced by prepare-source.sh; the dependency output
# directory holds the libvpx static library built by build-agent.sh.
set(MILKY_MEDIA_SRC "${CMAKE_CURRENT_SOURCE_DIR}/src" CACHE PATH
    "Prepared upstream source tree (tgcalls, voip, third_party, openssl)")
set(MILKY_MEDIA_OUT "${CMAKE_CURRENT_SOURCE_DIR}/out" CACHE PATH
    "Prepared dependency output directory (libvpx)")

if(NOT EXISTS "${MILKY_MEDIA_SRC}/tgcalls/tgcalls/v2/InstanceV2Impl.cpp")
    message(FATAL_ERROR "prepared source is missing at ${MILKY_MEDIA_SRC}; run prepare-source.sh first")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -O1 -w -fPIC")
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -O1 -w -fPIC -fno-strict-aliasing")

set(SRC ${MILKY_MEDIA_SRC})
file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/sources.txt" TG_SOURCES)

set(TG_INCLUDES
    ${SRC}
    ${SRC}/voip
    ${SRC}/tgcalls
    ${SRC}/tgcalls/tgcalls
    ${SRC}/third_party/boringssl/include
    ${SRC}/openssl/include
    ${SRC}/voip/rnnoise/include
    ${SRC}/voip/webrtc
    ${SRC}/third_party/absl
    ${SRC}/third_party/xiph/opus/include
    ${SRC}/third_party/xiph/opus/silk
    ${SRC}/third_party/xiph/opus/silk/fixed
    ${SRC}/third_party/xiph/opus/celt
    ${SRC}/third_party/xiph/opus
    ${SRC}/third_party/libyuv/include
    ${SRC}/third_party/libsrtp/include
    ${SRC}/third_party/libsrtp/config
    ${SRC}/third_party/libsrtp/crypto/include
    ${SRC}/third_party/usrsctplib
    ${SRC}/third_party
    ${SRC}/third_party/ffmpeg
    ${SRC}/third_party/libvpx
    ${SRC}/third_party/dav1d/include
    ${SRC}/voip/third_party/crc32c/src/include
)

set(TG_DEFINES
    WEBRTC_HAVE_DCSCTP
    RTC_DISABLE_TRACE_EVENTS
    WEBRTC_OPUS_SUPPORT_120MS_PTIME=1
    BWE_TEST_LOGGING_COMPILE_TIME_ENABLE=0
    ABSL_ALLOCATOR_NOTHROW=1
    WEBRTC_NS_FLOAT
    HAVE_PTHREAD
    RTC_ENABLE_VP9
    WEBRTC_POSIX
    WEBRTC_LINUX
    NDEBUG
    WEBRTC_HAVE_USRSCTP
    WEBRTC_HAVE_SCTP
    WEBRTC_APM_DEBUG_DUMP=0
    WEBRTC_USE_BUILTIN_ISAC_FLOAT
    WEBRTC_OPUS_VARIABLE_COMPLEXITY=0
    HAVE_NETINET_IN_H
    HAVE_WEBRTC_VIDEO
    HAVE_SSE2
    RTC_ENABLE_H265
)

# rnnoise (webrtc's own copy, symbol-prefixed exactly like the Android build)
add_library(rnnoise STATIC
    ${SRC}/voip/rnnoise/src/celt_lpc.c
    ${SRC}/voip/rnnoise/src/denoise.c
    ${SRC}/voip/rnnoise/src/kiss_fft.c
    ${SRC}/voip/rnnoise/src/pitch.c
    ${SRC}/voip/rnnoise/src/rnn_data.c
    ${SRC}/voip/rnnoise/src/rnn_reader.c
    ${SRC}/voip/rnnoise/src/rnn.c)
target_compile_definitions(rnnoise PRIVATE
    HAVE_PTHREAD __STDC_LIMIT_MACROS BSD=1 USE_KISS_FFT SOCKLEN_T=socklen_t LOCALE_NOT_USED
    _LARGEFILE_SOURCE=1 _FILE_OFFSET_BITS=64 restrict= __EMX__ OPUS_BUILD USE_ALLOCA HAVE_LRINT HAVE_LRINTF
    _celt_autocorr=rnnoise__celt_autocorr celt_fir=rnnoise_celt_fir celt_iir=rnnoise_celt_iir
    _celt_lpc=rnnoise__celt_lpc celt_pitch_xcorr=rnnoise_celt_pitch_xcorr
    compute_band_corr=rnnoise_compute_band_corr compute_band_energy=rnnoise_compute_band_energy
    compute_dense=rnnoise_compute_dense compute_gru=rnnoise_compute_gru compute_rnn=rnnoise_compute_rnn
    interp_band_gain=rnnoise_interp_band_gain
    opus_fft_alloc=rnnoise_opus_fft_alloc opus_fft_alloc_arch_c=rnnoise_opus_fft_alloc_arch_c
    opus_fft_alloc_twiddles=rnnoise_opus_fft_alloc_twiddles opus_fft_c=rnnoise_opus_fft_c
    opus_fft_free=rnnoise_opus_fft_free opus_fft_free_arch_c=rnnoise_opus_fft_free_arch_c
    opus_fft_impl=rnnoise_opus_fft_impl opus_ifft_c=rnnoise_opus_ifft_c
    pitch_downsample=rnnoise_pitch_downsample pitch_filter=rnnoise_pitch_filter
    pitch_search=rnnoise_pitch_search remove_doubling=rnnoise_remove_doubling
    WEBRTC_POSIX WEBRTC_LINUX)
target_include_directories(rnnoise PUBLIC ${SRC}/voip/rnnoise/include)

# tgcalls third-party: usrsctp, libsrtp, pffft, rnnoise vad
add_library(tgcalls_tp STATIC
    ${SRC}/third_party/rnnoise/src/rnn_vad_weights.cc
    ${SRC}/third_party/pffft/src/fftpack.c
    ${SRC}/third_party/pffft/src/pffft.c
    ${SRC}/third_party/libsrtp/crypto/cipher/aes_gcm_ossl.c
    ${SRC}/third_party/libsrtp/crypto/cipher/aes_icm_ossl.c
    ${SRC}/third_party/libsrtp/crypto/cipher/cipher.c
    ${SRC}/third_party/libsrtp/crypto/cipher/null_cipher.c
    ${SRC}/third_party/libsrtp/crypto/hash/auth.c
    ${SRC}/third_party/libsrtp/crypto/hash/hmac_ossl.c
    ${SRC}/third_party/libsrtp/crypto/hash/null_auth.c
    ${SRC}/third_party/libsrtp/crypto/kernel/alloc.c
    ${SRC}/third_party/libsrtp/crypto/kernel/crypto_kernel.c
    ${SRC}/third_party/libsrtp/crypto/kernel/err.c
    ${SRC}/third_party/libsrtp/crypto/kernel/key.c
    ${SRC}/third_party/libsrtp/crypto/math/datatypes.c
    ${SRC}/third_party/libsrtp/crypto/math/stat.c
    ${SRC}/third_party/libsrtp/crypto/replay/rdb.c
    ${SRC}/third_party/libsrtp/crypto/replay/rdbx.c
    ${SRC}/third_party/libsrtp/crypto/replay/ut_sim.c
    ${SRC}/third_party/libsrtp/srtp/ekt.c
    ${SRC}/third_party/libsrtp/srtp/srtp.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_asconf.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_auth.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_bsd_addr.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_callout.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_cc_functions.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_crc32.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_indata.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_input.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_output.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_pcb.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_peeloff.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_sha1.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_ss_functions.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_sysctl.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_timer.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_userspace.c
    ${SRC}/third_party/usrsctplib/netinet/sctp_usrreq.c
    ${SRC}/third_party/usrsctplib/netinet/sctputil.c
    ${SRC}/third_party/usrsctplib/netinet6/sctp6_usrreq.c
    ${SRC}/third_party/usrsctplib/user_environment.c
    ${SRC}/third_party/usrsctplib/user_mbuf.c
    ${SRC}/third_party/usrsctplib/user_recv_thread.c
    ${SRC}/third_party/usrsctplib/user_socket.c)
target_compile_definitions(tgcalls_tp PUBLIC
    WEBRTC_HAVE_DCSCTP RTC_DISABLE_TRACE_EVENTS WEBRTC_OPUS_SUPPORT_120MS_PTIME=1
    BWE_TEST_LOGGING_COMPILE_TIME_ENABLE=0 ABSL_ALLOCATOR_NOTHROW=1 HAVE_PTHREAD RTC_ENABLE_VP9
    WEBRTC_POSIX WEBRTC_LINUX NDEBUG WEBRTC_HAVE_USRSCTP WEBRTC_HAVE_SCTP WEBRTC_APM_DEBUG_DUMP=0
    WEBRTC_USE_BUILTIN_ISAC_FLOAT WEBRTC_OPUS_VARIABLE_COMPLEXITY=0 HAVE_NETINET_IN_H
    __Userspace__ SCTP_SIMPLE_ALLOCATOR SCTP_PROCESS_LEVEL_LOCKS __Userspace_os_Linux
    HAVE_WEBRTC_VIDEO RTC_ENABLE_H265)
target_include_directories(tgcalls_tp PUBLIC ${TG_INCLUDES})

set(TG_SOURCES_ABS "")
foreach(source IN LISTS TG_SOURCES)
    list(APPEND TG_SOURCES_ABS "${SRC}/${source}")
endforeach()

add_library(tgcalls STATIC ${TG_SOURCES_ABS})
target_compile_definitions(tgcalls PUBLIC ${TG_DEFINES})
target_include_directories(tgcalls PUBLIC ${TG_INCLUDES})
target_link_libraries(tgcalls PUBLIC tgcalls_tp rnnoise)

set(OPENSSL_NO_TESTS ON CACHE BOOL "" FORCE)
set(OPENSSL_NO_ASM OFF CACHE BOOL "" FORCE)
add_subdirectory(${SRC}/third_party/boringssl ${CMAKE_BINARY_DIR}/boringssl EXCLUDE_FROM_ALL)

set(OPUS_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_SHARED_LIBRARY OFF CACHE BOOL "" FORCE)
add_subdirectory(${SRC}/third_party/xiph/opus ${CMAKE_BINARY_DIR}/opus EXCLUDE_FROM_ALL)

set(YUV_BUILD_TESTS OFF CACHE BOOL "" FORCE)
add_subdirectory(${SRC}/third_party/libyuv ${CMAKE_BINARY_DIR}/libyuv EXCLUDE_FROM_ALL)

set(ABSL_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(ABSL_ENABLE_INSTALL OFF CACHE BOOL "" FORCE)
add_subdirectory(${SRC}/third_party/absl ${CMAKE_BINARY_DIR}/absl EXCLUDE_FROM_ALL)

add_library(vpx STATIC IMPORTED)
set_target_properties(vpx PROPERTIES
    IMPORTED_LOCATION "${MILKY_MEDIA_OUT}/vpx/lib/libvpx.a"
    INTERFACE_INCLUDE_DIRECTORIES "${SRC}/third_party/libvpx")

add_executable(milky_media_agent
    agent/main.cpp
    agent/agent.cpp
    agent/selftest.cpp
    agent/platform_linux.cpp
    agent/audio_device_stub.cpp
    ${SRC}/tgcalls/tgcalls/third-party/json11.cpp)
target_include_directories(milky_media_agent PRIVATE ${TG_INCLUDES})
target_compile_definitions(milky_media_agent PRIVATE ${TG_DEFINES})
target_link_libraries(milky_media_agent PRIVATE
    tgcalls ssl crypto opus yuv vpx z event
    absl::base absl::strings absl::int128 absl::synchronization absl::time absl::numeric
    -lpthread -ldl -lrt)
