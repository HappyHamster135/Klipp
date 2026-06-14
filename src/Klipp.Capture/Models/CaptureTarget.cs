namespace Klipp.Capture.Models;

/// <summary>
/// En källa som kan capture:as via Windows Graphics Capture API.
/// Antingen ett fönster (HWND) eller en hel skärm (HMONITOR).
/// </summary>
public sealed record class CaptureTarget
{
    /// <summary>Typ av capture-mål.</summary>
    public required CaptureTargetKind Kind { get; init; }

    /// <summary>
    /// Native handle. För <see cref="CaptureTargetKind.Window"/> är detta en HWND,
    /// för <see cref="CaptureTargetKind.Monitor"/> en HMONITOR.
    /// </summary>
    public required nint Handle { get; init; }

    /// <summary>Mänskligt läsbart namn för UI:t, t.ex. "Notepad" eller "Skärm 1 (3840x2160)".</summary>
    public required string DisplayName { get; init; }

    public override string ToString() => $"{Kind}: {DisplayName}";
}

/// <summary>Typ av capture-mål.</summary>
public enum CaptureTargetKind
{
    /// <summary>Ett enskilt fönster (HWND).</summary>
    Window,

    /// <summary>En hel skärm (HMONITOR).</summary>
    Monitor
}
