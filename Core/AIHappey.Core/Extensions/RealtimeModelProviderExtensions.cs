using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.Contracts;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Extensions;

/// <summary>
/// Contract-only placeholders pending per-provider realtime implementations.
/// Intentionally separate from IModelProvider and the existing client-secrets flow.
/// </summary>
public static class RealtimeModelProviderExtensions
{
    public static Task<LiveSessionResponse> CreateLiveSessionAsync(
        this IModelProvider provider, LiveSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<LiveSessionResponse>(new NotSupportedException("Live session creation is not implemented yet."));
    }

    /// <param name="model">Provider-local model ID, after normal gateway model resolution.</param>
    public static Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(
        this IModelProvider provider, string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<RealtimeTokenResponse>(new NotSupportedException("Generic realtime token creation is not implemented yet."));
    }

    /// <param name="model">Provider-local model ID, after normal gateway model resolution.</param>
    public static Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(
        this IModelProvider provider, string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<RealtimeSessionResponse>(new NotSupportedException("Generic realtime session creation is not implemented yet."));
    }
}
