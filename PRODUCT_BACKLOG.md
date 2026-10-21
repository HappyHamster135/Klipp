# Klipp produktbacklogg

## GPU recording and separate app audio — 21 October 2026

- Manual recording and instant clips select a tested NVIDIA, Intel or AMD H264 encoder before capture starts, with a CPU fallback and an actual encoder status. Probe results are bounded and cached; capture keeps its existing bounded frame pacing and crash-readable fragments. Advanced Capture settings retain an explicit CPU/vendor preference. This PC's synthetic 1080p60 test used about 29% less FFmpeg CPU time with NVENC; game frame rates depend on the rest of the capture workload.
- Audio settings offer Desktop audio or Game only, following the captured window or an explicit app. Optional Discord and music app selectors save independent named stereo tracks alongside microphone audio and a default playback mix. Process-tree capture includes child processes, pins process identity, avoids duplicate sources and never substitutes desktop audio. App capture requires supported Windows builds; a closed app stays silent while its track reconnects.
- Live source meters update at 10 Hz only while visible in the Audio popover or Audio settings. Per-source recording levels, held peak warnings and a configurable limiter remain separate from saved editor volume. Source names and role metadata survive recording, instant clips, recovery, editor import, save and export.
- Editor playback uses an in-process native float gain effect, preserving the current media players, selected streams, playhead and warm decoders. Boosts above 100%, normalization, fades and ducking use full gain. Game, Discord and music never act as microphone ducking controls; unclassified legacy projects retain their existing fallback. The old capped preview is retained only when effect activation is unavailable.
- Low-disk protection reserves room for repairing temporary audio and publishing the final MP4 on the actual output drive. It finishes manual capture or saves the latest instant clip, pauses further capture and explains the result. A failed instant-clip save preserves the entire rolling buffer in a recoverable session before it can be overwritten. Recovery copies named AAC streams without re-encoding and retains source files on failure.
- Temporary manual audio now uses RF64, avoiding the old 4 GB RIFF limit during long multitrack sessions. Recovery supports RF64 and legacy RIFF, complete sample frames and trailing metadata. Logical large-file tests avoid allocating or writing multi-gigabyte fixtures.
- Validation includes a clean x64 Desktop build, 511 Core tests, real GPU MP4/replay fixtures, process isolation using three silent parent apps with tone-playing children, four-source import/independent exports, measured native preview/export gain, RF64 repair/mux checks, and simulated low-disk/recovery failures. UI QA uses an isolated library and a silent fake DirectX game; user settings and project files are preserved.

## Recording recovery, audio reconnection and highlights — 21 October 2026

- Manual recording journals keep original video, audio layout, sync timing and highlight markers until a playable final file and marker metadata have been saved. One-second MP4 fragments allow recovery after a process interruption. Library and Storage offer Review, Recover selected, Later and an explicit discard confirmation; recovery never replaces an existing recording. The final unfinished fragment can be lost, and legacy MP4 files without a completed index can remain unrecoverable.
- Game audio and microphone reconnect independently without moving channel slots or restarting video. A missing source emits silence while the other source continues. Explicit device choices wait for that same endpoint; Windows default follows default-device changes. Reconnected samples rejoin the common buffered clock. Capture status and optional nonactivating notifications explain connection changes.
- Ctrl + Shift + M marks a highlight during manual recording, including while Klipp is in the tray. The recording toolbar also has Mark highlight, and Settings > Shortcuts can rebind it with conflict checks. Markers are journaled immediately and appear on the editor timeline when the recording is first opened; existing edits keep their own marker changes. Fixed a marker-list font resource cast that could crash the editor with saved highlights.
- Export presets offer Balanced, 1080p sharing, 720p smaller file and High quality while retaining the canvas framing and selected encoder. An approximate file size shows the entire exported timeline, resolution and frame rate; manual settings remain available. A real 720p H264/AAC export was within 7% of its bitrate-based estimate.
- Validation includes 501 Core tests, 38 interrupted-recording checks, 27 existing audio checks, 27 simulated reconnection checks, 69 separate-audio regression checks, 16 bookmark persistence/import checks, real export smoke, and native hotkey/notification checks. Physical headset unplug/replug remains a hardware test.

## Background capture and tray controls — 21 October 2026

- Minimizing hides the running taskbar button and keeps Klipp available through its branded notification-area icon. Capture, instant clips, shortcuts and exports continue; hidden video previews pause without unloading the current edit. Tray actions reopen Klipp, save an available instant clip, stop capture or finish recording before exit. If the icon cannot be registered, ordinary taskbar minimization remains available.
- Reopening the executable activates the existing instance, including from the tray, instead of starting a second background recorder. Native mouse and keyboard activation preserve the existing window layout; the icon is restored after Explorer recreates the taskbar and removed when Klipp exits.
- Notifications use a 75% opaque dark surface, remain 16 DIP from the left edge and sit 56 DIP below the top. Placement scales with DPI and reduces its inset on short displays. Desktop and in-game renderers share the same artwork and placement; fades, high contrast, nonactivation and capture exclusion remain in place.

## Separate audio adjustment and playback polish — 21 October 2026

- Audio controls expose a clip selector and Game audio/Microphone source picker, exact percentage entry, Reset, dB readout and numeric fades. Primary controls fit compact inspectors; Processing and Timing remain expandable. Editing locks disable every related control, and delayed analysis verifies project, stream and trim identity before applying results.
- Live level, fade and mute changes retain decoder position and waveform work. Muted nearby voices stay warm, while source/timing changes wait for the selected stream and completed seek. Video and audio wait together during brief buffering; missing voices time out independently.
- Ducking follows measured voice activity with quick attack and gradual release. Explicit microphone streams drive it when present; game audio no longer triggers the bed during microphone silence, mute or solo exclusion. Saved source roles survive renamed tracks and old separated projects adopt metadata without changing edits.
- Select A/V now applies ordinary pointer trims to aligned selected video and source stems, with shared source limits and a single Undo. Group dragging snaps all selected edges, avoids its own moving timeline end and ignores attached effects travelling with the selection. The playhead and zoom stay fixed during edits.
- Native playback now applies full amplification through the audio effect described above. The inspector explains the unity limit only if the native component cannot attach; exports continue to use the full edited gain.

## Separate capture audio and review workflow — 21 October 2026

- New recordings and instant clips keep game/system audio and microphone audio on independent, clock-aligned streams. A default playback mix lets ordinary players hear both. The editor creates named Game audio and Microphone rows automatically, omits the compatibility mix, and preserves stream selection through split, trim, save, export, waveforms, meters and loudness analysis. Existing mixed recordings retain their original audio.
- Library previews remember positions across clip changes, virtualization and app restarts. History is bounded to 500 entries; replaced and completed files restart. Fullscreen keeps the same player, and the cursor hides with the controls until pointer movement or an explicit control restores it.
- J/K/L reviews the editor timeline backward, pauses and reviews forward. Repeating a direction increases review speed to 2×, 4× and 8×. Reverse and fast review are silent; normal forward playback keeps audio. Ctrl+L opens the media library, and typing in fields keeps normal text behavior.
- Each named track has a Sync lock toggle alongside mute, solo and editing lock. Ripple edits use one time map across participating rows; aligned video/game/microphone source trims stay together. Editing locks win, and unrelated spanning media stops unsafe ripple changes with an explanation. Track names sit above the compact controls so longer names remain readable. Sync settings persist through undo, save and clipboard operations.

## Inline playback and fullscreen continuity — 21 October 2026

- Playback chrome hides after 700 ms of inactivity in both players, including the thumbnail fullscreen header and transport bar. Mouse movement and explicit controls reveal it; pause, seeking and keyboard navigation keep controls accessible. Fullscreen shortcuts are handled before a library field can consume them when hidden controls lose focus. Native UI QA verifies idle hiding, mouse reveal and Space-to-pause with hidden bars.
- Clicking a library card plays its video in the thumbnail with play/pause, seek, mute and fullscreen controls. Only the chosen card allocates a native player, and starting another preview or scrolling the video out of view releases the previous decoder and media file.
- Double-click or F moves the existing video view into fullscreen; Escape returns it to the same thumbnail. The MediaPlayer and MediaSource remain alive, preserving time, playback and audio state. The library layout stays stable while fullscreen and background list rebuilds wait until preview cleanup.
- Native UI QA verifies play/pause/seeking/muting, one active card, offscreen cleanup, playing fullscreen return at 27 seconds, and a paused fullscreen round trip at the same 38-second frame. Direction selection, Iris/Diagonal popup masks, applying Iris and dragging Wipe Right were also exercised on disposable media. Existing user projects and settings are preserved.

## Transition families and shape reveals — 21 October 2026

- The gallery contains distinct transition families rather than separate cards for every direction. Wipe and Slide each have Left, Right, Up and Down options; Barn doors offers Horizontal and Vertical. The popup sample and applied-join inspector expose the direction, and a later drag uses the chosen variant.
- Cut is removed from the gallery. The inspector uses **No transition** to restore an immediate join, while existing project files keep their compatible internal cut/wipe/slide style values.
- Iris, Box reveal, Barn doors and Diagonal add four transition families. Their card artwork, animated sample, live primary/overlay/multi-track previews and exported frames use the same normalized shape geometry. Native shape clips have dedicated child hosts so XAML rectangle clips cannot replace them during layout updates.
- Validation: the Desktop build passes without warnings or errors, all 394 unit tests pass, and all five new concrete styles pass project roundtrip plus actual service exports of 90 frames each. About 29,600 stable mask pixels per style match the expected incoming/outgoing picture.

## Exclusive game capture and notifications — 21 October 2026

- Settings → Capture has an opt-in **Exclusive fullscreen · DirectX 11** switch. Selected x64 D3D11 SDR game windows can use paired native capture and notification rendering; the preference applies when capture next starts, including a fresh instant-clip buffer. The app does not activate another window or change a game's display mode.
- Clean game frames are copied before the popup is drawn into the displayed backbuffer. Recording and instant clips consume only those clean textures. GPU handoff never waits for an encoder or slow reader on the game's render thread, and resizing keeps the delivered encoder canvas fixed.
- Cards sit near the upper-left edge with a DPI-aware 16 DIP left / 56 DIP top inset, a 75% opaque dark surface, opaque text/icons and soft rounded corners. Cached artwork fades in over 160 ms and out over 140 ms; Windows reduced-motion and high-contrast preferences are respected. The bright native frame is removed.
- Supported scope is same-user, non-elevated x64 D3D11 SDR BGRA8/RGBA8 discard or flip-discard presentation. HDR, D3D12, Vulkan, OpenGL, ARM64/32-bit, multisampled and retained/dirty presentation remain unsupported. Attachment failures use normal Windows capture with no injected popup; game protections are never bypassed.
- A clean-frame handshake gates every injected popup. Stopping capture retains a brief notification grace period. Switching to WGC/desktop capture requires a positive later clean Present, so a paused game cannot leak its frozen popup into the next capture; Klipp refuses the switch until resume or closure instead of forcing a game update. The last displayed popup cannot animate away while the game itself stops presenting.
- Fullscreen-request, flip-discard, resize, pause/resume barrier and unsupported-presentation fixtures pass with focus preserved and no popup pixels in native clean frames. Actual Klipp QA saved three instant clips with the in-game popup visible. The final 60-second/900-frame clip covered earlier popups: 4,284,000 decoded pixels in the popup text region had zero deviations above 12 from the clean blue fixture, with a maximum deviation of 2.
- DXGI reporting fullscreen does not prove physical exclusive scanout because Windows Fullscreen Optimizations can intercept the request. The in-backbuffer path avoids depending on a desktop overlay, but fake-game results do not establish compatibility with every title, driver or graphics API.
- Final manual recording QA kept the fake game focused and rendering while the capture shortcut showed Recording started and Recording saved cards. Its 17.53-second/263-frame recording decoded without errors; 1,251,880 pixels in the popup text region contained no notification pixels (maximum clean-blue deviation 2). The capture shortcut saves instant clips or toggles manual recording according to the selected mode.

## Audio devices and notification placement — 21 October 2026

- Notification cards use a DPI-aware inset near the display's upper-left edge, with safe bounds on smaller or secondary displays. Existing nonactivating, click-through and capture-exclusion behavior is retained for desktop popup windows.
- Audio settings select a Windows default or a specific output and microphone directly in Klipp. System audio and optional microphone capture have independent recording levels. Device choices persist without changing Windows defaults; disconnected explicit devices keep their own silent track and reconnect when the selected endpoint returns.
- A short test sound and opt-in, time-limited output/microphone meters help check devices before capture. Leaving the page or closing the app stops tests. An explicit Apply to instant clips action starts a fresh rolling buffer; manual recordings retain their current devices until stopped.
- Recording and instant clips normalize input formats to stereo 48 kHz before mixing, keep sample delivery ordered and bounded, align audio to the first video frame, and preserve the final prebuffered WAV samples on stop. App-specific quality profiles retain selected audio devices and levels.
- Live QA saved both a manual recording and an instant clip from the fake DirectX game window, verified non-silent stereo AAC, and applied a lower gain through a buffer restart. The original library and project files are kept separate from these temporary fixtures.
- The opt-in DXGI harness requests fullscreen and tests passive desktop popups, focus, click-through and in-memory capture delivery. Its separate paired-capture test also exercises the native D3D11 path described above. Windows Fullscreen Optimizations can intercept the exclusive request, so API state alone does not prove physical exclusive presentation; the external desktop overlay alone still cannot guarantee visibility above exclusive games.

## Fullscreen notification polish — 21 October 2026

- Notification cards use a flush dark surface with a compact icon badge and clearer title/detail spacing. Native caption, frame and bright DWM borders are removed.
- While another app or a fullscreen window has focus, notifications use a dedicated passive overlay. Mouse and keyboard input stays with the game; returning to Klipp restores the dismiss button without restarting the popup's lifetime. Placement follows the foreground display and fullscreen bounds.
- Capture exclusion is verified before either popup host becomes visible. A popup is suppressed if Windows cannot exclude it from capture. Passive rendering uses a separate native layered window so WinUI composition does not turn blank when enabling click-through.
- Borderless fullscreen and Windows Fullscreen Optimizations permit desktop-composited overlays. True exclusive fullscreen can hide desktop overlays; the app does not activate another window or change a game's display mode to force visibility.
- Native QA at 150% display scaling verifies a real click reaches a separate-thread fake fullscreen game, focus stays with it, and Windows Graphics Capture sees the original pixels throughout the popup area. Automated checks also cover the normal popup's client bounds, outline, rendered text, coalescing and dismissal.

## Capture sources, game detection and settings — 21 October 2026

- The capture toolbar shows the actual selected source beside Record and Instant clips. Its picker offers automatic game capture, whole desktop displays and individual windows; a compact layout keeps actions clear at smaller window widths.
- Automatic selection recognizes games conservatively, preserves the current game through Alt-Tab and waits without recording the desktop when no game is available. Unrecognized games can be remembered from the source picker. Manual selections override detection, and manual recordings keep their source until stopped.
- Recording and instant clips capture the chosen window/display, retain audio/cursor preferences and keep a fixed output canvas when a window is resized. Source closure finalizes a manual recording or pauses/restarts buffering as appropriate. Source changes and saves are serialized so a new source cannot mix into a clip being saved.
- Settings now has Capture, Audio, Storage, Shortcuts and Notifications pages. Quality profiles, app profiles and existing storage controls remain available alongside game detection, system audio, cursor and per-event popup settings.
- Clean nonactivating desktop notifications confirm a saved clip, recording start/stop and automatic source changes. Manual source choices stay quiet. Popups explain the event, honor duration preferences, coalesce bursts and are excluded from captured video on supported Windows versions.

## Soft edges and moving FX areas — 21 October 2026

- FX areas have an inward Soft edge control. Rectangle and oval coverage fade to the original picture at the boundary, preserving colors and alpha outside the selected area.
- Animate area stores position and size keyframes. Move the playhead and drag the area or its corners to add a pose; preview and export interpolate the movement. Previous/next, add/update and remove controls use the same compact inspector layout as other animation tools.
- Area gestures update the existing preview mask and retain the last decoded frame instead of tearing down the video image on each pointer movement. Dragging does not seek the decoder, move the timeline playhead or change timeline zoom, and one Undo restores the gesture.
- Region animation is saved with the FX and follows timeline moves, trims, splits and clipboard edits.

## On-video FX area editing — 21 October 2026

- Selecting an FX timeline item shows its area on the video, with corner handles and a draggable area. Controls keep a usable screen size as the preview zoom changes and follow each video row's source fit, crop and motion.
- Region gestures preserve the initial grab offset, clamp to the source frame and commit one undoable edit on release. Escape restores the original area; selecting and resizing FX leave the timeline playhead and scale alone.
- Video preview initializes its frame-server path before opening a source and keeps that mode through effect edits. This avoids the paused-seek color-range change that lifted blacks outside regional FX. Neutral video takes a direct GPU draw path.

## Timed and regional FX — 21 October 2026

- Choosing a built-in FX, saved look or pasted look creates a selected FX timeline item. FX can be moved, trimmed, split, duplicated and removed independently, with frame snapping and undo.
- FX follow their video through moves, trims, splits and clipboard operations. Trimming away the affected frames removes the uncovered FX instead of moving it onto clean frames.
- Affected-area presets cover the whole picture, center and each side. Rectangle and oval regions can also be drawn on a source frame or entered as percentages, preserving the picture outside the region.
- Native GPU preview and export share timing, region, amount and effect order. Saved looks retain their complete static grade and LUT. Inactive preview branches stay disconnected while their GPU resources remain reusable.

## Playback and direct manipulation polish — 21 October 2026

- The entire blue hover-time box can be dragged to scrub. Clip and trim cursors stay consistent, with precise move/trim feedback and source-bound indicators.
- Pointer updates are coalesced to render frames, with the final release position flushed before committing. Inspector work waits until the drag finishes, keeping editing feedback immediate without changing the playhead or timeline scale.
- Audio preview prepares active and upcoming clips as needed and avoids seeking every audio voice at a cut. Contiguous B-roll cuts keep their decoder running without a seek.
- Group dragging expands an initially selected clip to include its grouped video and audio. Locked members prevent partial moves that would separate the group.
- Independent audio waveforms retain their source scale while trimming, panning, zooming or changing speed. Cached ranges crop correctly, uncovered source handles remain blank until regeneration, and stale image deliveries cannot replace relinked or differently trimmed media.
- Fade handles have larger hit areas and preserve the grabbed offset. Move, trim and fade feedback uses the editor's existing visual style and frame-accurate timing.

## Everyday editing improvements — 21 October 2026

- Video and audio clips can be copied, cut and pasted at the playhead with Ctrl+C / Ctrl+X / Ctrl+V or the Edit and clip context menus. Pasting retains effects, automation, source trims, relative timing, groups and audio relationships, resolves destination tracks safely, respects track locks and creates one undoable edit.
- Frame controls move or trim actual video/audio selections and mixed groups. A common bounded delta preserves relative timing, playback-speed mapping and animation; source limits and locked group members prevent partial edits. The inspector now displays start, end and duration with frame precision.
- Previous/next edit navigation includes both clip edges and both sides of gaps on the selected track, with frame rounding that cannot leave the cursor stuck on an edge.
- Go to timecode (Ctrl+G) accepts exact frame timecodes and time values, validates input and seeks without changing clips. The Edit menu brings everyday commands and frame adjustment together, and F1 opens the current shortcuts and timeline gesture guide inside the editor.
- Modified keys no longer accidentally trigger unmodified timeline actions, and text fields and focused controls keep their expected keyboard behavior.
- Dialog surfaces center within the full editor window, with bounded, scrollable shortcut help that keeps command labels aligned and readable.

## Playback and visual preset polish — 20 October 2026

- The whole playhead cap and vertical stem can be dragged to scrub. Video, audio and transition trim edges remain usable when aligned with the playhead; ordinary clip selection still leaves the cursor in place.
- Contiguous cuts in the same source keep the decoder running. Upcoming source jumps and different files prepare a paused decoder before the cut, and independent audio prepares its in-point ahead of playback.
- Look cards show filtered samples of the selected footage. Presets apply on the first click, with clear feedback when selection or effect bypass prevents a visible change. Typeface and text-style samples show their appearance before applying.
- Pictured transition cards open a playing A/B popup sample on click, with play, loop, scrub and duration controls before dragging or applying to a join.
- A clipped preview viewport contains the full-resolution canvas independently of editor layout, keeping inspectors, export controls and the timeline within the window when resizing or using larger project canvases.

## Effects and transition previews — 20 October 2026

- Searchable, categorized look browser with consistent cards, descriptions and the selected clip's current look. Natural, Matte, Golden hour and Steel use shared RGB curves in native preview and FFmpeg export; all 20 looks support the original/effect amount mix.
- Direct color adjustments in Effects, with access to advanced curves/LUT controls. Remove look keeps manual grading; Reset all effects clears grading, LUT, effect order/bypass and amount animation in one undoable action, retaining timing, transforms and audio.
- Original comparison bypasses only the selected clip and ends when selecting another clip or changing an effect. Applying a preset restores a bypassed Look stage. Effect edits preserve mixed clip selection and remain reliable after a pending slider adjustment.
- Clicking a transition previews an isolated A/B sample before applying it. Play/pause, looping, scrubbing and duration controls do not seek the project or change history. Explicit Apply targets the selected join; dragging uses the chosen sample duration. Play applied join still previews actual footage.

## Editing interaction cleanup — 20 October 2026

- Double-click video or audio at the desired cut; splits snap to a frame and preserve the playhead and zoom. Groups and optional linked A/V split in one undo step. Alt-double-click audio adds a volume point.
- Ordinary selection and marquee selection keep playback running. Dragging begins only after the pointer threshold, and inspector/history/save work is deferred until release. Undo retains mixed selections and selects the unsplit parent when a right-half ID disappears.
- Video/audio trims respect playback speed and source limits. Canceled drags restore clip/marker state, and split/trim operations preserve partially used easing curves.
- Transition drops and resize handles work on every video row, respect locks and report rejected edits accurately. New overlaps use available source handles, keeping source alignment and program timing; duration controls show the overlap that can actually be played.
- Playback waits for seeks and decoder readiness, handles reversed scrubs and same-source cut boundaries, and keeps lower-row decoders running through higher-row gaps. Drag previews are coalesced and GPU effect graphs/fallback brushes are reused.
- Export preserves lower-row colors through transparent transitions and matches same-row transparent-clip priority and restoration. Render-cache version 7 invalidates older results.

## Tracks and editing workspace — 20 October 2026

- Persistent named video/audio tracks have independent mute, solo and lock controls, ordering and row heights. Legacy projects migrate to track IDs while retaining their timing and stacking order. Multiple active video rows play and export together.
- Ctrl-selection, marquee selection, named clip groups, optional linked A/V selection and relative group dragging work across video and audio. Splitting a group is a single undoable edit and keeps matching source audio linked to the correct half.
- The timeline tool menu provides Select, Ripple trim, Rolling trim and Slip. Ripple delete and Close gap shift later clips on the affected rows. Ordinary selection and trimming keep the playhead and timeline zoom unchanged.
- Select audio to drag its upper corner fade handles. Double-click video or audio to split at the pointer without moving the playhead. Alt-double-click a waveform to add a volume point, drag it to change time/gain, and right-click it to change interpolation or remove it. Audio crossfade is available at the playhead.
- The timeline height divider makes room for either the preview or more tracks. Track headers expose rename, order, height and empty-track removal in their context menu.
- Project media opens a source viewer with independent playback and In/Out marks before inserting a range. FX includes saved looks, reordered/bypassed effects, live 3D LUT preview, and transform/audio attribute copying.
- Project files include saved versions, recovery and portable collection of referenced video, audio, images, fonts and LUTs. Originals are copied; existing projects and source media are retained.
- FX also includes three-anchor speed ramps, adjustable video stabilization, editable source-space polygon masks with inversion, and region-based subject tracking that generates pan/zoom keyframes. Processing creates a reusable video derivative; independent audio keeps its own timing. Tracking stops when confidence or available pan range is exhausted.

The GPU LUT preview resamples tetrahedral LUT interpolation into a dense 3D transfer table. Full color management/HDR/scopes, multicam, optical-flow retiming and slide editing remain future work.

## English interface and editing polish — 20 October 2026

- Library cards measure their full 16:9 thumbnail, metadata, and action row before layout. Edit, Favorite, Folder and More remain inside the card as the library changes columns. Long titles have a full-title tooltip, and favorite actions describe their current state.
- App labels, dialogs, shortcut names, import/capture statuses, and propagated errors use English. Built-in controls and numeric input use the same English locale. Existing user names and saved metadata are retained.
- Copy look / Paste look reuses the preset, static amount, manual grade, tone curve and LUT settings. Target trim, transforms, audio and keyframes remain intact. Pasting is one undoable operation; Undo/Redo keep the current video clip selected when it still exists.

## Editor workflow review — 20 October 2026

This pass follows the direct editing workflows described by [DaVinci Resolve](https://www.blackmagicdesign.com/products/davinciresolve/edit) and [Premiere](https://www.adobe.com/learn/premiere-pro/web/remove-trim-extend-clips): work at the cut, use clip edges for timing, and see where a drag will land.

Implemented in this pass:

- Transition and look palettes that can be dragged onto a cut or a video clip. Existing transitions can move between joins; Ctrl-drag copies them. Their edges adjust duration.
- Visible trim grips, context-sensitive cursors, insertion guides when reordering primary clips, edge scrolling during a drag, Escape cancellation, and explicit Snap/Fit controls.
- Independent audio, video and B-roll movement can extend the project. Leading trims can reveal available source footage without repacking other clips.
- Up/Down navigation between edits, accessible effect amount, original comparison, and grouped look browsing.
- Decoded GPU video frames feed cached Win2D effects for live curves, color adjustments, detail, blur, chroma shifts, vignette and custom 3D LUTs. Frame callbacks are coalesced and preview surfaces are reused at up to 720p. Preview color balance and some spatial effects remain approximations.
- RGB effect-strength mixing and validated cubic curve knots/precision in export; tested at 0/50/100 percent for every current look.

Remaining substantial gaps compared with full nonlinear editors:

- Slide trim.
- Full color management, scopes and HDR workflows.
- Multicam editing and optical-flow retiming.

Detta ar en produktinventering av Klipps nuvarande kod och editor. Den skiljer pa vad som redan fungerar och vad som behovs for att Klipp ska kanna komplett i daglig anvandning.

## Levererad grund

- Flerklipps-timeline med ripple-insert, trim, dela, ta bort, ordna om och B-roll-overlays.
- Projekt-autosave, aterstallning efter krasch och undo/redo for projektandringar.
- Zoom i forhandsvisning, canvasanpassad export, text, subtitles, filter, element, fade, freeze frame och ljudvolym.
- Canvaspresets for 16:9, 9:16, 1:1 och 4:5. Projektexporten behaller nu valt format i stallet for att avslutas i 1280 x 720.
- Timeline-zoom, horisontell pan, snapping, multiselect och konfigurerbara kortkommandon med konfliktkontroll, oversikt och reset.
- Clip-inspektorn har en fast hoppnavigering for markerat klipp, projekt och exportko. Den landar pa markerat klipp vid oppning, sa langa installningspaneler inte maste skrollas manuellt.
- Safe-editorn initierar icke-nollstallda slider-varden efter att XAML-kontrollerna skapats, vilket tar bort WinUI-startkraschen som tidigare kunde uppsta nar editorn oppnades.
- Sökbar medieyta med thumbnail-grid, drag till timeline, knappar for Video/B-roll och lazy thumbnail-load.
- Bestandig biblioteksorganisation: favoriter, upp till tolv taggar och en samling per klipp sparas atomiskt separat fran videofilen, foljer med vid namnbyte och kan redigeras fran bade huvudbiblioteket och editorgridens kort. Bada vyerna soker i titel, filnamn, taggar och samling; huvudbiblioteket och editorn har favorit-/samlingsfilter samt sortering.
- Valbar previewkvalitet med Original, 720p och 540p ar implementerad. Projektets videokallor kan byggas som atomiska H.264/AAC-proxies med riktig FFmpeg-progress, avbrytning, cacheateranvandning, kallsignatur, dimensions-/langdvalidering och projektvis rensning. Huvudvideo, overgangar och B-roll anvander proxies i live-preview medan autosave och export alltid behaller originalets `SourcePath`.
- Thumbnail-, waveform- och proxyjobb delar en prioriterad tvaarbetarko: markerat klipp och anvandarstartade proxies gar fore synlig media och bibliotekets forhandsladdning. Cachetraffar, missar, ko per prioritet, aktiva jobb, snittid, appminne, GC-heap, tradar och uptime visas i installningarna. Thumbnail- och waveform-filer commits atomiskt och deras FFmpeg-processer termineras vid cancel.
- Bibliotekets klippkort har tydlig tangentbordsfokus, visar atgardsknappar vid fokus, oppnas med Enter och gar direkt till editorn med E. Huvudrail, timeline, play/undo/split/zoom, keyframes, fargval, ljudanalys och spelarverktyg har explicita screen-reader-namn.
- Capture-halsa visas bestandigt i topbaren med ledigt diskutrymme, uppskattad aterstaende tid, faktisk FPS, frame-drop, stall-detektering, encoderprocess och systemljudsignal. Kritisk lagringsniva stoppar capture och klippsparning innan skrivning startar. Biblioteksborttagning flyttar nu videon till Windows papperskorg och tar endast bort Klipp-metadata efter lyckad flytt.
- Namngivna captureprofiler (rekommenderad, eco, balanserad, kvalitet, mjuk rorelse och egen) kan kopplas till ett oppet spel/program. Klipp matchar processnamnet vid capture-start och faller sakert tillbaka till grundprofilen. Proxy-, thumbnail- och waveform-cache delar en installningsbar budget, rensas enligt senast anvand och kan tommas manuellt utan att originalmedia eller projekt berors.
- Flerfilsimport av MP4, MOV, MKV, WebM, AVI, M4V och transport streams fran editorns mediayta. H.264/AAC kopieras eller remuxas, andra codecs transkodas atomiskt till redigeringsvanlig MP4, och extern drag-and-drop till timeline anvander samma vag.
- Dockad verktygsrail till vanster sa media-inspektorn kan anvanda hela hogersidan.
- Duplicera markerade klipp med `Ctrl+D`.
- Volym och mute per klipp, sparat i autosave och anvant i preview/export, separat fran mastervolymen.
- Per-klipp-skala, position och grundfilter for huvudklipp och B-roll, med live-preview och projektexport.
- Icke-destruktiv crop per klipp fran vanster, topp, hoger och botten med live-preview, reset, undo/redo och projektexport.
- Picture-in-picture-presets for alla fyra horn och full canvas ar kopplade till clip-transformer och motion-keyframes. Video och B-roll har rundad rektangel eller ellipsmask med justerbar rundning, live-preview, autosave, undo/redo och pixelverifierad FFmpeg-export. Sparade 90/80-procentiga action/title-safe-guider visas endast i editorn och foljer alltid exportcanvasen.
- Stapelbar filterkedja per klipp: preset plus brightness, contrast, saturation, temperature, shadows, highlights, gamma, hue, fri tone curve, sharpen, blur och vignette. Kurvpunkterna dras direkt i inspektorn, autosparas och ingar i undo/redo. Exporten bygger exakta FFmpeg-kurvor; preview ger en snabb approximation.
- Extra textoverlays med egen text, stil, animation, font, exakt hex-farg, position och tidsintervall. De syns i previewn, sparas i projektet och renderas i exporten.
- Bestanding textmallbank med sex inbyggda kombinationer av stil, animation, font, farg, pixelstorlek, boxbredd och opacity. Egna mallar kan sparas, uppdateras via samma namn, anvandas i alla projekt och tas bort utan att lager som redan anvander dem paverkas.
- Flera oberoende subtitleblock med egen text, stil, font, exakt hex-farg, storlek, position och tidsintervall. Block kan valjas, trimmas i timeline, uppdateras, tas bort och renderas i exporten.
- SRT-import och SRT-export med flerradig text, robust tidsparsing och automatisk klippning mot projektets langd.
- Lokal automatisk transkribering med whisper.cpp och multilingual base-modell skapar redigerbara subtitleblock fran vald timeline-clip. Trim, playback speed och clip-position mappas till projekttid; omkorning ersatter bara auto-genererade block fran samma clip och bevarar manuella subtitles. Verktyg och SHA-256-verifierad modell hamtas vid forsta anvandning, cachas lokalt och kan avbrytas under alla faser.
- Extra ljudspar fran ett valt klipp kan laggas vid playhead, sparas i projektet och mixas med huvudljudet vid export.
- Musik, SFX och voice-over kan importeras som egna ljudspar med timeline-position, trim, gain, mute samt fade in/out. Varje trimmat spar har en egen cachad waveform och spelas live synkat med playhead, mastervolym, mute och fades; samma spar mixas i exporten.
- Loudness-normalisering till -14 LUFS ar implementerad for bade videoklipp och fristaende ljudspar. FFmpeg mater den trimmade kallan, sparar peak-saker gain i projektet och anvander samma gain i preview och export. Musik/SFX kan duckas mot projektets sammansatta klippljud; preview ger responsiv attenuation och exporten anvander riktig sidechain-kompression.
- Tvakanalig live-ljudmatare foljer playhead och summerar klippljud, fades, normalisering, ducking och fristaende ljudspar fran en cachad, strommande stereoanalys. Samma analys hittar transienter och skapar bestandiga beat-markorer utan att skriva over manuella markorer. Klipp och ljudspar har separat brusreducering med styrka, autosave/undo och riktig FFmpeg `afftdn`-export.
- Overgangar mellan primara klipp: Cut, Dissolve, Dip black och Wipe med 0.25/0.5/1 sekund och matchande audio-crossfade.
- Per-cut-overgangar: varje inkommande primarklipp kan arva projektstandard eller valja egen Cut, Dissolve, Dip black eller Wipe och egen langd. Blandade overgangar visas i timeline och renderas sekventiellt.
- Dragbara transitionregioner i timeline visar overlapptid och justerar individuell transitionlangd med snapping och undo/redo.
- Namngivna projekt med redigerbart namn, explicit Save project och lista for att oppna sparade projekt.
- Exportpresets for Draft/High/Max och 30/60 fps, anvanda i bade timeline-render och slutpass.
- Projekt behaller saknade mediereferenser och visar en relink-panel som ersatter kallfilen utan att tappa trim, lager eller effekter.
- Flera videofiler kan dras direkt fran Utforskaren till timeline och placeras sekventiellt fran slappunkten.
- Oberoende bild- och grafiklager kan importeras som PNG, JPG, WebP eller BMP, flyttas och storleksandras direkt pa canvasen eller med exakta pixelvarden, tidsattas, tonas med opacitet, autosparas, relinkas och renderas i projektexporten. Varje bildlager har ett eget markerbart, flyttbart och tvasidigt trimbart timelineblock med snapping.
- Motion-keyframes for grafiklager: position, pixelstorlek och opacity kan sattas vid playhead, interpoleras med linear/ease/hold, navigeras i inspektorn och visas som diamantmarkorer i timeline. Preview, autosave, undo/redo, duplicering, lagerflytt och FFmpeg-export delar samma keyframe-data.
- Motion-keyframes for video och B-roll: skala samt X/Y-position kan sattas vid playhead, interpoleras med linear/ease/hold, navigeras och tas bort i inspektorn och visas som diamantmarkorer i timeline. Split och trim bevarar kurvan, och preview, autosave, undo/redo, duplicering samt FFmpeg-export delar samma keyframe-data.
- Motion-keyframes for textlager: position, pixelstorlek och opacity kan sattas vid playhead, interpoleras med linear/ease/hold, navigeras i inspektorn och visas som diamantmarkorer i timeline. Sparade textoverlays kan oppnas och uppdateras igen; preview, autosave, undo/redo och FFmpeg-export delar samma data. Langa FFmpeg-filterkedjor skrivs automatiskt till temporara filterskript for att undvika Windows kommandoradsgrans.
- Volymautomation per video- och B-roll-klipp: volym kan keyframas vid playhead, interpoleras med linear/ease/hold, navigeras i Audio-inspektorn och visas som turkosa diamantmarkorer i timeline. Preview, autosave, undo/redo, duplicering, trim, split och FFmpeg-export delar samma kurva.
- Filterstyrke-keyframes per video- och B-roll-klipp: hela preset- och justeringskedjan kan mixas 0-100% mot originalet, animeras vid playhead med linear/ease/hold och navigeras i inspektorn. Rosa diamantmarkorer visas i timeline; preview, autosave, undo/redo, duplicering, trim, split och dynamisk FFmpeg-blend delar samma kurva.
- Exporten har valbar Auto/CPU/NVIDIA/Intel/AMD-encoder, sparad target bitrate och riktig hardvaruprovning. Auto valjer en fungerande GPU-encoder och hela exporten kor automatiskt om med x264 om hardvarukodningen fallerar.
- Saknade projektfiler kan sokas och aterlankas i grupp via Find all. Saker matchning kravs pa filnamn, mediatyp och, for video/ljud, kallans langd; tvetydiga traffar lamnas till manuell relink. Hela batchen ar en undo/redo-atgard.
- Dissolve, dip black och wipe visas nu i timeline-preview med en synkad inkommande decoder och ljud-crossfade. Playback fortsatter efter transition-overlappet utan att hoppa bakat.
- 3D `.cube` LUT kan importeras per klipp, valideras, mixas med 0-100% styrka, dupliceras/autosparas med klippet och renderas exakt i exporten.
- Headless projekt-export finns som `tools/Klipp.ProjectExportHarness` for reproducerbara exporttester utan att oppna eller styra editor-UI:t.
- Projektmarkorer kan laggas vid playhead med knapp eller `M`, valjas fran inspektorn, namnges, kommenteras, fargmarkeras och dras med snapping mot playhead, klippkanter och andra markorer. Klipp har egna anteckningar och fargetiketter som syns pa timeline. All metadata ingar i autosave, projektkloning och undo/redo.

## P0: behovs for en produktionsklar editor

- Fri tone curve ar implementerad med fem dragbara punkter, snapping till neutral diagonal, separat reset, live-preview, projektversionering, autosave och sammanhallen undo/redo per dragning.
- Waveform och live-previewmix for extra ljudspar ar implementerat, inklusive seek, pause, master mute/volym, gain och fade in/out.
- Transition-preview i realtid ar implementerad for Dissolve, Dip black och Wipe, inklusive ljud-crossfade. Per-cut-stil/langd och dragbara overlap-handtag finns ocksa.
- Detaljerade exportkontroller ar implementerade: outputupplosning frikopplad fran redigeringscanvas, Canvas/1080p/1440p/4K-presets, exakta custom-pixelmatt, bildforhallandelas och separat audio-bitrate. Slutpasset skalar proportionellt och letterboxar vid behov; hardware-encoder, video-bitrate, kvalitet och bildfrekvens sparas i samma projektflode.
- Tydlig aterstallning nar kallmaterial har flyttats: manuell relink och automatisk, saker sokning i projektets och anvandarens mediemappar ar implementerad.
- Kontrollerad transcode-vag och dubblettdetektering ar implementerad. Full SHA-256, ett verifierat importindex och bytejamforelse ateranvander befintliga filer aven efter namnbyte; copy, remux och H.264/AAC-transcode verifieras innan atomisk commit.

## P1: arbetsflode med hog nytta

- Automatisk transkribering till subtitleblock ar implementerad lokalt med sprakval, progress, cancel, clip-kallkoppling, undo/autosave och sakert bevarande av manuella subtitles.
- Markorer, klippanteckningar och fargetiketter for langre redigeringar ar implementerade och sparas i den versionshanterade projektmodellen.
- Keyframes for volym och filterstyrka ar implementerade for video och B-roll. Grafik- och textlager har position, pixelstorlek och opacity, och video/B-roll har skala samt X/Y-position.
- Justerbar previewkvalitet och bestanding proxycache for stora inspelningar ar implementerat med Original/720p/540p, projektkontroller och saker fallback till originalmedia.
- Persistent exportko med riktig FFmpeg-progress, sekventiell bakgrundsrendering, cancel som terminerar processtradet, retry fran fryst projektsnapshot och aterlasbar historik ar implementerad. Fardiga jobb laggs i biblioteket utan att editorn stangs och kan oppnas direkt fran kopanelen.
- Bibliotekstaggar, favoriter, samlingar och sokning i titel, tagg och datum ar implementerat i bade huvudbiblioteket och editorns medieyta.
- Valbara extra sokmappar for auto-relink. Automatisk batchsokning i projekt-, biblioteks- och standardmediemappar finns nu.
- Kortkommandooversikt och konfigurerbara kortkommandon ar implementerade for undo, redo, duplicera, dela, ta bort, play/pause, markor och media. Konflikter byter kommando i stallet for att skapa dubbletter, och editorns kommandon kan inte krocka med den globala Spara klipp-hotkeyen.

## P2: finish och kreativ bredd

- Exakt GPU-preview aterstar. Exporten har fri tone curve, masterkurva fran shadows/highlights, gamma, hue, LUT-import med styrka samt grundlaggande fargkontroller, blur, vignette, sharpen och staplade effekter.
- Enkel maskning, picture-in-picture-presets och safe-area-guider ar implementerade for video/B-roll. Grafiklager har samma rundade och elliptiska maskformer, och rektangular crop kan kombineras med videomasken.
- Ljudmatare, brusreducering och beat-markorer ar implementerade. Analysen ar cachad och minnessaker for langa filer; beat-markorer snappar i timeline och brusreducering renderas for bade klippljud och fristaende ljudspar.
- Ateranvandbara text- och motionpresets ar implementerade som en bestanding mallbank i Text-inspektorn. Brand kits ar nu bestanding appdata med namngivna fargpaletter, standardfont, validerad TTF/OTF-import och ateranvandbara logotyper. Kit kan appliceras pa text/subtitles och logotyper blir vanliga projektlager med transform, keyframes, undo, autosave och export. Importerade fonter sparas i projektet och anvands exakt i FFmpeg-exporten; saknade filer faller tillbaka till vald systemfont med tydlig status.
- Prioriterad bakgrundsladdning, detaljerad cache-/processdiagnostik och bestanding render-cache ar implementerad. Timelinen delas i atomiska 8-sekundersintervall med lokala fingeravtryck for media, LUT, transitions, keyframes, grafik och ljud. Oforandrade intervall ateranvands utan omkodning, lokala andringar invalidiserar bara berorda intervall och renderfilerna ingar i samma budgetstyrda LRU-rensning som proxy, thumbnails och waveforms.
- Tooltips, systemfokus, screen-reader-labels och tangentbordsoppning av bibliotekskort ar implementerade i huvudflodena. High-contrast-laget foljer nu Windows systemfarger, samtliga sekundara dialoger anvander ett gemensamt scrollbart och namngivet dialogskal, och trimning kan styras exakt med tangentbord. Auditen finns i `tools/Test-EditorAccessibility.ps1`.

## Capture och bibliotek

- Capture-halsa med diskutrymme, aterstaende tid, tappade/ogiltiga frames, encoderstatus, ljudsignal och tydliga varningsnivaer ar implementerad i topbaren.
- Captureprofiler per spel/app med rekommenderad upplosning, bildfrekvens och bitrate ar implementerade med processmatchning, manuellt val och saker fallback.
- Automatisk budgetstyrd LRU-stadning av atergenererbar proxy-, thumbnail- och waveform-cache ar implementerad. Valbar retention av gamla originalklipp ar avstangd som standard och har 7-365 dagars val, manuell forhandskontroll samt automatisk dygnskorning. Filer flyttas till Windows papperskorg och favoriter, all projektmedia, andrade filer och de 20 senaste klippen skyddas.
- Saker borttagning genom Windows papperskorg i stallet for permanent filradering ar implementerad for bibliotekskort och spelare.

## Implementeringsordning

1. Medialayout, grid och sokning: klart i denna omgang.
2. Duplicering i timeline, kortkommandon och MP4-import: klart i denna omgang.
3. Per-klipp-inspektor och exportkontroller: klart for nuvarande effekt- och exportmodell.
4. Ljudspar, ny textlagermodell och overgangar: grundflodena ar klara och verifierade.
5. Namngivna projekt, captions, exportko och bibliotekets organisering: klart for nuvarande metadata- och sokmodell.
6. Grafik-, video-, text-, volym- och filterkeyframes, projektmarkorer, text-/motionmallar, brand kits med paletter/logotyper/importerade fonter, PIP/masker/safe-area, ljudmatare, brusreducering, beat-markorer, loudness-normalisering, sidechain-ducking, capture-halsa, captureprofiler, cachebudget, intervallbaserad render-cache, saker borttagning och skyddad originalklippsretention: klart for nuvarande modell.
# Independent timeline editing — 20 October 2026

- Primary video and B-roll use explicit timeline positions. Dragging or trimming a clip preserves other clips' positions; gaps preview and export as black, and later clips take precedence during overlaps.
- Opening or importing video creates independent source audio clips automatically. Audio edits, moves and deletion stay independent of the video; old manually detached audio is adopted without duplication.
- Selecting, moving, trimming and splitting clips preserve the playhead. Seek with the ruler, playhead or navigation controls. The cursor can remain beyond shortened content.
- Timeline scale and scroll position survive duration changes. Fit and zoom controls explicitly change the view.
- Existing projects migrate their packed video positions once, including existing transition overlaps. Transitions require real overlap; applying one at an adjacent join moves only the incoming video to form it.
