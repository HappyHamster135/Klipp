# Mobile design implementation and verification

The reference is [mobile-concept.png](mobile-concept.png). Its example racing footage and identity illustrate the layout; production uses actual member data. The implementation keeps the desktop app’s neutral background, quiet borders, muted blue actions, and K mark.

## Five fidelity points

| Before: concept | After: implementation | Why |
| --- | --- | --- |
| Dark surfaces, blue actions, restrained typography and K mark | The same palette, mark, spacing and icon language across feed, account and upload screens | Mobile and desktop feel like the same product |
| Large clip image followed by title, author and social controls | 16:9 posters, duration badges, readable metadata, Like/Comments/More, and Community/My uploads tabs | Video remains the main content; real empty and incomplete states also fit |
| Familiar inline video controls and a dedicated viewing area | Native inline playback and available platform fullscreen; title/actions below, comments beside or below, selected cards outlined | Viewing stays focused without replacing browser media controls |
| Selected-video preview, thumbnail range, fields and audio switch | Actual local preview, six source frames, draggable trim handles plus numeric inputs, primary-audio choice, progress and cancel | The upload screen edits a real bounded section rather than displaying a static mockup |
| Compact upload header and fixed Media/Upload/Profile navigation | Upload hides the global header/footer; responsive columns stack, with safe-area padding and scroll space above navigation | Phone controls remain reachable; wider browsers get more room |

## Data and processing

React/Vite produces static files with public Supabase configuration. The web client reuses the desktop account and private Media backend. Email-code authentication defaults to browser session storage; **Remember me on this device** explicitly selects persistent local storage. Signing out clears local identity and attempts saved-session removal before remote revocation. See the [privacy policy](../../docs/PRIVACY.md).

A Mediabunny worker reads the selected file, generates previews, and prepares only the chosen section as H.264/AAC plus a JPEG poster. The audio switch includes or excludes the primary source track; it does not mix separate desktop stems. Originals remain unchanged. Audio uses native AAC encoding where supported, or copies compatible source AAC packets wholly inside the selected range. Other audio formats without native AAC support offer clear guidance or a silent share. Processing stays on the device, with no bundled audio encoder or paid transcoding service. Device codec support, memory and background execution still vary.

The service worker caches only bundled icons and the manifest. It does not cache account/API responses, signed media links or video. Media requires a connection. The [mobile guide](../README.md) documents setup, limits, recovery and the server’s metadata-only validation boundary.

## Verified on 2026-10-22

| Check | Observed result |
| --- | --- |
| Production TypeScript/Vite build | Passed; QA entry, mock API and synthetic test MP4 excluded from `dist/` |
| Unit regression suite | 56 tests passed, covering client API/session fencing and validation, mobile navigation guards and audio planning |
| Browser suite at 390 × 844 | 43 passed, 11 explicit capability skips, zero failures across 54 cases in five specs; Android Chromium and iPhone WebKit emulation |
| Rendering and interaction | Meaningful pages, no framework overlay or relevant console errors, no external requests in fixture QA; playback, comments, likes, profile save, email-code lifecycle, reports, block/unblock and deletion verified |
| Chromium preparation and cancellation | Real worker output and subsequent playback verified: 640 × 360 H.264; audio-enabled share 1.216 s / 234,635 bytes / one audio track; silent share 1.200 s / 219,070 bytes / no audio. JPEGs 9,265 bytes. Cancellation published no fixture upload |
| Native AAC unavailable | The real worker copied exactly 55 primary-source AAC packets, verified by SHA-256 hashes, within the selected 0.2–1.4 s range (source packets 0.2133–1.3867 s). The 1.200 s MP4 played with one audio track. Opus audio produced useful guidance, no upload, and a successful silent retry |
| Touch layout | Layout/visual viewport stayed 390 px at scale 1; centered audio switch accepted normal clicks, and bottom cancellation remained reachable |
| Email return and session recovery | Fixture tests cover returning from Mail after a suspended resend timer, changing an address during its wait, code autofill attributes and verification returning to Media; unit checks cover storage failures and session fencing |
| Phone navigation and unsaved uploads | Android Chromium and iPhone WebKit fixture checks verify Back/Forward, direct clip links, preservation of My uploads, and Keep editing/Discard after selecting a file. Unit checks cover busy Back restoration and successful upload navigation bypassing stale busy/dirty state |
| Playback recovery and fullscreen | Ten targeted browser cases passed across Chromium/WebKit: refreshed links retain paused or playing position/audio choices, denied fullscreen leaves inline playback usable, Safari's native fallback is selected, and landscape controls fit their video. Opening a clip or rotating into landscape brings the full player above the bottom navigation |
| Upload editing recovery | Regression checks cover empty/fractional trim drafts, selecting an unreadable file again, independent trim edges, preview background pause and the selected end boundary; encoding-dependent WebKit cases remain explicit capability skips |
| Windows WebKit encoding | Required WebCodecs unavailable; encoding-dependent cases skipped explicitly, useful guidance verified. This is not an iPhone encoding result |
| Live service versus local fixtures | Live backend/native desktop checks were performed separately and shared cloud clips exist. This mobile browser suite used in-memory fixtures only; it does not establish live mobile upload or cloud cleanup |
| Physical phones | User-reported Samsung Z Flip with Chrome can sign in, reach the account and watch clips; model generation, OS and browser versions were not supplied. Phone upload, iPhone behavior, installation, photo-library formats, hardware encoding and interruption/low-memory behavior remain unverified. Laptop reachability was reported separately |
| Hosting workflow | [Recovery-update deployment](https://github.com/HappyHamster135/Klipp/actions/runs/39793426015) succeeded from source commit `480eeb4`; the free beta is live at [happyhamster135.github.io/Klipp/](https://happyhamster135.github.io/Klipp/) with HTTPS |
| Published-site smoke check | The CI ZIP checksum passed and CI/local production HTML plus all four JS/CSS/worker assets matched. Twelve served files, including the manifest, icons, notices and worker, matched build hashes. Android Chromium and iPhone WebKit at 390 × 844 passed sign-in/create-account and Remember controls, portrait/landscape layout, and zero runtime console errors. Public email-login configuration was ready. No login email or upload was sent by this smoke check |

Repeatable tests and fixture setup are in [tests/README.md](../tests/README.md). The combined recovery run's local evidence is at `%TEMP%/klipp-phone-combined-qa`; initial preparation evidence is in the ignored `artifacts/community-mobile-qa/final-aac-browser-run/` directory and `browser-results.json`. The Browser plugin was unavailable; the first inspection used the in-app browser, followed by Playwright for repeatable phone-sized checks. Screenshots and emulation do not prove physical-device compatibility.

## Distribution

[mobile-pages.yml](../../.github/workflows/mobile-pages.yml) builds a review ZIP by default. Publishing requires an explicit manual input and an already-configured Pages site; it does not enable hosting, change DNS or modify Windows releases. The current work remains a web app using free-tier services. Native wrappers and app-store distribution are separate decisions.
