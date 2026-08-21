using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RenderByte.Sync.Agent.Configuration;

public class SyncConfigurationResolver
{
    private readonly ISecretProtector _secretProtector;
    private readonly string _configFilePath;
    private readonly string _secretsFilePath;

    public SyncConfigurationResolver(ISecretProtector secretProtector, string? configFilePath = null, string? secretsFilePath = null)
    {
        _secretProtector = secretProtector;
        _configFilePath = configFilePath ?? SyncPaths.GetConfigFilePath();
        _secretsFilePath = secretsFilePath ?? SyncPaths.GetSecretsFilePath();
    }

    public ResolvedSyncOptions Resolve()
    {
        var envOptions = TryGetEnvironmentConfiguration();
        
        PersistentSyncConfiguration? persistentConfig = null;
        SyncSecrets? secrets = null;

        if (File.Exists(_configFilePath))
        {
            try
            {
                var configContent = File.ReadAllText(_configFilePath);
                persistentConfig = JsonSerializer.Deserialize<PersistentSyncConfiguration>(configContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"[CONFIG ERROR] Error al leer el archivo de configuración en {_configFilePath}: {ex.Message}");
            }
        }

        if (File.Exists(_secretsFilePath))
        {
            try
            {
                var secretsContent = File.ReadAllText(_secretsFilePath);
                secrets = JsonSerializer.Deserialize<SyncSecrets>(secretsContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (secrets?.Version != 1)
                {
                    throw new InvalidOperationException($"[SECRETS ERROR] Versión desconocida en secret store.");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"[SECRETS ERROR] Error al leer el archivo de secretos en {_secretsFilePath}: {ex.Message}");
            }
        }

        // Resolution logic: Environment > Config > Default

        // 1. Connection String
        string? alegonConnStr = envOptions?.AlegonConnectionString;
        if (string.IsNullOrWhiteSpace(alegonConnStr))
        {
            var server = persistentConfig?.SqlServer;
            var database = persistentConfig?.Database;
            var user = persistentConfig?.SqlUser;
            var pass = GetSecret(secrets?.SqlPassword);

            if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(database) && !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
            {
                alegonConnStr = $"Server={server};Database={database};User ID={user};Password={pass};TrustServerCertificate=True;";
            }
        }

        if (string.IsNullOrWhiteSpace(alegonConnStr))
            throw new InvalidOperationException("[CONFIG ERROR] No persistent configuration found and required environment variables are missing (Alegon Connection String/SQL Credentials).");


        // 2. Source ID
        string? sourceId = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_SOURCE_ID");
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            sourceId = persistentConfig?.SourceId;
        }

        if (string.IsNullOrWhiteSpace(sourceId) || !Guid.TryParse(sourceId, out _))
            throw new InvalidOperationException("[CONFIG ERROR] Source ID es inválido o no existe.");

        // 3. API URL
        string? apiUrl = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_URL");
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            apiUrl = persistentConfig?.ApiUrl;
        }

        if (string.IsNullOrWhiteSpace(apiUrl) || !Uri.TryCreate(apiUrl, UriKind.Absolute, out var parsedUri))
            throw new InvalidOperationException("[CONFIG ERROR] API URL es inválida.");
        
        if (parsedUri.Scheme != Uri.UriSchemeHttps && parsedUri.Host != "localhost" && parsedUri.Host != "127.0.0.1")
        {
            throw new InvalidOperationException("[CONFIG ERROR] API URL debe usar HTTPS salvo que sea localhost.");
        }


        // 4. API Key
        string? apiKey = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = GetSecret(secrets?.ApiKey);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("[CONFIG ERROR] No persistent configuration found and required environment variables are missing (API Key).");

        return new ResolvedSyncOptions(
            AlegonConnectionString: alegonConnStr,
            SourceId: sourceId,
            ApiUrl: apiUrl,
            ApiKey: apiKey,
            ReadBatchSize: GetIntEnv("RENDERBYTE_SYNC_READ_BATCH_SIZE", 100, 1, 1000),
            UploadBatchSize: GetIntEnv("RENDERBYTE_SYNC_UPLOAD_BATCH_SIZE", 200, 1, 5000),
            PollSeconds: GetIntEnv("RENDERBYTE_SYNC_POLL_SECONDS", 60, 5, 3600),
            MovementIntervalSeconds: GetIntEnv("RENDERBYTE_SYNC_MOVEMENTS_SECONDS", persistentConfig?.MovementIntervalSeconds ?? 60, 30, 3600),
            StockIntervalSeconds: GetIntEnv("RENDERBYTE_SYNC_STOCK_SECONDS", persistentConfig?.StockIntervalSeconds ?? 300, 60, 86400),
            ProductIntervalSeconds: GetIntEnv("RENDERBYTE_SYNC_PRODUCTS_SECONDS", persistentConfig?.ProductIntervalSeconds ?? 3600, 300, 86400)
        );
    }

    private string? GetSecret(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return null;
        try
        {
            return _secretProtector.Unprotect(protectedValue);
        }
        catch
        {
            throw new InvalidOperationException("[SECRETS ERROR] Unable to decrypt local secret store.");
        }
    }

    private ResolvedSyncOptions? TryGetEnvironmentConfiguration()
    {
        var connStr = Environment.GetEnvironmentVariable("RENDERBYTE_ALEGON_CONNECTION_STRING");
        var sourceId = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_SOURCE_ID");
        var apiUrl = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_URL");
        var apiKey = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY");

        if (!string.IsNullOrWhiteSpace(connStr) && !string.IsNullOrWhiteSpace(sourceId) && !string.IsNullOrWhiteSpace(apiUrl) && !string.IsNullOrWhiteSpace(apiKey))
        {
            // Complete environment config exists (Legacy)
            return new ResolvedSyncOptions(
                AlegonConnectionString: connStr,
                SourceId: sourceId,
                ApiUrl: apiUrl,
                ApiKey: apiKey
            );
        }

        return null;
    }

    private static int GetIntEnv(string variable, int defaultValue, int min, int max)
    {
        var str = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(str)) return Math.Clamp(defaultValue, min, max);
        if (int.TryParse(str, out int val))
        {
            return Math.Clamp(val, min, max);
        }
        return Math.Clamp(defaultValue, min, max);
    }
}
