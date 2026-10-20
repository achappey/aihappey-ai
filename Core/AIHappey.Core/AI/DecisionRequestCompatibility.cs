using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

/// <summary>Loss-aware adaptation of SDK requests to current native decision APIs.</summary>
public static class DecisionRequestCompatibility
{
    public static DecisionRequest PrepareForProvider(this DecisionRequest request, string provider)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.State);
        if (request.MaxOutputTokens is not null)
            throw new NotSupportedException($"{provider} Decisions does not support maxOutputTokens.");
        if (request.Reasoning is not null)
            throw new NotSupportedException($"{provider} Decisions does not support reasoning.");
        if (request.StateFormat is not (null or "sdk"))
            throw new ArgumentException("stateFormat must be omitted (legacy JSON) or 'sdk'.");
        if (request.Questions is null || request.Questions.Count == 0)
            throw new ArgumentException("At least one decision question is required.");
        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id) || question is null)
                throw new ArgumentException("Every decision question requires a nonempty ID and a question.");
            switch (question)
            {
                case DecisionBoxQuestion when question.Instructions is null:
                    throw new ArgumentException($"Box question '{id}' requires instructions.");
                case DecisionBoxQuestion:
                    throw new NotSupportedException($"{provider} Decisions does not support box questions (question '{id}').");
                case DecisionBooleanQuestion boolean when boolean.Instructions is null
                    && boolean.Criteria?.True is null && boolean.Criteria?.False is null:
                    throw new ArgumentException($"Boolean question '{id}' requires instructions or at least one non-null criterion.");
                case DecisionChoiceQuestion choice when choice.Criteria is null || choice.Criteria.Count == 0:
                    throw new ArgumentException($"Choice question '{id}' requires a nonempty criteria map.");
                case DecisionScoreQuestion score when score.Criteria is null || score.Criteria.Count == 0:
                    throw new ArgumentException($"Score question '{id}' requires at least one level.");
            }
            // Native APIs currently require instructions. Never invent a prompt for a
            // criteria-only request or claim support just because the SDK permits it.
            if (question.Instructions is null)
                throw new NotSupportedException($"{provider} Decisions requires instructions; criteria-only question '{id}' is not supported.");
        }
        if (request.StateFormat != "sdk") return request;
        return new DecisionRequest
        {
            Model = request.Model, State = NormalizeSdkState(request.State, provider),
            Questions = request.Questions, Headers = request.Headers, ProviderOptions = request.ProviderOptions,
        };
    }

    private static DecisionInput NormalizeSdkState(DecisionInput state, string provider)
    {
        if (state.Value.ValueKind != JsonValueKind.Array || state.Value.GetArrayLength() == 0)
            throw new ArgumentException("SDK decision state must be a nonempty array of parts.");
        var content = new JsonArray();
        foreach (var part in state.Value.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("SDK decision state requires typed parts.");
            var type = RequiredString(part, "type");
            if (part.TryGetProperty("providerOptions", out var options)
                && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
                throw new NotSupportedException($"{provider} Decisions does not support state-part providerOptions.");
            switch (type)
            {
                case "text":
                    RequireOnly(part, "type", "text", "providerOptions");
                    AddText(content, RequiredString(part, "text"));
                    break;
                case "json":
                    RequireOnly(part, "type", "value");
                    if (!part.TryGetProperty("value", out var value)) throw new ArgumentException("JSON parts require a value.");
                    AddText(content, value.GetRawText());
                    break;
                case "file":
                    RequireOnly(part, "type", "data", "mediaType", "filename", "providerOptions");
                    AddFile(content, part, provider);
                    break;
                default: throw new NotSupportedException($"{provider} Decisions does not support state part '{type}'.");
            }
        }
        var messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content });
        return new DecisionInput(JsonSerializer.SerializeToElement(messages));
    }

    private static void AddFile(JsonArray content, JsonElement part, string provider)
    {
        var mediaType = RequiredString(part, "mediaType");
        if (!part.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SDK file parts require tagged data.");
        var kind = RequiredString(data, "type");
        if (part.TryGetProperty("filename", out _)) AddText(content, $"File: {RequiredString(part, "filename")}");
        if (kind == "text")
        {
            RequireOnly(data, "type", "text");
            if (!(mediaType == "text" || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType == "application/json"))
                throw new NotSupportedException($"{provider} Decisions does not support inline text files of media type '{mediaType}'.");
            AddText(content, RequiredString(data, "text"));
            return;
        }
        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{provider} Decisions does not support SDK files of media type '{mediaType}'.");
        string url;
        switch (kind)
        {
            case "data":
                RequireOnly(data, "type", "data");
                var base64 = RequiredString(data, "data");
                try { _ = Convert.FromBase64String(base64); }
                catch (FormatException ex) { throw new ArgumentException("SDK image data must be base64.", ex); }
                url = $"data:{mediaType};base64,{base64}";
                break;
            case "url":
                RequireOnly(data, "type", "url", "originalUrl");
                url = data.TryGetProperty("originalUrl", out _) ? RequiredString(data, "originalUrl") : RequiredString(data, "url");
                if (!url.StartsWith($"data:{mediaType};base64,", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException($"{provider} Decisions supports only inline SDK image URLs, not remote URLs.");
                break;
            default: throw new NotSupportedException($"{provider} Decisions does not support SDK file data '{kind}'.");
        }
        content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = url });
    }

    private static void AddText(JsonArray content, string text)
        => content.Add(new JsonObject { ["type"] = "input_text", ["text"] = text });

    private static string RequiredString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ArgumentException($"SDK decision part requires string '{name}'.");

    private static void RequireOnly(JsonElement value, params string[] names)
    {
        if (value.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal)))
            throw new NotSupportedException("Unrecognized SDK decision part fields cannot be forwarded without loss.");
    }
}
