# Changelog

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
