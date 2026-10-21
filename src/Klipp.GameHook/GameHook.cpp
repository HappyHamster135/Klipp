#include "Protocol.h"
#include "MinHook.h"
#include <d3d11_1.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <atomic>
#include <algorithm>
#include <array>
#include <memory>
#include <string>
#include <vector>
#include <cstring>

using Microsoft::WRL::ComPtr;
using namespace klipp;
namespace {
using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using Present1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using ResizeFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using Resize1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain3*, UINT, UINT, UINT, DXGI_FORMAT, UINT, const UINT*, IUnknown* const*);
using ColorFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain3*, DXGI_COLOR_SPACE_TYPE);
PresentFn originalPresent = nullptr;
Present1Fn originalPresent1 = nullptr;
ResizeFn originalResize = nullptr;
Resize1Fn originalResize1 = nullptr;
ColorFn originalColor = nullptr;
SRWLOCK gate = SRWLOCK_INIT;
std::atomic<bool> active{false}, ready{false}, resetResources{false}, workerRunning{false};
std::atomic<ULONGLONG> lastHeartbeat{0};
std::atomic<bool> popupWasPresented{false};
StartArgs arguments{};
FramePayload frameInfo{};
std::string failure;
uint32_t readyGeneration = 0;
uint32_t generationCounter = 0;
bool captureEnabled = true;
struct PopupState {
    PopupPayload info{};
    std::vector<uint8_t> pixels;
    uint64_t revision = 0;
    ULONGLONG shown = 0;
};
PopupState popup;
thread_local bool insideHook = false;
thread_local bool drewThisPresent = false;

void Fail(const char* message) {
    ready = false; active = false;
    failure = message;
    popup.shown = 0;
}
bool Live() {
    return active.load(std::memory_order_acquire) && GetTickCount64() - lastHeartbeat.load() < 3500;
}
struct RenderResources {
    ComPtr<ID3D11Device1> device;
    ComPtr<ID3D11DeviceContext1> context;
    ComPtr<ID3DDeviceContextState> state;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11BlendState> blend;
    ComPtr<ID3D11RasterizerState> rasterizer;
    ComPtr<ID3D11DepthStencilState> depth;
    std::array<ComPtr<ID3D11Texture2D>, Slots> textures;
    std::array<ComPtr<IDXGIKeyedMutex>, Slots> mutexes;
    std::array<HANDLE, Slots> handles{};
    ComPtr<ID3D11Texture2D> popupTexture;
    ComPtr<ID3D11ShaderResourceView> popupView;
    uint64_t popupRevision = 0;
    uint64_t sequence = 0;
    int64_t frequency = 0, lastCapture = 0;
    uint32_t width = 0, height = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    IDXGISwapChain* chain = nullptr; // Identity only. Never retains a game's swapchain/backbuffer.
    ~RenderResources() { for (HANDLE h : handles) if (h) CloseHandle(h); }
};
std::unique_ptr<RenderResources> resources;
// A resize can race a metadata message already in the pipe. Keep the preceding
// private texture handles valid until the host acknowledges the new generation.
std::unique_ptr<RenderResources> retiredResources;
ComPtr<ID3DBlob> vertexBytecode, pixelBytecode;

constexpr char Shader[] = R"(
cbuffer Params : register(b0) { float4 rectangle; float4 tint; };
struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
V vs(uint id:SV_VertexID) {
    float2 uv=float2(id&1,id>>1);
    V o; o.p=float4(lerp(rectangle.xy,rectangle.zw,uv),0,1); o.uv=uv; return o;
}
Texture2D picture:register(t0); SamplerState linearSampler:register(s0);
float4 ps(V v):SV_TARGET { return picture.Sample(linearSampler,v.uv)*tint.x; }
)";

bool CreateResources(IDXGISwapChain* chain, ID3D11Texture2D* buffer, const D3D11_TEXTURE2D_DESC& desc) {
    auto r = std::make_unique<RenderResources>();
    ComPtr<ID3D11Device> baseDevice;
    if (FAILED(chain->GetDevice(IID_PPV_ARGS(&baseDevice))) || FAILED(baseDevice.As(&r->device))) {
        Fail("This window does not use the supported D3D11 device path."); return false;
    }
    ComPtr<ID3D11DeviceContext> baseContext;
    baseDevice->GetImmediateContext(&baseContext);
    if (FAILED(baseContext.As(&r->context))) { Fail("D3D11.1 context state is unavailable."); return false; }
    if (baseDevice->GetFeatureLevel() < D3D_FEATURE_LEVEL_11_0) { Fail("D3D11 feature level 11 is required."); return false; }
    D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    if (FAILED(r->device->CreateDeviceContextState(0, levels, 2, D3D11_SDK_VERSION,
        __uuidof(ID3D11Device), nullptr, &r->state))) { Fail("The game cannot safely preserve its graphics state."); return false; }

    ComPtr<IDXGIOutput> output;
    ComPtr<IDXGIOutput6> output6;
    DXGI_OUTPUT_DESC1 od{};
    if (FAILED(chain->GetContainingOutput(&output)) || FAILED(output.As(&output6)) || FAILED(output6->GetDesc1(&od))) {
        Fail("The game output color space could not be verified as SDR."); return false;
    }
    if (od.ColorSpace != DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709) {
        Fail("HDR output is not supported by this SDR game capture backend."); return false;
    }
    ComPtr<IDXGIDevice> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC adapterDesc{};
    if (FAILED(baseDevice.As(&dxgiDevice)) || FAILED(dxgiDevice->GetAdapter(&adapter)) || FAILED(adapter->GetDesc(&adapterDesc))) {
        Fail("The game graphics adapter could not be identified."); return false;
    }

    D3D11_TEXTURE2D_DESC shared = desc;
    shared.MipLevels = shared.ArraySize = 1;
    shared.Usage = D3D11_USAGE_DEFAULT; shared.CPUAccessFlags = 0;
    shared.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    shared.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    for (uint32_t i = 0; i < Slots; ++i) {
        ComPtr<IDXGIResource1> share;
        if (FAILED(baseDevice->CreateTexture2D(&shared, nullptr, &r->textures[i])) ||
            FAILED(r->textures[i].As(&r->mutexes[i])) || FAILED(r->textures[i].As(&share)) ||
            FAILED(share->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &r->handles[i]))) {
            Fail("Private shared capture textures are unavailable."); return false;
        }
    }
    if (!vertexBytecode || !pixelBytecode ||
        FAILED(baseDevice->CreateVertexShader(vertexBytecode->GetBufferPointer(), vertexBytecode->GetBufferSize(), nullptr, &r->vertex)) ||
        FAILED(baseDevice->CreatePixelShader(pixelBytecode->GetBufferPointer(), pixelBytecode->GetBufferSize(), nullptr, &r->pixel))) {
        Fail("The notification shaders could not be prepared."); return false;
    }
    D3D11_BUFFER_DESC cb{}; cb.ByteWidth = 32; cb.Usage = D3D11_USAGE_DEFAULT; cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    D3D11_SAMPLER_DESC sampler{}; sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; sampler.MaxLOD = D3D11_FLOAT32_MAX;
    D3D11_BLEND_DESC blend{}; auto& b = blend.RenderTarget[0]; b.BlendEnable = TRUE;
    b.SrcBlend = b.SrcBlendAlpha = D3D11_BLEND_ONE; b.DestBlend = b.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    b.BlendOp = b.BlendOpAlpha = D3D11_BLEND_OP_ADD; b.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    D3D11_RASTERIZER_DESC raster{}; raster.FillMode = D3D11_FILL_SOLID; raster.CullMode = D3D11_CULL_NONE; raster.DepthClipEnable = TRUE;
    D3D11_DEPTH_STENCIL_DESC depth{}; depth.DepthEnable = FALSE; depth.StencilEnable = FALSE;
    if (FAILED(baseDevice->CreateBuffer(&cb, nullptr, &r->constants)) || FAILED(baseDevice->CreateSamplerState(&sampler, &r->sampler)) ||
        FAILED(baseDevice->CreateBlendState(&blend, &r->blend)) || FAILED(baseDevice->CreateRasterizerState(&raster, &r->rasterizer)) ||
        FAILED(baseDevice->CreateDepthStencilState(&depth, &r->depth))) { Fail("Notification render state could not be created."); return false; }
    LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency); r->frequency = frequency.QuadPart;
    r->width = desc.Width; r->height = desc.Height; r->format = desc.Format; r->chain = chain;
    frameInfo = {}; frameInfo.generation = ++generationCounter;
    frameInfo.width = desc.Width; frameInfo.height = desc.Height; frameInfo.format = desc.Format;
    frameInfo.adapterLow = adapterDesc.AdapterLuid.LowPart; frameInfo.adapterHigh = adapterDesc.AdapterLuid.HighPart;
    for (uint32_t i = 0; i < Slots; ++i) frameInfo.handles[i] = reinterpret_cast<uint64_t>(r->handles[i]);
    ready = false; readyGeneration = 0;
    resources = std::move(r);
    (void)buffer;
    return true;
}

bool Capture(RenderResources& r, ID3D11Texture2D* buffer, int64_t now) {
    // Never stall the game's rendering thread on a consumer or an encoder.
    const int64_t interval = r.frequency / std::clamp(arguments.frameRate, 1u, 240u);
    if (now - r.lastCapture < interval) return false;
    r.lastCapture = now;
    for (uint32_t i = 0; i < Slots; ++i) {
        if (r.mutexes[i]->AcquireSync(0, 0) != S_OK) continue;
        r.context->CopyResource(r.textures[i].Get(), buffer);
        const uint64_t sequence = ++r.sequence;
        const HRESULT released = r.mutexes[i]->ReleaseSync(sequence);
        if (FAILED(released)) { Fail("The clean capture texture synchronization failed."); return false; }
        frameInfo.timestamp[i] = now;
        frameInfo.sequence[i] = sequence;
        return true;
    }
    return false;
}

void Draw(RenderResources& r, ID3D11Texture2D* buffer) {
    if (!ready || readyGeneration != frameInfo.generation || !popup.shown || popup.pixels.empty()) return;
    const auto age = GetTickCount64() - popup.shown;
    if (age >= popup.info.durationMs) { popup.shown = 0; return; }
    if (r.popupRevision != popup.revision) {
        r.popupView.Reset(); r.popupTexture.Reset();
        D3D11_TEXTURE2D_DESC td{}; td.Width = popup.info.width; td.Height = popup.info.height;
        td.MipLevels = td.ArraySize = 1; td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_IMMUTABLE; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{}; data.pSysMem = popup.pixels.data(); data.SysMemPitch = td.Width * 4;
        if (FAILED(r.device->CreateTexture2D(&td, &data, &r.popupTexture)) || FAILED(r.device->CreateShaderResourceView(r.popupTexture.Get(), nullptr, &r.popupView))) {
            popup.shown = 0; return;
        }
        r.popupRevision = popup.revision;
    }
    float opacity = 1;
    if (popup.info.animate) opacity = std::min({1.f, static_cast<float>(age) / 160.f, static_cast<float>(popup.info.durationMs - age) / 140.f});
    const float left = static_cast<float>(std::clamp(popup.info.x, 0, std::max(0, static_cast<int>(r.width) - static_cast<int>(popup.info.width))));
    const float top = static_cast<float>(std::clamp(popup.info.y, 0, std::max(0, static_cast<int>(r.height) - static_cast<int>(popup.info.height))));
    float values[8]{left * 2.f / r.width - 1.f, 1.f - top * 2.f / r.height,
        (left + popup.info.width) * 2.f / r.width - 1.f, 1.f - (top + popup.info.height) * 2.f / r.height, opacity, 0, 0, 0};
    ComPtr<ID3D11RenderTargetView> target;
    if (FAILED(r.device->CreateRenderTargetView(buffer, nullptr, &target))) { popup.shown = 0; return; }

    // D3D11.1 captures the complete application pipeline, including shader instances,
    // UAVs, stream output and predication. Hand-picked state backups omit too much.
    ComPtr<ID3DDeviceContextState> previous;
    r.context->SwapDeviceContextState(r.state.Get(), &previous);
    r.context->UpdateSubresource(r.constants.Get(), 0, nullptr, values, 0, 0);
    ID3D11RenderTargetView* rt = target.Get(); r.context->OMSetRenderTargets(1, &rt, nullptr);
    const D3D11_VIEWPORT viewport{0, 0, static_cast<float>(r.width), static_cast<float>(r.height), 0, 1};
    r.context->RSSetViewports(1, &viewport); r.context->RSSetState(r.rasterizer.Get());
    r.context->OMSetBlendState(r.blend.Get(), nullptr, 0xffffffff); r.context->OMSetDepthStencilState(r.depth.Get(), 0);
    r.context->IASetInputLayout(nullptr); r.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
    r.context->VSSetShader(r.vertex.Get(), nullptr, 0); r.context->PSSetShader(r.pixel.Get(), nullptr, 0);
    r.context->GSSetShader(nullptr, nullptr, 0); r.context->HSSetShader(nullptr, nullptr, 0); r.context->DSSetShader(nullptr, nullptr, 0);
    auto constant = r.constants.Get(); auto view = r.popupView.Get(); auto sampler = r.sampler.Get();
    r.context->VSSetConstantBuffers(0, 1, &constant); r.context->PSSetConstantBuffers(0, 1, &constant);
    r.context->PSSetShaderResources(0, 1, &view); r.context->PSSetSamplers(0, 1, &sampler);
    r.context->Draw(4, 0);
    drewThisPresent = true;
    // Unbind the backbuffer from our retained state before returning it to the game.
    r.context->OMSetRenderTargets(0, nullptr, nullptr);
    ID3D11ShaderResourceView* none = nullptr; r.context->PSSetShaderResources(0, 1, &none);
    r.context->SwapDeviceContextState(previous.Get(), nullptr);
}

void BeforePresent(IDXGISwapChain* chain, UINT flags, const DXGI_PRESENT_PARAMETERS* params) noexcept {
    if (resetResources && TryAcquireSRWLockExclusive(&gate)) {
        if (resetResources.exchange(false)) { resources.reset(); retiredResources.reset(); }
        ReleaseSRWLockExclusive(&gate);
    }
    if ((flags & DXGI_PRESENT_TEST) || !Live() || !TryAcquireSRWLockExclusive(&gate)) return;
    try {
        if (!Live()) { ReleaseSRWLockExclusive(&gate); return; }
        DXGI_SWAP_CHAIN_DESC swap{};
        if (FAILED(chain->GetDesc(&swap)) || swap.OutputWindow != reinterpret_cast<HWND>(arguments.window)) {
            ReleaseSRWLockExclusive(&gate); return;
        }
        if (resetResources.exchange(false)) { resources.reset(); retiredResources.reset(); }
        if (params && (params->DirtyRectsCount || params->pScrollRect || params->pScrollOffset)) {
            Fail("Partial or scrolling presentation is unsupported. Use Windows Graphics Capture.");
            ReleaseSRWLockExclusive(&gate); return;
        }
        if (swap.SwapEffect != DXGI_SWAP_EFFECT_DISCARD && swap.SwapEffect != DXGI_SWAP_EFFECT_FLIP_DISCARD) {
            Fail("Retained/sequential swapchains are unsupported. Use Windows Graphics Capture.");
            ReleaseSRWLockExclusive(&gate); return;
        }
        ComPtr<ID3D11Texture2D> buffer;
        if (FAILED(chain->GetBuffer(0, IID_PPV_ARGS(&buffer)))) {
            Fail("The selected game has no supported D3D11 backbuffer."); ReleaseSRWLockExclusive(&gate); return;
        }
        D3D11_TEXTURE2D_DESC desc{}; buffer->GetDesc(&desc);
        if ((desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM && desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM) ||
            desc.SampleDesc.Count != 1 || !desc.Width || !desc.Height || desc.Width > 8192 || desc.Height > 8192 ||
            static_cast<uint64_t>(desc.Width) * desc.Height * 4 > 128ull * 1024 * 1024) {
            Fail("Only single-sample SDR BGRA8/RGBA8 game backbuffers are supported."); ReleaseSRWLockExclusive(&gate); return;
        }
        if (resources && (resources->chain != chain || resources->width != desc.Width || resources->height != desc.Height || resources->format != desc.Format)) retiredResources = std::move(resources);
        if (!resources && !CreateResources(chain, buffer.Get(), desc)) { ReleaseSRWLockExclusive(&gate); return; }
        LARGE_INTEGER now{}; QueryPerformanceCounter(&now);
        // No notification is drawn until the host has opened a clean frame from this generation.
        Capture(*resources, buffer.Get(), now.QuadPart);
        if (Live()) Draw(*resources, buffer.Get());
    } catch (...) { Fail("The D3D11 bridge encountered a graphics error."); }
    ReleaseSRWLockExclusive(&gate);
}

HRESULT AfterPresent(IDXGISwapChain* chain, UINT flags, HRESULT result) {
    bool selected = false;
    if (Live() || popupWasPresented || FAILED(result)) {
        DXGI_SWAP_CHAIN_DESC desc{};
        selected = SUCCEEDED(chain->GetDesc(&desc)) && desc.OutputWindow == reinterpret_cast<HWND>(arguments.window);
    }
    if (!(flags & DXGI_PRESENT_TEST) && SUCCEEDED(result) && result != DXGI_STATUS_OCCLUDED && (drewThisPresent || popupWasPresented)) {
        if (selected) popupWasPresented = drewThisPresent;
    }
    if (selected && FAILED(result) && TryAcquireSRWLockExclusive(&gate)) {
        Fail("The game presentation device failed; the in-game notification was disabled.");
        resetResources = true;
        ReleaseSRWLockExclusive(&gate);
    }
    return result;
}
HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* chain, UINT interval, UINT flags) {
    if (insideHook) return originalPresent(chain, interval, flags);
    insideHook = true; drewThisPresent = false; BeforePresent(chain, flags, nullptr);
    const HRESULT result = originalPresent(chain, interval, flags); insideHook = false;
    return AfterPresent(chain, flags, result);
}
HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* chain, UINT interval, UINT flags, const DXGI_PRESENT_PARAMETERS* params) {
    if (insideHook) return originalPresent1(chain, interval, flags, params);
    insideHook = true; drewThisPresent = false; BeforePresent(chain, flags, params);
    const HRESULT result = originalPresent1(chain, interval, flags, params); insideHook = false;
    return AfterPresent(chain, flags, result);
}
void BeforeResize(IDXGISwapChain* chain) {
    AcquireSRWLockExclusive(&gate);
    if (resources && resources->chain == chain) { retiredResources = std::move(resources); frameInfo = {}; ready = false; popup.shown = 0; }
    ReleaseSRWLockExclusive(&gate);
}
HRESULT STDMETHODCALLTYPE HookResize(IDXGISwapChain* c, UINT n, UINT w, UINT h, DXGI_FORMAT f, UINT flags) {
    BeforeResize(c); return originalResize(c, n, w, h, f, flags);
}
HRESULT STDMETHODCALLTYPE HookResize1(IDXGISwapChain3* c, UINT n, UINT w, UINT h, DXGI_FORMAT f, UINT flags, const UINT* masks, IUnknown* const* queues) {
    BeforeResize(c); return originalResize1(c, n, w, h, f, flags, masks, queues);
}
HRESULT STDMETHODCALLTYPE HookColor(IDXGISwapChain3* c, DXGI_COLOR_SPACE_TYPE color) {
    if (color != DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709) {
        AcquireSRWLockExclusive(&gate);
        DXGI_SWAP_CHAIN_DESC desc{};
        if (SUCCEEDED(c->GetDesc(&desc)) && desc.OutputWindow == reinterpret_cast<HWND>(arguments.window)) Fail("HDR/wide-color presentation is unsupported by this SDR backend.");
        ReleaseSRWLockExclusive(&gate);
    }
    return originalColor(c, color);
}

bool AddHook(void* target, void* replacement, void** original) {
    return MH_CreateHook(target, replacement, original) == MH_OK && MH_QueueEnableHook(target) == MH_OK;
}
bool InstallHooks() {
    static bool installed = false;
    if (installed) return true;
    ComPtr<ID3DBlob> error;
    if (FAILED(D3DCompile(Shader, sizeof(Shader), nullptr, nullptr, nullptr, "vs", "vs_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &vertexBytecode, &error)) ||
        FAILED(D3DCompile(Shader, sizeof(Shader), nullptr, nullptr, nullptr, "ps", "ps_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &pixelBytecode, &error))) return false;
    const wchar_t* name = L"Klipp.GameHook.Probe";
    WNDCLASSW cls{}; cls.hInstance = GetModuleHandleW(nullptr); cls.lpfnWndProc = DefWindowProcW; cls.lpszClassName = name;
    RegisterClassW(&cls);
    HWND window = CreateWindowW(name, L"", WS_OVERLAPPED, 0, 0, 2, 2, nullptr, nullptr, cls.hInstance, nullptr);
    if (!window) return false;
    DXGI_SWAP_CHAIN_DESC desc{}; desc.BufferDesc.Width = desc.BufferDesc.Height = 2;
    desc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2; desc.OutputWindow = window;
    desc.Windowed = TRUE; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    ComPtr<IDXGISwapChain> chain; ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
    const HRESULT created = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, &context);
    bool okay = false;
    if (SUCCEEDED(created)) {
        const auto initialized = MH_Initialize();
        if (initialized == MH_OK || initialized == MH_ERROR_ALREADY_INITIALIZED) {
            auto v = *reinterpret_cast<void***>(chain.Get());
            okay = AddHook(v[8], reinterpret_cast<void*>(HookPresent), reinterpret_cast<void**>(&originalPresent)) &&
                AddHook(v[13], reinterpret_cast<void*>(HookResize), reinterpret_cast<void**>(&originalResize));
            ComPtr<IDXGISwapChain1> chain1;
            if (okay && SUCCEEDED(chain.As(&chain1))) {
                auto v1 = *reinterpret_cast<void***>(chain1.Get());
                okay = AddHook(v1[22], reinterpret_cast<void*>(HookPresent1), reinterpret_cast<void**>(&originalPresent1));
            }
            ComPtr<IDXGISwapChain3> chain3;
            if (okay && SUCCEEDED(chain.As(&chain3))) {
                auto v3 = *reinterpret_cast<void***>(chain3.Get());
                okay = AddHook(v3[38], reinterpret_cast<void*>(HookColor), reinterpret_cast<void**>(&originalColor)) &&
                    AddHook(v3[39], reinterpret_cast<void*>(HookResize1), reinterpret_cast<void**>(&originalResize1));
            }
            if (okay) okay = MH_ApplyQueued() == MH_OK;
            if (!okay) MH_DisableHook(MH_ALL_HOOKS);
        }
    }
    context.Reset(); device.Reset(); chain.Reset(); DestroyWindow(window); UnregisterClassW(name, cls.hInstance);
    installed = okay;
    return okay;
}

bool Write(HANDLE pipe, uint32_t type, const void* data, uint32_t size) {
    Header header{type, size}; DWORD count = 0;
    return WriteFile(pipe, &header, sizeof(header), &count, nullptr) && count == sizeof(header) &&
        (!size || (WriteFile(pipe, data, size, &count, nullptr) && count == size));
}
bool ReadAll(HANDLE pipe, void* data, uint32_t size) {
    uint32_t offset = 0;
    while (offset < size) { DWORD count = 0; if (!ReadFile(pipe, static_cast<uint8_t*>(data) + offset, size - offset, &count, nullptr) || !count) return false; offset += count; }
    return true;
}
DWORD WINAPI Worker(void*) {
    const std::wstring path = L"\\\\.\\pipe\\" + std::wstring(arguments.pipe);
    HANDLE pipe = INVALID_HANDLE_VALUE;
    const auto deadline = GetTickCount64() + 5000;
    while (GetTickCount64() < deadline) {
        pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) break;
        Sleep(10);
    }
    ULONG server = 0;
    if (pipe == INVALID_HANDLE_VALUE || !GetNamedPipeServerProcessId(pipe, &server) || server != arguments.hostPid) {
        if (pipe != INVALID_HANDLE_VALUE) CloseHandle(pipe); workerRunning = false; return 1;
    }
    HelloPayload hello{Magic, Version, GetCurrentProcessId(), {}}; memcpy(hello.nonce, arguments.nonce, sizeof(hello.nonce));
    if (!Write(pipe, Hello, &hello, sizeof(hello)) || !InstallHooks()) {
        const char* error = "The supported D3D11 presentation hooks could not be installed.";
        Write(pipe, Failure, error, static_cast<uint32_t>(strlen(error))); CloseHandle(pipe); workerRunning = false; return 2;
    }
    AcquireSRWLockExclusive(&gate);
    failure.clear(); frameInfo = {}; captureEnabled = true; popup = {}; readyGeneration = 0;
    resetResources = true; lastHeartbeat = GetTickCount64(); active = true; ready = false;
    ReleaseSRWLockExclusive(&gate);
    uint64_t lastSequence = 0; uint32_t lastGeneration = 0;
    bool connected = true, disconnectRequested = false;
    while (connected && Live()) {
        FramePayload current{}; std::string error;
        AcquireSRWLockShared(&gate); current = frameInfo; error = failure; ReleaseSRWLockShared(&gate);
        if (!error.empty()) { Write(pipe, Failure, error.data(), static_cast<uint32_t>(error.size())); break; }
        const uint64_t sequence = std::max({current.sequence[0], current.sequence[1], current.sequence[2]});
        if (sequence && (sequence != lastSequence || current.generation != lastGeneration)) {
            if (!Write(pipe, Frame, &current, sizeof(current))) break;
            lastSequence = sequence; lastGeneration = current.generation;
        }
        DWORD available = 0;
        if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) break;
        if (available >= sizeof(Header)) {
            Header header{};
            if (!ReadAll(pipe, &header, sizeof(header)) || header.bytes > MaximumMessage) break;
            std::vector<uint8_t> data(header.bytes);
            if (header.bytes && !ReadAll(pipe, data.data(), header.bytes)) break;
            // Allocate/copy bitmap bytes off the render lock; Present only skips a
            // short metadata swap instead of waiting on a multi-megabyte vector copy.
            std::vector<uint8_t> incomingPixels;
            if (header.type == Popup && data.size() >= sizeof(PopupPayload)) incomingPixels.assign(data.begin() + sizeof(PopupPayload), data.end());
            const auto now = GetTickCount64();
            AcquireSRWLockExclusive(&gate);
            switch (header.type) {
            case Heartbeat: if (data.size() == 4) { captureEnabled = *reinterpret_cast<const uint32_t*>(data.data()) != 0; lastHeartbeat = now; } else connected = false; break;
            case Ready: if (data.size() == 4 && *reinterpret_cast<const uint32_t*>(data.data()) == frameInfo.generation) { readyGeneration = frameInfo.generation; ready = true; retiredResources.reset(); } break;
            case Hide: popup.shown = 0; break;
            case Disconnect: popup.shown = 0; active = false; connected = false; disconnectRequested = true; break;
            case Popup:
                if (data.size() >= sizeof(PopupPayload)) {
                    PopupPayload info{}; memcpy(&info, data.data(), sizeof(info));
                    if (ready && info.width && info.height && info.width <= 1024 && info.height <= 512 &&
                        info.durationMs >= 100 && info.durationMs <= 12000 && data.size() == sizeof(info) + static_cast<size_t>(info.width) * info.height * 4) {
                        popup.info = info; popup.pixels.swap(incomingPixels);
                        popup.revision++; popup.shown = now;
                    }
                }
                break;
            default: connected = false; break;
            }
            ReleaseSRWLockExclusive(&gate);
        }
        Sleep(3);
    }
    AcquireSRWLockExclusive(&gate);
    // Failure can become visible between the loop condition and the snapshot above.
    const std::string finalFailure = failure;
    active = false; ready = false; popup.shown = 0; resetResources = true;
    ReleaseSRWLockExclusive(&gate);
    if (!finalFailure.empty()) Write(pipe, Failure, finalFailure.data(), static_cast<uint32_t>(finalFailure.size()));
    {
        const auto cleanDeadline = GetTickCount64() + 1800;
        bool notifiedPending = false;
        while (popupWasPresented) {
            DWORD available = 0;
            if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) break;
            if (GetTickCount64() >= cleanDeadline && !notifiedPending) {
                const uint32_t pending = 0;
                if (!Write(pipe, Stopped, &pending, sizeof(pending))) break;
                notifiedPending = true;
            }
            Sleep(5);
        }
        if (!popupWasPresented) { const uint32_t clean = 1; Write(pipe, Stopped, &clean, sizeof(clean)); }
    }
    CloseHandle(pipe); workerRunning = false;
    return 0;
}
}

extern "C" __declspec(dllexport) DWORD WINAPI KlippGameCaptureStart(void* data) {
    if (!data || workerRunning.exchange(true)) return 1;
    StartArgs next{}; memcpy(&next, data, sizeof(next));
    DWORD owner = 0; GetWindowThreadProcessId(reinterpret_cast<HWND>(next.window), &owner);
    if (next.magic != Magic || next.version != Version || owner != GetCurrentProcessId() || !next.hostPid ||
        next.pipe[127] != 0 || next.nonce[63] == 0 || next.frameRate < 1 || next.frameRate > 240) {
        workerRunning = false; return 2;
    }
    arguments = next;
    HANDLE thread = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
    if (!thread) { workerRunning = false; return 3; }
    CloseHandle(thread); return 0;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
