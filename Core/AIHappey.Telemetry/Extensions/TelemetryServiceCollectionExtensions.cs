using AIHappey.Telemetry.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Telemetry;

public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the telemetry DbContext and ChatTelemetryService in DI.
    /// </summary>
    public static IServiceCollection AddTelemetryServices(
        this IServiceCollection services,
        string connectionString)
    {
        // DbContext registration (configure from appsettings)
        services.AddDbContext<AIHappeyTelemetryDatabaseContext>(options =>
            options.UseSqlServer(connectionString)); // or UseNpgsql etc.

        // your service
        services.AddScoped<IChatTelemetryService, ChatTelemetryService>();
        services.AddScoped<IChatStatisticsService, ChatStatisticsService>();

        return services;
    }
}
