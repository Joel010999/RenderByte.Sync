namespace RenderByte.Sync.Agent.Services;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IWindowsServiceManager
{
    Task InstallAsync(string serviceName, string displayName, string description, string exePath, string arguments, CancellationToken cancellationToken = default);
    Task UninstallAsync(string serviceName, CancellationToken cancellationToken = default);
    Task StartAsync(string serviceName, CancellationToken cancellationToken = default);
    Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<string> GetStatusAsync(string serviceName, CancellationToken cancellationToken = default);
    Task ConfigureRecoveryAsync(string serviceName, CancellationToken cancellationToken = default);
    bool IsInstalled(string serviceName);
}
