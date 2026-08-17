namespace RenderByte.Sync.Api.Auth;

/// <summary>
/// Contexto inyectado en el request tras una autenticación exitosa.
/// </summary>
public sealed record SyncAuthContext(
    int OrganizationId,
    string SourceId,
    int BranchId
);
