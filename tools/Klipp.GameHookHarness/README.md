# Paired game capture integration test

Build the native DLL, `Klipp.ExclusiveFullscreenHarness` and this project for x64. Start exactly one fake game. This console test deliberately refuses any process except `Klipp.ExclusiveFullscreenHarness`. Klipp itself exposes this backend through the opt-in **Settings → Capture → Exclusive fullscreen · DirectX 11** switch, effective when capture next starts.

Run from the repository root:

```
dotnet run --project tools/Klipp.GameHookHarness -p:Platform=x64 -- --report artifacts/game-hook-test.json
```

The host attaches through the same arbitrary selected-process loader as the app. The fake game does not load a cooperative plugin. It captures clean native frames and, intentionally for this diagnostic only, simultaneously observes the presented window through WGC. A synthetic 50% transparent red card must appear in the post-presentation observer while every native capture pixel at that location remains the original blue. It also verifies notification grace after stopping and a positive clean-Present acknowledgement before WGC fallback. Pixel inspection remains in memory; no video is saved.

Use `--hold 12` to leave time for a person to activate the fake game and press X for a real `SetFullscreenState(TRUE)` request. Windows Fullscreen Optimizations may still intercept the request; DXGI TRUE alone does not prove physical exclusive scanout. The in-backbuffer path does not depend on a topmost compositor window.

Use `--pause-barrier` to pause the fake game's presentation after drawing its notification. Detach must refuse fallback while the old displayed surface still contains the card. The fake game then resumes and the next clean Present must allow fallback.

Use `--resize` to change the fake game's actual backbuffer to 1280×720 and verify capture resumes through a new private texture generation while its delivered encoder canvas stays fixed.

Restart the fake game with `--sequential --partial`, or `--flip-sequential --partial --dirty`, and run this test with `--expect-rejected`. Retained/partial presentation must be rejected before a notification can be drawn. Normal discard and `--flip-discard` are supported test cases. Space in the fake game manually pauses/resumes presentation; Escape closes it.

No OS compatibility settings, registry, display mode, game protections or elevated access are changed. Test the real app/game combination before enabling this backend broadly.

The passing fixture set covers discard and flip-discard fullscreen requests, resize recovery with a fixed delivered canvas, notification grace after capture stops, pause/refused-fallback/resume recovery, and rejection of sequential and dirty presentation before a popup is drawn. Supported scope remains x64 D3D11 SDR discard presentation; HDR, other graphics APIs, retained surfaces and access-protected/elevated targets fall back without an injected popup.

The synthetic red rectangle checks clean/display pixel separation; it is not the production card artwork. Actual Klipp QA additionally saved three instant clips with the upper-left edge card visible. Production cards use a half-transparent dark surface with opaque labels and icons, 160 ms entrance/140 ms exit fades, and reduced-motion/high-contrast preferences. The final 60-second, 900-frame clip included earlier popups. Across 4,284,000 decoded pixels in the popup text region, no deviation from the clean blue fixture exceeded 12; the maximum was 2. This verifies saved-video exclusion in the tested fixture, not every title or graphics API.
