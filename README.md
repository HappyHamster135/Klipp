# Klipp

Game recording, instant clips and a built-in video editor for Windows. Built with .NET 10, WinUI 3, Windows Graphics Capture, Direct3D 11, WASAPI and FFmpeg.

## Download and run

Get the Windows x64 ZIP from [GitHub Releases](https://github.com/HappyHamster135/Klipp/releases), extract the entire archive, then run `Klipp.Desktop.exe`. Keep its DLLs and folders together. The portable release includes .NET, the Windows App Runtime and the VC++ runtime; no developer certificate installation is needed. Releases are currently unsigned.

- Windows 10 version 2004 (build 19041) or newer; Windows 11 is recommended.
- DirectX 11-capable GPU. Hardware H.264 encoding depends on the GPU/driver; CPU encoding remains available.
- Game-only, Discord and music capture require Windows build 20348 or newer. Each track captures an application and its child processes, rather than individual browser tabs.
- FFmpeg/FFprobe download on first use. Automatic captions download whisper.cpp and a speech model when requested.

Recordings default to `%USERPROFILE%\Videos\Klipp`. Settings and caches live in `%LOCALAPPDATA%\Klipp`; extracting an updated release does not replace those folders.

## Capture and edit

- Record a display or window, or keep an instant-clip buffer ready to save. Automatic game detection, global shortcuts, tray controls and capture notifications are included.
- Automatic GPU encoder selection with a CPU fallback, quality profiles, recording recovery and low-disk protection.
- Separate game, microphone, Discord and music tracks, device reconnection, volume controls, live meters and peak protection. Choose **Settings → Audio → Game only** to configure application audio.
- Play clips directly in their thumbnails; double-click or press **F** for fullscreen and **Escape** to return, using the same playback session.
- Multitrack video/audio timeline with trimming, splitting, group dragging, snapping, ripple editing, audio fades and volume automation.
- Effects, filters, editable effect regions, keyframes, transitions with previews, text and captions. Projects, backups and an export queue preserve editable work.

## Build from source

Install the .NET 10 SDK and Visual Studio with the Windows App SDK/.NET desktop workload, a Windows SDK and the x64 C++ build tools. The C++ tools build the native capture hook and audio gain effect. The development app uses Windows App Runtime 1.6 x64.

```powershell
git clone https://github.com/HappyHamster135/Klipp.git
cd Klipp
dotnet restore
dotnet test tests/Klipp.Tests/Klipp.Tests.csproj
```

Open `Klipp.sln` in Visual Studio, select **x64**, set `Klipp.Desktop` as the startup project and run it. Native harnesses under `tools/` cover capture, audio, recovery, preview and export paths.

To build a portable release in a new directory under `artifacts/releases/`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Publish-KlippRelease.ps1 -Version 1.0.5
```

The script publishes untrimmed Release binaries with .NET and Windows App SDK dependencies, adds the local VC++ redistributable DLLs, then writes a ZIP and `SHA256SUMS.txt`. It never uploads a release or includes user recordings, settings or certificates.

## Project layout

| Project | Purpose |
| --- | --- |
| `Klipp.Core` | Editing, capture and playback models and rules |
| `Klipp.Capture` | Windows Graphics Capture and Direct3D interop |
| `Klipp.Encoding` | FFmpeg video encoding, WASAPI audio and MP4 muxing |
| `Klipp.Storage` | Capture buffer storage |
| `Klipp.Desktop` | WinUI application, library, player and editor |
| `Klipp.AudioEffects` | Native playback gain processing |
| `Klipp.GameHook` | Optional DirectX game overlay/capture support |
| `Klipp.Tests` | Core regression tests |

## License

Klipp is [MIT licensed](LICENSE). MinHook has its own [license](src/Klipp.GameHook/third_party/minhook/LICENSE.txt). Microsoft runtimes and NuGet dependencies retain their respective licenses. FFmpeg and whisper.cpp are separate tools downloaded when needed.
