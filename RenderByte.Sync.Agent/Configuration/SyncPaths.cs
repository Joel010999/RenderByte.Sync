using System;
using System.IO;

namespace RenderByte.Sync.Agent.Configuration;

public static class SyncPaths
{
    public static string GetConfigDirectory()
    {
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(commonAppData, "RenderByte", "Sync");
    }

    public static string GetConfigFilePath()
    {
        var customPath = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            return customPath;
        }
        return Path.Combine(GetConfigDirectory(), "config.json");
    }

    public static string GetSecretsFilePath()
    {
        var configPath = GetConfigFilePath();
        var directory = Path.GetDirectoryName(configPath) ?? GetConfigDirectory();
        return Path.Combine(directory, "secrets.json");
    }
}
