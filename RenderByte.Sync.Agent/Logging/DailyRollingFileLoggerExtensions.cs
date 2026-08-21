namespace RenderByte.Sync.Agent.Logging;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class DailyRollingFileLoggerExtensions
{
    public static ILoggingBuilder AddDailyRollingFile(this ILoggingBuilder builder, string logDirectory, int retainedDays = 14)
    {
        builder.Services.AddSingleton<ILoggerProvider, DailyRollingFileLoggerProvider>(
            sp => new DailyRollingFileLoggerProvider(logDirectory, retainedDays));
        return builder;
    }
}
