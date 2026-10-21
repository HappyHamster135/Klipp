# Changelog

## 1.0.5 — 2026-10-21

- Keep the same live video presentation when switching between thumbnail playback and fullscreen. Playing videos continue on the same clock; paused videos keep their frame. Double-click, F and Escape preserve playback intent.
- Prevent thumbnail press highlights from washing out fullscreen video.
- Add GPU H.264 recording with automatic encoder detection and a CPU fallback.
- Capture game, microphone, Discord and music audio as separate named tracks, with independent gain, live meters and peak protection.
- Add recording recovery, automatic audio-device reconnection and low-disk protection.
- Expand the multitrack editor with trimming, splitting, group dragging, ripple editing, fades, transitions, effect regions, keyframes and export controls.
- Package Windows x64 releases with local .NET, Windows App SDK and VC++ runtimes. FFmpeg and optional caption tools download when needed.

Validation: 511 automated core tests; native capture/audio/recovery harnesses; repeated playing and paused fullscreen round trips; portable Release launch and playback checks.
