namespace RenderByte.Sync.Agent;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RenderByte.Sync.Agent.Configuration;
using RenderByte.Sync.Infrastructure.Alegon;

public class RenderByteSyncWorker : BackgroundService
{
    private readonly ResolvedSyncOptions _options;
    private readonly AlegonReader _reader;
    private readonly ILogger<RenderByteSyncWorker> _logger;

    public RenderByteSyncWorker(ResolvedSyncOptions options, AlegonReader reader, ILogger<RenderByteSyncWorker> logger)
    {
        _options = options;
        _reader = reader;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[START] RenderByte Sync v0.12.0 - Windows Service Mode starting...");
        
        SyncInstanceGuard? guard = null;
        try
        {
            guard = SyncInstanceGuard.AcquireOrThrow(_options.SourceId);
            _logger.LogInformation("Acquired instance guard for SourceId: {SourceId}", _options.SourceId);

            await ContinuousRunAgent.RunAsync(_options, _reader, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Service stop requested, shutting down cleanly.");
        }
        catch (SyncAlreadyRunningException ex)
        {
            _logger.LogCritical(ex, "Failed to start service because another instance is already running.");
            Environment.Exit(3);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "A fatal unhandled exception occurred in the sync worker.");
            Environment.Exit(1);
        }
        finally
        {
            guard?.Dispose();
            _logger.LogInformation("[STOP] RenderByte Sync worker has stopped.");
        }
    }
}
