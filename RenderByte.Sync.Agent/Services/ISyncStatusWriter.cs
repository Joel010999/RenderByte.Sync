namespace RenderByte.Sync.Agent.Services;

using System.Threading;
using System.Threading.Tasks;

public interface ISyncStatusWriter
{
    Task WriteStatusAsync(SyncStatus status, CancellationToken cancellationToken = default);
}
