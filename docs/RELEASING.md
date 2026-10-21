# Windows releases and updates

Klipp uses Velopack 1.2.161 for its per-user Windows installer and update packages. Keep the NuGet package and the pinned `vpk` tool version aligned when upgrading Velopack.

The package ID is **HappyHamster135.Klipp**, channel **win**, with `Klipp.Desktop.exe` as its entry point. Keep these identifiers stable across releases. In particular, do not shorten the package ID to `Klipp`: the existing `%LOCALAPPDATA%\Klipp` directory contains user settings and projects and must not become an installer-owned directory.

## Build

Commit the release source, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Publish-KlippRelease.ps1 -Version 1.0.6
```

The script creates a fresh directory under `artifacts/releases/`. It publishes the self-contained, untrimmed application, includes native runtimes and license notices, builds the installer and update feed, and calculates SHA-256 checksums. `vpk` installs locally under `artifacts/tools/vpk/1.2.161`; no global tool installation is needed.

Use `-PortableOnly` when only a developer ZIP is needed. `tools/Pack-KlippInstaller.ps1` can package an existing self-contained publish separately, as long as its `release-manifest.json` version matches `-Version`.

## Publish to GitHub

Create the version tag and a draft release in `HappyHamster135/Klipp`. Upload every file listed in the build's `release-artifacts.json`:

- `Klipp-Setup.exe` is the one-time installer users download.
- `HappyHamster135.Klipp-<version>-full.nupkg` is the complete update package.
- `releases.win.json` tells installed apps which package version to download.
- `assets.win.json` and `RELEASES` are generated packaging metadata.
- The portable ZIP and `SHA256SUMS.txt` remain available for manual installation and verification.

Keep the generated package filenames and feed together in the same GitHub release. The installer filename is intentionally stable so the app's **Get installer** link continues to work. Verify the uploaded files and publish the draft only when all assets are present. Installed apps check stable releases; a draft release is not offered as an update.

The updater uses the public GitHub repository without embedding credentials. It verifies downloaded package hashes using the generated feed. Replacing only the Setup executable does not publish an application update; the corresponding `.nupkg` and feed must also be uploaded.

## Delta packages

After the first installer release, a previous **published** full package can be provided to generate a smaller delta update:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Publish-KlippRelease.ps1 `
  -Version 1.0.7 -PreviousReleaseDirectory C:\Builds\Klipp-1.0.6\installer
```

The previous directory should contain the previous full `.nupkg` and its `releases.win.json`. The script copies these into the new staging directory and asks Velopack to build the delta. Upload the newly generated delta alongside the new full package and feed. Full packages remain available as the fallback for clients that cannot use a delta. Never seed a public release from private QA fixture packages.

## Validate before publishing

Check a fresh install, Start menu launch, Windows Installed apps entry, and uninstall using an isolated test installation. Confirm that `%LOCALAPPDATA%\Klipp` and the user's recordings are unchanged. For update testing, install an older private fixture and point a test updater at a local feed containing the new real package; do not create dummy public releases.

Test both idle update/restart and blocked restart while recording or exporting. Also test an offline check and a corrupted download. A rejected or unavailable update should leave the installed version usable.

Application binaries and Setup are currently unsigned. No developer certificate is included or installed. Production code signing can be added later without changing the package ID or update channel.
