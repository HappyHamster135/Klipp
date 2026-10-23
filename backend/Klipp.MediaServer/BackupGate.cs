using System.Text.Json;

namespace Klipp.MediaServer;

public interface IBackupGate { void EnsureUploadsAllowed(); }

public sealed class BackupGate(MediaOptions options, TimeProvider time) : IBackupGate
{
    public void EnsureUploadsAllowed()
    {
        if (!options.UploadsEnabled)
            throw new MediaException(503, "uploads_paused", "Uploads are paused while clip storage is being prepared.");
        try
        {
            using var file = new FileStream(options.BackupStatusPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 4096) throw MediaException.StorageUnavailable();
            using var json = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 4 });
            var successful = json.RootElement.GetProperty("successful_at").GetDateTimeOffset();
            var verified = json.RootElement.GetProperty("verified_restore_at").GetDateTimeOffset();
            var databaseSuccessful = json.RootElement.GetProperty("database_successful_at").GetDateTimeOffset();
            var databaseVerified = json.RootElement.GetProperty("database_verified_restore_at").GetDateTimeOffset();
            var now = time.GetUtcNow();
            if (successful > now.AddMinutes(5) || successful < now.AddHours(-48)
                || databaseSuccessful > now.AddMinutes(5) || databaseSuccessful < now.AddHours(-48)
                || verified > now.AddMinutes(5) || verified < now.AddDays(-8)
                || databaseVerified > now.AddMinutes(5) || databaseVerified < now.AddDays(-8))
                throw new MediaException(503, "backup_required", "Uploads are paused until the latest backup has been verified.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        { throw new MediaException(503, "backup_required", "Uploads are paused until the latest backup has been verified."); }
    }
}
