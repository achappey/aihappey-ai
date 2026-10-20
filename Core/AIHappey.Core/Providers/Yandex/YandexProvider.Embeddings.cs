using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Yandex;

public sealed partial class YandexProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => (await SendEmbeddingsAsync(request, cancellationToken)).Response;

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SendEmbeddingsAsync(request.ToOpenAIEmbeddingRequest(GetIdentifier()), cancellationToken);
        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }

    private async Task<OpenAICompatibleEmbeddingResult> SendEmbeddingsAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);
        ApplyAuthHeader();
        var inputs = request.Input.ValueKind == JsonValueKind.Array &&
            request.Input.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
                ? request.Input.EnumerateArray().ToArray() : [request.Input];

        // Yandex documents a single text per request; adapt standardized batches without changing native options.
        List<OpenAIEmbeddingData> data = [];
        var usage = new OpenAIEmbeddingUsage();
        OpenAICompatibleEmbeddingResult? last = null;
        foreach (var input in inputs)
        {
            var native = new OpenAIEmbeddingRequest
            {
                Model = request.Model.StartsWith($"{GetIdentifier()}/", StringComparison.OrdinalIgnoreCase)
                    ? request.Model[(GetIdentifier().Length + 1)..] : request.Model,
                Input = input,
                Dimensions = request.Dimensions,
                EncodingFormat = request.EncodingFormat ?? "float",
                User = request.User,
                AdditionalProperties = request.AdditionalProperties
            };
            last = await this.OpenAICompatibleEmbeddingRequestAsync(
                _client, native, endpoint: "v1/embeddings", cancellationToken: cancellationToken);
            var entries = last.Response.Data.ToArray();
            if (entries.Length != 1)
                throw new InvalidOperationException("Yandex AI Studio must return one embedding per input text.");
            var entry = entries[0];
            entry.Index = data.Count;
            data.Add(entry);
            usage.PromptTokens += last.Response.Usage.PromptTokens;
            usage.TotalTokens += last.Response.Usage.TotalTokens;
        }

        last!.Response.Data = data;
        last.Response.Usage = usage;
        return last;
    }
}
