using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    private bool IsParseModel(string? model)
        => NormalizeParseModel(model).StartsWith("parse-", StringComparison.OrdinalIgnoreCase);

    private string NormalizeParseModel(string? model)
    {
        var value = model?.Trim() ?? string.Empty;
        var prefix = $"{GetIdentifier()}/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
    }

    private async Task<AIResponse> ExecuteParseUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var files = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?
            .Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Cohere Parse requires at least one file in the latest user message.", nameof(request));

        var model = NormalizeParseModel(request.Model);
        var items = new List<AIOutputItem>(files.Count);
        var rawResponses = new List<JsonElement>(files.Count);
        var rawUsage = new List<JsonElement>();
        var finishReason = "stop";
        var status = "completed";

        ApplyAuthHeader();
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var payload = new JsonObject
            {
                ["model"] = model,
                ["document"] = new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = GetParseImageUrl(file)
                },
                ["output_format"] = "markdown"
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v2/parse")
            {
                Content = new StringContent(payload.ToJsonString(CohereJsonSerializerOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
            };
            ApplyRequestHeaders(httpRequest, request.Headers);
            var operationId = await EmitRequestDebugAsync(httpRequest, "v2/parse", cancellationToken);
            using var response = await _client.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            await EmitResponseDebugAsync(response, body, "v2/parse", operationId, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Cohere Parse failed for file {index + 1} ('{file.Filename}') with HTTP {(int)response.StatusCode}: {body}",
                    null, response.StatusCode);

            using var document = JsonDocument.Parse(body);
            var result = document.RootElement.Clone();
            rawResponses.Add(result);
            var meta = GetProperty(result, "meta");
            if (meta is { ValueKind: JsonValueKind.Object })
                rawUsage.Add(meta.Value);

            var documentFinishReason = GetString(result, "finish_reason").ToFinishReason();
            if (documentFinishReason == "error")
            {
                finishReason = "error";
                status = "failed";
            }
            else if (documentFinishReason == "length" && finishReason != "error")
            {
                finishReason = "length";
                status = "incomplete";
            }

            var markdown = new List<string>();
            if (GetProperty(result, "pages") is { ValueKind: JsonValueKind.Array } pages)
            {
                foreach (var page in pages.EnumerateArray())
                {
                    if (GetProperty(page, "markdown") is { } pageMarkdown
                        && GetString(pageMarkdown, "content") is { } content)
                        markdown.Add(content);
                }
            }

            items.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { 
                    Type = "text",
                    Text = string.Join("\n\n", markdown) }],
                Metadata = new Dictionary<string, object?>
                {
                    ["cohere.parse.file_index"] = index,
                    ["cohere.parse.filename"] = file.Filename,
                    ["cohere.parse.media_type"] = file.MediaType,
                    ["cohere.parse.response.raw"] = result,
                    ["cohere.parse.meta"] = meta,
                    ["finishReason"] = documentFinishReason
                }
            });
        }

        var usage = CreateParseUsage(rawUsage);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = $"{GetIdentifier()}/{model}",
            Status = status,
            Output = new AIOutput { Items = items },
            Usage = usage,
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = finishReason,
                ["cohere.parse.responses.raw"] = rawResponses,
                ["cohere.parse.meta"] = rawUsage
            }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamParseUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Parse has no native streaming endpoint. Adapt the completed documents to
        // ordinary unified text events, preserving the raw per-document metadata.
        var response = await ExecuteParseUnifiedAsync(request, cancellationToken);
        foreach (var item in response.Output?.Items ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eventId = Guid.NewGuid().ToString("n");
            var timestamp = DateTimeOffset.UtcNow;
            yield return CreateStreamEvent(GetIdentifier(), eventId, "text-start",
                new AITextStartEventData(), timestamp, item.Metadata);
            foreach (var text in item.Content?.OfType<AITextContentPart>() ?? [])
            {
                if (!string.IsNullOrEmpty(text.Text))
                    yield return CreateStreamEvent(GetIdentifier(), eventId, "text-delta",
                        new AITextDeltaEventData { Delta = text.Text }, timestamp, item.Metadata);
            }
            yield return CreateStreamEvent(GetIdentifier(), eventId, "text-end",
                new AITextEndEventData(), timestamp, item.Metadata);
        }

        var usage = response.NormalizedUsage;
        yield return CreateFinishStreamEvent(GetIdentifier(), request.Id ?? Guid.NewGuid().ToString("n"),
            DateTimeOffset.UtcNow, NormalizeParseModel(request.Model),
            response.Metadata?["finishReason"] as string, usage?.InputTokens, usage?.OutputTokens,
            usage?.TotalTokens, response.Metadata);
    }

    private static string? GetParseImageUrl(AIFileContentPart file)
    {
        var value = file.Data switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => file.Data?.ToString()
        };

        if (string.IsNullOrEmpty(value)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;

        // Transport normalization only: Cohere validates the image, base64, URL,
        // file type, and size. Do not decode, download, or inspect attachments here.
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? "application/octet-stream" : file.MediaType;
        return $"data:{mediaType};base64,{value}";
    }

    private static AIUsage? CreateParseUsage(List<JsonElement> metadata)
    {
        if (metadata.Count == 0)
            return null;

        var inputTokens = SumParseTokens(metadata, "input_tokens");
        var outputTokens = SumParseTokens(metadata, "output_tokens");
        return new AIUsage
        {
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalTokens = inputTokens is null && outputTokens is null ? null : (inputTokens ?? 0) + (outputTokens ?? 0),
            CachedInputTokens = SumParseTokens(metadata, "cached_tokens", nested: false)
        };
    }

    private static int? SumParseTokens(List<JsonElement> metadata, string name, bool nested = true)
    {
        int? total = null;
        foreach (var meta in metadata)
        {
            var tokens = nested ? GetProperty(meta, "tokens") : meta;
            if (tokens is { ValueKind: JsonValueKind.Object } value && GetInt32(value, name) is { } count)
                total = (total ?? 0) + count;
        }
        return total;
    }
}
