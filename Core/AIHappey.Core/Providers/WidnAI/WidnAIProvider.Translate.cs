using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.WidnAI;

public partial class WidnAIProvider
{
    private sealed class WidnTranslateRequest
    {
        [JsonPropertyName("sourceText")]
        public List<string> SourceText { get; set; } = [];

        [JsonPropertyName("config")]
        public WidnTranslateConfig Config { get; set; } = new();
    }

    private sealed class WidnTranslateConfig
    {
        [JsonPropertyName("sourceLocale")]
        public string SourceLocale { get; set; } = string.Empty;

        [JsonPropertyName("targetLocale")]
        public string TargetLocale { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;
    }

    private sealed class WidnTranslateResponse
    {
        [JsonPropertyName("targetText")]
        public List<string>? TargetText { get; set; }
    }

    private static bool TryParseTranslationModel(string modelId, out string baseModel, out string sourceLocale, out string targetLocale)
    {
        baseModel = string.Empty;
        sourceLocale = string.Empty;
        targetLocale = string.Empty;

        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var normalized = modelId.Trim();
        if (normalized.StartsWith("widnai/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["widnai/".Length..];

        var marker = "/translate/";
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
            return false;

        baseModel = normalized[..markerIndex].Trim();
        if (string.IsNullOrWhiteSpace(baseModel))
            return false;

        var rest = normalized[(markerIndex + marker.Length)..].Trim();
        var parts = rest.Split("/to/", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        sourceLocale = parts[0].Trim();
        targetLocale = parts[1].Trim();

        return !string.IsNullOrWhiteSpace(sourceLocale)
            && !string.IsNullOrWhiteSpace(targetLocale);
    }

    private static List<string> ExtractResponseRequestTexts(ResponseRequest options)
    {
        var texts = new List<string>();

        if (options.Input?.IsText == true)
        {
            if (!string.IsNullOrWhiteSpace(options.Input.Text))
                texts.Add(options.Input.Text!);
            return texts;
        }

        var items = options.Input?.Items;
        if (items is null)
            return texts;

        foreach (var msg in items.OfType<ResponseInputMessage>().Where(m => m.Role == ResponseRole.User))
        {
            if (msg.Content.IsText)
            {
                if (!string.IsNullOrWhiteSpace(msg.Content.Text))
                    texts.Add(msg.Content.Text!);
            }
            else if (msg.Content.IsParts)
            {
                foreach (var part in msg.Content.Parts!.OfType<InputTextPart>())
                {
                    if (!string.IsNullOrWhiteSpace(part.Text))
                        texts.Add(part.Text);
                }
            }
        }

        return texts;
    }

    private static List<string> ExtractSamplingTexts(CreateMessageRequestParams chatRequest)
        => chatRequest.Messages
            .Where(m => m.Role == ModelContextProtocol.Protocol.Role.User)
            .SelectMany(m => m.Content.OfType<TextContentBlock>())
            .Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

    private async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> sourceTexts,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceTexts);
        if (sourceTexts.Count == 0)
            throw new ArgumentException("At least one text is required.", nameof(sourceTexts));

        if (!TryParseTranslationModel(modelId, out var baseModel, out var sourceLocale, out var targetLocale))
            throw new ArgumentException($"Widn translation model is invalid: '{modelId}'.", nameof(modelId));

        if (string.Equals(sourceLocale, targetLocale, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source and target locales must be different for Widn translation models.", nameof(modelId));

        var payload = new WidnTranslateRequest
        {
            SourceText = [.. sourceTexts],
            Config = new WidnTranslateConfig
            {
                Model = baseModel,
                SourceLocale = sourceLocale,
                TargetLocale = targetLocale
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/translate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Widn translate failed ({(int)resp.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<WidnTranslateResponse>(body, JsonSerializerOptions.Web);
        var translated = parsed?.TargetText ?? [];

        if (translated.Count >= sourceTexts.Count)
            return translated.Take(sourceTexts.Count).ToList();

        var padded = translated.ToList();
        while (padded.Count < sourceTexts.Count)
            padded.Add(string.Empty);

        return padded;
    }

    private async Task<ChatCompletion> TranslateChatCompletionAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        var texts = options.Messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(m => ChatMessageContentExtensions.ToText(m.Content))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();

        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, options.Model, cancellationToken);
        var joined = string.Join("\n", translated);
        var now = DateTimeOffset.UtcNow;

        return new ChatCompletion
        {
            Id = Guid.NewGuid().ToString("n"),
            Created = now.ToUnixTimeSeconds(),
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    finish_reason = "stop",
                    message = new
                    {
                        role = "assistant",
                        content = joined
                    }
                }
            ]
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> TranslateChatCompletionUpdatesAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completion = await TranslateChatCompletionAsync(options, cancellationToken);

        var choice = completion.Choices.FirstOrDefault();
        var content = string.Empty;
        if (choice is not null)
        {
            var choiceJson = JsonSerializer.Serialize(choice, JsonSerializerOptions.Web);
            using var doc = JsonDocument.Parse(choiceJson);
            if (doc.RootElement.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.String)
            {
                content = contentEl.GetString() ?? string.Empty;
            }
        }

        yield return new ChatCompletionUpdate
        {
            Id = completion.Id,
            Created = completion.Created,
            Model = completion.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { role = "assistant", content },
                    finish_reason = (string?)null
                }
            ]
        };

        yield return new ChatCompletionUpdate
        {
            Id = completion.Id,
            Created = completion.Created,
            Model = completion.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "stop"
                }
            ]
        };
    }

    internal async Task<CreateMessageResult> TranslateSamplingAsync(
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModel() ?? throw new ArgumentException("Model missing.", nameof(chatRequest));
        var texts = ExtractSamplingTexts(chatRequest);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, modelId, cancellationToken);
        var joined = string.Join("\n", translated);

        return new CreateMessageResult
        {
            Role = ModelContextProtocol.Protocol.Role.Assistant,
            Model = modelId,
            StopReason = "stop",
            Content = [joined.ToTextContentBlock()]
        };
    }

    internal async Task<ResponseResult> TranslateResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model ?? throw new ArgumentException(nameof(options.Model));
        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, modelId, cancellationToken);
        var joined = string.Join("\n", translated);

        var now = DateTimeOffset.UtcNow;
        return new ResponseResult
        {
            Id = Guid.NewGuid().ToString("n"),
            Model = modelId,
            CreatedAt = now.ToUnixTimeSeconds(),
            CompletedAt = now.ToUnixTimeSeconds(),
            Output =
            [
                new
                {
                    type = "message",
                    id = Guid.NewGuid().ToString("n"),
                    status = "completed",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = joined,
                            annotations = Array.Empty<string>()
                        }
                    }
                }
            ]
        };
    }

    internal async IAsyncEnumerable<UIMessagePart> StreamTranslateAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var lastUser = chatRequest.Messages?.LastOrDefault(m => m.Role == AIHappey.Vercel.Models.Role.user);
        var texts = lastUser?.Parts?.OfType<TextUIPart>()
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList() ?? [];

        if (texts.Count == 0)
        {
            yield return "No prompt provided.".ToErrorUIPart();
            yield break;
        }

        IReadOnlyList<string>? translated = null;
        string? error = null;

        try
        {
            translated = await TranslateAsync(texts, chatRequest.Model, cancellationToken);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            yield return error!.ToErrorUIPart();
            yield break;
        }

        var id = Guid.NewGuid().ToString("n");
        yield return id.ToTextStartUIMessageStreamPart();

        for (var i = 0; i < translated!.Count; i++)
        {
            var text = translated[i];
            var delta = (i == translated.Count - 1) ? text : (text + "\n");
            yield return new TextDeltaUIMessageStreamPart { Id = id, Delta = delta };
        }

        yield return id.ToTextEndUIMessageStreamPart();
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
    }
}
