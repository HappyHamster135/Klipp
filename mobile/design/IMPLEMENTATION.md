# Mobile design implementation and verification

The [original concept](mobile-concept.png) established the desktop app's neutral background, quiet borders, muted blue actions and K mark. Its racing footage and identity are illustrative. The product now uses mobile/web for watching and interaction, with publishing in the installed Klipp PC app.

## Five fidelity points

| Before: concept | After: viewer implementation | Why |
| --- | --- | --- |
| Dark surfaces, blue actions, restrained typography and K mark | Shared palette, mark, spacing and icons across Media, playback and Profile | Mobile and desktop remain consistent |
| Large clip image followed by title, author and social controls | 16:9 posters, duration badges, metadata, Like/Comments/More, and Community/My uploads | Video and interaction stay central |
| Inline video controls and a dedicated viewing area | Browser-native playback/fullscreen, retry preserving position/audio choices, landscape player above navigation | Viewing stays focused while respecting browser capabilities |
| Selected-video preview, trim fields and audio switch | Publishing is intentionally kept in the installed PC app; the web viewer has no file picker, preparation worker or upload API | Matches the requested viewer-only phone/web product |
| Fixed Media/Upload/Profile navigation | Two stable Media/Profile destinations, browser Back/Forward, old upload links returning to PC guidance | Removes a dead end without adding a new visual style |

## Data and processing

React/Vite produces static files with public Supabase configuration. The viewer reuses the desktop account, profiles and private Media backend. Email-code authentication defaults to browser session storage; Remember explicitly selects persistent local storage. Sign-out clears local identity and attempts saved-session removal before remote revocation. See [privacy](../../docs/PRIVACY.md).

The web app plays shared H.264/AAC clips with the browser decoder. It does not select, inspect, trim, encode or upload videos from a phone or browser. Installed Klipp on Windows prepares the reviewed share copy and selected audio before publishing. My uploads remains available to view and remove the account's PC-published clips and retry unfinished cleanup.

The service worker caches only icons and the manifest. Account/API responses, signed media links and videos are not added to its cache. Media needs a connection. The [mobile guide](../README.md) covers viewing, device checks and free hosting.

## Viewer verification on 2026-10-22

| Check | Observed result |
| --- | --- |
| Production TypeScript/Vite build | Passed; production has one main bundle and the PWA service worker, with no upload chunk, preparation worker, encoder, reservation/publish RPC or QA fixture hooks. Thirteen retained package license hashes verified. Vite reports one bundle-size warning: 504.82 kB minified / 145.28 kB gzip |
| Unit regression suite | 64 tests passed across four files, including comment ordering to PostgreSQL microseconds and safe sharing/navigation helpers |
| Browser suite at 390 × 844 | 98 passed in 2.3 minutes, zero failures/skips across eight specs; Android Chromium and iPhone WebKit emulation |
| Viewer-only surface | Tests cover absence of upload controls/file inputs, legacy #upload recovery to Media with PC guidance, and empty Community/My uploads guidance |
| Retained interaction checks | Real synthetic MP4 decoding/playback, comments/likes, profile save, email-code lifecycle, reporting, block/unblock and owner deletion remain covered with in-memory fixtures |
| Feed recovery and accessibility | Initial failures offer Retry without claiming an empty feed; refresh failures retain readable cards. Collection/page changes hide unrelated rows, delayed responses preserve newer Likes/comment counts/deletions/blocks, and completed mutations cannot redirect an unmounted feed. Failed actions explain the error inside their dialog. Unavailable previews remain playable and retry on refresh/online. Collection tabs support arrows and Home/End with a labelled panel |
| Comments and sharing | Failed comment reads/pages retry without losing current rows; failed posts preserve drafts, acknowledged posts are not resent after a metadata-read failure, and late reads cannot resurrect deletions or undo newer interactions. Sharing uses the device sheet, clipboard or selectable-link fallback and exposes only the app's Watch link |
| Profile editing | Unsaved edits ask before in-app navigation, browser Back or sign-out; Keep editing retains the draft. Failed saves retain it for retry, accepted saves clear the guard, duplicate submits are locked, and delayed replies cannot update a different account |
| Navigation and recovery | Back/Forward and direct Watch links retain the feed tab; retry retains playback position and audio choices; denied/native fullscreen and landscape visibility remain covered |
| Live service versus fixtures | Browser regressions use no Supabase client or account credentials and block external requests. They establish local viewer behavior, not live account delivery or cloud cleanup |
| Backend upload boundary | Live upload RPCs and a ninth restrictive Storage policy reject browser-marked uploads; 85 Media and 24 account SQL assertions passed locally. Origin/fetch metadata is not device attestation: custom clients can omit or forge headers. Existing two clips/four objects remained unchanged by deployment |
| Physical phones | Before intentional removal of web publishing, the user confirmed Z Flip/Chrome existing-account sign-in, video upload and watching with sound. Model generation, OS and browser versions were not supplied. The upload result describes the previous product; phone account creation was not reported. iPhone was charging and remains untested; installation and phone background/low-memory behavior remain unverified. Laptop reachability was reported separately |
| Deployment | [Viewer-only Pages deployment](https://github.com/HappyHamster135/Klipp/actions/runs/39806052042) succeeded from source commit 7789ea0; the beta remains at [happyhamster135.github.io/Klipp/](https://happyhamster135.github.io/Klipp/) |
| Published-site verification | Ten served HTML/JS/CSS/PWA/icon/notice files matched the reviewed build hashes. The bundle contains no upload reservation/publication RPC or preparation chunk. Fresh Android Chromium/iPhone WebKit 390px contexts verified signed-out #upload recovery with dismissible PC guidance, no file picker/upload controls, account-form navigation, portrait/landscape layout and zero runtime console errors. No OTP or upload was sent |

Repeatable checks are in [tests/README.md](../tests/README.md); the combined usability run is saved locally at `%TEMP%/klipp-usability-final-qa`. Representative 390px fixture screenshots are `%TEMP%/klipp-feed-initial-retry-390.png`, `%TEMP%/klipp-feed-refresh-retry-390.png` and `%TEMP%/klipp-viewer-improved-390.png`. Strict UTF-8 and whitespace checks passed across 34 source/test/document files. The Browser plugin is unavailable in the agent session, so repeatable phone-sized checks use Playwright. Screenshots/emulation do not establish physical-device compatibility. Desktop upload processing remains covered by the separate [Windows Media harness](../../tools/Klipp.CommunityMediaHarness/README.md).

## Distribution

The [manual workflow](../../.github/workflows/mobile-pages.yml) builds a review ZIP unless Pages deployment is explicitly selected. It uses the existing free hosting/services and does not enable hosting, change DNS or modify Windows releases. Native wrappers and app-store distribution are separate decisions.
