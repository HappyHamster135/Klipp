using System.Diagnostics;
using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class SeparateCaptureAudioChecks
{
    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator, string directory)
    {
        var ffmpeg = await locator.GetFFmpegPathAsync();
        var source = Path.Combine(directory, "separate-capture-audio.mp4");
        await Run(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=24:d=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=2",
            "-filter_complex", "[1:a]asplit=2[g][gm];[2:a]volume=0.25,asplit=2[m][mm];[gm][mm]amix=inputs=2:normalize=0[mix]",
            "-map", "0:v", "-map", "[mix]", "-map", "[g]", "-map", "[m]", "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a", "aac", "-b:a", "192k", "-metadata:s:a:0", "handler_name=Playback mix",
            "-metadata:s:a:1", "handler_name=Game audio", "-metadata:s:a:2", "handler_name=Microphone",
            "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0", "-y", source]);
        var probe = await new MediaProbeService(locator).GetMediaInfoAsync(source) ?? throw new Exception("Probe failed");
        Check(probe.AudioStreams.Count == 3, "Probe lost independent audio streams");
        var project = new EditorProject { CanvasWidth = 320, CanvasHeight = 180 };
        var clip = new EditorProjectClip { SourcePath = source, Name = "Game capture", SourceDurationSeconds = 2, TrimEndSeconds = 2 };
        project.Clips.Add(clip);
        project.Normalize();
        var tracks = EditorSourceAudioService.SeparateClipAudioStreams(project, clip, probe.AudioStreams);
        Check(tracks.Count == 2 && tracks.Select(track => track.SourceAudioStreamIndex).SequenceEqual([1, 2]),
            "Editor must import both stems and omit the compatibility mix");
        Check(project.GetTrack(tracks[0].TrackId)?.Name == "Game audio" && project.GetTrack(tracks[1].TrackId)?.Name == "Microphone",
            "Audio tracks need distinct readable names");
        Check(EditorSourceAudioService.SeparateClipAudioStreams(project, clip, probe.AudioStreams).Count == 0,
            "Reopening duplicated source audio");
        var roundTrip = JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(project))!;
        roundTrip.Normalize();
        Check(roundTrip.AudioTracks.Select(track => track.SourceAudioStreamIndex).SequenceEqual([1, 2]), "Saving lost stream identity");

        var game = tracks[0];
        var microphone = tracks[1];
        microphone.Muted = true;
        var gameOnly = Path.Combine(directory, "separate-audio-game-only.mp4");
        await export.ExportAsync(project, gameOnly);
        var samples = await Decode(ffmpeg, gameOnly);
        Check(Amplitude(samples, 440) > .06 && Amplitude(samples, 880) < .002, "Muting microphone left voice in game export");
        microphone.Muted = false;
        game.Muted = true;
        var microphoneOnly = Path.Combine(directory, "separate-audio-microphone-only.mp4");
        await export.ExportAsync(project, microphoneOnly);
        samples = await Decode(ffmpeg, microphoneOnly);
        Check(Amplitude(samples, 880) > .015 && Amplitude(samples, 440) < .002, "Microphone export contains game audio");
        game.Muted = false;
        game.Volume = .5;
        var mixed = Path.Combine(directory, "separate-audio-balanced.mp4");
        await export.ExportAsync(project, mixed);
        samples = await Decode(ffmpeg, mixed);
        var ratio = Amplitude(samples, 440) / Amplitude(samples, 880);
        Check(ratio is > 1.7 and < 2.3, "Independent game volume was ignored or the compatibility mix was doubled");

        var signal = new AudioSignalAnalysisService(locator, Path.Combine(directory, "signal-cache"));
        var gameSignal = await signal.AnalyzeAsync(source, audioStreamIndex: 1);
        var micSignal = await signal.AnalyzeAsync(source, audioStreamIndex: 2);
        Check(gameSignal is not null && micSignal is not null &&
            gameSignal.Frames.Max(frame => frame.RmsLeft) > micSignal.Frames.Max(frame => frame.RmsLeft) * 3,
            "Audio meters reused the mix or another stream's analysis cache");
        var waveforms = new WaveformService(locator);
        var gameWave = await waveforms.GetOrCreateWaveformAsync(source, null, null,
            Klipp.Core.Performance.BackgroundWorkPriority.Background, audioStreamIndex: 1);
        var micWave = await waveforms.GetOrCreateWaveformAsync(source, null, null,
            Klipp.Core.Performance.BackgroundWorkPriority.Background, audioStreamIndex: 2);
        Check(gameWave is not null && micWave is not null && gameWave != micWave &&
            !File.ReadAllBytes(gameWave).SequenceEqual(File.ReadAllBytes(micWave)), "Waveforms were not generated per stream");
        var loudness = new AudioLoudnessAnalysisService(locator);
        var gameLoudness = await loudness.AnalyzeAsync(source, 0, 2, audioStreamIndex: 1);
        var micLoudness = await loudness.AnalyzeAsync(source, 0, 2, audioStreamIndex: 2);
        Check(gameLoudness is not null && micLoudness is not null &&
            gameLoudness.IntegratedLoudnessLufs > micLoudness.IntegratedLoudnessLufs + 8,
            "Loudness normalization measured the wrong audio stream");

        var legacy = new EditorProject();
        var legacyClip = new EditorProjectClip { SourcePath = source, SourceDurationSeconds = 2, TrimEndSeconds = 2 };
        legacy.Clips.Add(legacyClip);
        Check(EditorSourceAudioService.SeparateClipAudio(legacy, legacyClip, true)?.SourceAudioStreamIndex == 0,
            "Legacy recordings must retain default-stream behavior");
        await CheckAppTracksAsync(export, locator, directory, source);
        Console.WriteLine("PASS: separate capture streams import, save, mute, balance, export, meter, normalize and draw independent waveforms; legacy default audio preserved");
        Console.WriteLine($"QA_SOURCE={source}");
    }

    private static async Task CheckAppTracksAsync(EditorProjectExportService export, FFmpegLocator locator, string directory, string source)
    {
        var ffmpeg = await locator.GetFFmpegPathAsync();
        var allSources = Path.Combine(directory, "four-capture-sources.mp4");
        await Run(ffmpeg, ["-v", "error", "-filter_complex_threads", "1", "-i", source,
            "-f", "lavfi", "-i", "sine=frequency=1320:sample_rate=48000:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=1760:sample_rate=48000:duration=2",
            "-filter_complex", "[0:a:1]asplit=2[g][gm];[0:a:2]asplit=2[m][mm];[1:a]asplit=2[d][dm];[2:a]asplit=2[u][um];[gm][mm][dm][um]amix=inputs=4:normalize=0[mix]",
            "-map", "0:v", "-map", "[mix]", "-map", "[g]", "-map", "[m]", "-map", "[d]", "-map", "[u]",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
            "-metadata:s:a:0", "handler_name=Playback mix", "-metadata:s:a:1", "handler_name=Game audio",
            "-metadata:s:a:2", "handler_name=Microphone", "-metadata:s:a:3", "handler_name=Discord", "-metadata:s:a:4", "handler_name=Music",
            "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0", "-disposition:a:3", "0", "-disposition:a:4", "0", "-y", allSources]);
        var info = await new MediaProbeService(locator).GetMediaInfoAsync(allSources) ?? throw new Exception("Four-source probe failed");
        var project = new EditorProject { CanvasWidth = 320, CanvasHeight = 180 };
        var clip = new EditorProjectClip { SourcePath = allSources, Name = "App capture", SourceDurationSeconds = 2, TrimEndSeconds = 2 };
        project.Clips.Add(clip); project.Normalize();
        var tracks = EditorSourceAudioService.SeparateClipAudioStreams(project, clip, info.AudioStreams);
        Check(tracks.Count == 4 && tracks.Select(track => track.SourceAudioStreamIndex).SequenceEqual([1, 2, 3, 4]),
            "The editor did not import all four stems or imported the mix twice");
        var roundTrip = JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(project))!; roundTrip.Normalize();
        Check(roundTrip.AudioTracks.Select(track => track.SourceAudioRole).SequenceEqual(["system", "microphone", "discord", "music"]),
            "Project save/normalize lost the Discord/music source roles");
        foreach (var (role, frequency) in new[] { ("discord", 1320), ("music", 1760) })
        {
            foreach (var track in roundTrip.AudioTracks) track.Muted = track.SourceAudioRole != role;
            var output = Path.Combine(directory, $"{role}-only-edit.mp4");
            await export.ExportAsync(roundTrip, output);
            var samples = await Decode(ffmpeg, output);
            Check(Amplitude(samples, frequency) > .06 && new[] { 440, 880, 1320, 1760 }.Where(other => other != frequency)
                .All(other => Amplitude(samples, other) < .003), $"Isolating {role} in the editor exported another app's audio");
        }
        Console.WriteLine("PASS: four source roles import/save independently; Discord-only and music-only edits export their own audio without game, mic or mix leakage");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
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
            ["-v", "error", "-ss", "0.25", "-i", path, "-t", "1", "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]))!;
        var error = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes.ToArray(), 0, samples, 0, (int)bytes.Length);
        return samples;
    }
    private static double Amplitude(float[] samples, double frequency)
    {
        var real = 0d; var imaginary = 0d;
        for (var i = 0; i < samples.Length; i++)
        {
            var phase = 2 * Math.PI * frequency * i / 48000;
            real += samples[i] * Math.Cos(phase); imaginary += samples[i] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
    }
}
