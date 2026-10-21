namespace Klipp.Core.Editing;

/// <summary>Separates requested time from the decoder clock while an asynchronous seek settles.</summary>
public sealed class EditorPreviewSeekState
{
    public double? TargetSeconds { get; private set; }
    private double _requestedAt;
    private bool _timedOut;

    public void Reset() { TargetSeconds = null; _timedOut = false; }

    public bool TakeTimeout() { var result = _timedOut; _timedOut = false; return result; }

    public bool Request(double targetSeconds, double actualSeconds, double nowSeconds, bool force)
    {
        var alreadySeeking = TargetSeconds is not null;
        if (TargetSeconds is { } pending)
        {
            var elapsed = nowSeconds - _requestedAt;
            if (Math.Abs(pending - targetSeconds) <= 0.002 && elapsed < 0.5)
                return false;
            if (!force && elapsed < 0.25)
                return false;
        }
        if (!alreadySeeking && Math.Abs(actualSeconds - targetSeconds) <= 0.015)
        {
            Reset();
            return false;
        }
        TargetSeconds = Math.Max(0, targetSeconds);
        _requestedAt = nowSeconds;
        _timedOut = false;
        return true;
    }

    public bool IsPending(double actualSeconds, double nowSeconds, bool seekCompleted = false)
    {
        if (TargetSeconds is not { } target)
            return false;
        var difference = Math.Abs(actualSeconds - target);
        // Some decoders update Position before delivering SeekCompleted. A short
        // confirmation window also covers same-position seeks that raise no event.
        if ((seekCompleted && difference <= 0.1) ||
            difference <= 0.025 && nowSeconds - _requestedAt >= 0.05 ||
            difference <= 0.15 && nowSeconds - _requestedAt >= 1)
            Reset();
        else if (nowSeconds - _requestedAt >= 2)
        {
            TargetSeconds = null;
            _timedOut = true;
        }
        return TargetSeconds is not null;
    }
}
