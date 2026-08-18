using System;

namespace RenderByte.Sync.Agent;

public record SyncAgentOptions(
    string AlegonConnectionString,
    string SourceId,
    string ApiUrl,
    string ApiKey,
    int ReadBatchSize = 100,
    int UploadBatchSize = 200,
    int PollSeconds = 60)
{
    public static SyncAgentOptions FromEnvironment()
    {
        var connStr = Environment.GetEnvironmentVariable("RENDERBYTE_ALEGON_CONNECTION_STRING");
        var sourceId = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_SOURCE_ID");
        var apiUrl = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_URL");
        var apiKey = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY");

        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException("Falta RENDERBYTE_ALEGON_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new InvalidOperationException("Falta RENDERBYTE_SYNC_SOURCE_ID");
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new InvalidOperationException("Falta RENDERBYTE_SYNC_API_URL");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Falta RENDERBYTE_SYNC_API_KEY");

        return new SyncAgentOptions(
            AlegonConnectionString: connStr,
            SourceId: sourceId,
            ApiUrl: apiUrl,
            ApiKey: apiKey,
            ReadBatchSize: GetIntEnv("RENDERBYTE_SYNC_READ_BATCH_SIZE", 100, 1, 1000),
            UploadBatchSize: GetIntEnv("RENDERBYTE_SYNC_UPLOAD_BATCH_SIZE", 200, 1, 5000),
            PollSeconds: GetIntEnv("RENDERBYTE_SYNC_POLL_SECONDS", 60, 5, 3600)
        );
    }

    private static int GetIntEnv(string variable, int defaultValue, int min, int max)
    {
        var str = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(str)) return defaultValue;
        if (int.TryParse(str, out int val))
        {
            return Math.Clamp(val, min, max);
        }
        return defaultValue;
    }
}
