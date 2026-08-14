using AIHappey.Core.AI;
using AIHappey.Responses.Extensions;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using System.Net.Http.Headers;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var request = PrepareResponsesRequest(options);

        var response = await this.GetResponse(_client,
                   request,
                   relativeUrl: "v1/agent",
                   cancellationToken: cancellationToken);

        await EnrichSharedFilesAsync(response, cancellationToken);

        if (response.Usage is JsonElement usage)
        {
            response.Metadata = ModelCostMetadataEnricher.AddCost(
                response.Metadata,
                TryGetPerplexityTotalCost(usage));
        }

        return response;
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var request = PrepareResponsesRequest(options);
        string? responseId = null;

        await foreach (var update in this.GetResponses(_client,
                           request,
                           relativeUrl: "v1/agent",
                           cancellationToken: cancellationToken))
        {
            if (update is ResponseCreated created)
                responseId = ValidatePerplexityResponseId(created.Response.Id);

            if (update is ResponseOutputItemDone done
                && string.Equals(done.Item.Type, "share_file", StringComparison.OrdinalIgnoreCase))
            {
                await EnrichSharedFileAsync(done.Item, responseId, cancellationToken);
            }

            if (update is ResponseCompleted completed
                && completed.Response.Usage is JsonElement usage)
            {
                completed.Response.Metadata = ModelCostMetadataEnricher.AddCost(
                    completed.Response.Metadata,
                    TryGetPerplexityTotalCost(usage));
            }

            yield return update;
        }
    }

    private async Task EnrichSharedFilesAsync(ResponseResult response, CancellationToken cancellationToken)
    {
        var responseId = ValidatePerplexityResponseId(response.Id);
        var enriched = new List<object>();

        foreach (var rawItem in response.Output ?? [])
        {
            if (rawItem is JsonElement item
                && item.ValueKind == JsonValueKind.Object
                && TryGetProperty(item, "type", out var type)
                && string.Equals(type.GetString(), "share_file", StringComparison.OrdinalIgnoreCase))
            {
                var properties = item.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase);
                var streamItem = JsonSerializer.Deserialize<ResponseStreamItem>(item.GetRawText())
                    ?? throw new InvalidOperationException("Perplexity returned an invalid share_file item.");

                await EnrichSharedFileAsync(streamItem, responseId, cancellationToken);
                foreach (var property in streamItem.AdditionalProperties ?? [])
                    properties[property.Key] = property.Value.Clone();

                enriched.Add(JsonSerializer.SerializeToElement(properties, JsonSerializerOptions.Web));
            }
            else
            {
                enriched.Add(rawItem);
            }
        }

        response.Output = enriched;
    }

    private async Task EnrichSharedFileAsync(
        ResponseStreamItem item,
        string? responseId,
        CancellationToken cancellationToken)
    {
        responseId = ValidatePerplexityResponseId(responseId);
        var fileId = GetItemString(item, "file_id");
        if (string.IsNullOrWhiteSpace(fileId))
            throw new InvalidOperationException("Perplexity share_file item did not include a file_id.");

        var path = $"v1/agent/{Uri.EscapeDataString(responseId)}/files/{Uri.EscapeDataString(fileId)}/content";
        using var download = await _client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!download.IsSuccessStatusCode)
        {
            var error = await download.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Perplexity shared file download failed ({(int)download.StatusCode}): {error}",
                null,
                download.StatusCode);
        }

        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
            throw new InvalidOperationException($"Perplexity shared file '{fileId}' was empty.");

        var mediaType = download.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var filename = GetItemString(item, "filename")
            ?? GetContentDispositionFilename(download.Content.Headers.ContentDisposition)
            ?? fileId;

        item.AdditionalProperties ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        item.AdditionalProperties["file_data"] = JsonSerializer.SerializeToElement(
            $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}");
        item.AdditionalProperties["media_type"] = JsonSerializer.SerializeToElement(mediaType);
        item.AdditionalProperties["filename"] = JsonSerializer.SerializeToElement(filename);
        item.AdditionalProperties["response_id"] = JsonSerializer.SerializeToElement(responseId);
        item.AdditionalProperties["size_bytes"] = JsonSerializer.SerializeToElement(bytes.LongLength);
    }

    private static string ValidatePerplexityResponseId(string? responseId)
        => !string.IsNullOrWhiteSpace(responseId)
           && responseId.StartsWith("resp_", StringComparison.Ordinal)
            ? responseId
            : throw new InvalidOperationException("Perplexity shared files require a response id beginning with 'resp_'.");

    private static string? GetItemString(ResponseStreamItem item, string name)
        => item.AdditionalProperties?.TryGetValue(name, out var value) == true
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string? GetContentDispositionFilename(ContentDispositionHeaderValue? disposition)
        => disposition?.FileNameStar?.Trim('"') ?? disposition?.FileName?.Trim('"');



    private ResponseRequest PrepareResponsesRequest(ResponseRequest options)
    {
        var model = options.Model;
        var usePreset = UsesResponsesPreset(options.Model);

        this.SetDefaultResponseProperties(options);

        if (usePreset)
        {
            options.AdditionalProperties ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            options.AdditionalProperties["preset"] = JsonSerializer.SerializeToElement(model?.Split("/").LastOrDefault(), JsonSerializerOptions.Web);
            options.Model = null;
        }

        var sonarOptions = new List<string>
        {
            "search_mode",
            "reasoning_effort",
            "return_images",
            "disable_search",
            "return_related_questions",
            "search_recency_filter",
            "enable_search_classifier",
            "search_after_date_filter",
            "search_before_date_filter",
            "last_updated_after_filter",
            "last_updated_before_filter",
            "web_search_options",
            "media_response"
        };

        foreach (var opt in sonarOptions)
        {
            if (options.AdditionalProperties?.ContainsKey(opt) == true)
                options.AdditionalProperties.Remove(opt);
        }

        return options;
    }
}

