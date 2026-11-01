using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.Tilde;

public sealed partial class TildeProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Tilde API key.");

        var model = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.StartsWith("tilde/", StringComparison.OrdinalIgnoreCase))
            model = model["tilde/".Length..];
        if (!Guid.TryParse(model, out var engineId) || engineId == Guid.Empty)
            throw new ArgumentException("Tilde model must be an engine GUID.", nameof(options));
        if (options.Tools.Any() || options.ToolChoice is not (null or "none") || options.ResponseFormat is not null)
            throw new NotSupportedException("Tilde supports text translation, not tools or response formats.");

        var texts = new List<string>();
        foreach (var message in options.Messages)
        {
            if (message.ToolCalls?.Any() == true || message.ToolCallId is not null ||
                string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Tilde does not support tool messages.");
            var messageTexts = ExtractTexts((message.Content).ToJsonElement()).ToArray();
            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                texts.Clear();
                texts.AddRange(messageTexts);
            }
        }
        if (texts.Count == 0)
            throw new ArgumentException("No user text provided.", nameof(options));

        var payload = options.AdditionalProperties is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(options.AdditionalProperties);
        if (!payload.TryGetValue("trgLang", out var target) || target.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(target.GetString()))
            throw new ArgumentException("Tilde requires a nonempty native trgLang string.", nameof(options));
        payload["text"] = JsonSerializer.SerializeToElement(texts);
        payload["engineId"] = JsonSerializer.SerializeToElement(model);
        using var request = CreateRequest(HttpMethod.Post, "api/translate/text", key);
        request.Content = JsonContent.Create(payload);
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("translations", out var translations) ||
            translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() != texts.Count)
            throw new JsonException("Tilde must return one translation per input text.");
        var translated = translations.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.Object && GetString(item, "translation") is { } text
                ? text : throw new JsonException("Tilde translation must contain a translation string.")).ToArray();

        return new ChatCompletion
        {
            Id = Guid.NewGuid().ToString("n"),
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = $"{GetIdentifier()}/{model}",
            Choices = [new ChatCompletionChoice { Index = 0, Message = new ChatCompletionResponseMessage { Role = "assistant", Content = string.Join("\n", translated) }, FinishReason = "stop" }],
            Usage = null,
            AdditionalProperties = new() { ["tilde"] = root.Clone() }
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The native endpoint is non-streaming: buffer its result before emitting compatibility chunks.
        var completion = await CompleteChatAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var choice = JsonSerializer.SerializeToElement(completion.Choices.Single());
        yield return new ChatCompletionUpdate
        {
            Id = completion.Id, Created = completion.Created, Model = completion.Model,
            Choices = [new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta { Role = "assistant", Content = choice.GetProperty("message").GetProperty("content").GetString() }, FinishReason = (string?)null }],
            AdditionalProperties = completion.AdditionalProperties
        };
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatCompletionUpdate
        {
            Id = completion.Id, Created = completion.Created, Model = completion.Model,
            Choices = [new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta {  }, FinishReason = "stop" }]
        };
    }

    private static IEnumerable<string> ExtractTexts(JsonElement content)
    {
        if (content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            yield break;
        if (content.ValueKind == JsonValueKind.String)
        {
            if (!string.IsNullOrWhiteSpace(content.GetString()))
                yield return content.GetString()!;
            yield break;
        }
        if (content.ValueKind != JsonValueKind.Array)
            throw new NotSupportedException("Tilde supports only text content.");
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || GetString(part, "type") != "text" ||
                GetString(part, "text") is not { } text)
                throw new NotSupportedException("Tilde supports only text content parts.");
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
        }
    }
}
