#include <windows.h>
#include <unknwn.h>
#include <roapi.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Media.h>
#include <winrt/Windows.Media.Effects.h>
#include <winrt/Windows.Media.MediaProperties.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <memory>
#include <mutex>
#include <unordered_map>

using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Windows::Foundation::Collections;
using namespace winrt::Windows::Media;
using namespace winrt::Windows::Media::Effects;
using namespace winrt::Windows::Media::MediaProperties;

struct __declspec(uuid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")) MemoryBufferByteAccess : ::IUnknown
{
    virtual HRESULT __stdcall GetBuffer(BYTE** value, UINT32* capacity) = 0;
};

struct GainState
{
    std::atomic<float> gain{ 0.f };
    std::atomic<float> inputPeak{ 0.f }, outputPeak{ 0.f };
    std::atomic<uint64_t> processedSamples{ 0 };
};

static std::mutex statesMutex;
static std::unordered_map<uint64_t, std::shared_ptr<GainState>> states;
static std::atomic<uint64_t> nextState{ 1 };

static std::shared_ptr<GainState> FindState(uint64_t id)
{
    std::lock_guard<std::mutex> lock(statesMutex);
    const auto found = states.find(id);
    return found == states.end() ? nullptr : found->second;
}

extern "C" __declspec(dllexport) uint64_t __stdcall KlippCreateAudioGainState() noexcept
{
    try
    {
        auto state = std::make_shared<GainState>();
        const auto id = nextState.fetch_add(1);
        std::lock_guard<std::mutex> lock(statesMutex);
        states.emplace(id, std::move(state));
        return id;
    }
    catch (...) { return 0; }
}

extern "C" __declspec(dllexport) void __stdcall KlippSetAudioGain(uint64_t id, float value) noexcept
{
    if (const auto state = FindState(id))
        state->gain.store(std::isfinite(value) ? std::clamp(value, 0.f, 32.f) : 0.f, std::memory_order_relaxed);
}

extern "C" __declspec(dllexport) BOOL __stdcall KlippGetAudioGainStats(uint64_t id, float* input, float* output, uint64_t* samples) noexcept
{
    const auto state = FindState(id);
    if (!state || !input || !output || !samples) return FALSE;
    *input = state->inputPeak.load(std::memory_order_relaxed);
    *output = state->outputPeak.load(std::memory_order_relaxed);
    *samples = state->processedSamples.load(std::memory_order_relaxed);
    return TRUE;
}

extern "C" __declspec(dllexport) void __stdcall KlippReleaseAudioGainState(uint64_t id) noexcept
{
    std::lock_guard<std::mutex> lock(statesMutex);
    states.erase(id);
}

struct GainEffect : implements<GainEffect, IBasicAudioEffect, IMediaExtension>
{
    std::shared_ptr<GainState> state;
    hstring GetRuntimeClassName() const { return L"Klipp.AudioEffects.Gain"; }
    bool TimeIndependent() const noexcept { return true; }
    bool UseInputFrameForOutput() const noexcept { return true; }

    IVectorView<AudioEncodingProperties> SupportedEncodingProperties() const
    {
        auto formats = single_threaded_vector<AudioEncodingProperties>();
        for (const auto rate : { 22050u, 24000u, 32000u, 44100u, 48000u, 88200u, 96000u, 192000u })
            for (uint32_t channels = 1; channels <= 8; ++channels)
            {
                auto format = AudioEncodingProperties::CreatePcm(rate, channels, 32);
                format.Subtype(MediaEncodingSubtypes::Float());
                formats.Append(format);
            }
        return formats.GetView();
    }

    void SetEncodingProperties(AudioEncodingProperties const& properties) const
    {
        if (properties.Subtype() != MediaEncodingSubtypes::Float() || properties.BitsPerSample() != 32)
            throw hresult_invalid_argument();
    }

    void SetProperties(IPropertySet const& properties)
    {
        const auto id = unbox_value<uint64_t>(properties.Lookup(L"StateId"));
        state = FindState(id);
        if (!state) throw hresult_invalid_argument();
    }

    void ProcessFrame(ProcessAudioFrameContext const& context) const
    {
        if (!state) return;
        auto buffer = context.InputFrame().LockBuffer(AudioBufferAccessMode::ReadWrite);
        auto reference = buffer.CreateReference();
        BYTE* bytes = nullptr;
        UINT32 capacity = 0;
        check_hresult(reference.as<MemoryBufferByteAccess>()->GetBuffer(&bytes, &capacity));
        const auto count = std::min(buffer.Length(), capacity) / sizeof(float);
        auto samples = reinterpret_cast<float*>(bytes);
        const auto gain = state->gain.load(std::memory_order_relaxed);
        float inputPeak = 0.f, outputPeak = 0.f;
        // No allocations, COM lookups, locks, or format conversion in the sample loop.
        for (size_t index = 0; index < count; ++index)
        {
            const auto input = std::isfinite(samples[index]) ? samples[index] : 0.f;
            const auto output = input * gain;
            samples[index] = output;
            inputPeak = std::max(inputPeak, std::abs(input));
            outputPeak = std::max(outputPeak, std::abs(output));
        }
        state->inputPeak.store(inputPeak, std::memory_order_relaxed);
        state->outputPeak.store(outputPeak, std::memory_order_relaxed);
        state->processedSamples.fetch_add(count, std::memory_order_relaxed);
    }

    // Retain this tiny state through the effect object's lifetime. The final
    // IUnknown release then cannot race a last in-flight ProcessFrame callback.
    void Close(MediaEffectClosedReason) const noexcept { }
    void DiscardQueuedFrames() const noexcept { }
};

struct GainFactory : implements<GainFactory, winrt::Windows::Foundation::IActivationFactory>
{
    winrt::Windows::Foundation::IInspectable ActivateInstance() const { return make<GainEffect>(); }
};

HRESULT __stdcall DllGetActivationFactory(HSTRING classId, void** factory) noexcept
{
    if (!factory) return E_POINTER;
    *factory = nullptr;
    try
    {
        hstring className;
        copy_from_abi(className, classId);
        if (className != L"Klipp.AudioEffects.Gain") return CLASS_E_CLASSNOTAVAILABLE;
        *factory = detach_abi(make<GainFactory>());
        return S_OK;
    }
    catch (...) { return to_hresult(); }
}

HRESULT __stdcall DllCanUnloadNow()
{
    return get_module_lock() ? S_FALSE : S_OK;
}
