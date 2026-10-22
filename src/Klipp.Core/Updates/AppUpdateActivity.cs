namespace Klipp.Core.Updates;

/// <summary>Work that must finish before a quiet download or an update restart.</summary>
public sealed record AppUpdateActivity(
    bool Recording = false,
    bool InstantClips = false,
    bool Exporting = false,
    bool Editing = false,
    bool FileOperation = false,
    bool CaptureChanging = false,
    bool Maintenance = false,
    bool Playback = false)
{
    public bool CanDownloadAutomatically => !Recording && !InstantClips && !Exporting && !Editing &&
        !FileOperation && !CaptureChanging && !Maintenance && !Playback;

    public string? RestartBlockedReason =>
        Recording ? "Stop recording before restarting to update." :
        InstantClips ? "Pause instant clips before restarting to update." :
        Exporting ? "Wait for exports to finish before restarting to update." :
        FileOperation ? "Wait for file operations to finish before restarting to update." :
        CaptureChanging ? "Wait for the capture source to finish changing before restarting to update." :
        Editing ? "Return to the library before restarting to update." :
        Playback ? "Close video playback before restarting to update." :
        Maintenance ? "Wait for storage maintenance to finish before restarting to update." :
        null;
}
