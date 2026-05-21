using AIHappey.Abstractions.Http;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Zai;

public static class ZaiExtensions
{
    public static ProviderBackendCaptureRequest? GetZaiBackendCapture(this ChatCompletionOptions options, string providerId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        try
        {
            return options.Metadata?.GetProviderOption<ProviderBackendCaptureRequest>(providerId, "capture")
                ?? options.Metadata?.GetProviderOption<ProviderBackendCaptureRequest>(providerId, "backend_capture");
        }
        catch
        {
            return null;
        }
    }
}
