# Privacy policy

Klipp records the display, window and audio sources you select. Recordings, editor projects, waveforms, thumbnails, playback positions, settings and diagnostic logs are stored locally. Klipp has no built-in analytics or automatic upload of recordings, microphone audio, projects or diagnostic logs.

## Network access

- Installed copies check the public `HappyHamster135/Klipp` GitHub repository for updates while idle. Automatic checks and downloads can be disabled in **Settings → Updates**. Manual checks, opening release notes and downloading the installer also contact GitHub.
- Capture and editing can download FFmpeg/FFprobe from `gyan.dev` when those tools are first needed.
- Automatic captions download whisper.cpp from GitHub and its model from Hugging Face when requested. Transcription runs locally; Klipp does not send the recording to a transcription service.

Download providers receive the normal information sent with an HTTPS request, such as the IP address and requested file. Their services have their own privacy terms. These downloads do not include recordings, audio, project contents or local diagnostic logs.

If you choose a recording, export or project folder that your operating system synchronizes to a network or cloud service, that service may synchronize those files according to your configuration. Klipp writes to the folder you select.

## Storage and removal

Recordings default to `%USERPROFILE%\Videos\Klipp`. Settings, projects and caches use `%LOCALAPPDATA%\Klipp`. You can choose another recording folder in Settings. Uninstalling preserves these folders so your recordings and editable projects remain available; you may remove them yourself after saving anything you need.

## Signing service

The proposed SignPath workflow submits software build binaries and public build-origin information for release signing. It runs in developer CI and does not submit end-user recordings or local app data. SignPath approval and configuration are pending.

## Changes and questions

This policy describes the source in this repository. Material changes to network or data handling should update this page. Questions can be raised in the [Klipp repository](https://github.com/HappyHamster135/Klipp).
