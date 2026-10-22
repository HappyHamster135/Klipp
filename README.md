# Klipp

Game recording, instant clips and a built-in video editor for Windows. Built with .NET 10, WinUI 3, Windows Graphics Capture, Direct3D 11, WASAPI and FFmpeg.

## Download and run

Download [Klipp-Setup.exe](https://github.com/HappyHamster135/Klipp/releases/latest/download/Klipp-Setup.exe) and run it to install Klipp for your Windows account. Setup adds Start menu and desktop shortcuts and an entry in Windows **Installed apps**. It includes .NET, the Windows App Runtime and the VC++ runtime, and does not require administrator access or developer certificates. Releases are currently unsigned.

Klipp checks GitHub for updates and downloads them while it is idle. Open **Settings → Updates** to check manually, change **Automatic updates**, or choose **Restart to update** when an update is ready. Restarting waits until recording, instant capture and exports are stopped so work is not interrupted. The portable ZIP is still available, but ZIP installations need the installer once to enable in-app updates.

- Windows 10 version 2004 (build 19041) or newer; Windows 11 is recommended.
- DirectX 11-capable GPU. Hardware H.264 encoding depends on the GPU/driver; CPU encoding remains available.
- Game-only, Discord and music capture require Windows build 20348 or newer. Each track captures an application and its child processes, rather than individual browser tabs.
- FFmpeg/FFprobe download on first use. Automatic captions download whisper.cpp and a speech model when requested.

Recordings default to `%USERPROFILE%\Videos\Klipp`. Settings, projects and caches live in `%LOCALAPPDATA%\Klipp`. Installed application files use the separate `%LOCALAPPDATA%\HappyHamster135.Klipp` folder; updates and uninstalling Klipp preserve recordings and user data.

## Code signing policy

Public releases are currently unsigned. SignPath Foundation signing is being prepared and requires their approval. See the [code signing policy and setup](docs/SIGNING.md) and [privacy policy](docs/PRIVACY.md). A local development certificate is never used for public releases.

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

To build an installer, update packages and portable ZIP in a new directory under `artifacts/releases/`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Publish-KlippRelease.ps1 -Version 1.0.7
```

The script publishes untrimmed Release binaries with .NET and Windows App SDK dependencies, adds the local VC++ redistributable DLLs, then uses a pinned local Velopack tool to create `Klipp-Setup.exe`, a full update package and `releases.win.json`. It also writes a portable ZIP, `SHA256SUMS.txt` and a `release-artifacts.json` manifest listing all files to upload. It never uploads a release or includes user recordings, settings or certificates. See [release instructions](docs/RELEASING.md) for publishing updates and optional delta packages.

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
