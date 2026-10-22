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
| Deployment | [Mobile usability Pages deployment](https://github.com/HappyHamster135/Klipp/actions/runs/39811920929) succeeded from source commit 1b74c37; the beta remains at [happyhamster135.github.io/Klipp/](https://happyhamster135.github.io/Klipp/) |
| Published-site verification | Ten served HTML/JS/CSS/PWA/icon/notice files matched the reviewed build hashes, including index-_abu7LNm.js and index-CEHnsi4E.css. The bundle contains no upload reservation/publication RPC, preparation chunk or QA fixture hooks. Fresh Android Chromium/iPhone WebKit 390px contexts verified signed-out #upload recovery with dismissible PC guidance, no file picker/upload controls, opt-in Remember, account-form navigation, portrait/landscape layout and zero runtime console/page errors. No OTP, upload, native-share or clipboard call was made |

Repeatable checks are in [tests/README.md](../tests/README.md); the combined usability run is saved locally at `%TEMP%/klipp-usability-final-qa`. Representative 390px fixture screenshots are `%TEMP%/klipp-feed-initial-retry-390.png`, `%TEMP%/klipp-feed-refresh-retry-390.png` and `%TEMP%/klipp-viewer-improved-390.png`. Strict UTF-8 and whitespace checks passed across 34 source/test/document files. The Browser plugin is unavailable in the agent session, so repeatable phone-sized checks use Playwright. Screenshots/emulation do not establish physical-device compatibility. Desktop upload processing remains covered by the separate [Windows Media harness](../../tools/Klipp.CommunityMediaHarness/README.md).

Fresh published-site screenshots are saved locally at `%TEMP%/klipp-usability-live-android-390.png` and `%TEMP%/klipp-usability-live-iphone-390.png`.

## Feed and playback follow-up on 2026-10-22

The next usability batch passed **132/132 browser cases** across eleven specs in 3.4 minutes, with zero failures/skips, plus the unchanged **64 unit tests** and production TypeScript/Vite build. Phone checks use Android Chromium and iPhone WebKit emulation at 390 x 844, landscape at 844 x 390, and a short desktop at 1265 x 713. These results do not establish a physical iPhone result; the user is testing that separately.

| Before | After | Why |
| --- | --- | --- |
| Back lost focus and pagination left the viewer at the old page's bottom | Back restores the exact poster/title/Comments opener and scroll; requested pages/errors move into view | Continue browsing from the intended place |
| A failed direct clip link closed itself | Scoped Retry clip and Back keep its route available; stale requests are ignored | Recover a connection failure without finding the link again |
| Safari-blocked autoplay/resume had little feedback | Metadata-stage play attempt and a Play video fallback retain the current source/position | A tap can start the existing player without waiting for canplay |
| Waiting had no status and a background retry could resume unexpectedly | Brief waits stay quiet, buffering appears after 700 ms, prolonged stalls offer manual retry after 8 seconds, and backgrounding cancels playback intent | Explain recovery while respecting pause and avoiding automatic requests |
| Fixed navigation could cover the native transport | Initial/width-change positioning brings the player above navigation | Keep controls reachable in short viewports |
| A temporary saved-session check failure only offered another email code | Explicit Retry saved sign-in retains an entered email, serializes attempts and clears obsolete errors after accepted sign-in/sign-out | Recheck an existing session without sending another email |

Initial video loading remains metadata-only; explicit playback retries use auto preload for the current clip so WebKit can restore a paused position after decoding. No other clip is prefetched. Likes/comments/sharing retain the same video and source. Delayed player timers are canceled on pause, playback recovery, source changes and unmount.

The new regressions are `feed-navigation.pw.ts` (12 cases), `playback-polish.pw.ts` (14) and `session-recovery.pw.ts` (8). Run: `pnpm test`, `pnpm build`, and `pnpm exec playwright test --reporter=line --output C:/Users/xlent/AppData/Local/Temp/klipp-feed-playback-final-qa`. External requests are blocked in fixtures, all account/social actions stay in memory, and the actual decoded MP4 is synthetic. Root's in-app browser check also exercised opening native playback and returning focus to the original poster. The Browser plugin remains unavailable, so repeatable phone checks use Playwright.

The final local bundle is `index-BB-jx94C.js` (508.56 kB / 146.31 kB gzip) with `index-BWu4FYOX.css`; the existing 500 kB Vite advisory remains. No dependencies or paid services were added. Thirteen retained license hashes match. Production scans found no upload RPC, preparation/encoding worker or QA hooks. Strict UTF-8 and whitespace checks passed for the changed mobile files. PC upload restrictions and backend configuration are unchanged.

Deployment of this follow-up is **pending**, not yet verified live. Source commit `2c2b5be4c65b9dcfb4d9c6af23ff0f87037db201` was pushed and the existing manual Pages workflow was dispatched with `deploy=true` on `main`. At **2026-10-22 16:39:04 UTC**, [run 39814902354](https://github.com/HappyHamster135/Klipp/actions/runs/39814902354) and its build/deploy jobs were still queued with no runner assigned; the deployment approval list was empty. The run remains queued, with no cancellation, redispatch, hosting change or workaround.

The [official GitHub component status](https://www.githubstatus.com/api/v2/components.json) reported Actions as `degraded_performance` and Pages as `operational`. The [unresolved-incident response](https://www.githubstatus.com/api/v2/incidents/unresolved.json), updated at 16:35:07 UTC, included Actions in its investigating incident. The degradation may explain the queue; it is not a run-specific diagnosis. Local passing checks do not establish that this new build has reached the public beta.

The frozen local SHA-256 hashes are `4457411effe3c0464333e9c8970911d4cb33a5ffe8ee2cdaf0cb189cdc37c281` for `index-BB-jx94C.js` and `2d2f5908749d64d27b2ed0ab5c76fc6deb764f39a25de1bd2bb2a8eae9f32ee8` for `index-BWu4FYOX.css`. Scanning those exact files found no upload reservation/publication RPC, encoder/preparation worker or QA hooks; all thirteen license hashes match. The new commit's ten-file served-hash comparison and fresh signed-out phone smoke check have **not** run because deployment has not succeeded. Earlier published-site evidence above belongs to the prior source commit, not this follow-up.

## Distribution

The [manual workflow](../../.github/workflows/mobile-pages.yml) builds a review ZIP unless Pages deployment is explicitly selected. It uses the existing free hosting/services and does not enable hosting, change DNS or modify Windows releases. Native wrappers and app-store distribution are separate decisions.
