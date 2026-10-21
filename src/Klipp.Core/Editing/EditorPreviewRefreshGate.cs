namespace Klipp.Core.Editing;

public sealed class EditorPreviewRefreshGate(double minimumIntervalSeconds)
{
    private double? _previous;

    public bool TryEnter(double nowSeconds, bool immediate)
    {
        if (immediate) { _previous = null; return true; }
        if (_previous is { } previous && nowSeconds - previous < minimumIntervalSeconds)
            return false;
        _previous = nowSeconds;
        return true;
    }
}
