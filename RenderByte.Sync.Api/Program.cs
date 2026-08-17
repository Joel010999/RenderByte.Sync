using RenderByte.Sync.Api.Auth;
using RenderByte.Sync.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Add connection string configuration
builder.Configuration.AddEnvironmentVariables();
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Host=localhost;Database=renderbyte_sync;Username=renderbyte;Password=renderbyte_password";
builder.Configuration["ConnectionStrings:DefaultConnection"] = connectionString;

var app = builder.Build();

app.UseMiddleware<SyncAuthMiddleware>();

app.MapGet("/", () => "RenderByte Sync API");

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapSyncEndpoints();

app.Run();
