using System.Diagnostics;
using System.Text.Json;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Muxing;

internal static class FourStemRecoveryChecks
{
    internal static async Task RunAsync(RecordingRecoveryService recovery, FFmpegLocator locator,
        string root, string templateVideo, string outputDirectory)
    {
        var names = new[] { "Game audio", "Microphone", "Discord", "Music" };
        var frequencies = new[] { 330, 660, 990, 1320 };
        var session = recovery.CreateSession(Path.Combine(outputDirectory, "four-source-recovery.mp4"), 20,
            true, 8, "Game audio", names);
        File.Copy(templateVideo, Path.Combine(session.Directory, "video.mp4"));
        using (var writer = new Rf64WaveFileWriter(Path.Combine(session.Directory, "audio.wav"), 48000, 8))
        {
            var samples = new float[48000 * 3 * 8];
            for (var frame = 0; frame < samples.Length / 8; frame++)
                for (var source = 0; source < 4; source++)
                    samples[frame * 8 + source * 2] = samples[frame * 8 + source * 2 + 1] =
                        (float)(.1 * Math.Sin(2 * Math.PI * frequencies[source] * frame / 48000));
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            writer.Write(bytes, 0, bytes.Length);
        }
        // A process interruption leaves the initially durable RF64 format header
        // with stale 64-bit lengths while complete samples remain on disk.
        var interruptedWave = Path.Combine(session.Directory, "audio.wav");
        var interruptedBytes = await File.ReadAllBytesAsync(interruptedWave);
        Array.Clear(interruptedBytes, 20, 24);
        await File.WriteAllBytesAsync(interruptedWave, interruptedBytes);
        session.Manifest.FramesWritten = 60;
        session.Manifest.Markers.Add(new("four-source-marker", 1.2, "Four-source highlight"));
        recovery.SaveSession(session);
        var id = session.Manifest.Id;
        session.Dispose();
        var result = await recovery.RecoverAsync(id, outputDirectory);
        using var json = JsonDocument.Parse(await Run(locator.FFprobePath, ["-v", "error", "-select_streams", "a", "-show_streams", "-of", "json", result.OutputPath]));
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Require(streams.Length == 5, "Recovering four sources lost one of their audio tracks.");
        Require(streams.Select(stream => stream.GetProperty("tags").GetProperty("handler_name").GetString())
            .SequenceEqual(new[] { "Playback mix" }.Concat(names)), "Recovery changed Discord/music stem names or order.");
        Require(streams.All(stream => stream.GetProperty("channels").GetInt32() == 2), "Recovery exposed packed PCM as surround instead of stereo stems.");
        Require(result.Markers.Count == 1 && result.Markers[0].Id == "four-source-marker", "Four-source recovery lost its highlight marker.");
        for (var source = 0; source < 4; source++)
        {
            var pcm = Path.Combine(root, $"four-source-recovered-{source}.f32");
            await Run(locator.FFmpegPath, ["-v", "error", "-i", result.OutputPath, "-ss", "0.2", "-t", "0.7", "-map", $"0:a:{source + 1}", "-ac", "1", "-f", "f32le", "-n", pcm]);
            var bytes = await File.ReadAllBytesAsync(pcm); var samples = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            Require(Amplitude(samples, frequencies[source]) > .07, $"Recovered {names[source]} lost its own audio.");
            foreach (var other in frequencies.Where(frequency => frequency != frequencies[source]))
                Require(Amplitude(samples, other) < .01, $"Recovered {names[source]} contains another source's audio.");
        }
        Console.WriteLine("PASS: 20 four-source RF64 recovery checks; 8-channel raw audio recovered as independently named stereo stems with original highlight and tone isolation.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static double Amplitude(float[] samples, int frequency)
    {
        double real = 0, imaginary = 0;
        for (var index = 0; index < samples.Length; index++)
        {
            var phase = 2 * Math.PI * frequency * index / 48000;
            real += samples[index] * Math.Cos(phase); imaginary += samples[index] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
    }
    private static async Task<string> Run(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not run the recovery check.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
        return await output;
    }
}
