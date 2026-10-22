# Local browser QA

`qa.html` renders the real components with an in-memory `Api` implementation. It never creates a Supabase client or reads account credentials. The blue banner distinguishes it from the production entry. Uploads run the real preparation worker; the fixture records the resulting MP4/JPEG metadata and keeps blob URLs in that page only.

Copy the synthetic test file before running the browser suite (from the repository root):

```powershell
New-Item -ItemType Directory -Force mobile/.qa | Out-Null
Copy-Item artifacts/community-media-qa/generated-game-and-microphone.mp4 mobile/.qa/test-video.mp4
Set-Content -LiteralPath mobile/.qa/unreadable.mp4 -Value 'Synthetic invalid MP4 for local recovery QA.'
& "$env:LOCALAPPDATA/Klipp/bin/ffmpeg.exe" -hide_banner -loglevel error -y -i mobile/.qa/test-video.mp4 -map 0:v:0 -map 0:a:0 -t 3 -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -threads 2 -b:v 400k -c:a libopus -b:a 64k mobile/.qa/test-video-opus.webm
```

The synthetic files are ignored under `.qa/`, outside `public/`, and are not included in the normal Vite build. The FFmpeg command uses the app's installed copy; an equivalent local FFmpeg executable also works. Do not substitute a private recording. Start `pnpm dev` in `mobile/`, then open `http://127.0.0.1:5173/qa.html` for manual local testing.

```sh
pnpm exec playwright install chromium webkit
pnpm test:browser
```

The suite uses 390 × 844 Android Chromium and iPhone WebKit emulation, rejects external requests, and saves screenshots/traces under the ignored `artifacts/community-mobile-qa/playwright/` directory. `.pw.ts` keeps browser tests separate from Vitest unit tests. Source preparation verifies real encoded dimensions, duration, audio-track selection, MIME types, byte limits, and subsequent video playback.

The QA-only `native-aac-disabled.worker.ts` wraps the real worker with native audio encoding unavailable. Packet hashes prove compatible AAC is copied from only the primary source track and only within the selected trim boundaries. A synthetic Opus source verifies useful guidance and a successful audio-off retry. This wrapper is not a production capability flag or an extra encoder.

Desktop WebKit and mobile emulation cannot prove hardware codec availability, native fullscreen gestures, or the install flow on physical phones. Unsupported WebCodecs paths are checked for a useful error and annotated in the test results; they are not reported as successful encoding.
