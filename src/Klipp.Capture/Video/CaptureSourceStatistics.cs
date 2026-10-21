namespace Klipp.Capture.Video;

public readonly record struct CaptureSourceStatistics(
    bool IsCapturing,
    long CapturedFrames,
    long DeliveredFrames,
    long DroppedFrames,
    long InvalidFrames,
    DateTime? LastFrameUtc,
    int FrameWidth,
    int FrameHeight);
