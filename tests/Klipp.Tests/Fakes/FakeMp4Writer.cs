using Klipp.Core.Abstractions;
using Klipp.Core.Models;

namespace Klipp.Tests.Fakes;

/// <summary>
/// In-memory fake av <see cref="IMp4Writer"/>. Samlar samples i en lista istället
/// för att skriva till disk. Används i tester för att verifiera vad RingBufferClipBuffer
/// hade skrivit till en riktig MP4.
/// </summary>
internal sealed class FakeMp4Writer : IMp4Writer
{
    public List<EncodedSample> WrittenSamples { get; } = new();
    public string? InitializedPath { get; private set; }
    public RecordingSettings? InitializedSettings { get; private set; }
    public bool IsFinalized { get; private set; }
    public bool IsDisposed { get; private set; }

    public Task InitializeAsync(string outputPath, RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        InitializedPath = outputPath;
        InitializedSettings = settings;
        return Task.CompletedTask;
    }

    public Task WriteSampleAsync(EncodedSample sample, CancellationToken cancellationToken = default)
    {
        WrittenSamples.Add(sample);
        return Task.CompletedTask;
    }

    public Task FinalizeAsync(CancellationToken cancellationToken = default)
    {
        IsFinalized = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
