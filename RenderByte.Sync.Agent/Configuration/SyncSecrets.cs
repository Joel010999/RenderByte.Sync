using System;

namespace RenderByte.Sync.Agent.Configuration;

public record SyncSecrets(
    int Version = 1,
    string? SqlPassword = null,
    string? ApiKey = null);
