namespace Klipp.MediaServer;

public sealed class ExpiryJanitor(IExpiryGateway gateway, PrivateMediaStore store, ILogger<ExpiryJanitor> logger) : BackgroundService
{
    private readonly SemaphoreSlim _sweep = new(1, 1);

    public async Task<int> SweepAsync(CancellationToken cancellation)
    {
        if (!await _sweep.WaitAsync(0, cancellation)) return 0;
        try
        {
            store.Guard.Verify();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var claims = await gateway.ClaimExpiredAsync(deadline.Token);
            if (claims.Count > 10) throw MediaException.StorageUnavailable();
            var completed = 0;
            foreach (var claim in claims)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    await using var clipLock = await store.AcquireClipAsync(claim.OwnerId, claim.Id, deadline.Token);
                    await using var backupLock = await store.AcquireBackupReadLockAsync(deadline.Token);
                    store.DeleteAssets(claim.OwnerId, claim.Id);
                    // Quota is released only after the claimed files are actually gone.
                    await gateway.FinishExpiredAsync(claim.Id, deadline.Token);
                    completed++;
                }
                catch (MediaException) { logger.LogWarning("Expired upload cleanup will be retried."); }
                catch (IOException) { logger.LogWarning("Expired upload files could not be cleaned; their reservation remains."); }
                catch (UnauthorizedAccessException) { logger.LogWarning("Expired upload cleanup requires storage permissions review."); }
            }
            return completed;
        }
        finally { _sweep.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Expired upload maintenance is paused ({ExceptionType}).", error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
