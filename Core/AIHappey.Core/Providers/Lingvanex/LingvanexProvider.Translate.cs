using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Lingvanex;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Lingvanex;

public sealed partial class LingvanexProvider
{
    private static string GetTranslateTargetLanguageFromModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var m = model.Trim();

        const string prefix = "translate/";
        if (!m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Lingvanex translation model must start with '{prefix}'. Got '{model}'.", nameof(model));

        var lang = m[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(lang))
            throw new ArgumentException("Lingvanex translation target language is missing from model id.", nameof(model));

        return lang;
    }

    private static LingvanexProviderMetadata? GetLingvanexMetadataFromSampling(CreateMessageRequestParams chatRequest)
    {
        // MCP sampling metadata is a JsonElement map; we support `metadata.lingvanex`.
        // (No other providers currently expose a generic typed helper for this.)
        if (chatRequest.Metadata is not JsonElement el || el.ValueKind != JsonValueKind.Object)
            return null;

        if (!el.TryGetProperty("lingvanex", out var ln) || ln.ValueKind != JsonValueKind.Object)
            return null;

        return ln.Deserialize<LingvanexProviderMetadata>(JsonSerializerOptions.Web);
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
        if (items is null) return texts;

        foreach (var msg in items.OfType<ResponseInputMessage>().Where(m => m.Role == ResponseRole.User))
        {
            if (msg.Content.IsText)
            {
                if (!string.IsNullOrWhiteSpace(msg.Content.Text))
                    texts.Add(msg.Content.Text!);
            }
            else if (msg.Content.IsParts)
            {
                foreach (var p in msg.Content.Parts!.OfType<InputTextPart>())
                {
                    if (!string.IsNullOrWhiteSpace(p.Text))
                        texts.Add(p.Text);
                }
            }
        }

        return texts;
    }

    private async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        LingvanexProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        if (texts is null) throw new ArgumentNullException(nameof(texts));
        if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("Target language is required.", nameof(targetLanguage));

        var payload = new JsonObject
        {
            ["platform"] = "api",
            ["to"] = targetLanguage.Trim(),
        };

        // Lingvanex accepts `data` as string or array.
        payload["data"] = texts.Count == 1
            ? texts[0]
            : JsonSerializer.SerializeToNode(texts, JsonSerializerOptions.Web);

        if (!string.IsNullOrWhiteSpace(metadata?.TranslateMode))
            payload["translateMode"] = metadata!.TranslateMode;

        if (metadata?.EnableTransliteration is not null)
            payload["enableTransliteration"] = metadata.EnableTransliteration.Value;

        using var req = new HttpRequestMessage(HttpMethod.Post, "translate")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lingvanex translate failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // expected: { err: null, result: "..." } or { err: null, result: ["...", ...] }
        if (root.TryGetProperty("err", out var err) && err.ValueKind != JsonValueKind.Null && err.ValueKind != JsonValueKind.Undefined)
            throw new InvalidOperationException($"Lingvanex translate returned err: {err.GetRawText()}");

        if (!root.TryGetProperty("result", out var resultEl))
            return texts.Select(_ => string.Empty).ToList();

        if (resultEl.ValueKind == JsonValueKind.String)
            return [resultEl.GetString() ?? string.Empty];

        if (resultEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in resultEl.EnumerateArray())
            {
                list.Add(item.ValueKind == JsonValueKind.String ? (item.GetString() ?? string.Empty) : item.GetRawText());
            }
            return list;
        }

        return [resultEl.GetRawText()];
    }

    internal async Task<CreateMessageResult> TranslateSamplingAsync(
        CreateMessageRequestParams chatRequest,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var targetLanguage = GetTranslateTargetLanguageFromModel(modelId);
        var metadata = GetLingvanexMetadataFromSampling(chatRequest);

        var texts = chatRequest.Messages
            .Where(m => m.Role == ModelContextProtocol.Protocol.Role.User)
            .SelectMany(m => m.Content.OfType<TextContentBlock>())
            .Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, targetLanguage, metadata, cancellationToken);
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
        var targetLanguage = GetTranslateTargetLanguageFromModel(modelId);

        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        // Per plan: no Lingvanex metadata on Responses endpoint for now.
        var translated = await TranslateAsync(texts, targetLanguage, metadata: null, cancellationToken);
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

        var targetLanguage = GetTranslateTargetLanguageFromModel(chatRequest.Model);
        var metadata = chatRequest.GetProviderMetadata<LingvanexProviderMetadata>(GetIdentifier());

        // Translate each incoming text part from the last user message.
        var lastUser = chatRequest.Messages?.LastOrDefault(m => m.Role == Vercel.Models.Role.user);
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
            translated = await TranslateAsync(texts, targetLanguage, metadata, cancellationToken);
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

