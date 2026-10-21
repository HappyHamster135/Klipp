using Klipp.Capture.Models;
using Klipp.Core.Abstractions;
using Klipp.Core.Models;

namespace Klipp.Capture.Video;

/// <summary>A capture stream with the lifecycle and health information used by recording.</summary>
public interface IVideoCaptureSource : ICaptureSource<VideoFrame>
{
    CaptureTarget Target { get; }
    string? FailureMessage { get; }
    event EventHandler? TargetClosed;
    CaptureSourceStatistics GetStatistics();
}
