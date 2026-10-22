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

`pnpm configure` copies only the public project URL and `sb_publishable_` key from the ignored `backend/community.json` into the ignored `mobile/.env.local`; additional fields are refused. Never put a service-role key, database password, email-provider key or session token into a `VITE_` variable, because Vite embeds it in browser JavaScript.

If the desktop configuration is unavailable, create `mobile/.env.local`:

```dotenv
VITE_SUPABASE_URL=https://YOUR_PROJECT_REF.supabase.co
VITE_SUPABASE_PUBLISHABLE_KEY=sb_publishable_YOUR_PUBLIC_KEY
```

The development server binds to `127.0.0.1`. A phone cannot reach the PC through its own `localhost`; use the deployed HTTPS site for phone checks.

```sh
pnpm test
pnpm build
pnpm preview
```

The production files are in `mobile/dist/`. Preserve the directory structure when serving them over HTTPS. Relative assets, manifest and service-worker paths support a repository subdirectory such as `/Klipp/`; no server-side route rewrite is required.

## Use it on a phone

Open the HTTPS address in current Safari on iPhone or Chrome on Android. Create an account or sign in with the numeric email code; there is no additional mobile password. Returning from Mail recalculates the resend wait using elapsed time. **Remember me on this device** explicitly selects persistent local storage; otherwise the app uses browser session storage. Browser restoration behavior varies, so sign out when finished on a shared device.

On iPhone, use Safari's Share menu and **Add to Home Screen**, enabling **Open as Web App** if offered. On Android, use Chrome's **Install app** or **Add to Home screen** menu. This needs no Apple or Google developer account or app-store submission. [Apple instructions](https://support.apple.com/guide/iphone/bookmark-a-website-iph42ab2f3a7/ios), [Chrome instructions](https://support.google.com/chrome/answer/9658361?co=GENIE.Platform%3DAndroid).

Media uses the browser's video decoder for the H.264/AAC clips published by Klipp on PC. The web app does not encode video, read your photo library or request access to camera/microphone devices. Keep an internet connection for private playback and interactions.

## Watch and interact

**Media** shows community clips; **My uploads** shows clips published from your Klipp PC account. Choose a clip to play it, use Like or Comments to interact, and use its actions menu to report or block another member. Owners can delete their own shared clips and comments. **Profile** contains account details, sign-out and reversible **Blocked users** management.

Browser Back and Forward follow Media, clips and Profile. Returning from a clip keeps the selected feed tab. An old `#upload` bookmark returns to Media and explains: **Upload clips from Klipp on your PC.** Empty feeds use the same guidance without offering file selection.

If a playback link expires or a connection interrupts a clip, **Retry playback** keeps its position, paused/playing state and audio choices. Fullscreen uses the available browser video mode; inline playback remains usable when fullscreen is denied. Landscape opening/rotation brings the full player above the navigation bar. Phone browsers may suspend the app in the background; this is not background playback or an offline library.

Publishing is intentional in the Windows app: choose **Upload to Media**, review a section and its audio, then confirm. The current free beta accepts up to 60 seconds / 16 MiB of video and a JPEG poster up to 256 KiB, with five reserved clips per account and forty globally. Incomplete uploads/deletions occupy a slot until cleanup succeeds. The web app can retry removal through **My uploads**; it does not upload a replacement. Originals on the PC remain unchanged.

Signed links expire after a short time; an already-issued link may remain usable until expiry after blocking or sign-out. Do not treat them as permanent share links. The server validates stored metadata, paths, sizes and ownership rather than independently decoding video duration. Upload guards reject requests identified as browser requests through Origin/fetch metadata. This is a browser boundary, not device attestation: a custom client can omit or forge those headers. See [community setup](../docs/COMMUNITY_SETUP.md) and the [privacy policy](../docs/PRIVACY.md).

## Physical-phone check

Run these separately on iPhone and Android; browser emulation does not establish a device result:

1. Record the phone model, OS and browser versions. Sign in, switch to Mail for the code, then return and verify the account.
2. Watch a clip already published from the installed Klipp PC app. Check sound, seeking, rotation, fullscreen and Back returning to the correct feed tab.
3. Try a like, comment, profile edit and reversible block/unblock. Confirm there is no upload button or file picker, including when opening an old `#upload` link.
4. Background and reopen the app. Check playback pauses and sign-in behavior matches the optional Remember choice. Sign out on a shared device.

Record pass/failure and the failing step for each device/browser. No email code, token, signed link or private recording is needed in a report. Before web uploading was intentionally removed, the user confirmed Z Flip/Chrome existing-account sign-in, uploading and watching with sound. That upload result describes the previous version. Phone account creation and iPhone testing remain unverified. Current results are recorded in the [QA ledger](design/IMPLEMENTATION.md).

## Free hosting and verification

GitHub Pages serves public app files for this public repository on GitHub Free; the existing Supabase Free project handles accounts, protected metadata and private clips. No paid encoding server, new domain or app-store account is needed. Provider quotas still apply. [GitHub Pages availability](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages).

The [mobile workflow](../.github/workflows/mobile-pages.yml) runs only manually. Configure the public repository variables `KLIPP_SUPABASE_URL` and `KLIPP_SUPABASE_PUBLISHABLE_KEY`. With **deploy unchecked**, it builds a review ZIP and SHA-256 file. With **deploy checked** on reviewed `main`, it deploys to the already-configured Pages site. It does not enable Pages, change DNS, build Windows installers or create releases. Official actions are pinned to verified commit SHAs; only the deployment job receives Pages write/OIDC permissions. Rebuild after changing public configuration.

The service worker caches only icons and the manifest. It does not cache accounts, email-code responses, API data, signed links or videos. See [tests/README.md](tests/README.md) for local fixture checks and [design/IMPLEMENTATION.md](design/IMPLEMENTATION.md) for results and physical-device limitations. The published beta is a web app, not an app-store release.
