using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
{
    private const string SearchModel = "search";
    private const string FetchModel = "fetch";

    private static bool IsWebModel(string? model) => NormalizeModelId(model) is SearchModel or FetchModel;

    private static AIInputItem? LastUserInput(AIRequest request)
        => request.Input?.Items?.LastOrDefault(item => item.Type == "message"
            && string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));

    private static string LastUserText(AIRequest request)
        => LastUserInput(request) is { } item
            ? string.Join("\n", (item.Content ?? []).OfType<AITextContentPart>().Select(part => part.Text))
            : request.Input?.Text ?? string.Empty;

    private Dictionary<string, JsonElement> WebOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var value) != true || value is null)
            return [];
        var json = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        if (json.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("DeepSeek provider metadata must be a JSON object.");
        return json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static void ValidateSearchText(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || text.EnumerateRunes().Count() > 128)
            throw new ArgumentException($"DeepSeek {name} must contain 1–128 Unicode characters and cannot be all whitespace.");
    }

    private static Dictionary<string, object?> SearchPayload(AIRequest request, Dictionary<string, JsonElement> options)
    {
        var query = LastUserText(request);
        ValidateSearchText(query, "query");
        var payload = new Dictionary<string, object?> { ["query"] = query };

        if (options.TryGetValue("count", out var count))
        {
            if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var number) || number is < 1 or > 50)
                throw new ArgumentException("DeepSeek count must be an integer between 1 and 50.");
            payload["count"] = number;
        }
        if (options.TryGetValue("ori_question", out var question) && question.ValueKind != JsonValueKind.Null)
        {
            if (question.ValueKind != JsonValueKind.String)
                throw new ArgumentException("DeepSeek ori_question must be a string or null.");
            ValidateSearchText(question.GetString(), "ori_question");
            payload["ori_question"] = question.GetString();
        }
        if (options.TryGetValue("localization", out var localization))
        {
            if (localization.ValueKind != JsonValueKind.String
                || (localization.GetString() is { Length: > 0 } ip && !IPAddress.TryParse(ip, out _)))
                throw new ArgumentException("DeepSeek localization must be an IP address or an empty string.");
            payload["localization"] = localization.GetString();
        }
        if (options.TryGetValue("need_content", out var content))
        {
            if (content.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("DeepSeek need_content must be a boolean.");
            payload["need_content"] = content.GetBoolean();
        }
        return payload;
    }

    private static string FetchUrl(AIRequest request)
    {
        var candidates = new List<string>();
        foreach (Match match in Regex.Matches(LastUserText(request), "https?://[^\\s<>\"`]+", RegexOptions.IgnoreCase))
        {
            var url = match.Value.TrimEnd('.', ',', ';', '!', '?');
            // Remove Markdown/prose delimiters, but keep balanced URL parentheses.
            while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '('))
                url = url[..^1];
            url = url.TrimEnd(']');
            candidates.Add(url);
        }
        foreach (var file in (LastUserInput(request)?.Content ?? []).OfType<AIFileContentPart>())
        {
            var value = file.Data switch
            {
                string text => text,
                Uri uri => uri.AbsoluteUri,
                JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(value))
                candidates.Add(value);
        }

        var urls = candidates.Distinct(StringComparer.Ordinal).ToList();
        if (urls.Count != 1)
            throw new ArgumentException("DeepSeek Fetch requires exactly one distinct URL in the current user input.");
        var result = urls[0];
        if (result.Length > 8192 || !Uri.TryCreate(result, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo))
            throw new ArgumentException("DeepSeek Fetch requires an HTTP(S) URL with a host, no username/password, and at most 8192 characters.");
        return result;
    }

    private async Task<AIResponse> ExecuteWebAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var model = NormalizeModelId(request.Model)!;
        var options = WebOptions(request);
        var url = model == FetchModel ? FetchUrl(request) : null;
        var payload = model == SearchModel ? SearchPayload(request, options)
            : new Dictionary<string, object?> { ["url"] = url };
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, $"v1/tools/{model}")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _client.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"DeepSeek {model} API error: {body}", null, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var raw = document.RootElement.Clone();
        var sources = new List<WebSource>();
        string text;
        bool billable;
        if (model == SearchModel)
        {
            var results = raw.GetProperty("results");
            if (results.ValueKind != JsonValueKind.Array)
                throw new JsonException("DeepSeek search results must be an array.");
            billable = results.GetArrayLength() > 0;
            var sections = new List<string>();
            foreach (var result in results.EnumerateArray())
            {
                var title = result.GetProperty("title").GetString() ?? string.Empty;
                var resultUrl = result.GetProperty("url").GetString() ?? string.Empty;
                var snippet = result.GetProperty("snippet").GetString() ?? string.Empty;
                sections.Add($"## {title}\n\n{resultUrl}\n\n{snippet}");
                if (!string.IsNullOrWhiteSpace(resultUrl))
                    sources.Add(new(resultUrl, title, result.Clone()));
            }
            text = sections.Count > 0 ? string.Join("\n\n", sections) : "No search results found.";
        }
        else
        {
            text = raw.GetProperty("content").GetString() ?? string.Empty;
            billable = !string.IsNullOrWhiteSpace(text);
            if (billable)
                sources.Add(new(url!, raw.GetProperty("title").GetString() ?? url!, raw));
        }

        // Request units are usage, not fabricated input/output tokens. Empty HTTP 200s cost zero.
        var usage = new Dictionary<string, object?> { ["billable_requests"] = billable ? 1 : 0 };
        var requestPrice = ResolveRuntimePricing(null, request.Model)?.Input ?? 0.00075m;
        var metadata = ModelCostMetadataEnricher.AddCost(
            new Dictionary<string, object?> { [GetIdentifier()] = raw },
            billable ? requestPrice : 0m);
        var items = new List<AIOutputItem>
        {
            new() { Type = "message", Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = text }] }
        };
        items.AddRange(sources.DistinctBy(source => source.Url).Select(source => new AIOutputItem
        {
            Type = "source-url",
            Content = [new AITextContentPart { Type = "text", Text = source.Title }],
            Metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.source.url"] = source.Url, ["chatcompletions.source.title"] = source.Title,
                ["messages.source.url"] = source.Url, ["messages.source.title"] = source.Title,
                ["deepseek"] = source.Raw
            }
        }));
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"{GetIdentifier()}/{model}", Status = "completed",
            Output = new AIOutput { Items = items }, Usage = usage, Metadata = metadata
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamWebAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteWebAsync(request, cancellationToken);
        var id = request.Id ?? $"deepseek_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow;
        var text = response.Output!.Items!.First().Content!.OfType<AITextContentPart>().Single().Text;
        var providerMetadata = new Dictionary<string, object> { [GetIdentifier()] = response.Metadata![GetIdentifier()]! };

        yield return WebEvent("text-start", id, timestamp, new AITextStartEventData { ProviderMetadata = providerMetadata }, response.Metadata!);
        yield return WebEvent("text-delta", id, timestamp, new AITextDeltaEventData { Delta = text, ProviderMetadata = providerMetadata }, response.Metadata!);
        yield return WebEvent("text-end", id, timestamp, new AITextEndEventData { ProviderMetadata = providerMetadata }, response.Metadata!);
        foreach (var source in response.Output.Items!.Where(item => item.Type == "source-url"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceUrl = (string)source.Metadata!["chatcompletions.source.url"]!;
            yield return WebEvent("source-url", id, timestamp, new AISourceUrlEventData
            {
                SourceId = sourceUrl, Url = sourceUrl, Title = (string)source.Metadata["chatcompletions.source.title"]!,
                Type = "url_citation", ProviderMetadata = new() { [GetIdentifier()] = new() { ["result"] = source.Metadata[GetIdentifier()]! } }
            }, response.Metadata!);
        }
        cancellationToken.ThrowIfCancellationRequested();
        yield return WebEvent("finish", id, timestamp, new AIFinishEventData
        {
            FinishReason = "stop", Model = response.Model, CompletedAt = timestamp.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, timestamp,
                usage: response.Usage, temperature: request.Temperature, additionalProperties: response.Metadata)
        }, response.Metadata!);
    }

    private AIStreamEvent WebEvent(string type, string id, DateTimeOffset timestamp, object data, Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data }
        };

    private sealed record WebSource(string Url, string Title, JsonElement Raw);
}
