using System.Text.Json;
using Klipp.Core.Playback;

namespace Klipp.Tests.Playback;

public sealed class PlaybackPositionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Klipp-Resume-Tests-" + Guid.NewGuid().ToString("N"));
    private string HistoryPath => Path.Combine(_directory, "history.json");

    public PlaybackPositionStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ReopeningStore_RestoresSameFile_AndRejectsReplacedFile()
    {
        var path = Path.Combine(_directory, "clip.mp4");
        File.WriteAllBytes(path, new byte[100]);
        var original = PlaybackMediaIdentity.TryCreate(path)!;
        new PlaybackPositionStore(HistoryPath).Remember(original, 42.25, 100);
        var reopened = new PlaybackPositionStore(HistoryPath);
        Assert.Equal(42.25, reopened.GetPosition(original, 100));
        Assert.Equal(42.25, reopened.GetPosition(original with { Path = original.Path.ToUpperInvariant() }, 100));

        File.WriteAllBytes(path, new byte[101]);
        Assert.Equal(0, reopened.GetPosition(PlaybackMediaIdentity.TryCreate(path), 100));
        Assert.Equal(0, reopened.GetPosition(original with { CreationUtcTicks = original.CreationUtcTicks + 1 }, 100));
        Assert.Equal(0, reopened.GetPosition(original with { LastWriteUtcTicks = original.LastWriteUtcTicks + 1 }, 100));
    }

    [Theory]
    [InlineData(100, 100, 0)]
    [InlineData(99.5, 100, 0)]
    [InlineData(90, 100, 90)]
    [InlineData(0.6, 1, 0.6)]
    [InlineData(0.98, 1, 0)]
    [InlineData(-5, 100, 0)]
    [InlineData(double.NaN, 100, 0)]
    [InlineData(20, double.NaN, 0)]
    public void NormalizePosition_ReplaysCompletedClips_AndRejectsInvalidTimes(double position, double duration, double expected) =>
        Assert.Equal(expected, PlaybackPositionStore.NormalizePosition(position, duration));

    [Fact]
    public void DamagedOrOversizedHistory_DoesNotPreventStartingPlayback()
    {
        File.WriteAllText(HistoryPath, "{broken");
        var media = new PlaybackMediaIdentity("clip.mp4", 1, 2, 3);
        Assert.Equal(0, new PlaybackPositionStore(HistoryPath).GetPosition(media, 100));
        File.WriteAllText(HistoryPath, new string(' ', 512 * 1024 + 1));
        Assert.Equal(0, new PlaybackPositionStore(HistoryPath).GetPosition(media, 100));
        Assert.Null(PlaybackMediaIdentity.TryCreate(Path.Combine(_directory, "missing.mp4")));
    }

    [Fact]
    public void History_RetainsMostRecent500Clips_AndEvictsOldestWhenAddingAnother()
    {
        var entries = Enumerable.Range(0, 510).Select(index => new
        {
            Media = new PlaybackMediaIdentity($"clip-{index}.mp4", 1, 2, 3),
            PositionSeconds = 12d,
            UpdatedUtcTicks = index
        });
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(entries));
        var store = new PlaybackPositionStore(HistoryPath);
        Assert.Equal(0, store.GetPosition(new("clip-0.mp4", 1, 2, 3), 100));
        Assert.Equal(12, store.GetPosition(new("clip-10.mp4", 1, 2, 3), 100));
        store.Remember(new("new.mp4", 1, 2, 3), 5, 100);
        Assert.Equal(0, store.GetPosition(new("clip-10.mp4", 1, 2, 3), 100));
        using var saved = JsonDocument.Parse(File.ReadAllText(HistoryPath));
        Assert.Equal(PlaybackPositionStore.MaximumEntries, saved.RootElement.GetArrayLength());
        Assert.Equal(5, new PlaybackPositionStore(HistoryPath).GetPosition(new("new.mp4", 1, 2, 3), 100));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
