using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using RenderByte.Sync.Contracts;

namespace RenderByte.Sync.Api.Auth;

public class SyncAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _connectionString;

    public SyncAuthMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("DefaultConnection no configurada.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out var authHeaderValue))
        {
            await ReturnError(context, 401, "UNAUTHORIZED", "API key inválida o ausente.");
            return;
        }

        var header = authHeaderValue.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await ReturnError(context, 401, "UNAUTHORIZED", "Formato de token inválido. Use 'Bearer <key>'.");
            return;
        }

        var apiKey = header.Substring("Bearer ".Length).Trim();
        var hashedKeyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));

        using var connection = new NpgsqlConnection(_connectionString);
        var sources = await connection.QueryAsync<SourceAuthDto>(
            "SELECT organization_id, source_id, branch_id, api_key_hash FROM sources WHERE is_active = true"
        );

        SourceAuthDto? source = null;
        foreach (var s in sources)
        {
            try
            {
                var storedHashBytes = Convert.FromHexString(s.api_key_hash);
                if (CryptographicOperations.FixedTimeEquals(hashedKeyBytes, storedHashBytes))
                {
                    source = s;
                    break;
                }
            }
            catch { /* hash stored is invalid hex, ignore */ }
        }

        if (source == null)
        {
            await ReturnError(context, 401, "UNAUTHORIZED", "API key inválida o inactiva.");
            return;
        }

        // Si es una petición a /v1/sync/movements, validamos que el source_id del body coincida
        context.Request.EnableBuffering();
        
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            var bodyText = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                try
                {
                    var body = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(bodyText);
                    if (body != null && body.TryGetPropertyValue("source_id", out var sourceIdNode))
                    {
                        var bodySourceId = sourceIdNode?.ToString();
                        if (bodySourceId != source.source_id)
                        {
                            await ReturnError(context, 403, "SOURCE_MISMATCH", "El source_id del request no corresponde a las credenciales presentadas.");
                            return;
                        }
                    }
                }
                catch
                {
                    // Dejamos que el controller maneje el bad request si el JSON es inválido
                }
            }
        }

        // Auth exitosa: Inyectar contexto y actualizar last_seen_at
        context.Items["SyncAuthContext"] = new SyncAuthContext(source.organization_id, source.source_id, source.branch_id);

        try
        {
            await connection.ExecuteAsync("UPDATE sources SET last_seen_at = NOW() WHERE source_id = @SourceId", new { SourceId = source.source_id });
        }
        catch { /* Ignorar error no crítico */ }

        await _next(context);
    }

    private static async Task ReturnError(HttpContext context, int statusCode, string error, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var errorResponse = new SyncErrorResponse(error, message, null, null);
        await context.Response.WriteAsJsonAsync(errorResponse);
    }

    private record SourceAuthDto(int organization_id, string source_id, int branch_id, string api_key_hash);
}
