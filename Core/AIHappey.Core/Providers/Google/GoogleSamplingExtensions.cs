using System.Text.Json;
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

    public static GoogleSearch? ToGoogleSearch(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(GoogleExtensions.Identifier(), out var google) || google.ValueKind != JsonValueKind.Object)
            return null;

        if (!google.TryGetProperty("google_search", out var googleSearch) || googleSearch.ValueKind != JsonValueKind.Object)
            return null;

        Interval? timeRange = null;

        if (googleSearch.TryGetProperty("timeRangeFilter", out var timeRangeProp) && timeRangeProp.ValueKind == JsonValueKind.Object)
        {
            DateTime? start = null;
            DateTime? end = null;

            if (timeRangeProp.TryGetProperty("startTime", out var startProp) && startProp.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(startProp.GetString(), out var parsedStart))
                start = parsedStart;

            if (timeRangeProp.TryGetProperty("endTime", out var endProp) && endProp.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(endProp.GetString(), out var parsedEnd))
                end = parsedEnd;

            // Only set if both values are valid and start < end
            if (start.HasValue && end.HasValue && start.Value < end.Value)
                timeRange = new Interval { StartTime = start, EndTime = end };
        }

        return new GoogleSearch
        {
            TimeRangeFilter = timeRange,
        };
    }



    public static bool UseUrlContext(this JsonElement? element)
    {
        if (element == null) return false;

        if (!element.Value.TryGetProperty(GoogleExtensions.Identifier(), out var google) || google.ValueKind != JsonValueKind.Object)
            return false;

        if (!google.TryGetProperty("url_context", out var googleSearch) || googleSearch.ValueKind != JsonValueKind.Object)
            return false;

        return true;
    }

    public static bool UseGoogleMaps(this JsonElement? element)
    {
        if (element == null) return false;

        if (!element.Value.TryGetProperty(GoogleExtensions.Identifier(), out var google) || google.ValueKind != JsonValueKind.Object)
            return false;

        if (!google.TryGetProperty("googleMaps", out var googleSearch) || googleSearch.ValueKind != JsonValueKind.Object)
            return false;

        return true;
    }

    public static bool UseCodeExecution(this JsonElement? element)
    {
        if (element == null) return false;

        if (!element.Value.TryGetProperty(GoogleExtensions.Identifier(), out var google) || google.ValueKind != JsonValueKind.Object)
            return false;

        if (!google.TryGetProperty("code_execution", out var googleSearch) || googleSearch.ValueKind != JsonValueKind.Object)
            return false;

        return true;
    }

    public static ThinkingConfig? ToThinkingConfig(this JsonElement element)
    {
        if (!element.TryGetProperty(GoogleExtensions.Identifier(), out var google) || google.ValueKind != JsonValueKind.Object)
            return null;

        if (!google.TryGetProperty("thinkingConfig", out var thinkingConfig) || thinkingConfig.ValueKind != JsonValueKind.Object)
            return null;

        int? thinkingBudget = null;
        bool? includeThoughts = null;

        if (thinkingConfig.TryGetProperty("thinkingBudget", out var budgetProp)
            && budgetProp.ValueKind == JsonValueKind.Number)
            thinkingBudget = budgetProp.GetInt32();

        if (thinkingConfig.TryGetProperty("includeThoughts", out var includeProp)
            && includeProp.ValueKind == JsonValueKind.True || includeProp.ValueKind == JsonValueKind.False)
            includeThoughts = includeProp.GetBoolean();

        return new ThinkingConfig
        {
            ThinkingBudget = thinkingBudget,
            IncludeThoughts = includeThoughts
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