using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIHappey.Core.Diagnostics;

public static class DebugServiceExtensions
{
    public static IServiceCollection AddProviderDebugEvents(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddScoped<RequestDebugEvents>();
        services.TryAddScoped<IProviderDebugEmitter>(sp => sp.GetRequiredService<RequestDebugEvents>());
        return services;
    }

    /// <summary>Explicit host opt-in; does not change header gating.</summary>
    public static IServiceCollection AddProviderDebugConsole(this IServiceCollection services)
    {
        services.AddProviderDebugEvents();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProviderDebugSink, ConsoleProviderDebugSink>());
        return services;
    }
}

/// <summary>Writes the same event envelope as the chat sink, not provider-specific output.</summary>
public sealed class ConsoleProviderDebugSink : IProviderDebugSink
{
    public ValueTask WriteAsync(ProviderDebugEvent debugEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine(JsonSerializer.Serialize(debugEvent, JsonSerializerOptions.Web));
        return ValueTask.CompletedTask;
    }
}
