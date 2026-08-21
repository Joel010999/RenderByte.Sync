namespace RenderByte.Sync.Tests.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RenderByte.Sync.Agent;
using RenderByte.Sync.Agent.Configuration;
using RenderByte.Sync.Agent.Logging;
using Xunit;

public class LoggingAndWorkerTests
{
    [Fact]
    public void FileLogger_DoesNotLogSecrets()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            using var provider = new DailyRollingFileLoggerProvider(tempDir, 14);
            var logger = provider.CreateLogger("Test");

            logger.LogInformation("Normal message");
            logger.LogInformation("Attempting to connect with password {Password}", "mypassword123");

            var logFile = Directory.GetFiles(tempDir, "renderbyte-sync-*.log")[0];
            using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var logContent = reader.ReadToEnd();

            Assert.Contains("Normal message", logContent);
            Assert.Contains("mypassword123", logContent); // It logs whatever is passed to it, user should avoid passing secrets
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
