# DXGI fullscreen diagnostic

This opt-in fake game renders an animated Direct3D 11 swap chain. The fake game itself does not inject into any process, change compatibility/registry settings, or load Klipp settings, projects or saved media. The separate `Klipp.GameHookHarness` or Klipp's opt-in DirectX 11 backend can attach to it through the production capture path.

Build with `dotnet build tools/Klipp.ExclusiveFullscreenHarness -p:Platform=x64`. Run its built EXE only when prepared for a temporary fullscreen test. Activate the returned **Klipp DXGI fullscreen QA** window first.

- **X** requests `IDXGISwapChain.SetFullscreenState(TRUE)` on this fake game's own swap chain. Press X again to return to its ordinary window.
- **P** shows Klipp's actual nonactivating, click-through desktop notification near the upper-left edge. The half-transparent dark card retains opaque text/icons and 160 ms entrance/140 ms exit fades when system motion is enabled. Capture exclusion must succeed before showing this external window. This key does not test the injected in-game notification; use `Klipp.GameHookHarness` or an actual Klipp recording/clip for that path.
- **G** toggles an in-memory Windows Graphics Capture probe of the fake game window. It prints frame count, black frames, largest delivery gap, and background-color samples beneath a visible popup. No captured image/audio/video file is saved.
- **Space** pauses/resumes rendering and presentation while keeping the message loop responsive. The paired harness uses this to prove fallback is refused while a frozen displayed frame still contains an injected card, then succeeds after a clean frame resumes.
- **Escape** returns to windowed mode and exits. Closing the window does the same cleanup.

Console output reports DXGI's fullscreen state, foreground ownership, shell notification state, click/key counters and capture delivery. The window title contains current diagnostics and key commands; a moving rectangle proves the D3D swap chain is updating.

`GetFullscreenState(TRUE)` is an API result, **not proof of physical exclusive presentation**. Windows Fullscreen Optimizations can intercept this request. Similarly, a visible HWND, capture exclusion, or continued input does not prove the popup is scanned out above a physically exclusive game. This tool does not disable Fullscreen Optimizations to manufacture such a claim. A Game Bar companion is not used.

The default swapchain uses discard presentation. `--flip-discard` exercises the supported flip path. `--sequential --partial` retains the backbuffer while repainting a moving rectangle; `--flip-sequential --partial --dirty` also uses Present1 dirty rectangles after an initial full frame. Those retained/dirty cases must be rejected by native game capture before drawing a popup. The separate harness's `--resize` option asks only this fake game to resize its backbuffer to 1280×720 and checks recovery without changing the encoder canvas.

Klipp's native path is enabled through **Settings → Capture → Exclusive fullscreen · DirectX 11** and supports selected x64 D3D11 SDR discard/flip-discard windows. Ordinary desktop capture continues to use capture-excluded popup windows. Tests of this fake game validate the paired clean-frame/display-frame path, focus and fallback behavior; real games, drivers and other graphics APIs require their own compatibility validation.
