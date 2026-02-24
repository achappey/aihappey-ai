using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using Mscc.GenerativeAI;

namespace AIHappey.Core.Providers.Google;

public static class GoogleSamplingExtensions
{
    public static GenerationConfig ToGenerationConfig(this CreateMessageRequestParams chatRequest) => new()
    {
        Temperature = chatRequest.Temperature,
        ThinkingConfig = chatRequest.Metadata?.ToThinkingConfig(),
        ResponseModalities = [ResponseModality.Text]
    };

    private static JsonObject? GetGoogleTool(JsonObject? root, string key)
    {
        if (root?[GoogleExtensions.Identifier()] is not JsonObject google)
            return null;

        return google[key] as JsonObject;
    }

    public static GoogleSearch? ToGoogleSearch(this JsonObject? obj)
    {
        var googleSearch = GetGoogleTool(obj, "google_search");
        if (googleSearch is null)
            return null;

        Interval? timeRange = null;

        if (googleSearch["timeRangeFilter"] is JsonObject range)
        {
            DateTime? start = range["startTime"] is JsonValue s &&
                              s.TryGetValue<string>(out var sVal) &&
                              DateTime.TryParse(sVal, out var parsedStart)
                ? parsedStart
                : null;

            DateTime? end = range["endTime"] is JsonValue e &&
                            e.TryGetValue<string>(out var eVal) &&
                            DateTime.TryParse(eVal, out var parsedEnd)
                ? parsedEnd
                : null;

            if (start.HasValue && end.HasValue && start < end)
                timeRange = new Interval { StartTime = start, EndTime = end };
        }

        return new GoogleSearch
        {
            TimeRangeFilter = timeRange
        };
    }
    public static bool UseUrlContext(this JsonObject? obj)
        => GetGoogleTool(obj, "url_context") is not null;

    public static bool UseGoogleMaps(this JsonObject? obj)
        => GetGoogleTool(obj, "googleMaps") is not null;

    public static bool UseCodeExecution(this JsonObject? obj)
        => GetGoogleTool(obj, "code_execution") is not null;

    public static ThinkingConfig? ToThinkingConfig(this JsonObject? obj)
    {
        var thinking = GetGoogleTool(obj, "thinkingConfig");
        if (thinking is null)
            return null;

        int? budget = thinking["thinkingBudget"] is JsonValue b &&
                      b.TryGetValue<int>(out var parsedBudget)
            ? parsedBudget
            : null;

        bool? include = thinking["includeThoughts"] is JsonValue i &&
                        i.TryGetValue<bool>(out var parsedInclude)
            ? parsedInclude
            : null;

        return new ThinkingConfig
        {
            ThinkingBudget = budget,
            IncludeThoughts = include
        };
    }

    public static string ToRole(this ModelContextProtocol.Protocol.Role role)
        => role == ModelContextProtocol.Protocol.Role.Assistant
            ? "model" : "user";

    public static ContentResponse ToContentResponse(
        this SamplingMessage samplingMessage) =>
        new(samplingMessage.ToText() ?? string.Empty)
        {
            Role = samplingMessage.Role.ToRole(),
            Parts = [.. samplingMessage.Content.OfType<ImageContentBlock>().Select(a => a.ToImagePart()).OfType<Part>()]

        };

    public static Part? ToImagePart(
        this ImageContentBlock imageContentBlock) =>
        new()
        {
            InlineData = new()
            {
                MimeType = imageContentBlock.MimeType,
                Data = Convert.ToBase64String(imageContentBlock.Data.ToArray())
            }
        };
}