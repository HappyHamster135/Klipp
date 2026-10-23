# Klipp mobile

Klipp mobile is an installable React/Vite web app for Android, iPhone and desktop browsers. Sign in to watch the private Media feed, like and comment on clips, manage your profile, report content and block or unblock members. **Publishing clips belongs to the installed Klipp Windows app.** The web app has no video file picker, upload screen or media-preparation worker.

The free beta address is **[happyhamster135.github.io/Klipp/](https://happyhamster135.github.io/Klipp/)**. Recording, editing, exporting and the local Library in the Windows app continue to work without an account. Online Media requires a verified, permanent account and a completed username.

## Run locally

Use Node.js 24.19.0 and **pnpm 11.25.0**, pinned in `package.json`. From the repository root:

```sh
cd mobile
pnpm install --frozen-lockfile
pnpm configure
pnpm dev
```

`pnpm configure` copies only the public project URL, `sb_publishable_` key and optional `mediaApiUrl` from the ignored `backend/community.json` into the ignored `mobile/.env.local`; other fields are refused. Never put a service-role key, database password, email-provider key or session token into a `VITE_` variable, because Vite embeds it in browser JavaScript.

If the desktop configuration is unavailable, create `mobile/.env.local`:

```dotenv
VITE_SUPABASE_URL=https://YOUR_PROJECT_REF.supabase.co
VITE_SUPABASE_PUBLISHABLE_KEY=sb_publishable_YOUR_PUBLIC_KEY
```

For a verified home media host, publishers also set `VITE_MEDIA_API_URL` to its HTTPS origin (for example `https://media.shlipp.com`), or add the same origin as `mediaApiUrl` in the desktop publisher configuration before running `pnpm configure`. Add no path, token or trailing query. Keep this unset until that host, backups and external HTTPS have passed their checks; an unavailable home host never silently redirects a clip into Supabase Storage. Accounts, profiles and feed metadata remain in Supabase. Existing clips keep their original Supabase playback path; clips marked `storage_provider: home` use the configured host’s short-lived video and poster links. Desktop uploads stream directly to that host. Browsers still have no upload transport or file picker.

Profile also provides **Storage & usage** and **Help & requests** in connected operations builds. The app checks the current account’s server capabilities before exposing these screens; only a privately authorized operator sees report review and private request replies. Help and account deletion remain available to verified members who have not chosen a username. Deployment flags stay off until the API, SQL and account-cleanup function pass their checks. See [operations deployment](../backend/supabase/OPERATIONS.md).

The development server binds to `127.0.0.1`. A phone cannot reach the PC through its own `localhost`; use the deployed HTTPS site for phone checks.

```sh
pnpm test
pnpm build
pnpm preview
```

The production files are in `mobile/dist/`. Preserve the directory structure when serving them over HTTPS. Relative assets, manifest and service-worker paths support a repository subdirectory such as `/Klipp/`; no server-side route rewrite is required.

## Use it on a phone

Open the HTTPS address in current Safari on iPhone or Chrome on Android. Create an account or sign in with the numeric email code; there is no additional mobile password. Returning from Mail recalculates the resend wait using elapsed time. **Remember me on this device** explicitly selects persistent local storage; otherwise the app uses browser session storage. Browser restoration behavior varies, so sign out when finished on a shared device.

If a temporary connection problem prevents checking a saved sign-in, **Retry saved sign-in** checks it again without sending another email. You can also sign in below. An entered email stays in place during the check.

On iPhone, use Safari's Share menu and **Add to Home Screen**, enabling **Open as Web App** if offered. On Android, use Chrome's **Install app** or **Add to Home screen** menu. This needs no Apple or Google developer account or app-store submission. [Apple instructions](https://support.apple.com/guide/iphone/bookmark-a-website-iph42ab2f3a7/ios), [Chrome instructions](https://support.google.com/chrome/answer/9658361?co=GENIE.Platform%3DAndroid).

Media uses the browser's video decoder for the H.264/AAC clips published by Klipp on PC. The web app does not encode video, read your photo library or request access to camera/microphone devices. Keep an internet connection for private playback and interactions.

## Watch and interact

**Media** shows community clips; **My uploads** shows clips published from your Klipp PC account. Choose a clip to play it, use Like or Comments to interact, and use its actions menu to report or block another member. Owners can delete their own shared clips and comments. **Profile** contains account details, sign-out and reversible **Blocked users** management.

A failed feed load offers **Retry clips** rather than claiming the feed is empty. A failed refresh keeps the current clips readable. Unavailable thumbnails do not prevent watching; refresh or returning online retries them. Keyboard users can switch collections with the arrow keys and Home/End.

**Share clip** opens the device's share sheet when available, copies the link otherwise, or offers a selectable link if clipboard access is unavailable. It shares a Klipp viewer link, not a temporary video URL or sign-in token. Recipients still need permission to use Media. Profile drafts survive failed saves, and changing Klipp screens with unsaved details asks before discarding them.

Browser Back and Forward follow Media, clips and Profile. Returning from a clip keeps the selected feed tab, scroll position and the control you opened it from. Older/Newer clips brings the requested page into view. If a direct clip link fails to open, **Retry clip** keeps that link available. An old `#upload` bookmark returns to Media and explains: **Upload clips from Klipp on your PC.** Empty feeds use the same guidance without offering file selection.

If a playback link expires or a connection interrupts a clip, **Retry playback** keeps its position, paused/playing state and audio choices. Fullscreen uses the available browser video mode; inline playback remains usable when fullscreen is denied. Landscape opening/rotation brings the full player above the navigation bar. Phone browsers may suspend the app in the background; this is not background playback or an offline library.

When a browser requires a tap before playing, **Play video** starts the same player. Brief waits stay quiet; longer buffering shows a status and a prolonged stall offers a manual retry. Switching away pauses playback, and returning leaves you in control of when it starts again. No other feed videos are prefetched.

Publishing is intentional in the Windows app: choose **Upload to Media**, review a section and its audio, then confirm. The current free beta accepts up to 60 seconds / 16 MiB of video and a JPEG poster up to 256 KiB, with five reserved clips per account and forty globally. Incomplete uploads/deletions occupy a slot until cleanup succeeds. The web app can retry removal through **My uploads**; it does not upload a replacement. Originals on the PC remain unchanged.

Signed links expire after a short time; an already-issued link may remain usable until expiry after blocking or sign-out. New home-server GET/HEAD requests also recheck moderation and account-deletion visibility; existing downloaded bytes and open streams cannot be recalled. Older Supabase links can remain usable until their expiry. Do not treat these links as permanent share links. The home server validates local MP4/JPEG structure, dimensions, duration, sizes, hashes and ownership before publishing. Upload guards reject browser Origin/fetch metadata. This is a browser boundary, not device attestation: a custom client can omit or forge those headers. See [community setup](../docs/COMMUNITY_SETUP.md) and the [privacy policy](../docs/PRIVACY.md).

## Physical-phone check

Run these separately on iPhone and Android; browser emulation does not establish a device result:

1. Record the phone model, OS and browser versions. Sign in, switch to Mail for the code, then return and verify the account.
2. Watch a clip already published from the installed Klipp PC app. Check sound, seeking, rotation, fullscreen and Back returning to the correct feed tab.
3. Try a like, comment, profile edit and reversible block/unblock. Confirm there is no upload button or file picker, including when opening an old `#upload` link.
4. Background and reopen the app. Check playback pauses and sign-in behavior matches the optional Remember choice. Sign out on a shared device.

Record pass/failure and the failing step for each device/browser. No email code, token, signed link or private recording is needed in a report. The user confirmed sign-in and playback on Z Flip/Chrome and subsequently confirmed the viewer works on iPhone. Uploading was intentionally removed from the web app afterward. New account-management and moderation flows need their own targeted checks; earlier device results do not establish those new flows. Current results are recorded in the [QA ledger](design/IMPLEMENTATION.md).

## Free hosting and verification

GitHub Pages serves public app files for this public repository on GitHub Free; the existing Supabase Free project handles accounts and protected metadata. New clip files use the private HTTPS home server; older clips remain in private Supabase Storage. No paid encoding server, new domain or app-store account is needed. Provider quotas still apply. Usage distinguishes known logical clip bytes and reserved slots from SSD capacity, and labels provider billing measurements unknown or stale when no current observation exists. [GitHub Pages availability](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages).

The [mobile workflow](../.github/workflows/mobile-pages.yml) runs only manually. Configure the public repository variables `KLIPP_SUPABASE_URL` and `KLIPP_SUPABASE_PUBLISHABLE_KEY`; set optional `KLIPP_MEDIA_API_URL` only when the home host is ready. With **deploy unchecked**, it builds a review ZIP and SHA-256 file. With **deploy checked** on reviewed `main`, it deploys to the already-configured Pages site. It does not enable Pages, change DNS, build Windows installers or create releases. Official actions are pinned to verified commit SHAs; only the deployment job receives Pages write/OIDC permissions. Rebuild after changing public configuration.

The service worker caches only icons and the manifest. It does not cache accounts, email-code responses, API data, signed links or videos. See [tests/README.md](tests/README.md) for local fixture checks and [design/IMPLEMENTATION.md](design/IMPLEMENTATION.md) for results and physical-device limitations. The published beta is a web app, not an app-store release.
