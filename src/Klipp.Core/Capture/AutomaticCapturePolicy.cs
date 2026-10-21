namespace Klipp.Core.Capture;

public sealed record CaptureSelectionCandidate(string Id, bool IsEligible, bool IsGame,
    bool IsForeground = false, bool IsMinimized = false);

public static class AutomaticCapturePolicy
{
    /// <summary>Manual choice wins. Automatic mode waits for a game and never falls back to desktop capture.</summary>
    public static string? Select(IReadOnlyList<CaptureSelectionCandidate> windows, string? currentId,
        string? manualTargetId = null, bool autoDetectGames = true)
    {
        if (manualTargetId is not null)
            return windows.FirstOrDefault(item => item.Id == manualTargetId && item.IsEligible)?.Id;
        if (!autoDetectGames) return null;
        var active = windows.FirstOrDefault(item => item.IsEligible && item.IsGame && item.IsForeground && !item.IsMinimized);
        if (active is not null) return active.Id;
        var current = windows.FirstOrDefault(item => item.Id == currentId && item.IsEligible && item.IsGame);
        if (current is not null) return current.Id; // Alt-tab and minimize keep the existing game.
        // A single visible recognized game can be found while Klipp owns focus.
        // With several background games, wait for the user to activate one.
        var candidates = windows.Where(item => item.IsEligible && item.IsGame && !item.IsMinimized).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0].Id : null;
    }
}
