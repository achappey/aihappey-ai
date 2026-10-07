using System.Globalization;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static partial class ModelProviderDecisionExtensions
{
    public static OpenAIDecisionRequest ToOpenAIDecisionRequest(this DecisionRequest request, string providerIdentifier)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerIdentifier);
        ArgumentNullException.ThrowIfNull(request.State);
        if (request.Questions is null || request.Questions.Count == 0)
            throw new ArgumentException("At least one question is required.", nameof(request));

        var result = new OpenAIDecisionRequest
        {
            Model = request.Model,
            Input = ToNativeDecisionInput(request.State),
            Questions = request.Questions.Select(item => MapQuestion(item.Key, item.Value)).ToArray()
        };
        if (request.ProviderOptions?.TryGetValue(providerIdentifier, out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"providerOptions.{providerIdentifier} must be an object.", nameof(request));
            foreach (var property in options.EnumerateObject())
            {
                if (property.Name.Equals("safety_identifier", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("safetyIdentifier", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw new ArgumentException("Safety identifier must be a string or null.", nameof(request));
                    result.SafetyIdentifier = property.Value.GetString();
                }
                else if (!IsCanonicalDecisionProperty(property.Name))
                {
                    (result.AdditionalProperties ??= new(StringComparer.OrdinalIgnoreCase))[property.Name] = property.Value.Clone();
                }
            }
        }
        ValidateOpenAIDecisionRequest(result);
        return result;
    }

    private static OpenAIDecisionQuestion MapQuestion(string name, DecisionQuestion question)
    {
        if (string.IsNullOrWhiteSpace(name) || question is null)
            throw new ArgumentException("Every decision question requires a non-empty ID and a question.");
        var instructions = RenderDecisionText(question.Instructions);
        OpenAIDecisionQuestion mapped = question switch
        {
            DecisionBooleanQuestion { Criteria: null } => new OpenAIDecisionPredicateQuestion(),
            DecisionBooleanQuestion boolean => new OpenAIDecisionChoiceQuestion
            {
                Choices = [
                    new() { Value = true, Description = RenderOptionalDecisionText(boolean.Criteria!.True) },
                    new() { Value = false, Description = RenderOptionalDecisionText(boolean.Criteria.False) }
                ]
            },
            DecisionChoiceQuestion { Criteria.Count: > 0 } choice => new OpenAIDecisionChoiceQuestion
            {
                Choices = choice.Criteria.Select(item => new OpenAIDecisionChoice
                {
                    Value = item.Key, Description = RenderOptionalDecisionText(item.Value)
                }).ToArray()
            },
            DecisionScoreQuestion { Criteria.Count: >= 2 } score => new OpenAIDecisionScoreQuestion
            {
                Levels = score.Criteria.Select((criteria, index) => new OpenAIDecisionScoreLevel
                {
                    Label = index.ToString(CultureInfo.InvariantCulture), Description = RenderOptionalDecisionText(criteria)
                }).ToArray()
            },
            _ => throw new ArgumentException($"Question '{name}' has unsupported type or invalid criteria.")
        };
        mapped.Name = name;
        mapped.Instructions = instructions;
        return mapped;
    }

    private static string RenderDecisionText(DecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Value.ValueKind == JsonValueKind.String ? input.Value.GetString()! : input.Value.GetRawText();
    }

    private static string? RenderOptionalDecisionText(DecisionInput? input) => input is null ? null : RenderDecisionText(input);

    private static OpenAIDecisionInput ToNativeDecisionInput(DecisionInput state)
    {
        var value = state.Value;
        if (value.ValueKind == JsonValueKind.String)
            return new(value.GetString()!);

        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0
            && value.EnumerateArray().All(IsRawDecisionMessage))
        {
            try
            {
                var input = value.Deserialize<OpenAIDecisionInput>(DecisionJsonOptions)!;
                ValidateDecisionInput(input);
                return input;
            }
            catch (JsonException) { }
            catch (ArgumentException) { }
        }
        // Invalid native-looking state is still valid evidence. Preserve every field
        // as JSON text rather than allowing permissive converters to discard content.
        return new(value.GetRawText());
    }

    private static bool HasOnlyProperties(JsonElement value, params string[] allowed)
        => value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().All(property => allowed.Contains(property.Name, StringComparer.Ordinal));

    private static bool IsRawDecisionMessage(JsonElement message)
    {
        if (!HasOnlyProperties(message, "role", "type", "content")
            || !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != "user"
            || (message.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "message"))
            || !message.TryGetProperty("content", out var content))
            return false;
        return content.ValueKind == JsonValueKind.String
            || (content.ValueKind == JsonValueKind.Array && content.EnumerateArray().All(IsRawDecisionPart));
    }

    private static bool IsRawDecisionPart(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return false;
        return type.GetString() switch
        {
            "input_text" => HasOnlyProperties(part, "type", "text") && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String,
            "input_image" => HasOnlyProperties(part, "type", "image_url", "detail")
                && part.TryGetProperty("image_url", out var url) && url.ValueKind == JsonValueKind.String
                && (!part.TryGetProperty("detail", out var detail) || detail.ValueKind is JsonValueKind.String or JsonValueKind.Null),
            _ => false
        };
    }

    public static void ValidateOpenAIDecisionRequest(OpenAIDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        ValidateDecisionInput(request.Input);
        if (request.Questions is null || request.Questions.Count == 0)
            throw new ArgumentException("At least one question is required.", nameof(request));
        foreach (var question in request.Questions)
        {
            if (question is null || string.IsNullOrWhiteSpace(question.Instructions))
                throw new ArgumentException("Every question requires instructions.", nameof(request));
            switch (question)
            {
                case OpenAIDecisionPredicateQuestion: break;
                case OpenAIDecisionChoiceQuestion choice:
                    if (choice.Choices is null || choice.Choices.Count == 0
                        || choice.Choices.Any(item => item is null || !IsChoiceValue(item.Value.Value))
                        || choice.Choices.Select(item => ChoiceKey(item.Value)).Distinct().Count() != choice.Choices.Count)
                        throw new ArgumentException("Choice questions require distinct string or boolean values.", nameof(request));
                    break;
                case OpenAIDecisionScoreQuestion score:
                    if (score.Levels is null || score.Levels.Count == 0
                        || score.Levels.Any(item => item is null || string.IsNullOrWhiteSpace(item.Label))
                        || score.Levels.Select(item => item.Label).Distinct(StringComparer.Ordinal).Count() != score.Levels.Count)
                        throw new ArgumentException("Score questions require distinct, non-empty level labels.", nameof(request));
                    break;
                default: throw new ArgumentException("Unsupported decision question type.", nameof(request));
            }
        }
        if (request.AdditionalProperties?.Keys.Any(IsCanonicalDecisionProperty) == true)
            throw new ArgumentException("Additional properties cannot override canonical decision fields.", nameof(request));
    }

    private static bool IsCanonicalDecisionProperty(string name)
        => new[] { "model", "input", "questions", "safety_identifier", "safetyIdentifier" }
            .Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsChoiceValue(JsonElement value)
        => value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False;

    // Prefix distinguishes the string "true" from the boolean true.
    private static string ChoiceKey(OpenAIDecisionChoiceValue value)
        => value.Value.ValueKind == JsonValueKind.String ? "string:" + value.Value.GetString() : "boolean:" + value.Value.GetRawText();

    private static void ValidateDecisionInput(OpenAIDecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Text is not null)
            return;
        if (input.Messages is null || input.Messages.Count == 0)
            throw new ArgumentException("Decision input requires text or user messages.", nameof(input));
        var images = 0;
        foreach (var message in input.Messages)
        {
            if (message is null || message.Type is not (null or "message") || message.Content is null)
                throw new ArgumentException("Decision input requires user messages with content.", nameof(input));
            if (message.Content.Text is not null)
                continue;
            if (message.Content.Parts is null || message.Content.Parts.Count == 0)
                throw new ArgumentException("Message content requires text or text/image parts.", nameof(input));
            foreach (var part in message.Content.Parts)
            {
                switch (part)
                {
                    case OpenAIDecisionInputText text when text.Text is not null: break;
                    case OpenAIDecisionInputImage image:
                        if (!IsInlineDecisionImage(image.ImageUrl) || image.Detail is not (null or "low" or "high" or "auto" or "original"))
                            throw new ArgumentException("Decision images require base64 image data URLs and a supported detail level.", nameof(input));
                        if (++images > 128)
                            throw new ArgumentException("At most 128 images are allowed across a decision request.", nameof(input));
                        break;
                    default: throw new ArgumentException("Only input_text and input_image parts are supported.", nameof(input));
                }
            }
        }
    }

    private static bool IsInlineDecisionImage(string? url)
    {
        if (url is null || !url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return false;
        var comma = url.IndexOf(',');
        if (comma < 0 || !url.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            || comma <= "data:image/;base64".Length || comma == url.Length - 1)
            return false;
        try { return Convert.FromBase64String(url[(comma + 1)..]).Length > 0; }
        catch (FormatException) { return false; }
    }
}
