# Changelog

## 1.0.9 — 2026-10-22

- Add a compact, nonmodal update-ready card with **Restart to update** and **Later**, using the existing Klipp design and a short fade that respects Windows animation preferences. Showing the card does not move keyboard focus.
- Wait until capture, editing, video playback, exports, dialogs and file operations are finished and the library is active. Hide the card immediately when work starts or Klipp moves to the background or tray.
- Remember **Later** for the dismissed version across app restarts. Keep automatic checks enabled and the explicit restart action available in **Settings → Updates**; a newer version may show its own card.
- Keep capture and playback protected when restarting to update, finish saving work, and recheck activity before starting the native updater.
- Explain restart failures on the card. Continue discovering newer releases after choosing Later, and keep the prepared update available if a newer check or download fails.
- Keep code signing disabled.

## 1.0.8 — 2026-10-22

- Repair shortcut icon paths after installation and before the updated app opens, including Windows installations created through a redirected filesystem context.
- Keep Desktop, Start menu, Startup and pinned shortcuts tied to the physical installed files. Preserve shortcut names, arguments, hotkeys and window preferences; leave deleted and unrelated shortcuts alone.
- Check the installer, app executable, update launcher and packaged artwork before releasing. Add native Windows regression checks for shortcut repair, redirected paths, long Unicode paths and update replacement.
- Keep automatic updates available and code signing disabled.

## 1.0.7 — 2026-10-22

- Release the latest Windows x64 installer, automatic-update packages and portable ZIP with production signing disabled. Existing installed copies can update through Settings → Updates.
- Add smaller delta update packages while retaining the complete package as a fallback.
- Keep product and version metadata consistent across app libraries and native capture/audio components. Reject stale build output before packaging.
- Retain the optional SignPath workflow and signature verification tools for later setup; no signing service or development certificate is used for this release.

## 1.0.6 — 2026-10-21

- Add a per-user Windows installer with Start menu/Desktop shortcuts and an uninstaller. Application files live separately from existing recordings, projects and preferences.
- Add Settings → Updates, with automatic idle checks and downloads from stable GitHub releases, progress, pause/retry controls, and an explicit restart to install.
- Keep updates out of active capture, playback, editing, exports, recovery and file operations. Finish project and preference saves before starting the native updater.
- Verify downloaded packages before enabling restart; handle offline checks, rate limits, canceled transfers and failed downloads without interrupting capture. Existing ZIP users can use Get installer for the one-time migration.
- Generate the installer, full update package, feed and checksums together for GitHub publishing.

## 1.0.5 — 2026-10-21

- Keep the same live video presentation when switching between thumbnail playback and fullscreen. Playing videos continue on the same clock; paused videos keep their frame. Double-click, F and Escape preserve playback intent.
- Prevent thumbnail press highlights from washing out fullscreen video.
- Add GPU H.264 recording with automatic encoder detection and a CPU fallback.
- Capture game, microphone, Discord and music audio as separate named tracks, with independent gain, live meters and peak protection.
- Add recording recovery, automatic audio-device reconnection and low-disk protection.
- Expand the multitrack editor with trimming, splitting, group dragging, ripple editing, fades, transitions, effect regions, keyframes and export controls.
- Package Windows x64 releases with local .NET, Windows App SDK and VC++ runtimes. FFmpeg and optional caption tools download when needed.

Validation: 511 automated core tests; native capture/audio/recovery harnesses; repeated playing and paused fullscreen round trips; portable Release launch and playback checks.
