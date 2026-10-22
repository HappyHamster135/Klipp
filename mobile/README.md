# Klipp mobile

Klipp mobile is an installable React/Vite web app for Android and iPhone. It uses the same optional Klipp account, profiles, private Media feed, uploads, likes, comments, reports, and blocking as the desktop app. The first mobile version shares existing videos; it does not record or edit mobile gameplay.

The free mobile beta is live at **[happyhamster135.github.io/Klipp/](https://happyhamster135.github.io/Klipp/)**. Open that address in Safari or Chrome to use it or add it to your home screen.

Recording and editing in the Windows app continue to work without an account. Online Media requires a verified, permanent account and a completed username.

## Run locally

Use Node.js 24.19.0 and **pnpm 11.25.0**, the package manager pinned in `package.json`. Dependencies and their lockfile are pinned; use a frozen install rather than upgrading packages as part of setup.

From the repository root:

```sh
cd mobile
pnpm install --frozen-lockfile
pnpm configure
pnpm dev
```

`pnpm configure` copies only the public project URL and `sb_publishable_` key from the ignored `backend/community.json` into the ignored `mobile/.env.local`. It refuses additional configuration fields. Do not put a service-role key, database password, email-provider key, or session token into any `VITE_` variable: Vite embeds those variables into browser JavaScript.

If the desktop configuration is unavailable, create `mobile/.env.local` yourself:

```dotenv
VITE_SUPABASE_URL=https://YOUR_PROJECT_REF.supabase.co
VITE_SUPABASE_PUBLISHABLE_KEY=sb_publishable_YOUR_PUBLIC_KEY
```

Open the localhost URL printed by Vite. The development server binds to `127.0.0.1` by default. A phone cannot reach the PC through its own `localhost`; phone testing needs a reachable HTTPS preview or deployment. Do not disable browser security to test encoding over plain HTTP on a LAN.

```sh
pnpm test
pnpm build
pnpm preview
```

The production website is `mobile/dist/`. Serve it over HTTPS, preserving its directory structure. Relative asset, manifest, icon, and service-worker paths support a repository subdirectory such as `/Klipp/`; the app does not require server-side route rewrites.

## Use it on a phone

Open the deployed HTTPS address in current Safari on iPhone or Chrome on Android. Sign in using the numeric code from the same Klipp account email; there is no magic-link redirect or additional mobile password. Leave **Remember me on this device** unchecked on a shared device. The default session uses browser session storage; checking the box explicitly chooses persistent local storage. Browser lifecycle behavior can vary, so use **Sign out** when finished on a shared device.

To install on iPhone, open the site in Safari, use its Share menu, choose **Add to Home Screen**, and enable **Open as Web App** if offered. On Android, use Chrome’s **Install app** or **Add to Home screen** menu entry. Menu wording varies by browser version. This web installation needs no Apple or Google developer account and no app-store submission. [Apple’s installation instructions](https://support.apple.com/guide/iphone/bookmark-a-website-iph42ab2f3a7/ios), [Chrome’s web-app instructions](https://support.google.com/chrome/answer/9658361?co=GENIE.Platform%3DAndroid).

Installation is separate from clip encoding support. Preparing uploads requires a secure context, workers, and browser WebCodecs support for decoding the chosen video and encoding H.264. Capability checks report unsupported formats rather than sending the original large file to a server for conversion. Use a recent Safari/Chrome version and try an H.264 MP4 when a phone cannot read its original video. Exact codec support depends on the browser and device. [Mediabunny’s codec support](https://mediabunny.dev/guide/supported-formats-and-codecs).

## What happens to a selected video

The user chooses an existing MP4, MOV, or WebM file, previews it, and sets the start and end of a section up to 60 seconds long. The original file is read locally and remains unchanged. The initial mobile audio control includes or excludes the source’s primary audio track; separate desktop microphone/game/Discord/music track selection is not part of this first mobile flow.

A Mediabunny web worker decodes and compresses the selected section on the user’s device into an H.264 MP4, up to 1280 × 720 at 30 fps without enlarging a smaller source. Devices with native AAC encoding prepare audio at 96 kbps, stereo, 48 kHz. Otherwise, an existing AAC source track can be copied efficiently without re-encoding; its packet boundaries are kept inside the selected range, which can omit a small fraction of a second at either edge. Other source audio formats need native AAC support: choose an H.264/AAC MP4, turn audio off, or prepare the clip in Klipp on your PC. No FFmpeg/WASM encoder is included. A JPEG poster is produced from the share copy. Only the verified share copy and poster are uploaded; the original is not uploaded. Keep the app open until preparation and upload finish—background execution is not guaranteed on phones.

| Limit | Current free beta |
| --- | --- |
| Source selected on the device | At most 512 MiB |
| Uploaded video | At most 60 seconds and 16 MiB |
| Uploaded poster | JPEG, at most 256 KiB |
| Upload slots | 5 per member, 40 across the beta |
| Incomplete uploads/deletions | Continue to occupy a slot until cleanup succeeds |
| Title / description / comment | 120 / 500 / 500 characters |

The server reserves a full video-plus-poster slot before file upload, validates stored object size/MIME/path/owner on publication, and protects all media with the existing authenticated RPCs and private Storage policies. It does not decode MP4 bytes to independently measure duration. Signed playback links are short-lived; a link already issued may remain usable until it expires after a block or sign-out. Do not treat a copied signed link as a permanent share link.

After cancellation or a connection loss, check **My uploads**. Delete an incomplete entry again if cleanup could not finish. Storage removal happens through the Storage API before the reservation is released.

## Prepare free static hosting

GitHub Pages is available for a public repository on GitHub Free. It serves the web app files; the existing Free Supabase project continues to handle accounts, protected metadata, and private clip storage. No domain purchase, paid encoding server, or new backend service is required for this first version. Free service quotas still apply. [GitHub Pages availability](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages).

The separate [mobile workflow](../.github/workflows/mobile-pages.yml) runs **only manually**. It does not build Windows installers, create releases, push tags, enable Pages, or change DNS.

When the source is reviewed and ready on GitHub:

1. Add two repository **variables**, not server secrets, under **Settings → Secrets and variables → Actions → Variables**: `KLIPP_SUPABASE_URL` and `KLIPP_SUPABASE_PUBLISHABLE_KEY`. Use the same public values as the desktop community configuration.
2. Run **Mobile web build and optional Pages deployment** with **deploy unchecked**. It installs the locked mobile dependencies, runs mobile client tests, builds, and saves `klipp-mobile-site` for review. The downloaded artifact contains a static-site ZIP and SHA-256 file. Extract the ZIP before serving it; do not open `index.html` with `file://`.
3. Only when ready to publish, choose **GitHub Actions** as the source in **Settings → Pages**. Review the `github-pages` environment protection rules if present. This repository has one Pages site, so check that it is not already hosting another site.
4. Run the workflow from the reviewed `main` branch with **deploy checked**. Deployment is restricted to `HappyHamster135/Klipp` on `main`; `configure-pages` explicitly has automatic enablement disabled. The deployment job reports the actual address. This repository's beta is deployed at `https://happyhamster135.github.io/Klipp/`; its [first deployment](https://github.com/HappyHamster135/Klipp/actions/runs/39787692928) succeeded on 22 October 2026.

The workflow’s official actions are pinned to verified commit SHAs. Only its optional deployment job gets `pages: write` and an OIDC identity token. The normal build never publishes. Rebuild to change public Vite configuration; changing repository variables does not rewrite an already-built site.

Alternatively, upload only the contents of `mobile/dist/` to a free static HTTPS host you already use. No server credentials or `.env.local` belong in the deployed files. Keep API responses, tokens, and signed clip URLs out of static-host caches and logs.

## Offline behavior and verification

Media requires an internet connection. The service worker caches only icons and the manifest; it does not cache accounts, email-code responses, API data, signed URLs, or videos, and it does not promise a fully offline app shell.

The design and QA ledger is [design/IMPLEMENTATION.md](design/IMPLEMENTATION.md). A successful desktop-browser build or narrow viewport test does not prove physical iPhone/Android installation, camera-library formats, hardware encoding, or background behavior. Those checks remain explicit device QA items until recorded there. The published beta is a web app; there is no app-store release.
