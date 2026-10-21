namespace Klipp.Core.Editing;

/// <summary>Recognizes two released clip-body clicks, independently of pointer capture and UI gesture routing.</summary>
public sealed class EditorClipDoubleClickGate
{
    private string? _key;
    private double _time;
    private double _x;
    private double _y;
    private int _modifiers;
    private bool _released;

    public bool Press(string key, double timeSeconds, double x, double y, int modifiers = 0, bool isBody = true)
    {
        if (!isBody || string.IsNullOrEmpty(key) || !double.IsFinite(timeSeconds) || !double.IsFinite(x) || !double.IsFinite(y))
        { Reset(); return false; }
        var elapsed = timeSeconds - _time;
        var matched = _released && _key == key && _modifiers == modifiers && elapsed >= 0 && elapsed <= .55 &&
            Math.Abs(x - _x) <= 6 && Math.Abs(y - _y) <= 6;
        if (matched)
        { Reset(); return true; }
        _key = key;
        _time = timeSeconds;
        _x = x;
        _y = y;
        _modifiers = modifiers;
        _released = false;
        return false;
    }

    public void Release(bool wasClick)
    {
        if (wasClick) _released = _key is not null;
        else Reset();
    }

    public void Reset() { _key = null; _released = false; }
}
