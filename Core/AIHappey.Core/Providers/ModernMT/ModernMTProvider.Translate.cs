using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.ModernMT;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.ModernMT;

public sealed partial class ModernMTProvider
{
    private static string GetTranslateTargetLanguageFromModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var m = model.Trim();

        const string prefix = "translate/";
        if (!m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"ModernMT translation model must start with '{prefix}'. Got '{model}'.", nameof(model));

        var lang = m[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(lang))
            throw new ArgumentException("ModernMT translation target language is missing from model id.", nameof(model));

        return lang;
    }

    private static ModernMTProviderMetadata? GetModernMTMetadataFromSampling(CreateMessageRequestParams chatRequest)
    {
        // MCP sampling metadata is a JsonElement map; we support `metadata.modernmt`.
        if (chatRequest.Metadata is not JsonElement el || el.ValueKind != JsonValueKind.Object)
            return null;

        if (!el.TryGetProperty("modernmt", out var mm) || mm.ValueKind != JsonValueKind.Object)
            return null;

        return mm.Deserialize<ModernMTProviderMetadata>(JsonSerializerOptions.Web);
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
        ModernMTProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("Target language is required.", nameof(targetLanguage));

        // ModernMT docs: GET /translate
        // Workaround: POST + X-HTTP-Method-Override: GET
        var payload = new JsonObject
        {
            ["target"] = targetLanguage.Trim(),
            // Send q as array always to ensure stable output mapping.
            ["q"] = JsonSerializer.SerializeToNode(texts, JsonSerializerOptions.Web),
        };

        if (!string.IsNullOrWhiteSpace(metadata?.Source))
            payload["source"] = metadata!.Source;
        if (!string.IsNullOrWhiteSpace(metadata?.ContextVector))
            payload["context_vector"] = metadata!.ContextVector;
        if (!string.IsNullOrWhiteSpace(metadata?.Hints))
            payload["hints"] = metadata!.Hints;
        if (!string.IsNullOrWhiteSpace(metadata?.Glossaries))
            payload["glossaries"] = metadata!.Glossaries;
        if (metadata?.IgnoreGlossaryCase is not null)
            payload["ignore_glossary_case"] = metadata.IgnoreGlossaryCase.Value;
        if (!string.IsNullOrWhiteSpace(metadata?.Priority))
            payload["priority"] = metadata!.Priority;
        if (metadata?.Multiline is not null)
            payload["multiline"] = metadata.Multiline.Value;
        if (metadata?.Timeout is not null)
            payload["timeout"] = metadata.Timeout.Value;
        if (!string.IsNullOrWhiteSpace(metadata?.Format))
            payload["format"] = metadata!.Format;
        if (metadata?.AltTranslations is not null)
            payload["alt_translations"] = metadata.AltTranslations.Value;
        if (!string.IsNullOrWhiteSpace(metadata?.Session))
            payload["session"] = metadata!.Session;
        if (metadata?.MaskProfanities is not null)
            payload["mask_profanities"] = metadata.MaskProfanities.Value;

        using var req = new HttpRequestMessage(HttpMethod.Post, "translate")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-HTTP-Method-Override", "GET");

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ModernMT translate failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // ModernMT wraps results in: { status: 200, data: ... } or { status: 400, error: { type, message } }
        if (root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.Number)
        {
            var status = statusEl.GetInt32();
            if (status < 200 || status >= 300)
            {
                if (root.TryGetProperty("error", out var errEl))
                {
                    var type = errEl.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
                    var message = errEl.TryGetProperty("message", out var mEl) ? mEl.GetString() : null;
                    throw new InvalidOperationException($"ModernMT translate returned {status} ({type}): {message}");
                }
                throw new InvalidOperationException($"ModernMT translate returned status {status}: {body}");
            }
        }

        if (!root.TryGetProperty("data", out var dataEl))
            return [.. texts.Select(_ => string.Empty)];

        // If q is array, data is an array of objects (one per input).
        if (dataEl.ValueKind == JsonValueKind.Array)
        {
            var result = new List<string>(texts.Count);
            var items = dataEl.EnumerateArray().ToList();

            for (var i = 0; i < texts.Count; i++)
            {
                if (i >= items.Count)
                {
                    result.Add(string.Empty);
                    continue;
                }

                var item = items[i];
                var t = item.TryGetProperty("translation", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString()
                    : null;
                result.Add(t ?? string.Empty);
            }

            return result;
        }

        // If q is a string, data is a single object.
        if (dataEl.ValueKind == JsonValueKind.Object)
        {
            var t = dataEl.TryGetProperty("translation", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString()
                : null;
            return [t ?? string.Empty];
        }

        return [.. texts.Select(_ => string.Empty)];
    }

    internal async Task<CreateMessageResult> TranslateSamplingAsync(
        CreateMessageRequestParams chatRequest,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var targetLanguage = GetTranslateTargetLanguageFromModel(modelId);
        var metadata = GetModernMTMetadataFromSampling(chatRequest);

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

        // Keep consistent with other translation providers: no provider metadata support on Responses initially.
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
        var targetLanguage = GetTranslateTargetLanguageFromModel(chatRequest.Model);
        var metadata = chatRequest.GetProviderMetadata<ModernMTProviderMetadata>(GetIdentifier());

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

