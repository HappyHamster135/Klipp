using Klipp.Core.Performance;

namespace Klipp.Desktop.Services;

public static class MediaPreviewWorkQueue
{
    public static PriorityBackgroundWorkQueue Shared { get; } = new(maxConcurrency: 2);
}
