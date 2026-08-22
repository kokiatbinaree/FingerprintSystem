using Microsoft.EntityFrameworkCore;
using OfflineFingerprint.Collector.Data;

namespace OfflineFingerprint.Collector.Services;

public sealed class CloudSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CloudSyncBackgroundService> _logger;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    public CloudSyncBackgroundService(IServiceScopeFactory scopeFactory, ILogger<CloudSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected cloud sync worker error.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SyncPendingAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sync = scope.ServiceProvider.GetRequiredService<GoogleDriveSyncService>();

        var pending = await db.FingerprintImages
            .AsNoTracking()
            .Where(x => x.SyncStatus == "Pending" || x.SyncStatus == "Failed")
            .OrderBy(x => x.CapturedAtUtc)
            .Take(5)
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var id in pending)
        {
            try
            {
                var result = await sync.SyncFingerprintAsync(id, ct);
                _logger.LogInformation("Fingerprint {FingerprintId} sync result: {Result}", id, result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fingerprint {FingerprintId} sync failed. Retrying later.", id);
                try
                {
                    await Task.Delay(RetryDelay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                break;
            }
        }
    }
}
