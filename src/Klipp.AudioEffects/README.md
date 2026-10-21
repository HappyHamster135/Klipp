# Editor preview gain

`Klipp.AudioEffects.Gain` is an in-process `IBasicAudioEffect`/`IMediaExtension` for the editor's existing MediaPlayer voices. It multiplies float samples by the same combined master, clip, normalization, fade and ducking gain used by the editor. MediaPlayer's own output volume stays at unity, removing its `0..1` amplification limit.

The effect processes the input frame in place. Its sample loop does not allocate, lock, reopen a source, seek a decoder or access the UI. Slider changes update an atomic float through the controller. Source stream selection, buffering, timing and playback speed keep using the existing decoder pool.

Build `build.ps1` with `-Architecture x64` (default) or `ARM64`. Desktop builds copy the DLL and declare the runtime class in both unpackaged and packaged manifests. Unsupported systems or attachment failures retain ordinary capped playback and show the existing amplification warning.

The disposable `tools/Klipp.AudioPreviewHarness` creates its own tones and keeps native players muted. It measures samples from the real MediaPlayer effect path across live gain changes, independent voices, seeking and source replacement. Pass `--ffmpeg <path>` to compare amplified peaks with actual FFmpeg export processing.

References: [Microsoft custom audio effects](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/custom-audio-effects), [registration-free runtime components](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests#activatableclass).
