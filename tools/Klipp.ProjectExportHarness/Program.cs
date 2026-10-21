using System.Text.Json;
using Klipp.Core.Branding;
using Klipp.Core.Subtitles;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;

if (args.Length == 2 && args[0] == "--export-presets-smoke")
{
    await ExportPresetChecks.RunAsync(args[1]);
    return 0;
}

if (args.Length == 2 && args[0] == "--transition-families")
{
    await TransitionFamilyChecks.RunAsync(Path.GetFullPath(args[1]));
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--project-model-smoke", StringComparison.OrdinalIgnoreCase))
{
    var projectsDirectory = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(projectsDirectory);
    var customFontPath = Path.Combine(projectsDirectory, "Brand Sans.ttf");
    var metadataProject = new EditorProject
    {
        Name = "Metadata smoke",
        Settings = new EditorProjectSettings
        {
            PreviewQuality = "720p",
            ShowSafeAreaGuides = true,
            Text = "Animated title",
            TextEnabled = true,
            TextFontFamily = "georgia",
            TextFontFilePath = customFontPath,
            TextFontFamilyName = "Brand Sans",
            TextColor = "#12ab34",
            SubtitleFontFamily = "arial",
            SubtitleFontFilePath = customFontPath,
            SubtitleFontFamilyName = "Brand Sans",
            SubtitleColor = "#fedcba",
            TextStartSeconds = 0,
            TextEndSeconds = 8,
            TextKeyframes =
            [
                new EditorProjectTextKeyframe
                {
                    TimeSeconds = 0,
                    AnchorX = 0.2,
                    AnchorY = 0.3,
                    Size = 32,
                    Opacity = 0.4
                },
                new EditorProjectTextKeyframe
                {
                    TimeSeconds = 6,
                    AnchorX = 0.8,
                    AnchorY = 0.7,
                    Size = 72,
                    Opacity = 1,
                    Easing = "ease-in-out"
                }
            ]
        },
        Clips =
        [
            new EditorProjectClip
            {
                Id = "transcription-source",
                SourcePath = Path.Combine(projectsDirectory, "source.mp4"),
                Name = "Interview",
                Notes = "Use the second answer",
                ColorLabel = "blue",
                SourceDurationSeconds = 12,
                TrimEndSeconds = 12,
                Track = EditorClipTrack.Primary,
                MaskShape = "rounded",
                MaskRoundness = 0.22,
                NormalizeAudio = true,
                NoiseReductionEnabled = true,
                NoiseReductionStrength = 0.55,
                NormalizationGainDb = 3.5,
                IntegratedLoudnessLufs = -18.25,
                LoudnessAnalysisTrimEndSeconds = 12,
                VolumeKeyframes =
                [
                    new EditorProjectClipVolumeKeyframe
                    {
                        TimeSeconds = 0,
                        Volume = 0.25,
                        Easing = "linear"
                    },
                    new EditorProjectClipVolumeKeyframe
                    {
                        TimeSeconds = 6,
                        Volume = 1,
                        Easing = "ease-in-out"
                    }
                ],
                Filter = "mono",
                FilterStrengthKeyframes =
                [
                    new EditorProjectClipFilterKeyframe
                    {
                        TimeSeconds = 0,
                        Strength = 0,
                        Easing = "linear"
                    },
                    new EditorProjectClipFilterKeyframe
                    {
                        TimeSeconds = 6,
                        Strength = 1,
                        Easing = "ease-in-out"
                    }
                ],
                TransformKeyframes =
                [
                    new EditorProjectClipTransformKeyframe
                    {
                        TimeSeconds = 0,
                        Scale = 1,
                        Easing = "linear"
                    },
                    new EditorProjectClipTransformKeyframe
                    {
                        TimeSeconds = 6,
                        Scale = 1.5,
                        OffsetX = 0.25,
                        OffsetY = -0.15,
                        Easing = "ease-in-out"
                    }
                ]
            }
        ],
        AudioTracks =
        [
            new EditorProjectAudioTrack
            {
                Name = "Music bed",
                SourcePath = Path.Combine(projectsDirectory, "music.wav"),
                SourceDurationSeconds = 12,
                TrimEndSeconds = 12,
                NormalizeAudio = true,
                NormalizationGainDb = -2.25,
                IntegratedLoudnessLufs = -11.75,
                LoudnessAnalysisTrimEndSeconds = 12,
                DuckingEnabled = true,
                DuckingStrength = 0.8,
                NoiseReductionEnabled = true,
                NoiseReductionStrength = 0.7
            }
        ],
        Markers =
        [
            new EditorProjectMarker
            {
                TimeSeconds = 4.25,
                Label = "Best take",
                Notes = "Cut on the reaction",
                ColorLabel = "green"
            },
            new EditorProjectMarker
            {
                TimeSeconds = 6,
                Label = "Beat · Music bed",
                ColorLabel = "teal",
                Kind = "beat"
            }
        ],
        ExtraTextLayers =
        [
            new EditorProjectTextLayer
            {
                Text = "Saved overlay",
                FontFamily = "trebuchet",
                FontFilePath = customFontPath,
                FontFamilyName = "Brand Sans",
                Color = "#A970FF",
                StartSeconds = 1,
                EndSeconds = 7,
                Keyframes =
                [
                    new EditorProjectTextKeyframe { TimeSeconds = 1, AnchorX = 0.1, AnchorY = 0.2 },
                    new EditorProjectTextKeyframe { TimeSeconds = 7, AnchorX = 0.9, AnchorY = 0.8, Easing = "ease-out" }
                ]
            }
        ],
        SubtitleLayers =
        [
            new EditorProjectTextLayer
            {
                Text = "Generated caption",
                FontFilePath = customFontPath,
                FontFamilyName = "Brand Sans",
                Origin = "transcription",
                SourceClipId = "transcription-source",
                StartSeconds = 2,
                EndSeconds = 4
            }
        ]
    };

    var service = new EditorProjectService(projectsDirectory);
    var savedPath = await service.SaveNamedProjectAsync(metadataProject);
    var restored = await service.TryLoadAsync(savedPath)
        ?? throw new InvalidOperationException("The metadata project was not restored.");
    var restoredClip = restored.Clips.Single();
    var restoredMarker = restored.Markers.Single(marker => marker.Kind == "manual");
    var restoredBeat = restored.Markers.Single(marker => marker.Kind == "beat");
    var restoredCaption = restored.SubtitleLayers.Single();
    if (restored.Version != EditorProject.CurrentVersion ||
        restored.Settings.PreviewQuality != "720p" ||
        !restored.Settings.ShowSafeAreaGuides ||
        restoredClip.Notes != "Use the second answer" ||
        restoredClip.ColorLabel != "blue" ||
        restoredClip.MaskShape != "rounded" ||
        Math.Abs(restoredClip.MaskRoundness - 0.22) > 0.0001 ||
        restoredClip.TransformKeyframes.Count != 2 ||
        restoredClip.TransformKeyframes[1].Easing != "ease-in-out" ||
        Math.Abs(restoredClip.EvaluateTransform(3).Scale - 1.25) > 0.0001 ||
        restoredClip.VolumeKeyframes.Count != 2 ||
        restoredClip.VolumeKeyframes[1].Easing != "ease-in-out" ||
        Math.Abs(restoredClip.EvaluateVolume(3) - 0.625) > 0.0001 ||
        !restoredClip.NormalizeAudio ||
        !restoredClip.NoiseReductionEnabled ||
        Math.Abs(restoredClip.NoiseReductionStrength - 0.55) > 0.0001 ||
        Math.Abs(restoredClip.NormalizationGainDb - 3.5) > 0.0001 ||
        Math.Abs((restoredClip.IntegratedLoudnessLufs ?? 0) - -18.25) > 0.0001 ||
        !restored.AudioTracks.Single().DuckingEnabled ||
        Math.Abs(restored.AudioTracks.Single().DuckingStrength - 0.8) > 0.0001 ||
        !restored.AudioTracks.Single().NormalizeAudio ||
        !restored.AudioTracks.Single().NoiseReductionEnabled ||
        Math.Abs(restored.AudioTracks.Single().NoiseReductionStrength - 0.7) > 0.0001 ||
        restoredClip.FilterStrengthKeyframes.Count != 2 ||
        restoredClip.FilterStrengthKeyframes[1].Easing != "ease-in-out" ||
        Math.Abs(restoredClip.EvaluateFilterStrength(3) - 0.5) > 0.0001 ||
        restored.Settings.TextKeyframes.Count != 2 ||
        restored.Settings.TextFontFamily != "georgia" ||
        restored.Settings.TextFontFilePath != customFontPath ||
        restored.Settings.TextFontFamilyName != "Brand Sans" ||
        restored.Settings.TextColor != "#12AB34" ||
        restored.Settings.SubtitleFontFamily != "arial" ||
        restored.Settings.SubtitleFontFilePath != customFontPath ||
        restored.Settings.SubtitleFontFamilyName != "Brand Sans" ||
        restored.Settings.SubtitleColor != "#FEDCBA" ||
        Math.Abs(EditorProjectTextKeyframe.Evaluate(
            restored.Settings.TextKeyframes,
            new EditorProjectTextState(0.5, 0.84, 64, 1),
            3).Size - 52) > 0.0001 ||
        restored.ExtraTextLayers.Single().Keyframes.Count != 2 ||
        restored.ExtraTextLayers.Single().FontFamily != "trebuchet" ||
        restored.ExtraTextLayers.Single().FontFilePath != customFontPath ||
        restored.ExtraTextLayers.Single().FontFamilyName != "Brand Sans" ||
        restored.ExtraTextLayers.Single().Color != "#A970FF" ||
        restoredCaption.Origin != "transcription" ||
        restoredCaption.FontFilePath != customFontPath ||
        restoredCaption.FontFamilyName != "Brand Sans" ||
        restoredCaption.SourceClipId != "transcription-source" ||
        Math.Abs(restoredMarker.TimeSeconds - 4.25) > 0.0001 ||
        restoredMarker.Label != "Best take" ||
        restoredMarker.Notes != "Cut on the reaction" ||
        restoredMarker.ColorLabel != "green" ||
        restoredBeat.Label != "Beat · Music bed" ||
        restoredBeat.ColorLabel != "teal")
    {
        throw new InvalidOperationException("Marker or clip metadata changed during project persistence.");
    }

    var cloned = EditorProjectService.Clone(restored);
    if (cloned.Markers.Single(marker => marker.Kind == "manual").Label != restoredMarker.Label ||
        cloned.Markers.Single(marker => marker.Kind == "beat").Label != restoredBeat.Label ||
        cloned.Clips.Single().Notes != restoredClip.Notes ||
        cloned.Clips.Single().VolumeKeyframes.Count != 2 ||
        cloned.Clips.Single().FilterStrengthKeyframes.Count != 2 ||
        !cloned.Clips.Single().NormalizeAudio ||
        !cloned.Clips.Single().NoiseReductionEnabled ||
        !cloned.AudioTracks.Single().DuckingEnabled ||
        !cloned.AudioTracks.Single().NoiseReductionEnabled ||
        Math.Abs(cloned.AudioTracks.Single().NormalizationGainDb - -2.25) > 0.0001 ||
        cloned.Settings.PreviewQuality != "720p" ||
        !cloned.Settings.ShowSafeAreaGuides ||
        cloned.Clips.Single().MaskShape != "rounded" ||
        Math.Abs(cloned.Clips.Single().MaskRoundness - 0.22) > 0.0001 ||
        cloned.Settings.TextKeyframes.Count != 2 ||
        cloned.Settings.TextFontFamily != "georgia" ||
        cloned.Settings.TextFontFilePath != customFontPath ||
        cloned.Settings.TextFontFamilyName != "Brand Sans" ||
        cloned.Settings.TextColor != "#12AB34" ||
        cloned.Settings.SubtitleFontFamily != "arial" ||
        cloned.Settings.SubtitleFontFilePath != customFontPath ||
        cloned.Settings.SubtitleFontFamilyName != "Brand Sans" ||
        cloned.Settings.SubtitleColor != "#FEDCBA" ||
        cloned.ExtraTextLayers.Single().Keyframes.Count != 2 ||
        cloned.ExtraTextLayers.Single().FontFamily != "trebuchet" ||
        cloned.ExtraTextLayers.Single().FontFilePath != customFontPath ||
        cloned.ExtraTextLayers.Single().FontFamilyName != "Brand Sans" ||
        cloned.ExtraTextLayers.Single().Color != "#A970FF" ||
        cloned.SubtitleLayers.Single().Origin != "transcription" ||
        cloned.SubtitleLayers.Single().FontFilePath != customFontPath ||
        cloned.SubtitleLayers.Single().FontFamilyName != "Brand Sans" ||
        cloned.SubtitleLayers.Single().SourceClipId != "transcription-source")
    {
        throw new InvalidOperationException("Marker or clip metadata changed during project cloning.");
    }

    var retrimmedClip = restoredClip.CloneWithNewId();
    retrimmedClip.TrimStartSeconds = 1;
    retrimmedClip.Normalize();
    if (retrimmedClip.NormalizeAudio || retrimmedClip.IntegratedLoudnessLufs is not null)
        throw new InvalidOperationException("A stale loudness analysis survived a trim change.");

    var mappedCaptions = WhisperTranscriptionService.MapCuesToTimeline(
        [
            new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(2), " First caption "),
            new SubtitleCue(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), "Second caption")
        ],
        clipTimelineStartSeconds: 5,
        playbackSpeed: 2,
        clipTimelineDurationSeconds: 4,
        projectDurationSeconds: 20);
    if (mappedCaptions.Count != 2 ||
        Math.Abs(mappedCaptions[0].Start.TotalSeconds - 5) > 0.0001 ||
        Math.Abs(mappedCaptions[0].End.TotalSeconds - 6) > 0.0001 ||
        mappedCaptions[0].Text != "First caption" ||
        Math.Abs(mappedCaptions[1].Start.TotalSeconds - 6) > 0.0001 ||
        Math.Abs(mappedCaptions[1].End.TotalSeconds - 9) > 0.0001)
    {
        throw new InvalidOperationException("Transcription cues were not mapped to the clip timeline correctly.");
    }

    Console.WriteLine($"version={restored.Version} preview={restored.Settings.PreviewQuality} markers={restored.Markers.Count} beats=valid transformKeys={restoredClip.TransformKeyframes.Count} volumeKeys={restoredClip.VolumeKeyframes.Count} filterKeys={restoredClip.FilterStrengthKeyframes.Count} textKeys={restored.Settings.TextKeyframes.Count} typography=valid captions=valid mask=valid safeArea=valid normalize=valid noiseReduction=valid trimInvalidation=valid ducking=valid clipColor={restoredClip.ColorLabel} clone=valid");
    Console.WriteLine(savedPath);
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--library-metadata-smoke", StringComparison.OrdinalIgnoreCase))
{
    var metadataDirectory = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(metadataDirectory);
    var metadataPath = Path.Combine(metadataDirectory, "library.metadata.json");
    var oldClipPath = Path.Combine(metadataDirectory, "old-name.mp4");
    var newClipPath = Path.Combine(metadataDirectory, "new-name.mp4");

    var service = new ClipLibraryMetadataService(metadataPath);
    var updated = service.Update(
        oldClipPath,
        isFavorite: true,
        tags: ["Gaming", "highlight", "gaming", "  tutorial  "],
        collection: "Best of 2026");
    if (!updated.IsFavorite ||
        updated.Tags.Count != 3 ||
        updated.Collection != "Best of 2026")
    {
        throw new InvalidOperationException("Library metadata normalization failed.");
    }

    var restored = new ClipLibraryMetadataService(metadataPath);
    var restoredMetadata = restored.Get(oldClipPath);
    if (!restoredMetadata.IsFavorite ||
        restoredMetadata.Tags.Count != 3 ||
        restored.GetCollections().Single() != "Best of 2026")
    {
        throw new InvalidOperationException("Library metadata persistence failed.");
    }

    restored.Move(oldClipPath, newClipPath);
    if (restored.Get(oldClipPath) != ClipLibraryMetadata.Empty ||
        !restored.Get(newClipPath).IsFavorite)
    {
        throw new InvalidOperationException("Library metadata did not follow a renamed clip.");
    }

    restored.Remove(newClipPath);
    var finalReload = new ClipLibraryMetadataService(metadataPath);
    if (finalReload.Get(newClipPath) != ClipLibraryMetadata.Empty ||
        File.Exists(metadataPath + ".tmp"))
    {
        throw new InvalidOperationException("Library metadata removal or atomic save cleanup failed.");
    }

    Console.WriteLine("favorite=restored tags=3 collection=restored rename=valid remove=valid atomic=valid");
    Console.WriteLine(metadataPath);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--preview-proxy-smoke", StringComparison.OrdinalIgnoreCase))
{
    var sourcePath = Path.GetFullPath(args[1]);
    var cacheDirectory = Path.GetFullPath(args[2]);
    if (!File.Exists(sourcePath))
        throw new FileNotFoundException("The proxy smoke source is missing.", sourcePath);
    Directory.CreateDirectory(cacheDirectory);
    var workingSourcePath = Path.Combine(cacheDirectory, "proxy-source.mp4");
    File.Copy(sourcePath, workingSourcePath, overwrite: true);

    var proxyService = new PreviewProxyService(Path.Combine(cacheDirectory, "proxies"));
    var progressValue = 0d;
    var proxyPath = await proxyService.GetOrCreateProxyAsync(
        workingSourcePath,
        "540p",
        new InlineProgress<double>(value => progressValue = value));
    var mediaProbe = new MediaProbeService();
    var sourceInfo = await mediaProbe.GetMediaInfoAsync(workingSourcePath)
        ?? throw new InvalidOperationException("Could not probe proxy smoke source.");
    var proxyInfo = await mediaProbe.GetMediaInfoAsync(proxyPath)
        ?? throw new InvalidOperationException("Could not probe generated preview proxy.");
    var firstWriteTime = File.GetLastWriteTimeUtc(proxyPath);
    var cachedPath = await proxyService.GetOrCreateProxyAsync(workingSourcePath, "540p");

    if (!string.Equals(proxyPath, cachedPath, StringComparison.OrdinalIgnoreCase) ||
        !proxyService.TryGetExistingProxy(workingSourcePath, "540p", out var existingPath) ||
        !string.Equals(proxyPath, existingPath, StringComparison.OrdinalIgnoreCase) ||
        File.GetLastWriteTimeUtc(proxyPath) != firstWriteTime ||
        proxyInfo.Height > Math.Min(540, sourceInfo.Height) + 2 ||
        Math.Abs(proxyInfo.Duration.TotalSeconds - sourceInfo.Duration.TotalSeconds) >
            Math.Max(0.35, sourceInfo.Duration.TotalSeconds * 0.01) ||
        progressValue < 0.99 ||
        !File.Exists(sourcePath) ||
        !File.Exists(workingSourcePath))
    {
        throw new InvalidOperationException("Preview proxy generation or cache reuse failed.");
    }

    if (!proxyService.DeleteProxy(workingSourcePath, "540p") || File.Exists(proxyPath))
        throw new InvalidOperationException("Preview proxy cleanup failed.");

    File.SetLastWriteTimeUtc(workingSourcePath, File.GetLastWriteTimeUtc(workingSourcePath).AddSeconds(2));
    var invalidatedPath = proxyService.GetProxyPath(workingSourcePath, "540p");
    if (string.Equals(proxyPath, invalidatedPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Preview proxy fingerprint did not change with its source.");

    var exportPath = Path.Combine(cacheDirectory, "export-from-original.mp4");
    var exportProject = new EditorProject
    {
        Name = "Proxy export source smoke",
        CanvasWidth = 640,
        CanvasHeight = 360,
        Settings = new EditorProjectSettings
        {
            PreviewQuality = "540p",
            ExportQuality = "draft",
            ExportFrameRate = 30,
            ExportResolutionMode = "canvas",
            ExportWidth = 640,
            ExportHeight = 360
        },
        Clips =
        [
            new EditorProjectClip
            {
                SourcePath = workingSourcePath,
                SourceDurationSeconds = sourceInfo.Duration.TotalSeconds,
                TrimEndSeconds = sourceInfo.Duration.TotalSeconds,
                Track = EditorClipTrack.Primary
            }
        ]
    };
    exportProject.Normalize();
    await new EditorProjectExportService().ExportAsync(exportProject, exportPath);
    var exportInfo = await mediaProbe.GetMediaInfoAsync(exportPath);
    if (exportInfo is null || exportInfo.Width != 640 || exportInfo.Height != 360)
        throw new InvalidOperationException("Project export did not use the original source independently of proxies.");

    Console.WriteLine($"proxy=valid source={sourceInfo.Width}x{sourceInfo.Height} preview={proxyInfo.Width}x{proxyInfo.Height} cache=reused invalidation=valid cleanup=valid exportSource=original");
    Console.WriteLine(proxyPath);
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--capture-health-smoke", StringComparison.OrdinalIgnoreCase))
{
    var storagePath = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(storagePath);
    var healthService = new CaptureHealthService();
    var actualStorage = healthService.GetStorageHealth([storagePath], 16);
    var criticalStorage = CaptureHealthService.EvaluateStorage(
        "C:\\",
        1L * 1024 * 1024 * 1024,
        100L * 1024 * 1024 * 1024,
        16);
    var warningStorage = CaptureHealthService.EvaluateStorage(
        "C:\\",
        5L * 1024 * 1024 * 1024,
        100L * 1024 * 1024 * 1024,
        16);
    var healthyStorage = CaptureHealthService.EvaluateStorage(
        "C:\\",
        50L * 1024 * 1024 * 1024,
        100L * 1024 * 1024 * 1024,
        16);

    var healthyRuntime = new CaptureRuntimeHealth(
        true, true, true, true, true,
        600, 600, 0, 0, 600,
        20, DateTime.UtcNow, 1920, 1080, 30, "CPU x264", null);
    var warningRuntime = healthyRuntime with
    {
        CapturedFrames = 100,
        DeliveredFrames = 90,
        DroppedFrames = 10,
        ActiveSeconds = 10
    };
    var stalledRuntime = healthyRuntime with
    {
        ActiveSeconds = 10,
        LastFrameUtc = DateTime.UtcNow - TimeSpan.FromSeconds(5)
    };

    if (actualStorage.FailureMessage is not null || actualStorage.AvailableBytes <= 0 ||
        criticalStorage.Severity != CaptureHealthSeverity.Critical ||
        warningStorage.Severity != CaptureHealthSeverity.Warning ||
        healthyStorage.Severity != CaptureHealthSeverity.Healthy ||
        healthyRuntime.Severity != CaptureHealthSeverity.Healthy ||
        warningRuntime.Severity != CaptureHealthSeverity.Warning ||
        warningRuntime.DropRate < 0.099 ||
        stalledRuntime.Severity != CaptureHealthSeverity.Critical ||
        !stalledRuntime.CaptureStalled)
    {
        throw new InvalidOperationException("Capture health thresholds or runtime telemetry evaluation failed.");
    }

    Console.WriteLine($"storage=valid free={actualStorage.AvailableBytes} critical=valid warning=valid healthy=valid runtime=valid drop={warningRuntime.DropRate:P1} stall=valid");
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--profiles-cache-smoke", StringComparison.OrdinalIgnoreCase))
{
    var smokeRoot = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(smokeRoot);
    var root = Path.Combine(smokeRoot, $"profiles-cache-smoke-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);

    try
    {
        var cacheService = new CacheMaintenanceService(root);
        var proxies = Path.Combine(root, "proxies");
        var thumbnails = Path.Combine(root, "thumbnails");
        var oldest = Path.Combine(proxies, "oldest.mp4");
        var middle = Path.Combine(proxies, "middle.mp4");
        var newest = Path.Combine(proxies, "newest.mp4");
        foreach (var path in new[] { oldest, middle, newest })
            await File.WriteAllBytesAsync(path, new byte[1024]);

        SetCacheAge(oldest, TimeSpan.FromDays(3));
        SetCacheAge(middle, TimeSpan.FromDays(2));
        SetCacheAge(newest, TimeSpan.FromDays(1));
        var firstCleanup = await cacheService.EnforceBudgetAsync(2200, TimeSpan.Zero);
        if (File.Exists(oldest) || !File.Exists(middle) || !File.Exists(newest) ||
            firstCleanup.DeletedFiles != 1 || firstCleanup.AfterBytes > 2200)
        {
            throw new InvalidOperationException("Cache budget did not evict the least recently used file.");
        }

        var abandonedTemporary = Path.Combine(thumbnails, ".abandoned.tmp.jpg");
        await File.WriteAllBytesAsync(abandonedTemporary, new byte[128]);
        SetCacheAge(abandonedTemporary, TimeSpan.FromDays(2));
        var temporaryCleanup = await cacheService.EnforceBudgetAsync(10_000, TimeSpan.Zero);
        var snapshot = await cacheService.InspectAsync();
        if (File.Exists(abandonedTemporary) || temporaryCleanup.DeletedFiles != 1 ||
            snapshot.FileCount != 2 || snapshot.TotalBytes != 2048)
        {
            throw new InvalidOperationException("Abandoned cache temporary files were not cleaned safely.");
        }

        var settings = new KlippSettings
        {
            AutoSelectCaptureApplicationProfiles = true,
            CaptureProfileId = "balanced"
        };
        var configured = new CaptureOptions
        {
            ResolutionMode = "720p",
            FrameRate = 24,
            BitrateMbps = 8,
            HideCaptureBorder = true
        };
        var application = new CaptureApplicationInfo("ExampleGame", "Example Game", "Example Game");
        CaptureProfileService.SaveApplicationProfile(
            settings,
            application,
            new CaptureOptions
            {
                ResolutionMode = "1080p",
                FrameRate = 60,
                BitrateMbps = 24,
                HideCaptureBorder = true
            },
            "Mjuk rörelse");

        var profileService = new CaptureProfileService();
        var matched = profileService.ResolveForApplication(
            settings,
            configured,
            application.ProcessName,
            application);
        var unmatched = profileService.ResolveForApplication(settings, configured, "OtherGame");
        settings.AutoSelectCaptureApplicationProfiles = false;
        var disabled = profileService.ResolveForApplication(settings, configured, application.ProcessName);
        if (!matched.IsApplicationSpecific || matched.Options.FrameRate != 60 ||
            matched.Options.ResolutionMode != "1080p" || matched.Options.BitrateMbps != 24 ||
            unmatched.IsApplicationSpecific || unmatched.Options.FrameRate != 24 ||
            disabled.IsApplicationSpecific || disabled.Options.FrameRate != 24 ||
            settings.CaptureApplicationProfiles.Count != 1)
        {
            throw new InvalidOperationException("Capture application profile resolution failed.");
        }

        Console.WriteLine(
            $"profiles=valid app={matched.Application?.ProcessName} fps={matched.Options.FrameRate} " +
            $"cache=lru-valid files={snapshot.FileCount} bytes={snapshot.TotalBytes} temp=clean");
        return 0;
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

if (args.Length == 2 && string.Equals(args[0], "--text-template-smoke", StringComparison.OrdinalIgnoreCase))
{
    var smokeRoot = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(smokeRoot);
    var root = Path.Combine(smokeRoot, $"text-template-smoke-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var templatePath = Path.Combine(root, "templates.json");

    try
    {
        var service = new EditorTextTemplateService(templatePath);
        var initial = await service.GetAllAsync();
        if (initial.Count != 6 || initial.Any(template => !template.IsBuiltIn))
            throw new InvalidOperationException("Built-in text templates were not loaded correctly.");

        var saved = await service.SaveCustomAsync(
            "Brand title",
            "neon",
            "slide-left",
            82,
            656,
            0.75,
            "georgia",
            "#12ab34");
        var reloaded = await new EditorTextTemplateService(templatePath).GetAllAsync();
        var restored = reloaded.SingleOrDefault(template => template.Id == saved.Id);
        if (reloaded.Count != 7 || restored is null || restored.IsBuiltIn ||
            restored.Style != "neon" || restored.Animation != "slide-left" ||
            restored.FontFamily != "georgia" || restored.Color != "#12AB34" ||
            restored.PreviewFontFamily != "Georgia" || restored.PreviewColor != "#12AB34" ||
            Math.Abs(restored.Size - 82) > 0.001 ||
            Math.Abs(restored.BoxWidth - 656) > 0.001 ||
            Math.Abs(restored.Opacity - 0.75) > 0.001)
        {
            throw new InvalidOperationException("Custom text template persistence failed.");
        }

        var updated = await service.SaveCustomAsync(
            "Brand title",
            "impact",
            "pop",
            96,
            720,
            1,
            "impact",
            "#fedcba");
        var afterUpdate = await service.GetAllAsync();
        if (updated.Id != saved.Id || afterUpdate.Count != 7 ||
            afterUpdate.Single(template => template.Id == saved.Id).Style != "impact" ||
            afterUpdate.Single(template => template.Id == saved.Id).FontFamily != "impact" ||
            afterUpdate.Single(template => template.Id == saved.Id).Color != "#FEDCBA")
        {
            throw new InvalidOperationException("Saving a named text template did not update it atomically.");
        }

        var normalized = await service.SaveCustomAsync(
            "Normalized",
            "not-a-style",
            "not-an-animation",
            999,
            20,
            -1,
            "not-a-font",
            "not-a-color");
        if (normalized.Style != "plain" || normalized.Animation != "none" ||
            normalized.FontFamily != EditorTextTypography.Automatic ||
            normalized.Color != EditorTextTypography.Automatic ||
            normalized.Size != 220 || normalized.BoxWidth != 96 || normalized.Opacity != 0)
        {
            throw new InvalidOperationException("Text template normalization failed.");
        }

        if (await service.DeleteCustomAsync(initial[0].Id) ||
            !await service.DeleteCustomAsync(saved.Id) ||
            !await service.DeleteCustomAsync(normalized.Id))
        {
            throw new InvalidOperationException("Text template deletion rules failed.");
        }

        var finalTemplates = await new EditorTextTemplateService(templatePath).GetAllAsync();
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(templatePath));
        if (finalTemplates.Count != 6 || File.Exists(templatePath + ".tmp") ||
            json.RootElement.GetProperty("Version").GetInt32() != 1 ||
            json.RootElement.GetProperty("Templates").GetArrayLength() != 0)
        {
            throw new InvalidOperationException("Text template cleanup or atomic document format failed.");
        }

        Console.WriteLine(
            $"templates=valid builtIn={initial.Count} custom=roundtrip update=valid " +
            $"typography=valid normalize=valid delete=valid atomic=valid");
        return 0;
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

if (args.Length == 3 && string.Equals(args[0], "--video-keyframe-export-smoke", StringComparison.OrdinalIgnoreCase))
{
    var sourcePath = Path.GetFullPath(args[1]);
    var keyframeOutputPath = Path.GetFullPath(args[2]);
    if (!File.Exists(sourcePath))
        throw new FileNotFoundException("The keyframe smoke source is missing.", sourcePath);
    Directory.CreateDirectory(Path.GetDirectoryName(keyframeOutputPath)!);
    var keyframeProject = new EditorProject
    {
        Name = "Video keyframe export smoke",
        CanvasWidth = 320,
        CanvasHeight = 180,
        Settings = new EditorProjectSettings
        {
            ExportQuality = "draft",
            ExportFrameRate = 30,
            ExportEncoder = "software",
            ExportBitrateMbps = 4,
            ExportResolutionMode = "canvas",
            ExportWidth = 320,
            ExportHeight = 180,
            ExportAudioBitrateKbps = 96
        },
        Clips =
        [
            new EditorProjectClip
            {
                SourcePath = sourcePath,
                Name = "Animated source",
                SourceDurationSeconds = 4,
                TrimEndSeconds = 4,
                Track = EditorClipTrack.Primary,
                TransformKeyframes =
                [
                    new EditorProjectClipTransformKeyframe
                    {
                        TimeSeconds = 0,
                        Scale = 0.55,
                        OffsetX = -1,
                        OffsetY = -1,
                        Easing = "linear"
                    },
                    new EditorProjectClipTransformKeyframe
                    {
                        TimeSeconds = 2,
                        Scale = 1,
                        OffsetX = 0,
                        OffsetY = 0,
                        Easing = "ease-in-out"
                    },
                    new EditorProjectClipTransformKeyframe
                    {
                        TimeSeconds = 4,
                        Scale = 1.7,
                        OffsetX = 1,
                        OffsetY = 1,
                        Easing = "ease-in-out"
                    }
                ]
            }
        ]
    };

    keyframeProject.Normalize();
    await new EditorProjectExportService().ExportAsync(keyframeProject, keyframeOutputPath);
    if (!File.Exists(keyframeOutputPath))
        throw new InvalidOperationException("The video keyframe export did not create an output file.");
    Console.WriteLine($"status=Completed transformKeys={keyframeProject.Clips[0].TransformKeyframes.Count}");
    Console.WriteLine(keyframeOutputPath);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--video-keyframe-overlay-smoke", StringComparison.OrdinalIgnoreCase))
{
    var sourcePath = Path.GetFullPath(args[1]);
    var overlayOutputPath = Path.GetFullPath(args[2]);
    if (!File.Exists(sourcePath))
        throw new FileNotFoundException("The overlay keyframe smoke source is missing.", sourcePath);
    Directory.CreateDirectory(Path.GetDirectoryName(overlayOutputPath)!);
    var overlayProject = new EditorProject
    {
        Name = "Video overlay keyframe export smoke",
        CanvasWidth = 320,
        CanvasHeight = 180,
        Settings = new EditorProjectSettings
        {
            ExportQuality = "draft",
            ExportFrameRate = 30,
            ExportEncoder = "software",
            ExportBitrateMbps = 4,
            ExportResolutionMode = "canvas",
            ExportWidth = 320,
            ExportHeight = 180,
            ExportAudioBitrateKbps = 96
        },
        Clips =
        [
            new EditorProjectClip
            {
                SourcePath = sourcePath,
                Name = "Background",
                SourceDurationSeconds = 4,
                TrimEndSeconds = 4,
                Track = EditorClipTrack.Primary
            },
            new EditorProjectClip
            {
                SourcePath = sourcePath,
                Name = "Animated B-roll",
                SourceDurationSeconds = 4,
                TrimEndSeconds = 3,
                TimelineStartSeconds = 0.5,
                Track = EditorClipTrack.Overlay,
                TransformKeyframes =
                [
                    new EditorProjectClipTransformKeyframe
                    {
                        TimeSeconds = 0,
                        Scale = 0.25,
                        OffsetX = -1,
                        OffsetY = -1,
                        Easing = "linear"
                    },
                    new EditorProjectClipTransformKeyframe
                    {
                        TimeSeconds = 3,
                        Scale = 0.7,
                        OffsetX = 1,
                        OffsetY = 1,
                        Easing = "ease-in-out"
                    }
                ]
            }
        ]
    };

    overlayProject.Normalize();
    await new EditorProjectExportService().ExportAsync(overlayProject, overlayOutputPath);
    if (!File.Exists(overlayOutputPath))
        throw new InvalidOperationException("The overlay keyframe export did not create an output file.");
    Console.WriteLine($"status=Completed transformKeys={overlayProject.Clips[1].TransformKeyframes.Count}");
    Console.WriteLine(overlayOutputPath);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--custom-font-export-smoke", StringComparison.OrdinalIgnoreCase))
{
    var fontPath = Path.GetFullPath(args[1]);
    var fontOutputPath = Path.GetFullPath(args[2]);
    if (!File.Exists(fontPath))
        throw new FileNotFoundException("The custom font smoke file is missing.", fontPath);

    var fontInfo = await OpenTypeFontInspector.InspectAsync(fontPath);
    var resolvedFontPath = ClipEditService.ResolveFontFileForExport("arial", "plain", fontPath);
    if (!string.Equals(resolvedFontPath, fontPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The export pipeline did not select the custom font file.");

    Directory.CreateDirectory(Path.GetDirectoryName(fontOutputPath)!);
    var sourcePath = Path.Combine(Path.GetTempPath(), $"klipp-custom-font-{Guid.NewGuid():N}.mp4");
    try
    {
        var ffmpegPath = await new Klipp.Encoding.FFmpeg.FFmpegLocator().GetFFmpegPathAsync();
        await RunProcessAsync(ffmpegPath,
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=#202020:s=640x360:d=1:r=30",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
            sourcePath
        ]);

        var options = new ClipEditOptions(
            textOverlay: "BRAND FONT",
            textStyle: "plain",
            textPosition: "custom",
            textEnabled: true,
            textStart: TimeSpan.Zero,
            textEnd: TimeSpan.FromSeconds(1),
            textAnchorX: 0.5,
            textAnchorY: 0.5,
            textSize: 52,
            outputWidth: 640,
            outputHeight: 360,
            textFontFamily: "arial",
            textColor: "#FFFFFF",
            textFontFilePath: fontPath,
            textFontFamilyName: fontInfo.FamilyName);
        await new ClipEditService().TrimAsync(
            sourcePath,
            fontOutputPath,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            options);

        var outputInfo = await new MediaProbeService().GetMediaInfoAsync(fontOutputPath)
            ?? throw new InvalidOperationException("Could not probe the custom font export.");
        if (outputInfo.Duration.TotalSeconds is < 0.8 or > 1.2 ||
            new FileInfo(fontOutputPath).Length < 1_000)
        {
            throw new InvalidOperationException("The custom font export is incomplete.");
        }

        Console.WriteLine(
            $"status=Completed family={fontInfo.FamilyName} font={resolvedFontPath} duration={outputInfo.Duration.TotalSeconds:0.00}s");
        Console.WriteLine(fontOutputPath);
    }
    finally
    {
        try { File.Delete(sourcePath); } catch { }
    }
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--render-cache-smoke", StringComparison.OrdinalIgnoreCase))
{
    var smokeParent = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(smokeParent);
    var smokeRoot = Path.Combine(smokeParent, $"render-cache-smoke-{Guid.NewGuid():N}");
    var cacheDirectory = Path.Combine(smokeRoot, "render-cache");
    var sourcePath = Path.Combine(smokeRoot, "source.mp4");
    var firstOutputPath = Path.Combine(smokeRoot, "first.mp4");
    var secondOutputPath = Path.Combine(smokeRoot, "second.mp4");
    var thirdOutputPath = Path.Combine(smokeRoot, "third.mp4");
    Directory.CreateDirectory(smokeRoot);

    try
    {
        var locator = new Klipp.Encoding.FFmpeg.FFmpegLocator();
        var ffmpegPath = await locator.GetFFmpegPathAsync();
        await RunProcessAsync(ffmpegPath,
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=12",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=12",
            "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "96k", sourcePath
        ]);

        var cacheProject = new EditorProject
        {
            Name = "Render cache smoke",
            CanvasWidth = 320,
            CanvasHeight = 180,
            Settings = new EditorProjectSettings
            {
                ExportQuality = "draft",
                ExportFrameRate = 30,
                ExportEncoder = "software",
                ExportBitrateMbps = 4,
                ExportResolutionMode = "canvas",
                ExportWidth = 320,
                ExportHeight = 180,
                ExportAudioBitrateKbps = 96
            },
            Clips =
            [
                new EditorProjectClip
                {
                    SourcePath = sourcePath,
                    Name = "Primary A",
                    SourceDurationSeconds = 12,
                    TrimEndSeconds = 8.5,
                    Track = EditorClipTrack.Primary
                },
                new EditorProjectClip
                {
                    SourcePath = sourcePath,
                    Name = "Primary B",
                    SourceDurationSeconds = 12,
                    TrimStartSeconds = 2,
                    TrimEndSeconds = 6.5,
                    Track = EditorClipTrack.Primary,
                    TransitionInStyle = "dissolve",
                    TransitionInDurationSeconds = 1
                }
            ],
            AudioTracks =
            [
                new EditorProjectAudioTrack
                {
                    SourcePath = sourcePath,
                    Name = "Late music",
                    SourceDurationSeconds = 12,
                    TrimStartSeconds = 0,
                    TrimEndSeconds = 2,
                    TimelineStartSeconds = 9,
                    Volume = 0.8
                }
            ]
        };
        cacheProject.Normalize();

        var mediaProbe = new MediaProbeService(locator);
        var renderCache = new EditorTimelineRenderCacheService(cacheDirectory, mediaProbe);
        var exporter = new EditorProjectExportService(locator, renderCache);

        await exporter.ExportAsync(cacheProject, firstOutputPath);
        var firstRun = exporter.LastRenderCacheRun;
        if (firstRun.SegmentCount != 2 || firstRun.CacheHits != 0 || firstRun.CacheMisses != 2)
        {
            throw new InvalidOperationException(
                $"Cold render cache run was {firstRun.CacheHits} hits/{firstRun.CacheMisses} misses.");
        }

        await exporter.ExportAsync(cacheProject, secondOutputPath);
        var secondRun = exporter.LastRenderCacheRun;
        if (secondRun.SegmentCount != 2 || secondRun.CacheHits != 2 || secondRun.CacheMisses != 0)
        {
            throw new InvalidOperationException(
                $"Warm render cache run was {secondRun.CacheHits} hits/{secondRun.CacheMisses} misses.");
        }

        cacheProject.AudioTracks.Single().Volume = 0.35;
        cacheProject.Normalize();
        await exporter.ExportAsync(cacheProject, thirdOutputPath);
        var thirdRun = exporter.LastRenderCacheRun;
        if (thirdRun.SegmentCount != 2 || thirdRun.CacheHits != 1 || thirdRun.CacheMisses != 1)
        {
            throw new InvalidOperationException(
                $"Selective invalidation was {thirdRun.CacheHits} hits/{thirdRun.CacheMisses} misses.");
        }

        var outputInfo = await mediaProbe.GetMediaInfoAsync(thirdOutputPath)
            ?? throw new InvalidOperationException("Could not probe the render cache smoke output.");
        var cacheFiles = Directory.EnumerateFiles(cacheDirectory, "*.mp4").ToList();
        var temporaryFiles = Directory.EnumerateFiles(cacheDirectory, "*.tmp.*").ToList();
        if (outputInfo.Width != 320 || outputInfo.Height != 180 ||
            outputInfo.Duration.TotalSeconds is < 11.6 or > 12.4 ||
            cacheFiles.Count != 3 || temporaryFiles.Count != 0)
        {
            throw new InvalidOperationException(
                $"Render cache output or atomic cleanup failed: {outputInfo.Width}x{outputInfo.Height}, " +
                $"{outputInfo.Duration.TotalSeconds:0.00}s, files={cacheFiles.Count}, temp={temporaryFiles.Count}.");
        }

        var snapshot = await new CacheMaintenanceService(smokeRoot).InspectAsync();
        var renderSnapshot = snapshot.Directories.Single(directory => directory.Name == "Renders");
        if (renderSnapshot.FileCount != 3 || renderSnapshot.SizeBytes <= 3_000)
            throw new InvalidOperationException("Render cache was not included in cache diagnostics.");

        Console.WriteLine(
            $"cold=0/2 warm=2/0 selective=1/1 segments=2 cacheFiles={cacheFiles.Count} " +
            $"duration={outputInfo.Duration.TotalSeconds:0.00}s diagnostics=valid atomic=valid");
    }
    finally
    {
        try { Directory.Delete(smokeRoot, recursive: true); } catch { }
    }
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--text-keyframe-export-smoke", StringComparison.OrdinalIgnoreCase))
{
    var sourcePath = Path.GetFullPath(args[1]);
    var textOutputPath = Path.GetFullPath(args[2]);
    if (!File.Exists(sourcePath))
        throw new FileNotFoundException("The text keyframe smoke source is missing.", sourcePath);
    Directory.CreateDirectory(Path.GetDirectoryName(textOutputPath)!);
    var textProject = new EditorProject
    {
        Name = "Text keyframe export smoke",
        CanvasWidth = 320,
        CanvasHeight = 180,
        Settings = new EditorProjectSettings
        {
            ExportQuality = "draft",
            ExportFrameRate = 30,
            ExportEncoder = "software",
            ExportBitrateMbps = 4,
            ExportResolutionMode = "canvas",
            ExportWidth = 320,
            ExportHeight = 180,
            ExportAudioBitrateKbps = 96,
            Text = "KLIPP",
            TextStyle = "caption",
            TextAnimation = "none",
            TextEnabled = true,
            TextStartSeconds = 0,
            TextEndSeconds = 4,
            TextBoxWidth = 150,
            TextKeyframes =
            [
                new EditorProjectTextKeyframe
                {
                    TimeSeconds = 0,
                    AnchorX = 0.18,
                    AnchorY = 0.2,
                    Size = 20,
                    Opacity = 0.3
                },
                new EditorProjectTextKeyframe
                {
                    TimeSeconds = 2,
                    AnchorX = 0.5,
                    AnchorY = 0.5,
                    Size = 46,
                    Opacity = 1,
                    Easing = "ease-in-out"
                },
                new EditorProjectTextKeyframe
                {
                    TimeSeconds = 4,
                    AnchorX = 0.82,
                    AnchorY = 0.8,
                    Size = 26,
                    Opacity = 0.45,
                    Easing = "ease-out"
                }
            ]
        },
        Clips =
        [
            new EditorProjectClip
            {
                SourcePath = sourcePath,
                Name = "Background",
                SourceDurationSeconds = 4,
                TrimEndSeconds = 4,
                Track = EditorClipTrack.Primary
            }
        ]
    };

    textProject.Normalize();
    await new EditorProjectExportService().ExportAsync(textProject, textOutputPath);
    if (!File.Exists(textOutputPath))
        throw new InvalidOperationException("The text keyframe export did not create an output file.");
    Console.WriteLine($"status=Completed textKeys={textProject.Settings.TextKeyframes.Count}");
    Console.WriteLine(textOutputPath);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--volume-keyframe-export-smoke", StringComparison.OrdinalIgnoreCase))
{
    var sourcePath = Path.GetFullPath(args[1]);
    var volumeOutputPath = Path.GetFullPath(args[2]);
    if (!File.Exists(sourcePath))
        throw new FileNotFoundException("The volume keyframe smoke source is missing.", sourcePath);
    Directory.CreateDirectory(Path.GetDirectoryName(volumeOutputPath)!);
    var volumeProject = new EditorProject
    {
        Name = "Volume keyframe export smoke",
        CanvasWidth = 320,
        CanvasHeight = 180,
        Settings = new EditorProjectSettings
        {
            ExportQuality = "draft",
            ExportFrameRate = 30,
            ExportEncoder = "software",
            ExportBitrateMbps = 4,
            ExportResolutionMode = "canvas",
            ExportWidth = 320,
            ExportHeight = 180,
            ExportAudioBitrateKbps = 192
        },
        Clips =
        [
            new EditorProjectClip
            {
                SourcePath = sourcePath,
                Name = "Automated audio",
                SourceDurationSeconds = 4,
                TrimEndSeconds = 4,
                Track = EditorClipTrack.Primary,
                VolumeKeyframes =
                [
                    new EditorProjectClipVolumeKeyframe
                    {
                        TimeSeconds = 0,
                        Volume = 0.1,
                        Easing = "linear"
                    },
                    new EditorProjectClipVolumeKeyframe
                    {
                        TimeSeconds = 2,
                        Volume = 1,
                        Easing = "ease-in-out"
                    },
                    new EditorProjectClipVolumeKeyframe
                    {
                        TimeSeconds = 4,
                        Volume = 0.1,
                        Easing = "ease-in-out"
                    }
                ]
            }
        ]
    };

    volumeProject.Normalize();
    await new EditorProjectExportService().ExportAsync(volumeProject, volumeOutputPath);
    if (!File.Exists(volumeOutputPath))
        throw new InvalidOperationException("The volume keyframe export did not create an output file.");
    Console.WriteLine($"status=Completed volumeKeys={volumeProject.Clips[0].VolumeKeyframes.Count}");
    Console.WriteLine(volumeOutputPath);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--filter-keyframe-export-smoke", StringComparison.OrdinalIgnoreCase))
{
    var sourcePath = Path.GetFullPath(args[1]);
    var filterOutputPath = Path.GetFullPath(args[2]);
    if (!File.Exists(sourcePath))
        throw new FileNotFoundException("The filter keyframe smoke source is missing.", sourcePath);
    Directory.CreateDirectory(Path.GetDirectoryName(filterOutputPath)!);
    var filterProject = new EditorProject
    {
        Name = "Filter keyframe export smoke",
        CanvasWidth = 320,
        CanvasHeight = 180,
        Settings = new EditorProjectSettings
        {
            ExportQuality = "draft",
            ExportFrameRate = 30,
            ExportEncoder = "software",
            ExportBitrateMbps = 4,
            ExportResolutionMode = "canvas",
            ExportWidth = 320,
            ExportHeight = 180,
            ExportAudioBitrateKbps = 96
        },
        Clips =
        [
            new EditorProjectClip
            {
                SourcePath = sourcePath,
                Name = "Animated mono mix",
                SourceDurationSeconds = 4,
                TrimEndSeconds = 4,
                Track = EditorClipTrack.Primary,
                Filter = "mono",
                FilterStrengthKeyframes =
                [
                    new EditorProjectClipFilterKeyframe
                    {
                        TimeSeconds = 0,
                        Strength = 0,
                        Easing = "linear"
                    },
                    new EditorProjectClipFilterKeyframe
                    {
                        TimeSeconds = 2,
                        Strength = 1,
                        Easing = "ease-in-out"
                    },
                    new EditorProjectClipFilterKeyframe
                    {
                        TimeSeconds = 4,
                        Strength = 0,
                        Easing = "ease-in-out"
                    }
                ]
            }
        ]
    };

    filterProject.Normalize();
    await new EditorProjectExportService().ExportAsync(filterProject, filterOutputPath);
    if (!File.Exists(filterOutputPath))
        throw new InvalidOperationException("The filter keyframe export did not create an output file.");
    Console.WriteLine($"status=Completed filterKeys={filterProject.Clips[0].FilterStrengthKeyframes.Count}");
    Console.WriteLine(filterOutputPath);
    return 0;
}

if (args.Length == 4 && string.Equals(args[0], "--queue-smoke", StringComparison.OrdinalIgnoreCase))
{
    var queuedProject = await LoadProjectAsync(args[1]);
    var queuedOutputPath = Path.GetFullPath(args[2]);
    var queueDirectory = Path.GetFullPath(args[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(queuedOutputPath)!);

    var maximumObservedProgress = 0.0;
    var observedIntermediateProgress = false;
    string jobId;
    using (var queue = new ExportQueueService(queueDirectory: queueDirectory))
    {
        queue.JobsChanged += (_, _) =>
        {
            maximumObservedProgress = Math.Max(
                maximumObservedProgress,
                queue.Jobs.Select(job => job.Progress).DefaultIfEmpty(0).Max());
            observedIntermediateProgress |= queue.Jobs.Any(job => job.Progress is > 0.001 and < 0.999);
        };
        await queue.InitializeAsync();
        var job = await queue.EnqueueAsync(queuedProject, queuedOutputPath);
        jobId = job.Id;

        await WaitForStatusAsync(queue, jobId, EditorExportJobStatus.Rendering, TimeSpan.FromSeconds(20));
        if (!await queue.CancelAsync(jobId))
            throw new InvalidOperationException("The running queue job could not be cancelled.");
        await WaitForStatusAsync(queue, jobId, EditorExportJobStatus.Cancelled, TimeSpan.FromSeconds(20));

        if (!await queue.RetryAsync(jobId))
            throw new InvalidOperationException("The cancelled queue job could not be retried.");
        var completed = await WaitForTerminalStatusAsync(queue, jobId, TimeSpan.FromMinutes(3));
        if (completed.Status != EditorExportJobStatus.Completed)
            throw new InvalidOperationException($"Retried queue job ended as {completed.Status}: {completed.ErrorMessage}");
        if (!File.Exists(completed.OutputPath))
            throw new InvalidOperationException("The completed queue output is missing.");
        if (!observedIntermediateProgress)
            throw new InvalidOperationException("The queue did not report intermediate FFmpeg progress.");
    }

    using (var restoredQueue = new ExportQueueService(queueDirectory: queueDirectory))
    {
        await restoredQueue.InitializeAsync();
        var restored = restoredQueue.Jobs.FirstOrDefault(job => job.Id == jobId)
            ?? throw new InvalidOperationException("The completed queue job was not restored from history.");
        if (restored.Status != EditorExportJobStatus.Completed)
            throw new InvalidOperationException($"Restored queue status was {restored.Status}.");
    }

    Console.WriteLine($"status=Completed progress={maximumObservedProgress:0.000} intermediate=true history=restored");
    Console.WriteLine(queuedOutputPath);
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--audio-production-smoke", StringComparison.OrdinalIgnoreCase))
{
    var smokeParent = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(smokeParent);
    var smokeRoot = Path.Combine(smokeParent, $"audio-production-smoke-{Guid.NewGuid():N}");
    Directory.CreateDirectory(smokeRoot);
    try
    {
        var ffmpegPath = await new Klipp.Encoding.FFmpeg.FFmpegLocator().GetFFmpegPathAsync();
        var primaryPath = Path.Combine(smokeRoot, "primary.mp4");
        var pipPath = Path.Combine(smokeRoot, "pip.mp4");
        var musicPath = Path.Combine(smokeRoot, "music.wav");
        var audioOutputPath = Path.Combine(smokeRoot, "normalized-ducked.mp4");
        var framePath = Path.Combine(smokeRoot, "pip-frame.rgb");
        await RunProcessAsync(ffmpegPath,
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=0x202020:s=640x360:r=30:d=3",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=3",
            "-filter:a", "volume=0.08", "-shortest",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", primaryPath
        ]);
        await RunProcessAsync(ffmpegPath,
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=0xFF2020:s=640x360:r=30:d=3",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", pipPath
        ]);
        await RunProcessAsync(ffmpegPath,
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "anoisesrc=color=white:amplitude=0.02:sample_rate=48000:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=120:sample_rate=48000:duration=3",
            "-filter_complex", "[1:a]volume='if(lt(mod(t,0.5),0.06),0.9,0)':eval=frame[pulses];[0:a][pulses]amix=inputs=2:normalize=0[a]",
            "-map", "[a]", "-c:a", "pcm_s16le", musicPath
        ]);

        var signalAnalyzer = new AudioSignalAnalysisService(
            cacheDirectory: Path.Combine(smokeRoot, "audio-analysis"));
        var signalAnalysis = await signalAnalyzer.AnalyzeAsync(musicPath, 0, 3)
            ?? throw new InvalidOperationException("Audio signal analysis failed.");
        var cachedSignalAnalysis = await signalAnalyzer.AnalyzeAsync(musicPath, 0, 3)
            ?? throw new InvalidOperationException("Cached audio signal analysis failed.");
        if (signalAnalysis.Frames.Count < 40 ||
            signalAnalysis.BeatSeconds.Count < 3 ||
            signalAnalysis.BeatSeconds.Count > 8 ||
            signalAnalysis.Frames.Max(frame => Math.Max(frame.PeakLeft, frame.PeakRight)) < 0.08 ||
            cachedSignalAnalysis.BeatSeconds.Count != signalAnalysis.BeatSeconds.Count)
        {
            throw new InvalidOperationException(
                $"Audio signal analysis was invalid: frames={signalAnalysis.Frames.Count}, beats={signalAnalysis.BeatSeconds.Count}.");
        }

        const string parserSample = "loudnorm output\n{\"input_i\":\"-30.0\",\"input_tp\":\"-0.5\"}";
        if (!AudioLoudnessAnalysisService.TryParseAnalysis(parserSample, out var parsed) ||
            Math.Abs(parsed.GainDb - -1) > 0.0001)
        {
            throw new InvalidOperationException("Peak-safe loudness parsing failed.");
        }

        var analyzer = new AudioLoudnessAnalysisService();
        var primaryAnalysis = await analyzer.AnalyzeAsync(primaryPath, 0, 3)
            ?? throw new InvalidOperationException("Primary loudness analysis failed.");
        var musicAnalysis = await analyzer.AnalyzeAsync(musicPath, 0, 3)
            ?? throw new InvalidOperationException("Music loudness analysis failed.");
        var audioProject = new EditorProject
        {
            Name = "Audio production smoke",
            CanvasWidth = 640,
            CanvasHeight = 360,
            Settings = new EditorProjectSettings
            {
                ExportEncoder = "software",
                ExportFrameRate = 30,
                ExportBitrateMbps = 4,
                ExportAudioBitrateKbps = 128,
                ShowSafeAreaGuides = true,
                Text = "Typography",
                TextEnabled = true,
                TextStartSeconds = 0,
                TextEndSeconds = 3,
                TextStyle = "bold",
                TextFontFamily = "georgia",
                TextColor = "#12ab34"
            },
            Clips =
            [
                new EditorProjectClip
                {
                    Name = "Voice",
                    SourcePath = primaryPath,
                    SourceDurationSeconds = 3,
                    TrimEndSeconds = 3,
                    Track = EditorClipTrack.Primary,
                    NormalizeAudio = true,
                    NoiseReductionEnabled = true,
                    NoiseReductionStrength = 0.35,
                    NormalizationGainDb = primaryAnalysis.GainDb,
                    IntegratedLoudnessLufs = primaryAnalysis.IntegratedLoudnessLufs,
                    LoudnessAnalysisTrimEndSeconds = 3
                },
                new EditorProjectClip
                {
                    Name = "Rounded PIP",
                    SourcePath = pipPath,
                    SourceDurationSeconds = 3,
                    TrimEndSeconds = 2.5,
                    TimelineStartSeconds = 0.25,
                    Track = EditorClipTrack.Overlay,
                    MaskShape = "rounded",
                    MaskRoundness = 0.24,
                    TransformKeyframes =
                    [
                        new EditorProjectClipTransformKeyframe
                        {
                            TimeSeconds = 0,
                            Scale = 0.36,
                            OffsetX = 0.92,
                            OffsetY = -0.92,
                            Easing = "linear"
                        },
                        new EditorProjectClipTransformKeyframe
                        {
                            TimeSeconds = 2.5,
                            Scale = 0.36,
                            OffsetX = 0.92,
                            OffsetY = 0.92,
                            Easing = "ease-in-out"
                        }
                    ]
                }
            ],
            AudioTracks =
            [
                new EditorProjectAudioTrack
                {
                    Name = "Music",
                    SourcePath = musicPath,
                    SourceDurationSeconds = 3,
                    TrimEndSeconds = 3,
                    NormalizeAudio = true,
                    NormalizationGainDb = musicAnalysis.GainDb,
                    IntegratedLoudnessLufs = musicAnalysis.IntegratedLoudnessLufs,
                    LoudnessAnalysisTrimEndSeconds = 3,
                    DuckingEnabled = true,
                    DuckingStrength = 0.75,
                    NoiseReductionEnabled = true,
                    NoiseReductionStrength = 0.6
                }
            ],
            Markers = signalAnalysis.BeatSeconds.Select(seconds => new EditorProjectMarker
            {
                TimeSeconds = seconds,
                Label = "Beat · Music",
                ColorLabel = "teal",
                Kind = "beat"
            }).ToList()
        };

        var projectService = new EditorProjectService(Path.Combine(smokeRoot, "projects"));
        var savedProjectPath = await projectService.SaveNamedProjectAsync(audioProject);
        var restored = await projectService.TryLoadAsync(savedProjectPath)
            ?? throw new InvalidOperationException("Audio project persistence failed.");
        var restoredPrimary = restored.PrimaryClips.Single();
        var restoredPip = restored.OverlayClips.Single();
        if (!restoredPrimary.NormalizeAudio ||
            !restoredPrimary.NoiseReductionEnabled ||
            Math.Abs(restoredPrimary.NoiseReductionStrength - 0.35) > 0.0001 ||
            !restored.AudioTracks.Single().NormalizeAudio ||
            !restored.AudioTracks.Single().DuckingEnabled ||
            Math.Abs(restored.AudioTracks.Single().DuckingStrength - 0.75) > 0.0001 ||
            !restored.AudioTracks.Single().NoiseReductionEnabled ||
            Math.Abs(restored.AudioTracks.Single().NoiseReductionStrength - 0.6) > 0.0001 ||
            restored.Markers.Count != signalAnalysis.BeatSeconds.Count ||
            restored.Markers.Any(marker => marker.Kind != "beat") ||
            restored.Settings.TextFontFamily != "georgia" ||
            restored.Settings.TextColor != "#12AB34" ||
            !restored.Settings.ShowSafeAreaGuides ||
            restoredPip.MaskShape != "rounded" ||
            Math.Abs(restoredPip.MaskRoundness - 0.24) > 0.0001 ||
            restoredPip.TransformKeyframes.Count != 2)
        {
            throw new InvalidOperationException("Audio production settings changed during persistence.");
        }

        await new EditorProjectExportService().ExportAsync(restored, audioOutputPath);
        var outputInfo = await new MediaProbeService().GetMediaInfoAsync(audioOutputPath)
            ?? throw new InvalidOperationException("Could not probe the audio production export.");
        if (outputInfo.AudioCodecs.Count == 0 ||
            outputInfo.Duration.TotalSeconds is < 2.5 or > 3.5 ||
            new FileInfo(audioOutputPath).Length < 1_000)
        {
            throw new InvalidOperationException("The audio production export is incomplete.");
        }

        await RunProcessAsync(ffmpegPath,
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-ss", "0.30", "-i", audioOutputPath,
            "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", framePath
        ]);
        var frame = await File.ReadAllBytesAsync(framePath);
        var center = ReadRgbPixel(frame, 640, 508, 74);
        var roundedCorner = ReadRgbPixel(frame, 640, 395, 11);
        if (center.R < 150 || center.R < center.G * 2 || center.R < center.B * 2 ||
            roundedCorner.R > 90 || roundedCorner.G > 90 || roundedCorner.B > 90)
        {
            throw new InvalidOperationException(
                $"Rounded PIP pixel validation failed. Center={center}, corner={roundedCorner}.");
        }

        Console.WriteLine(
            $"version={restored.Version} analysis=valid meterFrames={signalAnalysis.Frames.Count} beats={signalAnalysis.BeatSeconds.Count} cache=valid peakSafe=valid normalize=valid noiseReduction=afftdn ducking=sidechain typography=valid pipMask=pixels-valid safeArea=valid export=valid duration={outputInfo.Duration.TotalSeconds:0.00}s");
    }
    finally
    {
        try { Directory.Delete(smokeRoot, recursive: true); } catch { }
    }
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Klipp.ProjectExportHarness <project.klippproject> <output.mp4>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --queue-smoke <project> <output.mp4> <queue-directory>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --project-model-smoke <projects-directory>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --library-metadata-smoke <metadata-directory>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --preview-proxy-smoke <source.mp4> <cache-directory>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --capture-health-smoke <storage-directory>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --profiles-cache-smoke <temporary-directory>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --text-template-smoke <temporary-directory>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --audio-production-smoke <temporary-directory>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --custom-font-export-smoke <font.ttf> <output.mp4>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --render-cache-smoke <temporary-directory>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --video-keyframe-export-smoke <source.mp4> <output.mp4>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --video-keyframe-overlay-smoke <source.mp4> <output.mp4>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --text-keyframe-export-smoke <source.mp4> <output.mp4>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --volume-keyframe-export-smoke <source.mp4> <output.mp4>");
    Console.Error.WriteLine("   or: Klipp.ProjectExportHarness --filter-keyframe-export-smoke <source.mp4> <output.mp4>");
    return 2;
}

var project = await LoadProjectAsync(args[0]);
var outputPath = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await new EditorProjectExportService().ExportAsync(project, outputPath);
Console.WriteLine(outputPath);
return 0;

static (byte R, byte G, byte B) ReadRgbPixel(byte[] frame, int width, int x, int y)
{
    var offset = checked((y * width + x) * 3);
    if (offset < 0 || offset + 2 >= frame.Length)
        throw new InvalidOperationException("The rendered RGB frame is smaller than expected.");
    return (frame[offset], frame[offset + 1], frame[offset + 2]);
}

static void SetCacheAge(string path, TimeSpan age)
{
    var timestamp = DateTime.UtcNow - age;
    File.SetLastWriteTimeUtc(path, timestamp);
    File.SetLastAccessTimeUtc(path, timestamp);
}

static async Task RunProcessAsync(string executablePath, IReadOnlyList<string> arguments)
{
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = executablePath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);

    using var process = System.Diagnostics.Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(executablePath)}.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await outputTask;
    var error = await errorTask;
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{Path.GetFileName(executablePath)} failed: {error}\n{output}");
}

static async Task<EditorProject> LoadProjectAsync(string path)
{
    var projectPath = Path.GetFullPath(path);
    if (!File.Exists(projectPath))
        throw new FileNotFoundException($"Project not found: {projectPath}", projectPath);

    var json = await File.ReadAllTextAsync(projectPath);
    return JsonSerializer.Deserialize<EditorProject>(json, new JsonSerializerOptions
           {
               PropertyNameCaseInsensitive = true
           })
           ?? throw new InvalidOperationException($"Could not read project: {projectPath}");
}

static async Task WaitForStatusAsync(
    ExportQueueService queue,
    string jobId,
    EditorExportJobStatus expected,
    TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var job = queue.Jobs.FirstOrDefault(candidate => candidate.Id == jobId);
        if (job?.Status == expected)
            return;
        if (job?.Status is EditorExportJobStatus.Failed or EditorExportJobStatus.Completed)
            throw new InvalidOperationException($"Queue job reached {job.Status} while waiting for {expected}.");
        await Task.Delay(25);
    }

    throw new TimeoutException($"Queue job did not reach {expected} within {timeout}.");
}

static async Task<EditorExportJob> WaitForTerminalStatusAsync(
    ExportQueueService queue,
    string jobId,
    TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var job = queue.Jobs.FirstOrDefault(candidate => candidate.Id == jobId);
        if (job?.Status is EditorExportJobStatus.Completed or EditorExportJobStatus.Failed or
            EditorExportJobStatus.Cancelled)
        {
            return job;
        }

        await Task.Delay(50);
    }

    throw new TimeoutException($"Queue job did not finish within {timeout}.");
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
