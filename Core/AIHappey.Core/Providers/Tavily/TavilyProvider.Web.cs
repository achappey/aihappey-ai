using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    private async Task<AIResponse> ExecuteWebAsync(AIRequest request, string model, CancellationToken cancellationToken)
    {
        var payload = ReadOptions(request);
        var urls = ResolveUrls(request, payload);
        if (model == "crawl" && urls.Count != 1)
            throw new ArgumentException("Tavily Crawl requires exactly one distinct URL from metadata and current user file-URL inputs.");
        if (model == "extract" && urls.Count is < 1 or > 20)
            throw new ArgumentException("Tavily Extract requires 1–20 distinct URLs from metadata and current user file-URL inputs.");

        payload.Remove("url");
        payload.Remove("urls");
        payload.Remove("stream"); // These two upstream endpoints are synchronous.
        payload[model == "crawl" ? "url" : "urls"] = model == "crawl" ? urls[0] : urls;
        var promptKey = model == "crawl" ? "instructions" : "query";
        var prompt = CurrentUserText(request);
        if (!payload.ContainsKey(promptKey) && !string.IsNullOrWhiteSpace(prompt))
            payload[promptKey] = prompt;

        using var httpRequest = CreateJsonRequest(model, payload);
        var root = await SendJsonAsync(httpRequest, cancellationToken);
        var results = GetProperty(root, "results");
        var sources = results is JsonElement pages ? ParseSources(pages) : [];
        var sections = new List<string>();
        if (results is { ValueKind: JsonValueKind.Array })
        {
            foreach (var page in results.Value.EnumerateArray())
            {
                var content = GetString(page, "raw_content") ?? string.Empty;
                var url = GetString(page, "url");
                sections.Add(url is null ? content : $"## {url}\n\n{content}");
            }
        }
        if (GetProperty(root, "failed_results") is { ValueKind: JsonValueKind.Array } failures)
        {
            foreach (var failure in failures.EnumerateArray())
                sections.Add($"## Extraction failed: {GetString(failure, "url")}\n\n{GetString(failure, "error") ?? failure.GetRawText()}");
        }
        if (sections.Count == 0)
            sections.Add("Tavily returned no extracted content.");
        return CreateResponse(request, model, root, string.Join("\n\n", sections), sources);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamWebAsync(AIRequest request, string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteWebAsync(request, model, cancellationToken);
        var metadata = response.Metadata!;
        var id = metadata["responses.id"]!.ToString()!;
        var timestamp = DateTimeOffset.UtcNow;
        var text = string.Concat(response.Output!.Items!.Where(item => item.Type == "message")
            .SelectMany(item => item.Content ?? []).OfType<AITextContentPart>().Select(part => part.Text));
        var providerMetadata = TextProviderMetadata(metadata);
        yield return CreateStreamEvent(GetIdentifier(), "text-start", id, timestamp,
            new AITextStartEventData { ProviderMetadata = providerMetadata }, metadata);
        yield return CreateStreamEvent(GetIdentifier(), "text-delta", id, timestamp,
            new AITextDeltaEventData { Delta = text, ProviderMetadata = providerMetadata }, metadata);
        yield return CreateStreamEvent(GetIdentifier(), "text-end", id, timestamp,
            new AITextEndEventData { ProviderMetadata = providerMetadata }, metadata);
        if (GetProperty((JsonElement)metadata["tavily.response.raw"]!, "results") is JsonElement pages)
        {
            foreach (var source in ParseSources(pages).DistinctBy(source => UrlKey(source.Url)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return CreateSourceStreamEvent(GetIdentifier(), source, timestamp, metadata, id);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        yield return CreateFinishEvent(request, id, response.Model!, timestamp, response.Usage, metadata);
    }
}
