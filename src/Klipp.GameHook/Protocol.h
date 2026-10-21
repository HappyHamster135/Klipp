#pragma once
#include <Windows.h>
#include <cstdint>

// Local, authenticated, versioned IPC. Never accepts paths or executable code.
namespace klipp {
constexpr uint32_t Magic = 0x50494C4B;
constexpr uint32_t Version = 1;
constexpr uint32_t Slots = 3;
constexpr uint32_t MaximumMessage = 4 * 1024 * 1024;
enum Message : uint32_t { Hello = 1, Frame = 2, Failure = 3, Stopped = 4, Heartbeat = 10,
    Popup = 11, Hide = 12, Disconnect = 13, Ready = 14 };
#pragma pack(push, 1)
struct StartArgs {
    uint32_t magic, version, hostPid, frameRate;
    uint64_t window;
    wchar_t pipe[128];
    char nonce[64];
};
struct Header { uint32_t type, bytes; };
struct HelloPayload { uint32_t magic, version, processId; char nonce[64]; };
struct FramePayload {
    uint32_t generation, width, height, format;
    uint32_t adapterLow; int32_t adapterHigh;
    uint64_t handles[Slots], sequence[Slots];
    int64_t timestamp[Slots];
};
struct PopupPayload { uint32_t width, height; int32_t x, y; uint32_t durationMs, animate; };
#pragma pack(pop)
static_assert(sizeof(StartArgs) == 344);
static_assert(sizeof(FramePayload) == 96);
}
