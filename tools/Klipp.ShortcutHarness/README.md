# Native shortcut icon checks

Run on Windows:

```powershell
dotnet run --project tools/Klipp.ShortcutHarness/Klipp.ShortcutHarness.csproj -c Release
```

The harness links the production shortcut repair service and creates real Windows shell links in a new temporary directory. It never opens Klipp, installs software, or reads or changes user settings, media, Desktop shortcuts, Start menu entries or pinned items.

It checks actual shell metadata and icon pixels, redirected installation paths through an NTFS junction, custom shortcut settings, renamed pinned links, idempotence, corrupt and unrelated links, portable and incomplete installations, user-deleted links, Unicode paths, canonical file-path buffer growth beyond 512 characters, and replacing the current-version files during an update. Windows Shell can reject shortcut targets beyond its native `MAX_PATH` boundary; the harness reports that boundary and checks safe handling rather than claiming arbitrary-length `.lnk` support. Fixtures remain in the reported temporary directory for inspection.

These checks complement a real installer/update test. For release verification, inspect the Desktop and Start menu shortcut metadata after a fresh installation and after an actual upgrade, including a deliberately broken icon path before the upgrade.

Validate released artwork with Windows' native ICO/PE decoders:

```powershell
dotnet run --project tools/Klipp.ShortcutHarness/Klipp.ShortcutHarness.csproj -c Release -- --verify-release-icons --reference src/Klipp.Desktop/Assets/Klipp.ico --package <publish-directory> --installer <Klipp-Setup.exe> --full-package <full.nupkg>
```

This mode compares rendered icon pixels from the app, installer and packaged execution stub with the source ICO, checks all nine ICO sizes and verifies the distributed ICO bytes. It extracts only the three named icon-bearing files from the full package into its own temporary directory. It does not rely on `System.Drawing` or .NET Framework's ICO decoder.
