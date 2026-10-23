# Privacy policy

Klipp records the display, window and audio sources you choose. Recordings, editor projects, waveforms, thumbnails, playback positions, settings and diagnostic logs are stored locally. Recording, editing, exporting and the local Library work without an account. Klipp has no built-in analytics and does not automatically upload recordings, microphone audio, projects or diagnostic logs.

## Online services

The beta uses **Supabase Free** for accounts, profiles and community records, **Resend Free** for login emails, and the operator’s own server at `media.shlipp.com` for newly shared videos and thumbnails. Two older beta clips remain in private Supabase Storage. GitHub serves the source, Windows downloads, update metadata and the mobile web app’s public files through GitHub Pages. No paid transcoding service is used.

These services receive normal connection information, including IP addresses. Their own policies also apply: [Supabase](https://supabase.com/privacy), [Resend](https://resend.com/legal/privacy-policy) and [GitHub](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement). Login emails use **Klipp / login@accounts.shlipp.com**. Receiving is disabled on this sending domain; it is not a support inbox.

## When Klipp connects

- Installed copies check GitHub for updates while idle. Automatic checks/downloads can be disabled in **Settings → Updates**. Manual update checks, release notes and installer downloads also contact GitHub.
- Capture and editing can download FFmpeg/FFprobe from `gyan.dev` when first needed. Requested automatic captions download whisper.cpp from GitHub and its model from Hugging Face; transcription then runs locally.
- Opening **Media** can contact Supabase to restore sign-in and load profiles, feed records, comments and authorized thumbnails. Selecting a clip streams its video from the home media server, or from private Supabase Storage for an older clip. Feed cards do not automatically play every video.
- Requesting/verifying an email code sends the email address and verification request to Supabase Auth. Resend delivers the code. Saving a profile sends the chosen username, display name and biography. Likes, comments, reports, blocking, support requests and moderation send the corresponding identifiers and submitted text or choices.
- Choosing **Upload to Media** in the installed Windows app prepares a share copy on that PC, then uploads the reviewed video, thumbnail, title and description. Only the chosen section and selected audio are included. Capturing, editing or adding a file to the Library never shares it automatically. Mobile/browser clients have no upload screen or file picker and do not read a phone’s photo library.

Downloads do not include recordings, projects or diagnostic logs. If a chosen recording/project folder is synchronized by another operating-system or cloud service, that service may synchronize the local files according to your settings.

## Accounts and sessions

Supabase Auth stores the account email and authentication information. A separate profile stores the account identifier, optional unique username, display name, biography and creation/update times. Profiles are visible to signed-in members; emails and authentication tokens are not member-visible profile fields. A verified permanent account is required for Media, and a username is required to share or interact with clips.

Windows sessions use Windows-protected storage scoped to the configured service. The mobile web app uses browser `sessionStorage` by default; **Remember me on this device** explicitly selects persistent `localStorage`. A small remember-device preference is stored locally. Browser storage can survive tab restoration or be discarded by browser settings. Use **Sign out** on shared devices.

Sign-out clears the local identity and attempts to remove the saved session before remote revocation; a storage-removal failure is reported for retry. Signing out, clearing browser data or uninstalling does not delete the online account or local recordings.

## Shared clips and access

Publishing makes the video, thumbnail, title, description, author profile, duration, publication time and interaction counts available to eligible signed-in members. Likes and comments include their authors and times. Reports/reasons and moderation notes are operator-visible, rather than public feed content. Block lists are private to the member who created them; **Blocked users** can reverse a block.

Files have no public directory listing. Authorized playback links expire after five minutes for video and two minutes for thumbnails. Treat them as private: someone who receives a valid link may use it before expiry. The home server rechecks whether a clip is still published and permitted on each new file request, including after moderation or a pending account deletion. Existing open streams, downloaded bytes and browser buffers cannot be recalled. Previously issued links to older Supabase clips can remain usable until their short expiry. Signing out stops local playback but does not invalidate every existing link.

**Share clip** shares an app link containing the clip identifier, not a session token or temporary playback link. The receiving app handles that link under its own policy. Recipients still need to sign in and be allowed to view the clip.

The Windows app creates a share copy at up to 720p / 30 fps using H.264 and optional AAC, mixing only the selected audio tracks. A mixed track may already contain voices. Temporary share files use `%TEMP%\Klipp\CommunityShare` and are removed on completion/cancellation when possible. Originals remain unchanged. The server checks ownership, quotas, actual file size, media/container/codec information and duration with FFprobe. It does not transcode uploads. Preparation/playback use the device’s processing, memory, battery and network connection.

## Help, moderation and deletion

Account tools appear when the deployed service confirms support. **Help & requests** lets a signed-in member send a private help, privacy or deletion request and read the operator’s reply. Do not include email codes, passwords, session tokens or unrelated sensitive material. Requests and replies are visible to their author and authorized operators. Public GitHub issues remain available for non-personal bugs; they are not a private support channel.

Operators can review reports, hide or keep a clip, and reply to support requests. Operator access requires a private server-side allowlist and a live authenticated session. A report does not automatically remove content. Moderation decisions are audited; support audits retain an action summary rather than duplicating the reply text.

**Delete shared clip** hides the clip before physical removal and removes its associated interactions when cleanup completes. Failed removal remains queued for retry; reserved capacity is released only after file removal is confirmed. The original local recording is not deleted. Members can remove their own comments.

When enabled, **Delete account** requires a live session and typing the current username exactly, or `DELETE` if no username was set. It freezes new account writes and hides owned clips, removes shared files, then permanently removes the Auth account/profile and associated application data. A temporary cleanup error leaves a retryable pending request rather than falsely reporting completion. A historical orphaned Storage file can require operator review. Deletion does not remove recordings/projects from your PC. If account tools are unavailable, use the private in-app help route when offered; do not post a deletion request containing private account information in a public issue.

## Retention and recovery

| Data | Retention/removal |
| --- | --- |
| Account/profile | Until completed account deletion; no automatic inactivity expiry |
| Shared clip | Until owner/operator removal completes; unfinished upload reservations expire after 30 minutes and queued cleanup removes files before releasing capacity |
| Likes/comments | Until unlike/comment removal, or associated clip/account deletion |
| Reports | Retained for operator review until clip/reporting-account deletion or operator removal; no automatic age-based report purge |
| Blocks | Until unblocking or deletion of either account |
| Private support requests/replies | Until account deletion or operator removal; no automatic inactivity purge |
| Moderation/action audit | Entries older than 90 days are pruned during later successful moderation/support actions; inactive records can remain longer. Capacity is bounded at 5,000 entries; account deletion removes its direct identifier from audit references |
| Comment-frequency history | Identifiers/event times only; entries older than an hour are pruned during later successful comment activity, so inactive records can remain longer |

The home server keeps encrypted recovery snapshots on a separate physical SSD: media, application/configuration, legacy clip copies and Supabase account/community exports. Exports include sensitive Auth data and private requests/reports. Database staging and isolated restore checks use temporary memory storage with swap disabled for those jobs; encrypted repositories and keys are root-only. Live uploaded clips on the main server filesystem are **not encrypted at rest**; operating-system permissions and application access checks protect them.

Automatic retention keeps seven daily, four weekly and three monthly snapshots. Deleted data can therefore remain in retained encrypted recovery snapshots until their retention expires. A separately chosen offline copy can retain older history until replaced or destroyed. Recovery must reconcile deletion requests and revoke restored historical Auth sessions before reopening restored data. Backups do not guarantee uninterrupted availability or protect against loss of the whole server until a separate off-site copy is verified. No verified off-site copy is currently claimed.

Provider logs/email delivery records have separate provider-defined retention. Short dashboard log-history windows do not mean account records or every internal provider copy are deleted on that schedule. Klipp does not publish account data in operations checks: the public availability checker requests only anonymous health responses and denial of a synthetic unsigned clip URL. Workflow logs contain service results, not account credentials or real media identifiers. Private server alerts go only to the configured operator.

## Local storage and browser caching

Recordings default to `%USERPROFILE%\Videos\Klipp`; settings, projects and caches use `%LOCALAPPDATA%\Klipp`. You can choose another recording folder. Uninstalling preserves these folders; remove them yourself after saving anything needed.

The mobile service worker caches bundled icons and its installation manifest. It does not add account responses, email codes, signed playback links or videos to its cache and provides no offline media library. Browsers/operating systems may retain temporary playback buffers. Clearing browser site data removes local session/cache data, not the online account or shared clips.

## Signing, changes and contact

Release signing is currently disabled. A future SignPath build-signing workflow would submit software build binaries and build-origin information, not user recordings.

This policy describes the beta’s implemented data handling; availability of account tools is controlled by verified deployment capabilities. The [setup guide](COMMUNITY_SETUP.md) and [operations contract](../backend/supabase/OPERATIONS.md) document the service design. No public support email inbox has been configured. Use the private in-app help tools when available, and [GitHub issues](https://github.com/HappyHamster135/Klipp/issues) only for non-personal questions. Never post email codes, tokens, playback links or private media there.
