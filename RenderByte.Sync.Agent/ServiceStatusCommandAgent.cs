namespace RenderByte.Sync.Agent;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent.Configuration;
using RenderByte.Sync.Agent.Services;

public static class ServiceStatusCommandAgent
{
    public static async Task<int> RunAsync(IWindowsServiceManager serviceManager, CancellationToken cancellationToken)
    {
        const string serviceName = "RenderByteSync";
        if (!serviceManager.IsInstalled(serviceName))
        {
            Console.WriteLine($"Service: {serviceName}");
            Console.WriteLine("Status: NOT INSTALLED");
            return 0;
        }

        try
        {
            var status = await serviceManager.GetStatusAsync(serviceName, cancellationToken);
            Console.WriteLine($"Service: {serviceName}");
            Console.WriteLine($"Status: {status}");
            
            var statusFile = Path.Combine(SyncPaths.GetConfigDirectory(), "status.json");
            if (File.Exists(statusFile))
            {
                try
                {
                    var content = File.ReadAllText(statusFile);
                    var syncStatus = JsonSerializer.Deserialize<SyncStatus>(content);
                    if (syncStatus != null)
                    {
                        Console.WriteLine($"\n--- Operational Status ---");
                        Console.WriteLine($"Source ID: {syncStatus.SourceId}");
                        Console.WriteLine($"Version: {syncStatus.ServiceVersion}");
                        Console.WriteLine($"Started At (UTC): {syncStatus.StartedAtUtc}");
                        Console.WriteLine($"Movements Pending: {syncStatus.MovementPending}");
                        Console.WriteLine($"Stocks Pending: {syncStatus.StockPending}");
                        Console.WriteLine($"Products Pending: {syncStatus.ProductPending}");
                        if (syncStatus.LastError != null)
                        {
                            Console.WriteLine($"Last Error: {syncStatus.LastError}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"\n[ERROR] Failed to read operational status: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("Operational status: not yet available");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to query service status: {ex.Message}");
            return 1;
        }
    }
}
