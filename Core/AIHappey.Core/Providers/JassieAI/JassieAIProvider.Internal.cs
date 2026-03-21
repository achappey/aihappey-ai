using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.JassieAI;

public partial class JassieAIProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string NormalizeModelId(string? model)
    {
        var normalized = model?.Trim() ?? string.Empty;
        if (normalized.Contains('/'))
            normalized = normalized[(normalized.LastIndexOf('/') + 1)..];

        return normalized;
    }

    private static string ResolveEndpoint(string model)
        => string.Equals(model, "jassie-code", StringComparison.OrdinalIgnoreCase)
            ? "v1/generate-code"
            : "v1/generate-text";

    private static string ResolveApiModel(string model, out string? webMode)
    {
        webMode = null;
        var normalized = NormalizeModelId(model);

        if (string.Equals(normalized, "jassie-web", StringComparison.OrdinalIgnoreCase))
        {
            webMode = "auto";
            return "jassie-pulse";
        }

        return normalized;
    }

    private async Task<JassieNativeResponse> SendNativeAsync(JassieNativeRequest payload, string endpoint, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"JassieAI request error ({(int)resp.StatusCode}): {err}");
        }

        var result = await resp.Content.ReadFromJsonAsync<JassieNativeResponse>(Json, cancellationToken)
                     ?? throw new InvalidOperationException("JassieAI returned an empty response.");

        return result;
    }

    private async IAsyncEnumerable<JassieNativeResponse> StreamNativeAsync(
        JassieNativeRequest payload,
        string endpoint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"JassieAI stream error ({(int)resp.StatusCode}): {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            line = line.Trim();
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                line = line["data:".Length..].Trim();

            if (line is "[DONE]" or "[done]")
                yield break;

            JassieNativeResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<JassieNativeResponse>(line, Json);
            }
            catch
            {
                continue;
            }

            if (chunk is not null)
                yield return chunk;
        }
    }

    private static object? CollapseMedia(IReadOnlyCollection<string> items)
        => items.Count switch
        {
            0 => null,
            1 => items.First(),
            _ => items.ToArray()
        };

    private static object BuildUsage(JassieNativeResponse response)
        => new
        {
            prompt_tokens = 0,
            completion_tokens = response.Chunks ?? 0,
            total_tokens = response.Chunks ?? 0
        };

    private static string? ExtractWebMode(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return null;

        if (!metadata.TryGetValue("web", out var value) || value is null)
            return null;

        return value switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => null
        };
    }

    private static string NormalizeRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "system" => "system",
            "assistant" => "assistant",
            _ => "user"
        };

    private static string FlattenCompletionMessageContent(JsonElement content, JassieCollectedMedia media)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        parts.Add(s);
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var type = item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString()
                    : null;

                if ((type == "text" || type == "input_text")
                    && item.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                    continue;
                }

                if ((type == "image" || type == "input_image" || type == "image_url") && TryExtractMediaUrl(item, out var imageUrl))
                {
                    media.Images.Add(imageUrl!);
                    continue;
                }

                if ((type == "video" || type == "input_video") && TryExtractMediaUrl(item, out var videoUrl))
                {
                    media.Videos.Add(videoUrl!);
                    continue;
                }

                if (item.TryGetProperty("text", out var genericTextEl) && genericTextEl.ValueKind == JsonValueKind.String)
                {
                    var text = genericTextEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                }
            }

            return string.Join("\n", parts.Where(a => !string.IsNullOrWhiteSpace(a)));
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? string.Empty;
        }

        return content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? string.Empty
            : content.GetRawText();
    }

    private static bool TryExtractMediaUrl(JsonElement item, out string? value)
    {
        value = null;

        if (item.TryGetProperty("image_url", out var imageUrl))
        {
            if (imageUrl.ValueKind == JsonValueKind.String)
            {
                value = imageUrl.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }

            if (imageUrl.ValueKind == JsonValueKind.Object
                && imageUrl.TryGetProperty("url", out var imageNested)
                && imageNested.ValueKind == JsonValueKind.String)
            {
                value = imageNested.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        foreach (var propertyName in new[] { "url", "src", "data", "video_url" })
        {
            if (item.TryGetProperty(propertyName, out var candidate) && candidate.ValueKind == JsonValueKind.String)
            {
                value = candidate.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return true;
            }
        }

        return false;
    }

    private static string ExtractAssistantText(JassieNativeResponse response)
        => string.Equals(response.Type, "text", StringComparison.OrdinalIgnoreCase)
            ? response.Content ?? string.Empty
            : string.Empty;

    private static string ExtractReasoningText(JassieNativeResponse response)
        => string.Equals(response.Type, "think", StringComparison.OrdinalIgnoreCase)
            ? response.Content ?? string.Empty
            : string.Empty;

    private static string ExtractAssistantTextFromChoices(IEnumerable<object>? choices)
    {
        var lines = new List<string>();
        foreach (var choice in choices ?? [])
        {
            JsonElement root;
            try
            {
                root = JsonSerializer.SerializeToElement(choice, Json);
            }
            catch
            {
                continue;
            }

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                continue;

            if (!message.TryGetProperty("content", out var content))
                continue;

            if (content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add(text);
            }
        }

        return string.Join("\n", lines);
    }

    private JassieNativeRequest BuildNativeRequest(ChatCompletionOptions options, bool stream)
    {
        ArgumentNullException.ThrowIfNull(options);

        var media = new JassieCollectedMedia();
        var messages = new List<JassieNativeMessage>();
        foreach (var message in options.Messages ?? [])
        {
            var content = FlattenCompletionMessageContent(message.Content, media);
            if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(message.Role))
                continue;

            messages.Add(new JassieNativeMessage
            {
                Role = NormalizeRole(message.Role),
                Content = content
            });
        }

        if (messages.Count == 0)
        {
            messages.Add(new JassieNativeMessage
            {
                Role = "user",
                Content = string.Empty
            });
        }

        var model = ResolveApiModel(options.Model, out var webMode);
        return new JassieNativeRequest
        {
            Model = model,
            Stream = stream,
            Temperature = options.Temperature,
            MaxTokens = null,
            Web = webMode,
            Image = CollapseMedia(media.Images),
            Video = CollapseMedia(media.Videos),
            Messages = messages
        };
    }

    private ChatCompletionOptions BuildChatOptionsFromChatRequest(ChatRequest chatRequest, bool stream)
    {
        var messages = new List<ChatMessage>();

        foreach (var message in chatRequest.Messages ?? [])
        {
            var role = message.Role switch
            {
                Role.system => "system",
                Role.assistant => "assistant",
                _ => "user"
            };

            var parts = new List<object>();
            foreach (var part in message.Parts ?? [])
            {
                switch (part)
                {
                    case TextUIPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                        parts.Add(new { type = "text", text = textPart.Text });
                        break;
                    case ReasoningUIPart reasoningPart when !string.IsNullOrWhiteSpace(reasoningPart.Text):
                        parts.Add(new { type = "text", text = reasoningPart.Text });
                        break;
                }
            }

            if (parts.Count == 0)
                continue;

            messages.Add(new ChatMessage
            {
                Role = role,
                Content = JsonSerializer.SerializeToElement(parts, Json)
            });
        }

        if (messages.Count == 0)
        {
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(string.Empty, Json)
            });
        }

        return new ChatCompletionOptions
        {
            Model = chatRequest.Model,
            Temperature = chatRequest.Temperature,
            Stream = stream,
            Messages = messages,
            ResponseFormat = chatRequest.ResponseFormat,
            ToolChoice = chatRequest.ToolChoice,
            Tools = []
        };
    }

    private ChatCompletionOptions BuildChatOptionsFromResponseRequest(ResponseRequest request, bool stream)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(new ChatMessage
            {
                Role = "system",
                Content = JsonSerializer.SerializeToElement(request.Instructions, Json)
            });
        }

        if (request.Input?.IsText == true)
        {
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(request.Input.Text ?? string.Empty, Json)
            });
        }

        if (request.Input?.IsItems == true && request.Input.Items is not null)
        {
            foreach (var item in request.Input.Items)
            {
                if (item is not ResponseInputMessage message)
                    continue;

                var role = message.Role switch
                {
                    ResponseRole.System => "system",
                    ResponseRole.Assistant => "assistant",
                    ResponseRole.Developer => "system",
                    _ => "user"
                };

                var parts = new List<object>();
                if (message.Content.IsText)
                {
                    parts.Add(new { type = "text", text = message.Content.Text ?? string.Empty });
                }
                else if (message.Content.IsParts && message.Content.Parts is not null)
                {
                    foreach (var part in message.Content.Parts)
                    {
                        switch (part)
                        {
                            case InputTextPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                                parts.Add(new { type = "text", text = textPart.Text });
                                break;
                            case InputImagePart imagePart when !string.IsNullOrWhiteSpace(imagePart.ImageUrl):
                                parts.Add(new { type = "input_image", image_url = imagePart.ImageUrl, detail = imagePart.Detail });
                                break;
                            case InputImagePart imagePart when !string.IsNullOrWhiteSpace(imagePart.FileId):
                                parts.Add(new { type = "input_image", image_url = imagePart.FileId, detail = imagePart.Detail });
                                break;
                        }
                    }
                }

                if (parts.Count == 0)
                    continue;

                messages.Add(new ChatMessage
                {
                    Role = role,
                    Content = JsonSerializer.SerializeToElement(parts, Json)
                });
            }
        }

        if (messages.Count == 0)
        {
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(string.Empty, Json)
            });
        }

        return new ChatCompletionOptions
        {
            Model = request.Model ?? throw new ArgumentException("Model is required.", nameof(request)),
            Temperature = request.Temperature,
            Stream = stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ResponseFormat = request.Text,
            ToolChoice = request.ToolChoice as string,
            Tools = [],
            Messages = messages
        };
    }
}
