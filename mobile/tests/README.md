# Local browser QA

`qa.html` renders the real viewer components with an in-memory `Api`. It creates no Supabase client, reads no account credentials and makes no online writes. The blue banner distinguishes it from production. `?empty=1` supplies an empty feed for PC-publishing guidance checks.

Copy only the generated synthetic video before running the suite (repository root):

```powershell
New-Item -ItemType Directory -Force mobile/.qa | Out-Null
Copy-Item artifacts/community-media-qa/generated-game-and-microphone.mp4 mobile/.qa/test-video.mp4
```

The ignored `.qa/` file is outside `public/` and excluded from production. Do not substitute a private recording. Start `pnpm dev` in `mobile/`, then open `http://127.0.0.1:5173/qa.html` for local checks.

```sh
pnpm exec playwright install chromium webkit
pnpm test:browser
```

The suite uses 390 × 844 Android Chromium and iPhone WebKit emulation with external requests blocked. It verifies real browser decoding/progress of the synthetic H.264/AAC MP4, playback recovery and fullscreen fallback, landscape geometry, Back/Forward navigation, account-code lifecycle, profiles, likes/comments, reports, block/unblock and owner deletion. Viewer-only checks assert no upload controls or file inputs and recovery of old `#upload` links to Media with PC guidance.

Recovery specs inject deferred and failing in-memory API responses through the QA-only `window.__klippQA.api` and pre-render `window.__klippQASetup` hooks. They check that old feed pages and delayed mutations cannot replace newer interactions, thumbnails and comments can retry, failed actions explain themselves inside their dialog, sharing exposes only an app link, and unsaved profile edits survive failed saves and guarded navigation. The hooks are absent from the production entry.

No source file is selected, prepared or uploaded by these tests. Mobile encoding tests and their worker have been removed along with the web upload feature. Desktop upload processing is covered separately by the [Windows Media harness](../../tools/Klipp.CommunityMediaHarness/README.md).

By default screenshots/traces use ignored `artifacts/community-mobile-qa/playwright/`; use a separate output path for parallel targeted checks. Browser emulation does not prove physical-phone fullscreen gestures, installation, background lifecycle or real email delivery. Record device results separately in [the QA ledger](../design/IMPLEMENTATION.md).
