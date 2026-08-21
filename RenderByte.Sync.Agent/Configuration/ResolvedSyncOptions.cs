namespace RenderByte.Sync.Agent.Configuration;

public record ResolvedSyncOptions(
    string AlegonConnectionString,
    string SourceId,
    string ApiUrl,
    string ApiKey,
    int ReadBatchSize = 100,
    int UploadBatchSize = 200,
    int PollSeconds = 60,
    int MovementIntervalSeconds = 60,
    int StockIntervalSeconds = 300,
    int ProductIntervalSeconds = 3600);
