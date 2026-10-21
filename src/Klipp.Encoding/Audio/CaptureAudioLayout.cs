namespace Klipp.Encoding.Audio;

/// <summary>Stable audio stream names shared by recording, replay segments and editor import.</summary>
public static class CaptureAudioLayout
{
    public const string MixName = "Playback mix";
    public const string SystemName = "Game audio";
    public const string MicrophoneName = "Microphone";
    public const string DiscordName = "Discord";
    public const string MusicName = "Music";

    internal static IReadOnlyList<string> OutputArguments(string input, int channels,
        string alignmentFilter, string singleSourceName, IReadOnlyList<string>? trackNames = null)
    {
        var count = Math.Clamp(channels / 2, 1, 4);
        var sources = trackNames?.Count == count ? trackNames.ToArray() : count == 2
            ? [SystemName, MicrophoneName] : count == 1 ? [singleSourceName]
            : Enumerable.Range(1, count).Select(index => $"Audio {index}").ToArray();
        var separate = count > 1;
        // Channel numbers (not surround channel names) preserve exactly the
        // system L/R + microphone L/R order carried by the shared PCM clock.
        var filter = separate
            ? $"{input}{alignmentFilter},asplit={count + 1}[mixInput]" +
                string.Concat(Enumerable.Range(0, count).Select(index => $"[stemInput{index}]")) + ";" +
              "[mixInput]pan=stereo|c0=" + string.Join('+', Enumerable.Range(0, count).Select(index => $"c{index * 2}")) +
              "|c1=" + string.Join('+', Enumerable.Range(0, count).Select(index => $"c{index * 2 + 1}")) +
              ",alimiter=limit=0.98:attack=1:release=60:level=false:latency=true,aformat=channel_layouts=stereo[mix];" +
              string.Join(';', Enumerable.Range(0, count).Select(index =>
                  $"[stemInput{index}]pan=stereo|c0=c{index * 2}|c1=c{index * 2 + 1}[stem{index}]"))
            : $"{input}{alignmentFilter}[audio]";
        var args = new List<string> { "-filter_complex", filter };
        var labels = separate ? new[] { "mix" }.Concat(Enumerable.Range(0, count).Select(index => $"stem{index}")).ToArray() : new[] { "audio" };
        var names = separate ? new[] { MixName }.Concat(sources).ToArray() : sources;
        for (var index = 0; index < labels.Length; index++)
        {
            args.AddRange(["-map", $"[{labels[index]}]",
                $"-metadata:s:a:{index}", $"handler_name={names[index]}",
                $"-metadata:s:a:{index}", $"title={names[index]}",
                $"-disposition:a:{index}", index == 0 ? "default" : "0"]);
        }
        return args;
    }
}
