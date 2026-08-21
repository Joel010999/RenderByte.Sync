using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent.Configuration;

namespace RenderByte.Sync.Agent;

public static class ConfigureCommandAgent
{
    public static Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("RenderByte Sync Configuration");
        Console.WriteLine("-----------------------------");

        var sqlServer = Prompt("SQL Server [SERVIDOR]: ", "SERVIDOR");
        var database = Prompt("Database [sistema]: ", "sistema");
        var sqlUser = Prompt("SQL User [sa]: ", "sa");
        var sourceId = Prompt("Source ID: ");
        var apiUrl = Prompt("API URL: ");

        var sqlPassword = PromptPassword("SQL Password: ");
        var apiKey = PromptPassword("API Key: ");

        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(apiUrl))
        {
            Console.WriteLine("[ERROR] Source ID y API URL son obligatorios.");
            return Task.FromResult(1);
        }

        try
        {
            var configDir = SyncPaths.GetConfigDirectory();
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            var configPath = SyncPaths.GetConfigFilePath();
            var secretsPath = SyncPaths.GetSecretsFilePath();

            var config = new PersistentSyncConfiguration(
                SourceId: sourceId,
                ApiUrl: apiUrl,
                Database: database,
                SqlServer: sqlServer,
                SqlUser: sqlUser
            );

            var protector = new WindowsDpapiSecretProtector();
            var secrets = new SyncSecrets(
                Version: 1,
                SqlPassword: protector.Protect(sqlPassword),
                ApiKey: protector.Protect(apiKey)
            );

            AtomicWriteJson(configPath, config);
            AtomicWriteJson(secretsPath, secrets);

            Console.WriteLine("\n[OK] Configuración guardada en: " + configDir);
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[ERROR] No se pudo guardar la configuración: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static string Prompt(string message, string defaultValue = "")
    {
        Console.Write(message);
        var input = Console.ReadLine();
        return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
    }

    private static string PromptPassword(string message)
    {
        Console.Write(message);
        var password = string.Empty;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password = password[..^1];
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password += key.KeyChar;
            }
        }
        return password;
    }

    private static void AtomicWriteJson<T>(string filePath, T data)
    {
        var tempFile = filePath + ".tmp";
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, filePath, overwrite: true);
    }
}
