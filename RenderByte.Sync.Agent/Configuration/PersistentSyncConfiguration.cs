using System;

namespace RenderByte.Sync.Agent.Configuration;

public record PersistentSyncConfiguration(
    string? SourceId,
    string? ApiUrl,
    string? Database,
    string? SqlServer,
    string? SqlUser,
    int MovementIntervalSeconds = 60,
    int StockIntervalSeconds = 300,
    int ProductIntervalSeconds = 3600,
    int TransportIdleSeconds = 60);
