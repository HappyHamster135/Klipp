# Privacy policy

Klipp records the display, window and audio sources you select. Recordings, editor projects, waveforms, thumbnails, playback positions, settings and diagnostic logs are stored locally. Klipp has no built-in analytics or automatic upload of recordings, microphone audio, projects or diagnostic logs. An account is optional: recording, editing, exporting and the local Library work without signing in.

## Current service providers

The current beta uses **Supabase Free** for accounts, profiles, Media metadata and private video/poster storage, and **Resend Free** for login emails. The verified sender is **Klipp** / `login@accounts.shlipp.com`; receiving is disabled for that sending domain, so it is not a support inbox. No paid transcoding service is used. The [deployment notes](COMMUNITY_SETUP.md) describe the configured services.

GitHub serves the repository, Windows release downloads and update metadata. The mobile publication workflow also targets GitHub Pages for public app files; private account/API requests and shared clips use Supabase. These providers receive normal request information, such as IP addresses, and apply their own [Supabase](https://supabase.com/privacy), [Resend](https://resend.com/legal/privacy-policy) and [GitHub](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement) privacy policies.

## Network access

- Installed copies check the public `HappyHamster135/Klipp` GitHub repository for updates while idle. Automatic checks and downloads can be disabled in **Settings → Updates**. Manual checks, opening release notes and downloading the installer also contact GitHub.
- Capture and editing can download FFmpeg/FFprobe from `gyan.dev` when those tools are first needed.
- Automatic captions download whisper.cpp from GitHub and its model from Hugging Face when requested. Transcription runs locally; Klipp does not send the recording to a transcription service.
- If the publisher has configured the optional online service, opening **Media** in Windows or the mobile web app can contact Supabase to restore an existing sign-in and load profiles, clip metadata, comments and visible thumbnails. A selected video streams from private Storage through a temporary access link. Feed cards do not autoplay; choosing a clip starts inline playback when the browser permits it.
- Requesting or verifying a sign-in code sends the email address and verification request to Supabase Auth; its configured email delivery provider sends the code. Saving a profile sends the chosen username, display name and biography. Likes, comments, reports, blocks and unblocks send the corresponding account/clip identifiers and the text or choice you submit. These requests also carry normal connection information such as the IP address.
- Choosing **Upload to Media** in the installed Windows app prepares a share copy on that PC, then sends the video, thumbnail and reviewed title/description to Supabase. Only the chosen section and included audio are shared. The original file and editor project are not uploaded or changed; no recording is uploaded simply because it was captured, edited or added to the Library. The mobile/web app is for watching and interacting: it has no upload screen or video file picker and does not read a phone's photo library, prepare or upload local videos.

Download providers receive the normal information sent with an HTTPS request, such as the IP address and requested file. Their services have their own privacy terms. Tool and update downloads do not include recordings, audio, project contents or local diagnostic logs.

If you choose a recording, export or project folder that your operating system synchronizes to a network or cloud service, that service may synchronize those files according to your configuration. Klipp writes to the folder you select.

## Optional accounts and profiles

Accounts and Media use Supabase when configured by the publisher. Supabase Auth stores account email and authentication information. A separate profile stores an account identifier, optional unique username, display name, biography and creation/update times. Profile information is available to signed-in members; anonymous clients cannot read it. Account emails and authentication tokens are not included in member-visible profiles.

The Windows app saves sign-in sessions in Windows-protected storage scoped to the configured online service. The mobile web app uses browser `sessionStorage` by default; **Remember me on this device** explicitly selects persistent `localStorage`. A small remember-device preference is also saved in local storage. Browser session data can survive tab restoration, and browser settings or private mode may discard it; use **Sign out** on shared devices. Web storage does not use the Windows app’s protected session store.

Sign-out clears the app’s local identity and attempts to remove its saved session before remote revocation. A storage-removal failure is reported so it can be retried. Signing out or clearing browser site data does not delete local recordings, projects or the online account. Provider-side session and email-delivery records follow the operator's configured retention and the providers' terms.

## Shared clips and interactions

Publishing a clip makes its video, thumbnail, title, description, author profile, duration, publication time and interaction counts available through Media to eligible signed-in members. Shared clips are not limited to their uploader. The service stores likes, comments and their authors/times. Reports and the reason you submit are available to the operator, rather than being shown in the public feed. Block lists are private to the member who created them; **Blocked users** allows a block to be removed.

Video and thumbnail buckets are private. Klipp requests short-lived playback links for the current signed-in account: five minutes for a video and two minutes for a thumbnail. Anyone given one of these links can use it until it expires. Blocking hides the other member's content and prevents new access requests; an already-issued link may remain usable until expiry. Signing out stops playback and removes local access to the session, but does not revoke every previously issued link.

**Share clip** in the web viewer shares an app link containing the clip identifier through the device's share sheet, clipboard or a selectable text field. It does not include a session token or temporary Storage link. A chosen receiving app can handle that link under its own policy; recipients still need to sign in and have access to the clip through Media.

The installed Windows app creates a share copy at up to 720p / 30 fps and mixes only the audio tracks selected before upload. A primary or mixed track may already contain voices and other sounds. The mobile web app plays the shared copy using the browser's video decoder; it does not encode or prepare local media.

Windows temporary share files use `%TEMP%\Klipp\CommunityShare`; Klipp attempts to remove them on completion or cancellation. Preparation uses the PC's processing, memory and battery; originals remain unchanged. The server checks upload access, size, path, ownership and MIME metadata; it does not decode or transcode videos. Web playback and interactions still use device memory, processing and network data.

Shared clips remain online until the owner removes them or the operator takes action. **Delete shared clip** first hides the clip, then removes its video and thumbnail and associated likes/comments/reports. A failed file removal leaves a hidden entry in **My uploads** so cleanup can be retried. This does not remove the original local recording. Members can delete their own comments. Reporting a clip does not automatically remove it; reports and moderation are handled by the operator.

There is no in-app account-deletion action yet. Account deletion currently requires administrative removal of shared files and clip reservations before the Auth account and profile can be deleted. Members can delete their own shared clips and comments, and submit clip reports in the app. An unconfigured build cannot create or access an online account.

## Online retention

The current application rules are:

| Data | Removal or expiry |
| --- | --- |
| Account and profile | Kept until administrative account deletion; there is no automatic inactivity expiry |
| Shared video, poster and clip record, including unfinished uploads | Kept until owner/operator removal completes; failed cleanup leaves a hidden entry for retry, with no automatic age-based purge |
| Likes and comments | Removed by unlike/comment deletion, or when the associated clip or author account is deleted |
| Clip reports | Removed with the clip or reporting account, or by operator action; resolving a report does not automatically delete it and there is no timed purge |
| Blocks | Removed by unblocking or deletion of either account |
| Comment-frequency history | Stores member identifiers and event times, not comment text. Entries older than one hour are pruned when a later comment transaction succeeds; there is no scheduled purge, so inactive records can remain longer |

These rules are implemented in the [account](../backend/supabase/migrations/202610220001_accounts_profiles.sql) and [Media](../backend/supabase/migrations/20261022114947_community_media.sql) schemas. Expiry of a signed playback link limits access through that link; it does not delete the stored clip.

Provider log windows are separate from these application records. As checked on 22 October 2026, [Supabase lists](https://supabase.com/pricing) one day of API/database log history and one hour of Auth audit-log history on Free. Those access windows are not a promise that account records, application reports or every internal provider copy are deleted on that schedule. Klipp does not configure a separate timed purge for provider audit records. [Resend states](https://resend.com/security) that email and log data is retained for 30 days on Free; login email content and delivery records are handled under that provider policy.

## Storage and removal

Recordings default to `%USERPROFILE%\Videos\Klipp`. Settings, projects and caches use `%LOCALAPPDATA%\Klipp`. You can choose another recording folder in Settings. Uninstalling preserves these folders so your recordings and editable projects remain available; you may remove them yourself after saving anything you need.

The mobile web app’s service worker caches only bundled icons and its installation manifest. It does not add account/API responses, email-code responses, signed playback URLs or videos to that cache, and does not provide an offline media library. Browsers and operating systems may still retain temporary playback buffers. Clearing the site’s browser data removes its local storage and service-worker cache; it does not remove shared clips from Supabase.

## Signing service

The proposed SignPath workflow submits software build binaries and public build-origin information for release signing. It runs in developer CI and does not submit end-user recordings or local app data. SignPath approval and configuration are pending.

## Changes and questions

This policy describes the current source and configured beta services. Material changes to network or data handling should update this page. Non-personal bug reports and general questions can use [public GitHub issues](https://github.com/HappyHamster135/Klipp/issues). They are not a private support channel: do not post email codes, tokens, signed playback links or private media there. No dedicated support inbox is configured yet; adding one is future work.
