using System.Diagnostics;
using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class AudioDuckingChecks
{
    private const int Rate = 48000;

    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator, string directory)
    {
        var ffmpeg = await locator.GetFFmpegPathAsync();
        var source = Path.Combine(directory, "speech-ducking-source.mp4");
        var bedPath = Path.Combine(directory, "speech-ducking-bed.wav");
        await Run(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=24:d=6",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=6",
            "-filter_complex", "[1:a]asplit=2[g][gm];[2:a]volume='if(between(t,2,3),1,0)':eval=frame,asplit=2[m][mm];[gm][mm]amix=inputs=2:normalize=0[mix]",
            "-map", "0:v", "-map", "[mix]", "-map", "[g]", "-map", "[m]", "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a", "aac", "-b:a", "192k", "-metadata:s:a:0", "handler_name=Playback mix",
            "-metadata:s:a:1", "handler_name=Game audio", "-metadata:s:a:2", "handler_name=Microphone",
            "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0", "-y", source]);
        await Run(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=110:sample_rate=48000:duration=6",
            "-c:a", "pcm_s16le", "-y", bedPath]);
        var info = await new MediaProbeService(locator).GetMediaInfoAsync(source) ?? throw new Exception("Could not probe fixture");
        var project = new EditorProject { CanvasWidth = 320, CanvasHeight = 180 };
        var clip = new EditorProjectClip { SourcePath = source, Name = "Capture", SourceDurationSeconds = 6, TrimEndSeconds = 6 };
        project.Clips.Add(clip);
        project.Normalize();
        var stems = EditorSourceAudioService.SeparateClipAudioStreams(project, clip, info.AudioStreams);
        var game = stems.Single(track => track.SourceAudioRole == "system");
        var microphone = stems.Single(track => track.SourceAudioRole == "microphone");
        Check(game.SourceAudioStreamIndex == 1 && microphone.SourceAudioStreamIndex == 2, "Capture stem roles or stream identities were lost");
        var bed = new EditorProjectAudioTrack { Name = "Music bed", SourcePath = bedPath, SourceDurationSeconds = 6,
            TrimEndSeconds = 6, Volume = .5 };
        EditorSourceAudioService.AssignAvailableAudioTrack(project, bed);
        project.AudioTracks.Add(bed);
        project.Normalize();

        // User-visible names may change; signal routing must retain source roles.
        game.Name = "Desktop sound";
        microphone.Name = "Commentary";
        project.GetTrack(game.TrackId)!.Name = "Computer";
        project.GetTrack(microphone.TrackId)!.Name = "Voice";
        project = JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(project))!;
        project.Normalize();
        game = project.AudioTracks.Single(track => track.SourceAudioRole == "system");
        microphone = project.AudioTracks.Single(track => track.SourceAudioRole == "microphone");
        bed = project.AudioTracks.Single(track => string.IsNullOrEmpty(track.SourceClipId));
        Check(game.Name == "Desktop sound" && microphone.Name == "Commentary", "Role persistence changed user labels");

        var cacheProject = JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(project))!;
        var distantVoice = cacheProject.AudioTracks.Single(track => track.SourceAudioRole == "microphone");
        distantVoice.TimelineStartSeconds = 20;
        var cache = new EditorTimelineRenderCacheService(Path.Combine(directory, "ducking-policy-cache"));
        var voicePolicyKey = cache.BuildSegments(cacheProject, "libx264")[0].CacheKey;
        distantVoice.SourceAudioRole = string.Empty;
        Check(cache.BuildSegments(cacheProject, "libx264")[0].CacheKey != voicePolicyKey,
            "A microphone outside the rendered segment must invalidate its ducking policy cache");

        var baseline = await Export("baseline");
        game.DuckingEnabled = microphone.DuckingEnabled = bed.DuckingEnabled = true;
        game.DuckingStrength = microphone.DuckingStrength = bed.DuckingStrength = .75;
        var ducked = await Export("voice-ducked");
        var baseQuiet = Measure(baseline, .6);
        var quiet = Measure(ducked, .6);
        var speech = Measure(ducked, 2.35);
        var baseSpeech = Measure(baseline, 2.35);
        var recovered = Measure(ducked, 4.4);
        Check(baseQuiet.Game > .04 && baseSpeech.Microphone > .04 && baseQuiet.Bed > .02, "Fixture audio levels are invalid");
        Near(quiet.Game, baseQuiet.Game, "Game audio ducked during microphone silence");
        Near(quiet.Bed, baseQuiet.Bed, "Music ducked during microphone silence");
        Near(speech.Microphone, baseSpeech.Microphone, "Game audio or music incorrectly ducked the microphone");
        Check(speech.Game < baseSpeech.Game * .65 && speech.Bed < baseSpeech.Bed * .65,
            "Microphone speech did not lower game audio and the imported music bed");
        Near(recovered.Game, Measure(baseline, 4.4).Game, "Game audio failed to recover after speech");
        Near(recovered.Bed, Measure(baseline, 4.4).Bed, "Music failed to recover after speech");

        microphone.Muted = true;
        var mutedClip = Measure(await Export("muted-microphone-clip"), 2.35);
        Near(mutedClip.Game, baseSpeech.Game, "Muted microphone clip caused fallback game-driven ducking");
        Near(mutedClip.Bed, baseSpeech.Bed, "Muted microphone clip still ducked music");
        Check(mutedClip.Microphone < .002, "Muted microphone clip remained audible");
        microphone.Muted = false;
        project.GetTrack(microphone.TrackId)!.Muted = true;
        var mutedRow = Measure(await Export("muted-microphone-row"), 2.35);
        Near(mutedRow.Game, baseSpeech.Game, "Muted microphone row caused fallback game-driven ducking");
        Near(mutedRow.Bed, baseSpeech.Bed, "Muted microphone row still ducked music");
        Check(mutedRow.Microphone < .002, "Muted microphone row remained audible");
        project.GetTrack(microphone.TrackId)!.Muted = false;
        project.GetTrack(game.TrackId)!.Solo = true;
        project.GetTrack(bed.TrackId)!.Solo = true;
        var excluded = Measure(await Export("solo-excludes-microphone"), 2.35);
        Near(excluded.Game, baseSpeech.Game, "Solo-excluded microphone caused fallback game-driven ducking");
        Near(excluded.Bed, baseSpeech.Bed, "Solo-excluded microphone still ducked music");
        Check(excluded.Microphone < .002, "Solo-excluded microphone remained audible");

        Console.WriteLine("PASS: source audio roles survive rename/save; microphone speech ducks game/music, silence releases ducking, game never ducks microphone, and muted/solo-excluded microphones never fall back to game-driven ducking");
        Console.WriteLine(JsonSerializer.Serialize(new { Baseline = baseSpeech, Speech = speech, Quiet = quiet, Recovered = recovered }));
        Console.WriteLine($"QA_SOURCE={source}");
        return;

        async Task<float[]> Export(string name)
        {
            var path = Path.Combine(directory, $"audio-ducking-{name}.mp4");
            await export.ExportAsync(project, path);
            return await Decode(ffmpeg, path);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double actual, double expected, string message) =>
        Check(Math.Abs(actual - expected) < expected * .12 + .001, $"{message}: {actual:0.00000} vs {expected:0.00000}");
    private static async Task Run(string executable, string[] args)
    {
        using var process = Process.Start(Start(executable, args))!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        await output;
    }
    private static ProcessStartInfo Start(string executable, string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    private static async Task<float[]> Decode(string executable, string path)
    {
        using var process = Process.Start(Start(executable,
            ["-v", "error", "-i", path, "-vn", "-ac", "1", "-ar", Rate.ToString(), "-f", "f32le", "pipe:1"]))!;
        var error = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes.ToArray(), 0, samples, 0, (int)bytes.Length);
        return samples;
    }
    private static Signal Measure(float[] samples, double start) => new(
        Amplitude(samples, 440, start), Amplitude(samples, 880, start), Amplitude(samples, 110, start));
    private static double Amplitude(float[] samples, double frequency, double start)
    {
        var real = 0d; var imaginary = 0d;
        var first = (int)Math.Round(start * Rate);
        var count = Math.Min((int)(.4 * Rate), samples.Length - first);
        for (var i = 0; i < count; i++)
        {
            var phase = 2 * Math.PI * frequency * i / Rate;
            real += samples[first + i] * Math.Cos(phase); imaginary += samples[first + i] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / count;
    }
    private sealed record Signal(double Game, double Microphone, double Bed);
}
