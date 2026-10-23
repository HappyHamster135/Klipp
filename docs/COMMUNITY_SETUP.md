# Optional accounts and Media

Klipp’s Windows recorder, editor, export and local Library work without an account or an internet connection. Online Media requires a verified account. The mobile web app supports sign-in, watching, seeking and interactions on iPhone and Android; uploads stay in the installed Windows app.

## Service design — 23 October 2026

The beta keeps **Supabase Free** for Auth, profiles, clip metadata, interactions and operations records. **Resend Free** delivers email codes. Newly shared videos/posters are stored on the operator’s own HTTPS media server at `https://media.shlipp.com`; two older beta clips remain in private Supabase Storage. GitHub Pages serves the mobile app, and GitHub releases serve Windows installers/update packages. No paid storage, video processing or tunnel service is enabled.

The home host passed trusted HTTPS, authenticated desktop uploads and full video/poster hash checks, HTTP/1.1 and HTTP/2 seeking, interaction/deletion and browser-upload rejection. iPhone and Android viewing were tested. These checks use generated temporary media rather than personal recordings. Public installer versions and the current deployment checkpoint belong in release notes; a local test installer is not a published release.

Usage, private help, moderation and self-service account deletion are capability-gated additions. Deploy and verify the SQL, media API and deletion function before enabling them or shipping an enabled client. The exact sequence and restricted privileges are in [Online operations deployment](../backend/supabase/OPERATIONS.md). This guide does not imply an unverified feature flag is enabled.

## Free-tier limits and costs

| Resource | Current plan/design |
| --- | --- |
| Accounts | Supabase Free: 50,000 monthly active users |
| Database | Supabase Free: 500 MB; profiles, comments, reports and Auth also occupy space |
| Legacy cloud files | Supabase Free: 1 GB Storage; new clip bytes use the home server |
| Supabase delivery | Finite 5 GB uncached and 5 GB cached egress allowances; home video traffic does not remove Auth/API/legacy usage |
| Login/owner-alert emails | Resend Free: 3,000/month, at most 100/day, shared by configured sending activity |
| Upload beta | 40 reserved clips globally, 5 per account; video at most 60 seconds / 16 MiB, JPEG poster at most 256 KiB |
| Clip reservation budget | Each slot reserves 16.25 MiB, at most 650 MiB total; this is an application budget, not measured SSD free space |
| External checks | Standard GitHub Actions runners in this public repository; no paid runner, artifact or extra provider |

Provider allowances can change. Check [Supabase pricing](https://supabase.com/pricing), [Resend pricing](https://resend.com/pricing) and [GitHub Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions) before increasing the beta. Free Supabase projects may pause after inactivity. Keep paid upgrades, add-ons and overages disabled. A large Auth allowance does not mean unlimited login email, database space or video viewing. Home electricity, hardware, backups and internet service still have real costs.

The usage dashboard reports actual reservations and known logical media bytes. The operator can see database size, reports/requests and provider allowance observations. Unconnected or stale provider counters say **Not measured recently**, rather than showing a fabricated zero. Capacity warnings appear before application limits; the operator must still review provider dashboards before inviting more users. Partial files and unknown server files are not represented as measured SSD usage.

## Public client configuration

The Windows publisher configuration contains only public values:

```json
{
  "projectUrl": "https://YOUR_PROJECT.supabase.co",
  "publishableKey": "sb_publishable_YOUR_PUBLIC_KEY",
  "mediaApiUrl": "https://media.shlipp.com"
}
```

Use the configuration scripts under `tools/` to validate and package it. Never put SMTP passwords, database backup credentials, gateway/signing secrets or a Supabase secret/service-role key in this file or either app. The home server has a separate root-protected configuration; its independent gateway proof is not a client secret.

Mobile builds use the equivalent public build configuration. See the mobile project’s instructions for its current build and publication flags; do not embed administrative credentials. A missing backend keeps local capture/editing usable and shows online features as unavailable.

## Account email setup

The configured sending domain is `accounts.shlipp.com`, verified with Resend. Receiving is disabled; no support mailbox is hosted there. Supabase custom SMTP uses:

| Field | Value |
| --- | --- |
| Sender name | `Klipp` |
| Sender email | `login@accounts.shlipp.com` |
| SMTP host | `smtp.resend.com` |
| Port | `465` |
| Username | `resend` |
| Password | Restricted **Sending access** key for the verified domain, entered privately in Supabase |

Both signup and sign-in templates contain `{{ .Token }}` and use code-only Klipp messages. The verified subjects are **Your Klipp confirmation code** and **Your Klipp sign-in code**. Keep email confirmation enabled. A template/provider test email alone does not prove account creation: request a code to a real test address, enter it privately, save a profile, sign out, sign in again and verify session restoration.

Supabase’s default SMTP is unsuitable for general public signup. Use custom SMTP before setting code-only templates, keep conservative resend/rate limits and monitor both provider allowances. Enter keys directly into protected provider settings; never place them in Git, command arguments, screenshots, chat or installers. Operational owner email uses a separate restricted credential on the server; it is not a support inbox or a client capability. See [Supabase SMTP](https://supabase.com/docs/guides/auth/auth-smtp) and [Resend integration](https://resend.com/docs/send-with-supabase-smtp).

## Database and file access

Apply the reviewed migration chain in filename order to the intended project. An earlier local operations placeholder was empty and was never applied; the public chain contains the later reviewed operations migration described in its [deployment contract](../backend/supabase/OPERATIONS.md). Do not reset a hosted database to apply source changes.

Profiles expose only account ID, username, display name, biography and timestamps. Signup ignores client-supplied profile/privilege metadata and starts with an incomplete profile. Usernames are unique lowercase ASCII, 3–24 characters, beginning with a letter; display names are at most 40 characters and biographies 160. Verified incomplete accounts can use account help/usage/deletion when those capabilities are enabled, while sharing/interactions require profile completion.

Media tables deny direct client access. Checked public RPCs call private implementations that validate the current verified, non-anonymous, non-banned Auth account, ownership and blocking. Operator status comes from a private allowlist, never an email string or JWT/user metadata. Row-level security, restricted grants and fixed search paths remain in place.

New uploads use the home provider. Direct new uploads to the legacy Supabase buckets are closed. Existing `community-videos` and `community-posters` buckets remain private, retaining their size/MIME restrictions for historical content and cleanup. Use the supported Storage API to remove physical objects; never manipulate `storage.objects` directly to pretend files were removed.

The desktop-only guard rejects browser Origin/Fetch Metadata headers in reservation, upload and publication. It blocks normal browser/mobile upload requests. It is **not device attestation**: a custom HTTP client can emulate the native header shape. Account verification, ownership, real media validation and quotas are the security boundary; no shared secret is hidden in the distributed app.

The Windows app prepares an explicit selected section at up to 720p / 30 fps, H.264 and optional AAC, with reviewed audio selection. Originals and editor projects remain local. The API independently checks size, duration, container/codec and poster validity with FFprobe before publication. It never transcodes on the home server. Upload and download bodies are streamed through bounded buffers.

Reservation locks enforce five slots per account and forty globally. Unfinished uploads expire after 30 minutes. Cleanup removes files before releasing capacity, including owner deletions queued if the app closes. Failed cleanup stays hidden and retryable; there are bounded pending-work limits. Playback can continue while a backup briefly pauses media mutations.

## Watching and interaction

Feed/comment pages use timestamp/ID cursors. Posters load for visible cards; videos play when selected rather than auto-playing the entire feed. Members can like/unlike, comment, remove their own comments, report clips, block and unblock users. Reports do not automatically remove content.

Videos and thumbnails use private short-lived links: five minutes and two minutes. The home API rechecks publication/moderation/deletion state on each new GET/HEAD. Downloaded bytes and open streams cannot be recalled. A previously issued legacy Storage link can remain usable until expiry. App sharing sends a clip identifier link, not an access token or playback capability.

When operations are enabled, **Storage & usage** shows reservations and capacity warnings. **Help & requests** provides private support/deletion requests and operator replies. An allowlisted operator can review reports, hide/keep clips and reply; actions are audited. Account deletion requires a live session and exact username confirmation (or `DELETE` if incomplete), durable pending intent, confirmed physical file removal and finally server-side Auth hard deletion. Failed cleanup is retried; it must not falsely report the account deleted. No administrative key goes to either app or the home API. See the [deletion function contract](../backend/supabase/functions/community-delete-account/README.md).

## Sessions, privacy and recovery

Windows sessions use per-user DPAPI storage separated by project. Mobile defaults to session-only browser storage; **Remember me on this device** explicitly opts into persistent storage. Navigation/sign-out cancel stale online work and playback. Auth/Media errors must not prevent local recording, editing or export. The [privacy policy](PRIVACY.md) describes retention, browser storage, support records and deletion limits.

The home host has daily encrypted backups to a separate physical SSD and weekly isolated restore checks. They include video/poster/manifests, legacy clip copies, exact API artifacts/configuration and full account/community database exports, including Auth/private records. Read-only database credentials use verified TLS and remain root-only. Restore checks recover data in temporary isolated memory storage with job swap disabled; no persistent public PostgreSQL listener is installed.

Uploads fail closed if required media/database backups exceed 48 hours or isolated restore proofs exceed eight days. Local operations alerts warn earlier about backups, capacity, certificate renewal and proxy updates. A separate external HTTPS checker can detect loss of the whole host after the [monitor workflow](../tools/hosting-monitor/README.md) is published and configured. It uses no accounts or paid provider; GitHub schedules and notification preferences have limitations documented there.

The separate SSD does not protect against whole-server loss, theft, fire or root compromise. An offline export helper is prepared, but no off-site copy is claimed until an explicitly selected third disk passes encrypted copy and full read verification. No disk is selected or formatted automatically. Keep the recovery password off the server and offline disk. Deleted data can remain in retained encrypted backups; restore must reconcile deletion intent and revoke restored historical sessions before reopening service.

Custom proxy updates require review and verified installation; the operating-system package is not silently substituted. Public clients use normal HTTPS certificate validation. Never disable certificate checks, open database/administration ports or weaken network separation to work around a playback problem. Private machine-specific installation and recovery runbooks are kept separately from the public source.

## Development and verification

Use a disposable local Supabase/PostgreSQL environment for fixture tests. Inspect the installed CLI’s help before initialization/reset commands, and never run a reset against the public project. Apply the full migration chain before rollback-based SQL suites. Source includes separate read-only deployment checks, account/media harnesses and operations/deletion tests; run the tests relevant to a change rather than relying on an earlier assertion count.

The [media API README](../backend/Klipp.MediaServer/README.md) documents validation, auth and streaming. The [account harness](../tools/Klipp.CommunityHarness/README.md) and [media harness](../tools/Klipp.CommunityMediaHarness/README.md) document local checks and deliberately opt-in live generated-media drills. Do not use an existing member’s account for a deletion test; use a disposable account with no personal content. Keep all email codes/tokens private and remove generated test clips afterward.
