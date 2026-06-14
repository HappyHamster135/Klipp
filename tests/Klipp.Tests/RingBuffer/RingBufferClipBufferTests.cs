using Klipp.Core.Abstractions;
using Klipp.Core.Models;
using Klipp.Storage.RingBuffer;
using Klipp.Tests.Fakes;

namespace Klipp.Tests.RingBuffer;

public class RingBufferClipBufferTests
{
    private static RecordingSettings TestSettings(int ringBufferSeconds = 10) =>
        RecordingSettings.Default1080p60 with { RingBufferSeconds = ringBufferSeconds };

    private static (RingBufferClipBuffer Buffer, FakeMp4Writer Writer) CreateBuffer(int ringBufferSeconds = 10)
    {
        FakeMp4Writer writer = new();
        RingBufferClipBuffer buffer = new(() => writer, TestSettings(ringBufferSeconds));
        return (buffer, writer);
    }

    [Fact]
    public void BufferedSeconds_EmptyBuffer_ReturnsZero()
    {
        var (buffer, _) = CreateBuffer();

        buffer.BufferedSeconds.ShouldBe(0);
    }

    [Fact]
    public async Task AppendAsync_OneGop_BufferedSecondsMatchesGopDuration()
    {
        var (buffer, _) = CreateBuffer();

        foreach (var sample in SampleBuilder.Gop(startTimestamp: 0, frameCount: 60))
        {
            await buffer.AppendAsync(sample);
        }

        buffer.BufferedSeconds.ShouldBe(1.0, tolerance: 0.01);
    }

    [Fact]
    public async Task AppendAsync_PFrameBeforeKeyframe_IsDropped()
    {
        var (buffer, _) = CreateBuffer();

        await buffer.AppendAsync(SampleBuilder.VideoFrame(timestamp: 0));

        buffer.BufferedSeconds.ShouldBe(0);
    }

    [Fact]
    public async Task AppendAsync_ExceedsLimit_EvictsOldestGopWhole()
    {
        var (buffer, _) = CreateBuffer(ringBufferSeconds: 5);

        const long twoSeconds = 2 * 10_000_000L;
        for (int gopIndex = 0; gopIndex < 4; gopIndex++)
        {
            foreach (var sample in SampleBuilder.Gop(
                startTimestamp: gopIndex * twoSeconds,
                frameCount: 120))
            {
                await buffer.AppendAsync(sample);
            }
        }

        // Tolerera mikrosekund-precision från floating-point konvertering.
        // Förväntat resultat: 4-6 sekunder buffrat (GOP-aligned eviction kan inte vara exakt).
        buffer.BufferedSeconds.ShouldBeInRange(3.5, 6.5);
    }

    [Fact]
    public async Task FlushLastSecondsAsync_EmptyBuffer_ReturnsZero()
    {
        var (buffer, writer) = CreateBuffer();

        var duration = await buffer.FlushLastSecondsAsync(seconds: 5, outputPath: "fake.mp4");

        duration.ShouldBe(0);
        writer.WrittenSamples.ShouldBeEmpty();
    }

    [Fact]
    public async Task FlushLastSecondsAsync_StartsOnKeyframe()
    {
        var (buffer, writer) = CreateBuffer(ringBufferSeconds: 30);

        const long twoSeconds = 2 * 10_000_000L;
        for (int gopIndex = 0; gopIndex < 5; gopIndex++)
        {
            foreach (var sample in SampleBuilder.Gop(
                startTimestamp: gopIndex * twoSeconds,
                frameCount: 120))
            {
                await buffer.AppendAsync(sample);
            }
        }

        await buffer.FlushLastSecondsAsync(seconds: 5, outputPath: "fake.mp4");

        writer.WrittenSamples.ShouldNotBeEmpty();
        writer.WrittenSamples[0].IsKeyframe.ShouldBeTrue();
    }

    [Fact]
    public async Task FlushLastSecondsAsync_WritesInTimestampOrder()
    {
        var (buffer, writer) = CreateBuffer(ringBufferSeconds: 30);

        const long twoSeconds = 2 * 10_000_000L;
        for (int gopIndex = 0; gopIndex < 3; gopIndex++)
        {
            foreach (var videoSample in SampleBuilder.Gop(
                startTimestamp: gopIndex * twoSeconds,
                frameCount: 120))
            {
                await buffer.AppendAsync(videoSample);
            }
            for (long audioTs = gopIndex * twoSeconds;
                 audioTs < (gopIndex + 1) * twoSeconds;
                 audioTs += SampleBuilder.AudioBlockDuration)
            {
                await buffer.AppendAsync(SampleBuilder.AudioBlock(audioTs));
            }
        }

        await buffer.FlushLastSecondsAsync(seconds: 6, outputPath: "fake.mp4");

        var timestamps = writer.WrittenSamples.Select(s => s.Timestamp).ToList();
        timestamps.ShouldBe(timestamps.OrderBy(t => t).ToList());
    }

    [Fact]
    public async Task FlushLastSecondsAsync_InitializesAndFinalizesWriter()
    {
        var (buffer, writer) = CreateBuffer();
        await buffer.AppendAsync(SampleBuilder.VideoKeyframe(timestamp: 0));

        await buffer.FlushLastSecondsAsync(seconds: 5, outputPath: "C:\\test.mp4");

        writer.InitializedPath.ShouldBe("C:\\test.mp4");
        writer.IsFinalized.ShouldBeTrue();
        writer.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Clear_RemovesAllSamples()
    {
        var (buffer, _) = CreateBuffer();
        foreach (var sample in SampleBuilder.Gop(startTimestamp: 0, frameCount: 60))
        {
            await buffer.AppendAsync(sample);
        }

        buffer.Clear();

        buffer.BufferedSeconds.ShouldBe(0);
    }

    [Fact]
    public async Task AppendAsync_AfterDispose_Throws()
    {
        var (buffer, _) = CreateBuffer();
        await buffer.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            async () => await buffer.AppendAsync(SampleBuilder.VideoKeyframe(0)));
    }

    [Fact]
    public async Task AppendAsync_ConcurrentWritesFromMultipleThreads_DoesNotCrashOrDeadlock()
    {
        var (buffer, _) = CreateBuffer(ringBufferSeconds: 30);

        // 4 trådar lägger till samples parallellt. Pga ordnings-icke-determinism
        // mellan trådar kan BufferedSeconds bli vad som helst — det enda vi BEVISAR här
        // är att det inte kraschar och inte deadlockar. Riktigt eviction-beteende testas
        // i AppendAsync_ExceedsLimit_EvictsOldestGopWhole där en enda producer används.
        const long twoSeconds = 2 * 10_000_000L;
        var tasks = Enumerable.Range(0, 4).Select(threadIndex => Task.Run(async () =>
        {
            for (int gopIndex = 0; gopIndex < 30; gopIndex++)
            {
                var startTs = (threadIndex * 30 + gopIndex) * twoSeconds;
                foreach (var sample in SampleBuilder.Gop(startTs, frameCount: 120))
                {
                    await buffer.AppendAsync(sample);
                }
            }
        }));

        await Should.NotThrowAsync(async () => await Task.WhenAll(tasks));
    }
}
