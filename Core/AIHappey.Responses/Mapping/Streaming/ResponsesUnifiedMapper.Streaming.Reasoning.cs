using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static AIEventEnvelope CreateReasoningStartEnvelope(string providerId, string id, ResponseStreamContentPart responseStreamItem)
    {
        //var signature = GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "signature")?.ToString();
        var encryptedContent = GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "encrypted_content");

        return new()
        {
            Type = "reasoning-start",
            Id = id,
            Data = new AIReasoningStartEventData
            {
                Signature = encryptedContent?.ToString(),
                ProviderMetadata = CreateReasoningProviderMetadata(
                    providerId,
                    itemId: id,
                    encryptedContent: encryptedContent)
            },
        };
    }

    private static AIEventEnvelope CreateReasoningEndEnvelope(string providerId, string id, ResponseStreamContentPart responseStreamItem)
    {
        //   var signature = GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "signature")?.ToString();
        var encryptedContent = GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "encrypted_content");
        var summary = GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "summary");

        return new()
        {
            Type = "reasoning-end",
            Id = id,
            Data = new AIReasoningEndEventData
            {
                Signature = encryptedContent?.ToString(),
                ProviderMetadata = CreateReasoningProviderMetadata(
                    providerId,
                    itemId: id,
                    //  signature: signature,
                    encryptedContent: encryptedContent,
                    summary: summary)
            },
        };
    }

    private static IEnumerable<AIEventEnvelope> CreateReasoningEnvelope(
    string providerId,
    string id,
    ResponseStreamItem responseStreamItem)
    {
        string? reasoning = null;

        if (responseStreamItem.AdditionalProperties?.TryGetValue("summary", out var summaryObj) == true
            && summaryObj is JsonElement summary)
        {
            reasoning = summary.ValueKind switch
            {
                JsonValueKind.Array =>
                    string.Join(
                        "\n\n",
                        summary.EnumerateArray()
                            .Select(x => x.TryGetProperty("text", out var t) ? t.GetString() : x.ToString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                    ),

                JsonValueKind.String => summary.GetString(),

                _ => summary.ToString()
            };
        }

        var summaryVal = GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "summary");
        var encrypted = GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "encrypted_content");

        yield return new AIEventEnvelope
        {
            Type = "reasoning-start",
            Id = id,
            Data = new AIReasoningStartEventData
            {
                Signature = encrypted?.ToString(),
                ProviderMetadata = CreateReasoningProviderMetadata(
                    providerId,
                    itemId: id,
                    encryptedContent: encrypted)
            }
        };

        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            yield return new AIEventEnvelope
            {
                Type = "reasoning-delta",
                Id = id,
                Data = new AIReasoningDeltaEventData
                {
                    Delta = reasoning
                }
            };
        }

        yield return new AIEventEnvelope
        {
            Type = "reasoning-end",
            Id = id,
            Data = new AIReasoningEndEventData
            {
                Signature = encrypted?.ToString(),
                ProviderMetadata = CreateReasoningProviderMetadata(
                    providerId,
                    itemId: id,
                    encryptedContent: encrypted,
                    summary: summaryVal)
            }
        };
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateReasoningProviderMetadata(
        string providerId,
        string? itemId = null,
        object? encryptedContent = null,
        object? summary = null)
    {
        var providerMetadata = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(itemId))
        {
            providerMetadata["id"] = itemId;
            providerMetadata["item_id"] = itemId;
        }

        if (HasMeaningfulReasoningValue(encryptedContent))
            providerMetadata["encrypted_content"] = encryptedContent!;

        if (HasMeaningfulReasoningValue(summary))
            providerMetadata["summary"] = summary!;

        return providerMetadata.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = providerMetadata
            };
    }

    private static bool HasMeaningfulReasoningValue(object? value)
        => value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            JsonElement json => json.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined,
            _ => true
        };

    private static AIEventEnvelope CreateReasoningDeltaEnvelope(string id, string delta)
            => new()
            {
                Type = "reasoning-delta",
                Id = id,
                Data = new AIReasoningDeltaEventData
                {
                    Delta = delta
                }
            };
}
