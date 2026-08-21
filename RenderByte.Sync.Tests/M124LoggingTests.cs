using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RenderByte.Sync.Agent.Logging;
using Xunit;

namespace RenderByte.Sync.Tests;

public class M124LoggingTests : IDisposable
{
    private readonly string _tempDir;

    public M124LoggingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public void DailyFileLogger_WritesVisibleDataBeforeDispose()
    {
        using var provider = new DailyRollingFileLoggerProvider(_tempDir);
        var logger = provider.CreateLogger("TestCat");
        
        logger.LogInformation("This is a test message");
        
        // Ensure it's reasonably visible to another process BEFORE dispose
        var files = Directory.GetFiles(_tempDir, "*.log");
        Assert.Single(files);
        
        using var fs = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var content = sr.ReadToEnd();
        
        Assert.Contains("This is a test message", content);
    }

    [Fact]
    public void DailyFileLogger_FlushesOnDispose()
    {
        var provider = new DailyRollingFileLoggerProvider(_tempDir);
        var logger = provider.CreateLogger("TestCat2");
        
        logger.LogInformation("Message 1");
        provider.Dispose(); // simulates service stop
        
        var files = Directory.GetFiles(_tempDir, "*.log");
        Assert.Single(files);
        
        var content = File.ReadAllText(files[0]);
        Assert.Contains("Message 1", content);
    }
}
