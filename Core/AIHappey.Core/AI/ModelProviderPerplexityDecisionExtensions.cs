using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

/// <summary>Perplexity's Decisions wire contract is not the OpenAI Decisions contract.</summary>
public static partial class ModelProviderPerplexityDecisionExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Provider = "perplexity";
    private const int MaxBodyBytes = 32 * 1024 * 1024;

    public static async Task<DecisionResponse> PerplexityDecisionRequestAsync(
        this HttpClient client, DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var model = request.Model;
        if (model?.StartsWith(Provider + "/", StringComparison.OrdinalIgnoreCase) == true) model = model[(Provider.Length + 1)..];
        if (model is not ("pplx-decider-v1.1-27b" or "pplx-decider-v1-27b")) throw new ArgumentException("Unsupported Perplexity decision model.");
        if (request.State is null) throw new ArgumentException("Decision state is required.");
        if (request.Questions is null || request.Questions.Count is < 1 or > 128) throw new ArgumentException("Perplexity supports 1 to 128 questions.");
        // No provider-specific body options are currently documented. Never leak gateway fields upstream.
        if (request.ProviderOptions?.TryGetValue(Provider, out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw new ArgumentException("Perplexity Decisions currently supports no additional provider options.");

        var warnings = new List<object>();
        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id) || question?.Instructions is null) throw new ArgumentException("Questions require a nonempty ID and instructions.");
            var mapped = new JsonObject { ["type"] = question.Type == "boolean" ? "noul" : question.Type,
                ["instructions"] = JsonNode.Parse(question.Instructions.Value.GetRawText()) };
            switch (question)
            {
                case DecisionBooleanQuestion boolean:
                    if (boolean.Criteria is not null) mapped["criteria"] = JsonSerializer.SerializeToNode(boolean.Criteria, JsonOptions);
                    break;
                case DecisionChoiceQuestion choice when choice.Criteria is not null && choice.Criteria.Count is >= 1 and <= 255:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, JsonOptions);
                    break;
                case DecisionScoreQuestion score when score.Criteria is not null && score.Criteria.Count is >= 2 and <= 10:
                    if (score.Criteria.Any(level => level is null)) throw new ArgumentException("Perplexity score levels cannot be null.");
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, JsonOptions);
                    break;
                default: throw new ArgumentException($"Question '{id}' has unsupported criteria; choices require 1–255 options and scores 2–10 nonnull levels.");
            }
            questions[id] = mapped;
        }
        var payload = new JsonObject { ["model"] = model, ["state"] = MapState(request.State.Value, warnings), ["questions"] = questions };
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOptions));
        if (bytes.Length > MaxBodyBytes) throw new ArgumentException("Perplexity decision request exceeds 32 MiB.");
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/decisions") { Content = new ByteArrayContent(bytes) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(Provider) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
        {
            var id = headers.GetValueOrDefault("x-request-id");
            var retry = headers.GetValueOrDefault("Retry-After");
            throw new HttpRequestException($"Perplexity decision request failed ({(int)response.StatusCode} {response.ReasonPhrase})"
                + (id is null ? "" : $" [request {id}]") + (retry is null ? "" : $" [Retry-After: {retry}]") + $": {raw}", null, response.StatusCode);
        }
        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapResponse(body, request);
            result.Warnings = warnings;
            result.ProviderMetadata = Provider.CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new() { ModelId = model!.ToModelId(Provider), Timestamp = DateTimeOffset.UtcNow,
                Id = headers.GetValueOrDefault("x-request-id"), Headers = headers.ToDictionary(p => p.Key, p => (string?)p.Value), Body = body.Clone() };
            return result;
        }
        catch (JsonException ex) { throw new InvalidOperationException("Perplexity returned an invalid decision response.", ex); }
    }

    private static JsonNode? MapState(JsonElement state, List<object> warnings)
    {
        if (state.ValueKind == JsonValueKind.Array && state.GetArrayLength() > 0
            && state.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("role", out var role) && role.GetString() == "user"))
        {
            var parts = new JsonArray();
            foreach (var message in state.EnumerateArray())
            {
                RequireOnly(message, "role", "type", "content");
                if (message.TryGetProperty("type", out var type) && type.GetString() != "message") throw new ArgumentException("Unsupported decision message type.");
                if (!message.TryGetProperty("content", out var content)) throw new ArgumentException("Decision messages require content.");
                if (content.ValueKind == JsonValueKind.String) parts.Add(content.GetString());
                else if (content.ValueKind == JsonValueKind.Array) foreach (var part in content.EnumerateArray()) parts.Add(MapPart(part, warnings));
                else throw new ArgumentException("Decision message content must contain text or image parts.");
            }
            if (parts.Count == 0) throw new ArgumentException("Decision messages require content.");
            return parts;
        }
        // Recognize top-level image/text parts, not similarly named fields inside arbitrary evidence.
        if (state.ValueKind == JsonValueKind.Object && IsPart(state)) return MapPart(state, warnings);
        if (state.ValueKind == JsonValueKind.Array)
        {
            var parts = new JsonArray();
            foreach (var part in state.EnumerateArray()) parts.Add(IsPart(part) ? MapPart(part, warnings) : JsonNode.Parse(part.GetRawText()));
            return parts;
        }
        return JsonNode.Parse(state.GetRawText());
    }

    private static bool IsPart(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
        && type.GetString() is "input_text" or "input_image" or "image_url";

    private static JsonNode? MapPart(JsonElement part, List<object> warnings)
    {
        if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type)) throw new ArgumentException("Unsupported decision content part.");
        if (type.GetString() == "input_text")
        {
            RequireOnly(part, "type", "text");
            return JsonValue.Create(part.GetProperty("text").GetString());
        }
        string? url;
        if (type.GetString() == "input_image")
        {
            RequireOnly(part, "type", "image_url", "detail");
            url = part.GetProperty("image_url").GetString();
            if (part.TryGetProperty("detail", out var detail) && detail.ValueKind != JsonValueKind.Null && detail.GetString() is not (null or "auto"))
                warnings.Add(new { type = "other", message = "Perplexity Decisions does not support image detail hints; the original image is used." });
        }
        else if (type.GetString() == "image_url")
        {
            RequireOnly(part, "type", "image_url");
            var image = part.GetProperty("image_url");
            RequireOnly(image, "url");
            url = image.GetProperty("url").GetString();
        }
        else throw new ArgumentException("Unsupported decision content part.");
        ValidateImage(url);
        return new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } };
    }

    private static void RequireOnly(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal)))
            throw new ArgumentException("Unsupported fields in decision message/image input; refusing to discard content.");
    }

    private static DecisionResponse MapResponse(JsonElement body, DecisionRequest request)
    {
        if (body.ValueKind != JsonValueKind.Object || body.GetProperty("model").ValueKind != JsonValueKind.String
            || body.GetProperty("model").GetString() != request.Model.Split('/').Last()) throw new JsonException("Decision model mismatch.");
        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object || nativeAnswers.EnumerateObject().Count() != request.Questions.Count
            || nativeAnswers.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");
        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            if (!nativeAnswers.TryGetProperty(id, out var answer)) throw new JsonException("Missing question answer.");
            var type = answer.GetProperty("type").GetString();
            switch (question)
            {
                case DecisionBooleanQuestion when type == "noul":
                    answers[id] = new DecisionBooleanAnswer { Probability = ReadProbability(answer.GetProperty("noul")) }; break;
                case DecisionChoiceQuestion choice when type == "choice":
                    ReadProbability(answer.GetProperty("confidence"));
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    var distribution = ReadDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    if (!distribution.TryGetValue(selected, out var top) || top + 1e-6 < distribution.Values.Max()) throw new JsonException("Choice is not a maximal option.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = distribution }; break;
                case DecisionScoreQuestion score when type == "score":
                    ReadProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var probabilities = ReadDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(k => !legend.TryGetProperty(k, out var level) || level.GetRawText() != score.Criteria[int.Parse(k)]!.Value.GetRawText()))
                        throw new JsonException("Score legend must match the submitted rubric.");
                    var expected = probabilities.Sum(p => int.Parse(p.Key) * p.Value);
                    var value = answer.GetProperty("score").GetDouble();
                    if (!double.IsFinite(value) || value < 0 || value > keys.Length - 1 || Math.Abs(value - expected) > 1e-6) throw new JsonException("Invalid weighted score.");
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = probabilities }; break;
                default: throw new JsonException("Answer type does not match question.");
            }
        }
        var usage = body.GetProperty("usage");
        var input = usage.GetProperty("input_tokens").GetInt32();
        var output = usage.GetProperty("output_tokens").GetInt32();
        if (input < 0 || output < 0) throw new JsonException("Invalid decision usage.");
        return new() { Answers = answers, Usage = new() { InputTokens = input, OutputTokens = output } };
    }

    private static double ReadProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return number;
    }

    private static Dictionary<string, double> ReadDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadProbability(property.Value))) throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6) throw new JsonException("Incomplete or unnormalized distribution.");
        return result;
    }
}
