# Community Media Windows checks

This harness generates a short test pattern with separate synthetic game/microphone tones. It never selects or uploads a real Library recording. Run it on Windows with the .NET 10 SDK and Klipp's existing FFmpeg/FFprobe installation; it fails rather than downloading missing tools.

## Local checks

From the repository root:

```powershell
dotnet run --project tools/Klipp.CommunityMediaHarness -- --local
```

The default mode is also `--local`. It generates `artifacts/community-media-qa/generated-game-and-microphone.mp4`, prepares share copies with one audio track, both tracks and no audio, then checks codec, dimensions, selected range, size limits, temporary-copy removal, cancellation and the generated source's unchanged hash. It makes no hosted requests, reads no account session and does not start capture. A successful run ends with `LOCAL_COMPLETE`.

Preparation uses the actual desktop service: local H.264 at up to 720p / 30 fps, optional AAC audio and a JPEG thumbnail, limited to 60 seconds, 16 MiB of video and 256 KiB of thumbnail. It does not test a cloud transcoder; none is configured.

## Opt-in live checks

Sign in through the app first and complete the profile. The configured backend must already have both migrations and the private `community-videos` / `community-posters` buckets with their required limits. Use the same Windows account and `backend/community.json` configuration as the app so its project-scoped DPAPI session can be restored.

```powershell
dotnet run --project tools/Klipp.CommunityMediaHarness -- --live --config backend/community.json --leave-test-clip
```

This is an explicit upload to the configured service. It generates its own fixture, prepares a two-second share copy with the synthetic game-audio track, uploads and publishes it, reads the feed and private video/poster, checks anonymous feed denial, sets/unsets a like, and creates/removes its own test comment. It uses the actual Core account/media service and the existing protected session. It does not request login emails or pass credentials through command-line arguments.

The fixture appears to signed-in members as **Klipp Media verification**, with description **Generated test video. Safe to remove.** It consumes one of the account's five reserved slots and one of the beta's forty global slots. `--leave-test-clip` keeps it for a manual UI check. Without that option, the harness attempts to delete its uploaded clip when the run finishes, including after a later check fails.

Successful live checks end with `LIVE_COMPLETE` and safe boolean results. Output contains check labels and safe error codes/types, never session tokens, signed URLs or recipient addresses. The only persisted remote identifier is the generated clip ID in `artifacts/community-media-qa/test-clip-id.txt`.

## Remove the generated fixture

After inspecting a retained test clip:

```powershell
dotnet run --project tools/Klipp.CommunityMediaHarness -- --cleanup --config backend/community.json
```

Cleanup reads that saved clip ID, looks it up in the current account's **My uploads**, verifies the generated title/description and calls the normal hide → Storage file removal → reservation-release flow. It verifies the clip is absent afterward and ends with `CLEANUP_COMPLETE`. It does not search for or delete personal recordings. The local generated source and ID file remain under `artifacts/community-media-qa`.

If network cleanup fails, retry the cleanup command or use **Delete shared clip** in **My uploads**. Hidden/unfinished reservations continue to count against the quota until their files have been removed. Do not substitute a real recording or another member's clip ID for this generated-fixture check.

See [community setup](../../docs/COMMUNITY_SETUP.md) for deployment, security, quotas and separate disposable local SQL checks. Live Media verification covers the upload/storage/interaction path, not a new installer release or every multi-user moderation scenario.
