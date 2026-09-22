using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.CARouter;

public partial class CARouterProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            request,
            endpoint: "v1/embeddings",
            cancellationToken: cancellationToken);

        return result.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            request.ToOpenAIEmbeddingRequest(GetIdentifier()),
            endpoint: "v1/embeddings",
            cancellationToken: cancellationToken);

        var providerMetadata = GetIdentifier().CreatePrimitiveProviderMetadata();
        if (TryReadCARouterHeaderCost(result.Headers, out var cost))
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(
                new { cost },
                JsonSerializerOptions.Web);
        }

        return result.ToEmbeddingResponse(providerMetadata);
    }

    private static bool TryReadCARouterHeaderCost(
        IDictionary<string, string> headers,
        out decimal cost)
    {
        cost = 0m;

        return headers.TryGetValue("X-CARouter-Cost", out var value)
            && decimal.TryParse(
                value,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out cost);
    }
}
