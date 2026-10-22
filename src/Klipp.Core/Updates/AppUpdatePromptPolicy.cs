namespace Klipp.Core.Updates;

/// <summary>
/// Keeps a verified update available without repeatedly prompting after Later.
/// The UI supplies its complete foreground, activity and dialog safety gate.
/// </summary>
public sealed class AppUpdatePromptPolicy
{
    private readonly HashSet<string> _deferredVersions = new(StringComparer.Ordinal);

    public AppUpdatePromptPolicy(string? deferredVersion = null) => Defer(deferredVersion);

    /// <summary>
    /// Pass a version only while its verified package is ready to install.
    /// Checking visibility does not consume an announcement: activity may hide
    /// the card temporarily, then reveal it again when the user is idle.
    /// </summary>
    public bool ShouldShow(string? readyVersion, bool canShow) =>
        canShow && Normalize(readyVersion) is { } version && !_deferredVersions.Contains(version);

    /// <summary>Suppress this version for this policy's lifetime; other updates remain eligible.</summary>
    public void Defer(string? version)
    {
        if (Normalize(version) is { } value) _deferredVersions.Add(value);
    }

    private static string? Normalize(string? version) =>
        string.IsNullOrWhiteSpace(version) ? null : version.Trim();
}
