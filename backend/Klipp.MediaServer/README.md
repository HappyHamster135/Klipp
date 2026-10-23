# Klipp private media host

This small .NET 10 service stores prepared clips on the Linux media volume. Accounts,
profiles, feed metadata and interactions stay in Supabase Free. It uses a publishable
Supabase key and the caller's access token, never a service-role key. A separate scoped
gateway secret can only attest validated home files and confirmed cleanup through the
checked database RPCs.

Run behind an HTTPS reverse proxy; the application binds only `127.0.0.1:5080`.
There is no directory listing, public storage directory or static-file middleware.

## Configuration

Environment variables use .NET's `Media__` prefix:

| Variable | Purpose / default |
| --- | --- |
| `Media__SupabaseProjectUrl` | Exact HTTPS Supabase origin |
| `Media__SupabasePublishableKey` | `sb_publishable_…`, not a secret key |
| `Media__PublicBaseUrl` | Exact HTTPS media origin, e.g. `https://media.shlipp.com` |
| `Media__StorageRoot` | `/srv/klipp/media/data` |
| `Media__MountPoint` | `/srv/klipp/media` |
| `Media__ExpectedMountSource` | `/dev/mapper/ubuntu--vg-klipp--media` |
| `Media__SigningKeyBase64` | Separate, random 32-byte HMAC key, encoded as base64 |
| `Media__GatewaySecret` | Random scoped gateway secret; database stores its SHA256 digest |
| `Media__FfprobePath` | Installed `/usr/bin/ffprobe` |
| `Media__UploadsEnabled` | `false` until TLS, backups and a restore drill pass |
| `Media__BackupStatusPath` | Root-managed `/var/lib/klipp-backup/status.json` |
| `Media__MinimumFreeBytes` | 1 GiB reserve; at least 256 MiB |
| `Media__AllowedOrigins__0` | Exact mobile web origin; default `https://happyhamster135.github.io` |
| `Media__MaximumVideoStreams` | Eight active video responses globally |
| `Media__MaximumVideoStreamsPerClient` | Four active video responses per client IP |
| `Media__MaximumPlaybackStreams` | 64 active file responses, including posters and HEAD |
| `Media__MaximumPlaybackStreamsPerClient` | 16 active file responses per client IP |
| `Media__PlaybackBytesPerSecond` | Shared 20 MiB/s budget for videos and thumbnails |
| `Media__PlaybackBurstBytes` | Shared 4 MiB startup burst, including range requests |
| `Media__PlaybackStreamTimeoutSeconds` | 180-second response lifetime; configurable from 30 to 180 |

Keep configuration root-owned and readable only by the service group. Public clients
receive only `mediaApiUrl`, the media origin; they never receive either private secret.
The signing and gateway secrets must be different.

The backup receipt must contain UTC `successful_at` and `database_successful_at`
(at most 48 hours old), plus `verified_restore_at` and `database_verified_restore_at`
from an actual media/database restore drill (at most eight days old). Missing, stale
or malformed receipts pause new uploads/publication. Playback and owner cleanup remain
available. The service cannot write this root-owned receipt or read the encrypted
backup repository. Database home upload activation is a separate, initially disabled
switch.

## API

All `/v1/clips` routes require a current Supabase access token as `Authorization:
Bearer …`. The service checks `/auth/v1/user` and checked database RPCs; it does not
trust user-editable JWT metadata or a client-supplied owner ID.

- Reserve in Supabase with `community_begin_home_upload` (same arguments as the legacy
  reservation RPC). Both providers share the existing five-per-member / forty-total
  reservation limits. New legacy cloud uploads are retired. Unfinished and deleting
  reservations still count. Pending reservations expire after thirty minutes, with
  separate pending and creation-rate limits enforced by the database.
- `PUT /v1/clips/{id}/video`: raw `video/mp4`, exact `Content-Length`, at most 16 MiB.
- `PUT /v1/clips/{id}/poster`: raw `image/jpeg`, exact `Content-Length`, at most 256 KiB.
- `POST /v1/clips/{id}/publish`: validates both immutable files, computes SHA256,
  writes recovery metadata and calls `community_home_publish`. Returns the standard
  clip object with `storage_provider: "home"`.
- `POST /v1/clips/{id}/playback`: rechecks current feed visibility and returns
  `video_url`, `poster_url`, and `expires_at` (the earlier expiry).
- `DELETE /v1/clips/{id}`: owner-only hide, local file cleanup, then scoped
  `community_home_finish_delete`; returns `{id, deleted: true}`. Retries are safe.
- Signed `GET` / `HEAD /v1/assets/{owner}/{id}/{video|poster}`: private, short-lived
  capabilities with normal byte-range seeking. Video links last five minutes,
  thumbnails two minutes. Each new asset request checks the scoped
  `community_home_asset_allowed` RPC; hidden clips and pending account deletions
  cannot open a previously issued link. An unavailable access check fails closed.

Uploads and publication reject `Origin` and browser Fetch Metadata headers, and
ordinary browser CORS does not allow PUT. Phones retain watching, likes, comments
and deletion only. This is a product/browser boundary, **not hardware attestation**:
an arbitrary non-browser client can omit those headers. Authentication, ownership,
format validation, quotas and bounds still apply to every upload.

Already-issued playback URLs are bearer capabilities, as with Supabase Storage's
signed URLs. A block/sign-out prevents obtaining new links; an existing video URL
can remain usable for up to five minutes. Tokens are not access tokens and cannot
call account or database APIs. Moderation and deletion revoke new home requests;
already downloaded bytes and open streams cannot be recalled. Do not log query
strings at the reverse proxy.

Mutation work has two slots per account and four globally. Duplicate edits acquire
the clip lock before a global work slot; each wait is bounded and cancellation
returns every permit. Playback preserves normal seeking and HEAD responses while
sharing one byte budget across all URLs and ranges. All file responses have global
and per-IP stream caps, with tighter limits for videos. A three-minute absolute response deadline, request cancellation
or service shutdown closes the stream and frees its slot, including idle readers.

A non-overlapping background sweep runs every five minutes. It claims up to ten
expired uploads using `community_home_claim_expired_uploads`, removes their files
under the normal clip/backup locks, then calls `community_home_finish_expired_upload`.
Both maintenance RPCs accept only the scoped gateway proof and publishable key; they
do not need a user session or privileged Supabase key. Failed cleanup retains the
database tombstone and quota for a later retry. The SQL hardening migration and new
backup receipt fields must be installed before deploying this version.

## Disk and backup contract

The Linux guard requires the exact ext4 mount and `rw,nodev,nosuid,noexec`; a missing
mount fails closed. It never falls back to storing files on the root filesystem.
Symlinks in media paths are rejected. Keep `data/` root-owned, `data/clips/` writable
only by the service, and precreate root-owned group-writable `data/.mutation.lock`.

Files live at `data/clips/{owner}/{id}/video.mp4`, `poster.jpg`, and `manifest.json`.
The manifest holds owner/clip identifiers, standard clip metadata, actual byte sizes,
measured duration and both hashes. It contains no credentials. Writes use bounded
streams and same-directory atomic renames; published files cannot be replaced.

The API takes a shared Linux `flock` on `data/.mutation.lock` around final renames,
publication and deletion. Backup export takes an exclusive lock, excludes all
`*.partial` and the lock, and refuses snapshots with read errors. Partial upload
bytes can continue arriving during backup, but finalization waits. A process killed
during upload may leave a bounded `.partial` file; it is never played or backed up.
Stop the service for restores. The service unit must also bind its lifetime to the
media mount and keep ffprobe's CPU/memory/process permissions constrained.

Only small prepared H264/AAC fast-start MP4s of up to 60 seconds and 1280×720, and
JPEG thumbnails, are accepted. ffprobe uses only the local file protocol, disables
MOV external track references and absolute aliases, limits individual allocations,
and has a 15-second deadline. Encoding runs on the user's PC, not the server.

## Verification

The dependency-free console harness starts real Kestrel, generates tiny local test
media, and checks authentication/ownership, PC upload restrictions, fail-closed
storage/backup gates, format validation, immutable publishing and hashes, signed-link
tampering/expiry, actual GET byte ranges, HEAD, 416 responses and deletion.

```sh
dotnet run --project backend/Klipp.MediaServer.Tests -c Release -- /usr/bin/ffprobe /usr/bin/ffmpeg
dotnet publish backend/Klipp.MediaServer -c Release -r linux-x64 --self-contained false
```

The harness uses temporary generated files and an in-memory gateway. It does not
upload to Supabase, alter accounts, contact the public host or publish user content.
