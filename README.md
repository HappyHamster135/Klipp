# Klipp

A Medal-style screen clipping app for Windows. Built as a portfolio project to demonstrate modern .NET desktop development with low-level Windows API interop.

## Features

- **Continuous background capture** of selected window or monitor using Windows Graphics Capture API
- **Ring buffer** holds the last N seconds in memory, ready to save instantly
- **Instant clip save** captures the last 30 seconds to disk on demand
- **Clip library** with auto-detected duration and metadata
- **Modern dark UI** built with WinUI 3
- **Architecture designed for swappable components** via interface-based design

## Technology

- **.NET 10** with C# latest
- **WinUI 3** for the desktop UI (modern Microsoft stack)
- **Windows Graphics Capture API** for low-overhead screen capture
- **Media Foundation** via custom COM interop for H.264 encoding
- **Direct3D 11** for GPU-accelerated capture
- **Source-generated COM interop** (`GeneratedComInterface`) for AOT-friendly performance

## Architecture

Clean Architecture with clear separation:
Klipp.Core         — Domain models and interfaces (no dependencies)

Klipp.Capture      — Windows Graphics Capture implementation

Klipp.Encoding     — Media Foundation H.264 encoder + raw passthrough

Klipp.Storage      — Ring buffer with GOP-aligned eviction

Klipp.Desktop      — WinUI 3 application

Klipp.Tests        — Unit tests (xUnit + Shouldly)

Klipp.SmokeTest    — End-to-end pipeline verification

Every encoder, capture source, and clip writer implements an interface in `Klipp.Core` — components can be swapped without touching the rest of the app.

## Requirements

- Windows 10 build 19041 (May 2020 Update) or later
- DirectX 11.1-capable GPU
- Visual Studio 2026 with the Windows App SDK workload (for development)
- Media Feature Pack installed (required for H.264 encoders on some Windows N or OEM installations)

## Status

Work in progress. Current functionality:

- [x] Continuous WGC capture at 30/60 FPS
- [x] Thread-safe ring buffer
- [x] End-to-end pipeline: capture → encoder → buffer → file
- [x] WinUI 3 library UI with live recording status
- [x] Save last 30 seconds to disk
- [ ] H.264 encoding (in progress — phase 1 of 5 complete)
- [ ] MP4 muxing
- [ ] Built-in video player
- [ ] System tray integration
- [ ] Global hotkey for clip-save
- [ ] WASAPI loopback audio capture

## Build

```bash
git clone https://github.com/yourusername/Klipp.git
cd Klipp
dotnet restore
dotnet build
```

Run the smoke test (verifies the full pipeline works):

```bash
dotnet run --project tools/Klipp.SmokeTest
```

Run the WinUI app from Visual Studio: open `Klipp.sln`, set `Klipp.Desktop` as startup project, press F5.

## License

MIT
