using System;
using System.IO;
using System.Text.Json;
using Xunit;
using RenderByte.Sync.Agent.Configuration;
using Moq;

namespace RenderByte.Sync.Tests.Configuration;

[Collection("EnvVars")]
public class SyncConfigurationResolverTests : IDisposable
{
    private readonly string _tempConfig;
    private readonly string _tempSecrets;
    private readonly Mock<ISecretProtector> _protectorMock;

    public SyncConfigurationResolverTests()
    {
        _tempConfig = Path.GetTempFileName();
        _tempSecrets = Path.GetTempFileName();
        _protectorMock = new Mock<ISecretProtector>();

        // Clear relevant env vars to isolate tests
        Environment.SetEnvironmentVariable("RENDERBYTE_ALEGON_CONNECTION_STRING", null);
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_SOURCE_ID", null);
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", null);
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", null);
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_MOVEMENTS_SECONDS", null);
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_STOCK_SECONDS", null);
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_PRODUCTS_SECONDS", null);
    }

    public void Dispose()
    {
        if (File.Exists(_tempConfig)) File.Delete(_tempConfig);
        if (File.Exists(_tempSecrets)) File.Delete(_tempSecrets);
    }

    private void WriteConfig(PersistentSyncConfiguration config) => 
        File.WriteAllText(_tempConfig, JsonSerializer.Serialize(config));

    private void WriteSecrets(SyncSecrets secrets) => 
        File.WriteAllText(_tempSecrets, JsonSerializer.Serialize(secrets));

    [Fact]
    public void ConfigResolver_EnvironmentOverridesPersistentConfig()
    {
        WriteConfig(new PersistentSyncConfiguration(
            SourceId: Guid.NewGuid().ToString(),
            ApiUrl: "https://config.example.com",
            Database: "configDb",
            SqlServer: "configServer",
            SqlUser: "sa"));
        
        WriteSecrets(new SyncSecrets(1, "encSqlPass", "encApiKey"));
        _protectorMock.Setup(p => p.Unprotect("encSqlPass")).Returns("sqlPass");
        _protectorMock.Setup(p => p.Unprotect("encApiKey")).Returns("apiKey");

        Environment.SetEnvironmentVariable("RENDERBYTE_ALEGON_CONNECTION_STRING", "Server=env;Database=env;User=sa;Password=123;");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_SOURCE_ID", Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "https://env.example.com");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "envKey");

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        var options = resolver.Resolve();

        Assert.Equal("Server=env;Database=env;User=sa;Password=123;", options.AlegonConnectionString);
        Assert.Equal(Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_SOURCE_ID"), options.SourceId);
        Assert.Equal("https://env.example.com", options.ApiUrl);
        Assert.Equal("envKey", options.ApiKey);
    }

    [Fact]
    public void ConfigResolver_PersistentConfigUsedWhenEnvMissing()
    {
        var expectedSourceId = Guid.NewGuid().ToString();
        WriteConfig(new PersistentSyncConfiguration(
            SourceId: expectedSourceId,
            ApiUrl: "https://config.example.com",
            Database: "configDb",
            SqlServer: "configServer",
            SqlUser: "sa"));
        
        WriteSecrets(new SyncSecrets(1, "encSqlPass", "encApiKey"));
        _protectorMock.Setup(p => p.Unprotect("encSqlPass")).Returns("sqlPass");
        _protectorMock.Setup(p => p.Unprotect("encApiKey")).Returns("apiKey");

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        var options = resolver.Resolve();

        Assert.Equal($"Server=configServer;Database=configDb;User ID=sa;Password=sqlPass;TrustServerCertificate=True;", options.AlegonConnectionString);
        Assert.Equal(expectedSourceId, options.SourceId);
        Assert.Equal("https://config.example.com", options.ApiUrl);
        Assert.Equal("apiKey", options.ApiKey);
    }

    [Fact]
    public void ConfigResolver_DefaultIntervalsApplied()
    {
        var expectedSourceId = Guid.NewGuid().ToString();
        WriteConfig(new PersistentSyncConfiguration(
            SourceId: expectedSourceId,
            ApiUrl: "https://config.example.com",
            Database: "configDb",
            SqlServer: "configServer",
            SqlUser: "sa")); // omit intervals

        WriteSecrets(new SyncSecrets(1, "encSqlPass", "encApiKey"));
        _protectorMock.Setup(p => p.Unprotect(It.IsAny<string>())).Returns("fake");

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        var options = resolver.Resolve();

        Assert.Equal(60, options.MovementIntervalSeconds);
        Assert.Equal(300, options.StockIntervalSeconds);
        Assert.Equal(3600, options.ProductIntervalSeconds);
    }

    [Fact]
    public void ConfigResolver_RejectsMissingSecrets()
    {
        WriteConfig(new PersistentSyncConfiguration(
            SourceId: Guid.NewGuid().ToString(),
            ApiUrl: "https://config.example.com",
            Database: "configDb",
            SqlServer: "configServer",
            SqlUser: "sa"));
        
        // No secrets file
        if (File.Exists(_tempSecrets)) File.Delete(_tempSecrets);

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        
        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve());
        Assert.Contains("missing (Alegon Connection String", ex.Message);
    }

    [Fact]
    public void ConfigResolver_RejectsInvalidSourceId()
    {
        WriteConfig(new PersistentSyncConfiguration(
            SourceId: "not-a-guid",
            ApiUrl: "https://config.example.com",
            Database: "configDb",
            SqlServer: "configServer",
            SqlUser: "sa"));
        
        WriteSecrets(new SyncSecrets(1, "encSqlPass", "encApiKey"));
        _protectorMock.Setup(p => p.Unprotect(It.IsAny<string>())).Returns("fake");

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        
        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve());
        Assert.Contains("Source ID es inválido", ex.Message);
    }

    [Fact]
    public void ConfigResolver_RejectsRemoteHttpApiUrl()
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", null);
        WriteConfig(new PersistentSyncConfiguration(
            SourceId: Guid.NewGuid().ToString(),
            ApiUrl: "http://config.example.com",
            Database: "db", SqlServer: "server", SqlUser: "sa"));
        WriteSecrets(new SyncSecrets(1, "p", "a"));
        _protectorMock.Setup(p => p.Unprotect(It.IsAny<string>())).Returns("fake");

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve());
        Assert.Contains("API URL debe usar HTTPS", ex.Message);
    }

    [Fact]
    public void ConfigResolver_AllowsLocalhostHttp()
    {
        WriteConfig(new PersistentSyncConfiguration(
            SourceId: Guid.NewGuid().ToString(),
            ApiUrl: "http://localhost:5000",
            Database: "db", SqlServer: "server", SqlUser: "sa"));
        WriteSecrets(new SyncSecrets(1, "p", "a"));
        _protectorMock.Setup(p => p.Unprotect(It.IsAny<string>())).Returns("fake");

        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", null);

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        var options = resolver.Resolve(); // Should not throw
        Assert.Equal("http://localhost:5000", options.ApiUrl);
    }

    [Theory]
    [InlineData(10, 30)] // Below min -> clamped to min
    [InlineData(4000, 3600)] // Above max -> clamped to max
    [InlineData(100, 100)] // Within range -> accepted
    public void ConfigResolver_ValidatesMovementIntervalRange(int input, int expected)
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_MOVEMENTS_SECONDS", null);
        WriteConfig(new PersistentSyncConfiguration(
            SourceId: Guid.NewGuid().ToString(),
            ApiUrl: "https://localhost",
            Database: "db", SqlServer: "srv", SqlUser: "sa",
            MovementIntervalSeconds: input));
        WriteSecrets(new SyncSecrets(1, "p", "a"));
        _protectorMock.Setup(p => p.Unprotect(It.IsAny<string>())).Returns("fake");

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        var options = resolver.Resolve();
        Assert.Equal(expected, options.MovementIntervalSeconds);
    }

    [Theory]
    [InlineData(30, 60)]
    [InlineData(90000, 86400)]
    [InlineData(500, 500)]
    public void ConfigResolver_ValidatesStockIntervalRange(int input, int expected)
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_STOCK_SECONDS", null);
        WriteConfig(new PersistentSyncConfiguration(
            SourceId: Guid.NewGuid().ToString(),
            ApiUrl: "https://localhost",
            Database: "db", SqlServer: "srv", SqlUser: "sa",
            StockIntervalSeconds: input));
        WriteSecrets(new SyncSecrets(1, "p", "a"));
        _protectorMock.Setup(p => p.Unprotect(It.IsAny<string>())).Returns("fake");

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        var options = resolver.Resolve();
        Assert.Equal(expected, options.StockIntervalSeconds);
    }

    [Theory]
    [InlineData(100, 300)]
    [InlineData(90000, 86400)]
    [InlineData(4000, 4000)]
    public void ConfigResolver_ValidatesProductIntervalRange(int input, int expected)
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_PRODUCTS_SECONDS", null);
        WriteConfig(new PersistentSyncConfiguration(
            SourceId: Guid.NewGuid().ToString(),
            ApiUrl: "https://localhost",
            Database: "db", SqlServer: "srv", SqlUser: "sa",
            ProductIntervalSeconds: input));
        WriteSecrets(new SyncSecrets(1, "p", "a"));
        _protectorMock.Setup(p => p.Unprotect(It.IsAny<string>())).Returns("fake");

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        var options = resolver.Resolve();
        Assert.Equal(expected, options.ProductIntervalSeconds);
    }

    [Fact]
    public void LegacyEnvironmentOnlyConfiguration_StillWorks()
    {
        // No files
        if (File.Exists(_tempConfig)) File.Delete(_tempConfig);
        if (File.Exists(_tempSecrets)) File.Delete(_tempSecrets);

        Environment.SetEnvironmentVariable("RENDERBYTE_ALEGON_CONNECTION_STRING", "Server=legacy");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_SOURCE_ID", Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "https://legacy.example.com");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "legacyKey");

        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        var options = resolver.Resolve(); // Should not throw

        Assert.Equal("Server=legacy", options.AlegonConnectionString);
        Assert.Equal("https://legacy.example.com", options.ApiUrl);
    }

    [Fact]
    public void ConfigStore_RejectsCorruptJson()
    {
        File.WriteAllText(_tempConfig, "{ corrupt_json ");
        var resolver = new SyncConfigurationResolver(_protectorMock.Object, _tempConfig, _tempSecrets);
        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve());
        Assert.Contains("Error al leer el archivo de configuración", ex.Message);
    }
}
