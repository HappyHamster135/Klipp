using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Klipp.MediaServer;

public interface IMediaValidator
{
    Task<double> ValidateVideoAsync(string path, CancellationToken token);
    Task ValidatePosterAsync(string path, CancellationToken token);
}

public sealed class FfprobeMediaValidator(MediaOptions options) : IMediaValidator
{
    public async Task<double> ValidateVideoAsync(string path, CancellationToken token)
    {
        if (!HasFastStartMp4Structure(path)) throw InvalidMedia();
        using var result = await ProbeAsync(path, video: true, token);
        if (!result.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) throw InvalidMedia();
        var videoStreams = 0;
        var audioStreams = 0;
        foreach (var stream in streams.EnumerateArray())
        {
            var kind = Text(stream, "codec_type");
            var codec = Text(stream, "codec_name");
            if (kind == "video" && codec == "h264" && Dimension(stream, "width", 1280) && Dimension(stream, "height", 720)) videoStreams++;
            else if (kind == "audio" && codec == "aac") audioStreams++;
            else throw InvalidMedia();
        }
        if (videoStreams != 1 || audioStreams > 1 || !result.RootElement.TryGetProperty("format", out var format)
            || !double.TryParse(Text(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            || !double.IsFinite(duration) || duration <= 0 || duration > MediaOptions.MaximumDurationSeconds + 0.2) throw InvalidMedia();
        return Math.Min(duration, MediaOptions.MaximumDurationSeconds);
    }

    public async Task ValidatePosterAsync(string path, CancellationToken token)
    {
        await using (var file = File.OpenRead(path))
        {
            var header = new byte[3];
            if (await file.ReadAsync(header, token) != 3 || header[0] != 0xff || header[1] != 0xd8 || header[2] != 0xff) throw InvalidMedia();
            file.Position = Math.Max(0, file.Length - 2);
            var end = new byte[2];
            if (await file.ReadAsync(end, token) != 2 || end[0] != 0xff || end[1] != 0xd9) throw InvalidMedia();
        }
        using var result = await ProbeAsync(path, video: false, token);
        if (!result.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() != 1) throw InvalidMedia();
        var stream = streams[0];
        if (Text(stream, "codec_type") != "video" || Text(stream, "codec_name") != "mjpeg"
            || !Dimension(stream, "width", 1280) || !Dimension(stream, "height", 720)) throw InvalidMedia();
    }

    private async Task<JsonDocument> ProbeAsync(string path, bool video, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo(options.FfprobePath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-v", "error", "-max_alloc", "67108864", "-protocol_whitelist", "file", "-threads", "1", "-f", video ? "mov" : "mjpeg" }) start.ArgumentList.Add(arg);
        if (video)
            foreach (var arg in new[] { "-enable_drefs", "0", "-use_absolute_path", "0" }) start.ArgumentList.Add(arg);
        foreach (var arg in new[] { "-show_entries", "format=duration:stream=codec_type,codec_name,width,height", "-of", "json", "-i", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw MediaException.StorageUnavailable();
        using var cancellation = timeout.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, 16 * 1024, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError, 16 * 1024, timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw InvalidMedia();
            return JsonDocument.Parse(await output, new JsonDocumentOptions { MaxDepth = 12 });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw InvalidMedia(); }
        catch (JsonException) { throw InvalidMedia(); }
        finally { if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } } }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var output = new char[maximum + 1];
        var count = 0;
        while (count <= maximum)
        {
            var read = await reader.ReadAsync(output.AsMemory(count, maximum + 1 - count), token);
            if (read == 0) return new string(output, 0, count);
            count += read;
            if (count > maximum) throw InvalidMedia();
        }
        throw InvalidMedia();
    }

    public static bool HasFastStartMp4Structure(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length is < 24 or > MediaOptions.MaximumVideoBytes) return false;
        Span<byte> header = stackalloc byte[16];
        var first = true;
        var moov = false;
        var mdat = false;
        while (file.Position < file.Length)
        {
            var start = file.Position;
            if (file.Read(header[..8]) != 8) return false;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header[4..8]);
            var headerSize = 8;
            if (size == 1)
            {
                if (file.Read(header[8..]) != 8) return false;
                var extended = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                if (extended > long.MaxValue) return false;
                size = (long)extended;
                headerSize = 16;
            }
            if (size == 0) size = file.Length - start;
            if (size < headerSize || size > file.Length - start) return false;
            if (first && (type != "ftyp" || size < 16)) return false;
            if (type == "moov") { if (mdat || moov) return false; moov = true; }
            if (type == "mdat") { if (!moov) return false; mdat = true; }
            file.Position = start + size;
            first = false;
        }
        return moov && mdat;
    }

    private static string? Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Dimension(JsonElement stream, string name, int maximum) => stream.TryGetProperty(name, out var value) && value.TryGetInt32(out var dimension) && dimension is > 0 && dimension <= maximum;
    private static MediaException InvalidMedia() => new(400, "invalid_media", "Upload a prepared Klipp MP4 clip and JPEG thumbnail.");
}
