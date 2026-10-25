# Windows releases and updates

Klipp uses Velopack 1.2.161 for its per-user Windows installer and update packages. Keep the NuGet package and the pinned `vpk` tool version aligned when upgrading Velopack.

The package ID is **HappyHamster135.Klipp**, channel **win**, with `Klipp.Desktop.exe` as its entry point. Keep these identifiers stable across releases. In particular, do not shorten the package ID to `Klipp`: the existing `%LOCALAPPDATA%\Klipp` directory contains user settings and projects and must not become an installer-owned directory.

## Build

Commit the release source, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Publish-KlippRelease.ps1 -Version 1.0.10
```

The script creates a fresh directory under `artifacts/releases/`. It publishes the self-contained, untrimmed application, includes native runtimes and license notices, builds the installer and update feed, and calculates SHA-256 checksums. `vpk` installs locally under `artifacts/tools/vpk/1.2.161`; no global tool installation is needed.

Use `-PortableOnly` when only a developer ZIP is needed. `tools/Pack-KlippInstaller.ps1` can package an existing self-contained publish separately, as long as its `release-manifest.json` version matches `-Version`.

The Windows candidate workflow generates ignored `backend/community.json` from the same public repository variables as mobile: required `KLIPP_SUPABASE_URL`, `KLIPP_SUPABASE_PUBLISHABLE_KEY` and `KLIPP_MEDIA_API_URL`. Only a Supabase HTTPS project URL and `sb_publishable_` key are accepted; the media value must be a reviewed HTTPS origin. Do not put a secret/service-role key, SMTP credential or session token in these variables. Official CI candidates fail if account configuration or the home media origin is missing or incomplete, so an update cannot silently remove Media. The helper still supports deliberate capture-only local builds when all values are absent and `-RequireOnlineConfiguration` is omitted. Recording and editing remain available without signing in in either build.

The project copies that file beside the application for both publishing and Velopack updates. After packaging, the workflow verifies that `lib/app/community.json` in the full update package exactly matches the prepared configuration. The verification helper also checks absence for intentional capture-only local builds. This prevents a missing, misplaced or stale publisher configuration from being offered to users after an update. Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-KlippCommunityConfigurationChecks.ps1` for isolated configuration and payload validation checks. For a local release, prepare the reviewed ignored `backend/community.json` before running the publish script; editing an installed `current/community.json` alone will not survive a future update.

Installer packaging extracts only the exact `## <version>` section from `CHANGELOG.md` into the build's `ReleaseNotes.md`. Velopack embeds those Markdown notes in the update feed and full package, and packaging verifies both copies. Use the first three bullets for short user-facing highlights; the app shows these without rendering remote HTML. The popup's **Full release notes** link opens that version's GitHub release rather than whichever release is currently latest.

Custom versions without a matching changelog section have no inline notes; the app offers the full-notes link with a neutral fallback. To supply notes for such a build, pass `-ReleaseNotesPath C:\Builds\CustomNotes.md`. Empty or ambiguous notes fail packaging rather than borrowing another version's changes. `ReleaseNotes.md` is a local build artifact and can be reused as the GitHub release body; it does not add an extra required upload asset. Run `powershell -NoProfile -File tools/Test-KlippReleaseNotes.ps1` for isolated extraction checks.

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
  -Version 1.0.10 -PreviousReleaseDirectory C:\Builds\Klipp-1.0.9\installer
```

The previous directory should contain the previous full `.nupkg` and its `releases.win.json`. The script copies these into the new staging directory and asks Velopack to build the delta. Upload the newly generated delta alongside the new full package and feed. Full packages remain available as the fallback for clients that cannot use a delta. Never seed a public release from private QA fixture packages.

## Validate before publishing

`Pack-KlippInstaller.ps1` runs `Test-KlippReleaseIcons.ps1` automatically. It checks the shipped ICO sizes and uses native Windows icon extraction/rendering to compare the app, installer and update launcher with the source artwork. Missing or incorrect icons stop packaging.

Run `dotnet run --project tools/Klipp.ShortcutHarness/Klipp.ShortcutHarness.csproj -c Release` for disposable native shortcut checks. These exercise real Shell Links, redirected paths, long Unicode paths, shortcut customization, deleted/foreign links and update replacement without changing the user's shortcuts.

The installer fast callback repairs existing shortcuts after creation. Normal startup repairs them again before opening the main window, because Velopack rewrites shortcuts after its update callback. Canonical paths come from existing file handles rather than assumptions about AppData. The repair refreshes only changed shell entries; it does not recreate deleted shortcuts or restart Explorer.

Check a fresh install, Start menu launch, Windows Installed apps entry, and uninstall using an isolated test installation. Confirm that `%LOCALAPPDATA%\Klipp` and the user's recordings are unchanged. For update testing, install an older private fixture and point a test updater at a local feed containing the new real package; do not create dummy public releases.

Test the update-ready card in the active library, with unchanged keyboard focus and working **Restart to update** and **Later** actions. It should wait during recording, instant capture, editing, playback, exports, dialogs and background/tray use, and disappear immediately when work starts. Confirm that **Later** stays dismissed for that version after reopening, while **Settings → Updates** still offers restart and automatic checks remain enabled. A newer version should be eligible for its own card.

Confirm that **What's new** shows at most three readable highlights from the prepared version, retains them when restoring a downloaded package offline, and clears old highlights when the version changes. **Full release notes** must target that exact version. Missing notes should show the neutral fallback; long or formatted notes should remain plain text and keep both update actions accessible.

Test both idle update/restart and blocked restart while recording, editing, playing video or exporting. Restart must finish saving preferences and projects, then recheck activity before handing off to the native updater. Also test an offline check and a corrupted download. A rejected or unavailable update should leave the installed version usable.

Application binaries and Setup are currently unsigned. No developer certificate is included or installed. The [SignPath setup and code signing policy](SIGNING.md) describe the pending provider approval and the manual signing workflow.

For an approved provider, `Publish-KlippRelease.ps1` and `Pack-KlippInstaller.ps1` accept `-SignTemplate '<signer command> {{file...}}' -ExpectedPublisher '<exact certificate subject>' -RequireSigning`. Keep authentication in the CI secret environment. Never put tokens or passwords in the template. `RequireSigning` fails before building if no signer is configured; invalid, unexpected, missing or untimestamped signatures fail the release. Signing errors never fall back to unsigned output.

Signed builds use Velopack's portable ZIP, with `Klipp.exe`, `Update.exe` and `current/` at its root. Run `Klipp.exe` after extracting. The ordinary unsigned developer ZIP keeps its existing layout. Velopack signs before creating the portable archive, full update package and bundled Setup, so the feed and checksums describe the signed bytes. Do not sign package contents after packaging or replace the published 1.0.6 files in place.

Verify a complete signed release independently before upload:

```powershell
powershell -NoProfile -File tools/Test-KlippReleaseSignatures.ps1 `
  -ReleaseManifestPath '<build>/release-artifacts.json' `
  -ExpectedPublisher '<exact approved certificate subject>'
```

This checks asset hashes, update-feed hashes, required app/launcher/updater files in both archives, Windows Authenticode trust, the publisher, code-signing usage and RFC 3161 timestamps. The manifest's `signing.verified` field is set only after verification succeeds. A trusted signature identifies the publisher; SmartScreen reputation can still take time to build, as explained by [Microsoft](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).
