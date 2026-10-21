namespace Klipp.Core.Models;

public sealed record RecordingSessionMarker(string Id, double TimeSeconds, string Label);

/// <summary>Durable recording metadata. Media paths are fixed names inside the session, never journal input.</summary>
public sealed class RecordingSessionManifest
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string SuggestedFileName { get; set; } = "Recovered recording.mp4";
    public string OutputPath { get; set; } = string.Empty;
    public string State { get; set; } = "recording";
    public int FrameRate { get; set; } = 24;
    public long FramesWritten { get; set; }
    public double? AudioOffsetSeconds { get; set; }
    public int AudioChannels { get; set; } = 2;
    public string AudioTrackName { get; set; } = "Game audio";
    public List<string> AudioTrackNames { get; set; } = [];
    public bool AudioExpected { get; set; }
    public string? FailureReason { get; set; }
    public string? PublishedOutputPath { get; set; }
    public string? PublishedSha256 { get; set; }
    public long PublishedLength { get; set; }
    public List<RecordingSessionMarker> Markers { get; set; } = [];
    public bool IsReplayBuffer { get; set; }
    public double ReplayDurationSeconds { get; set; }

    public double DurationSeconds => IsReplayBuffer && double.IsFinite(ReplayDurationSeconds)
        ? Math.Max(0, ReplayDurationSeconds) : FrameRate > 0 ? Math.Max(0, FramesWritten) / (double)FrameRate : 0;
}
