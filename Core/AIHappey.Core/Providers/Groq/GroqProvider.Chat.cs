using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
         ChatRequest chatRequest,
         [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken: cancellationToken);

        switch (model.Type)
        {
            case "speech":
                {
                    await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                        yield return p;

                    yield break;
                }
            case "transcription":
                {
                    await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                        yield return p;

                    yield break;
                }
            default:
                {
                    await foreach (var part in this.StreamResponsesAsync(
                            chatRequest,
                            mappingOptionsFactory: CreateResponsesStreamMappingOptions,
                            cancellationToken: cancellationToken))
                    {
                        yield return part;
                    }

                    yield break;
                }


        }
    }

    private ResponsesStreamMappingOptions CreateResponsesStreamMappingOptions(ChatRequest chatRequest)
        => new()
        {
            FinishFactory = CreateResponsesFinishPart
        };

    private FinishUIPart CreateResponsesFinishPart(ResponseResult response)
    {
        var resolvedModelId = response.Model.ToModelId(GetIdentifier());
        var pricing = ResolveCatalogPricing(string.IsNullOrWhiteSpace(response.Model)
            ? null
            : response.Model);

        var finish = "stop".ToFinishUIPart(
            resolvedModelId,
            GetUsageValue(response.Usage, "output_tokens", "outputTokens") ?? 0,
            GetUsageValue(response.Usage, "input_tokens", "inputTokens", "prompt_tokens", "promptTokens") ?? 0,
            ModelCostMetadataEnricher.GetTotalTokens(response.Usage) ?? 0,
            response.Temperature,
            reasoningTokens: GetUsageValue(response.Usage, "reasoning_tokens", "reasoningTokens"));

        return ModelCostMetadataEnricher.AddCost(finish, pricing);
    }

    private static int? GetUsageValue(object? usage, params string[] propertyNames)
    {
        if (usage == null)
            return null;

        try
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(usage, System.Text.Json.JsonSerializerOptions.Web);
            foreach (var name in propertyNames)
            {
                if (json.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var number))
                    return number;
            }

            if (json.TryGetProperty("output_tokens_details", out var outputDetails))
            {
                foreach (var name in propertyNames)
                {
                    if (outputDetails.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var number))
                        return number;
                }
            }
        }
        catch
        {
        }

        return null;
    }
}
