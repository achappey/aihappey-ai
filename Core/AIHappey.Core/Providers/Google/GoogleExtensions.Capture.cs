using AIHappey.Abstractions.Http;
using AIHappey.Common.Extensions;
using AIHappey.Interactions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Google;

public static partial class GoogleExtensions
{
    public static ProviderBackendCaptureRequest? GetGoogleBackendCapture(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return request.Metadata.GetGoogleBackendCapture(providerId);
    }

    internal static ProviderBackendCaptureRequest? GetGoogleBackendCapture(this InteractionRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return request.Metadata.GetGoogleBackendCapture(providerId);
    }

    private static ProviderBackendCaptureRequest? GetGoogleBackendCapture(
        this Dictionary<string, object?>? metadata,
        string providerId)
    {
        try
        {
            return metadata.TryGetGoogleBackendCapture(providerId, "capture")
                ?? metadata.TryGetGoogleBackendCapture(providerId, "backend_capture");
        }
        catch
        {
            return null;
        }
    }

    private static ProviderBackendCaptureRequest? TryGetGoogleBackendCapture(
        this Dictionary<string, object?>? metadata,
        string providerId,
        string key)
    {
        return metadata?.GetProviderOption<ProviderBackendCaptureRequest>(providerId, key)
            ?? metadata?.GetProviderOption<ProviderBackendCaptureRequest>(ToSnakeCaseProviderId(providerId), key)
            ?? metadata?.GetProviderOption<ProviderBackendCaptureRequest>(ToPascalCaseProviderId(providerId), key);
    }

    private static string ToSnakeCaseProviderId(string providerId)
        => string.Concat(providerId.Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));

    private static string ToPascalCaseProviderId(string providerId)
        => string.Concat(providerId
            .Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
}
