using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Xpectrum;

public sealed partial class XpectrumProvider
{
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mode = await ResolveModeAsync(options.Model, cancellationToken);
        if (mode == "workflow")
            return await RunWorkflowAsync(options, cancellationToken);
        // Autonomous Agent apps reject blocking requests; aggregate the native stream instead.
        if (mode == "agent")
            return await AggregateAsync(StreamChatAsync(options, mode, cancellationToken), mode, cancellationToken);

        using var request = JsonPost("chat/completions", ChatPayload(options, false), false, options.Headers);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Discovery may be stale after an app type change in the console; follow the server's verdict.
            if (body.Contains("does not support blocking mode", StringComparison.OrdinalIgnoreCase))
                return await AggregateAsync(StreamChatAsync(options, "agent", cancellationToken), "agent", cancellationToken);
            throw CreateError(response.StatusCode, body, response.Headers.RetryAfter);
        }
        await ThrowForStatusAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var completion = document.RootElement.ReadProviderChatCompletion();
        completion.Model = (string.IsNullOrWhiteSpace(completion.Model) ? NativeModel(options.Model) : completion.Model)
            .ToModelId(GetIdentifier());
        AttachMetadata(completion, document.RootElement, mode, response.GetHeaders());
        return completion;
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mode = await ResolveModeAsync(options.Model, cancellationToken);
        var stream = mode == "workflow"
            ? StreamWorkflowAsync(options, cancellationToken)
            : StreamChatAsync(options, mode, cancellationToken);
        await foreach (var update in stream.WithCancellation(cancellationToken))
            yield return update;
    }

    private Dictionary<string, object?> RawPayload(ChatCompletionOptions options)
    {
        var metadata = options.GetProviderMetadata();
        var payload = metadata.TryGetValue(GetIdentifier(), out var raw)
            ? CopyObject(ToElement(raw))
            : new(StringComparer.Ordinal);
        foreach (var property in options.AdditionalProperties ?? [])
            if (property.Key != "providerMetadata")
                payload[property.Key] = property.Value.Clone();
        if (!string.IsNullOrWhiteSpace(options.User))
            payload["user"] = options.User;
        return payload;
    }

    private Dictionary<string, object?> ChatPayload(ChatCompletionOptions options, bool stream)
    {
        var payload = RawPayload(options);
        // Xpectrum keeps history server-side (thread_id) and reads only the last user message.
        var last = options.Messages?.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (last is not null)
            payload["messages"] = new[] { JsonSerializer.SerializeToElement(last) };
        else if (!payload.ContainsKey("messages"))
            throw new ArgumentException("Xpectrum chat requires a user message.");
        if (!string.IsNullOrWhiteSpace(options.Model))
            payload["model"] = NativeModel(options.Model);
        payload["stream"] = stream;
        return payload;
    }

    private HttpRequestMessage JsonPost(string path, Dictionary<string, object?> payload, bool stream, Dictionary<string, string>? headers)
    {
        ApplyAuthHeader();
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(JsonSerializer.SerializeToElement(payload))
        };
        request.Headers.Accept.Add(new(stream ? "text/event-stream" : "application/json"));
        foreach (var header in headers ?? [])
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return request;
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> StreamChatAsync(
        ChatCompletionOptions options, string mode, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = ChatPayload(options, true);
        var user = StringValue(payload.GetValueOrDefault("user"));
        using var request = JsonPost("chat/completions", payload, true, options.Headers);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowForStatusAsync(response, cancellationToken);
        string? runId = null;
        var finished = false;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
            {
                var data = item.Data;
                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                {
                    finished = true;
                    yield break;
                }
                if (string.IsNullOrWhiteSpace(data))
                    continue;
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                runId ??= Str(root, "run_id");
                if (item.EventType == "error" || root.TryGetProperty("error", out _))
                {
                    finished = true;
                    throw StreamError(root);
                }
                var update = JsonSerializer.Deserialize<ChatCompletionUpdate>(data)
                    ?? throw new InvalidOperationException("Xpectrum returned an empty chat chunk.");
                update.Model = (string.IsNullOrWhiteSpace(update.Model) ? NativeModel(options.Model) : update.Model)
                    .ToModelId(GetIdentifier());
                if (!root.TryGetProperty("mode", out _))
                    (update.AdditionalProperties ??= [])["mode"] = JsonSerializer.SerializeToElement(mode);
                if (update.Choices.Any(choice => !string.IsNullOrEmpty(choice.FinishReason)))
                    finished = true;
                yield return update;
            }
            cancellationToken.ThrowIfCancellationRequested();
            finished = true;
        }
        finally
        {
            // Closing the connection does not stop server-side generation.
            if (!finished && runId is not null && user is not null)
                await CancelRunAsync(runId, user);
        }
    }

    private static InvalidOperationException StreamError(JsonElement root)
    {
        var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        var code = Str(error, "code") ?? Str(error, "type");
        var message = Str(error, "message") ?? Str(root, "error") ?? root.GetRawText();
        var exception = new InvalidOperationException($"Xpectrum stream error{(code is null ? "" : $" ({code})")}: {message}");
        if (code is not null)
            exception.Data["code"] = code;
        return exception;
    }

    private async Task CancelRunAsync(string runId, string user)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CancelTimeout);
            using var request = JsonPost($"runs/{Uri.EscapeDataString(runId)}/cancel",
                new() { ["user"] = user }, false, null);
            using var response = await _client.SendAsync(request, timeout.Token);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // Best-effort cleanup must not mask the original cancellation or failure.
        }
    }

    private async Task<ChatCompletion> AggregateAsync(
        IAsyncEnumerable<ChatCompletionUpdate> updates, string mode, CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var native = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? id = null, model = null, finishReason = null;
        long created = 0;
        ChatCompletionUsage? usage = null;
        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            id ??= update.Id;
            model ??= update.Model;
            if (update.Created > 0 && created == 0)
                created = update.Created;
            usage = update.Usage ?? usage;
            foreach (var property in update.AdditionalProperties ?? [])
                native[property.Key] = property.Value.Clone();
            foreach (var choice in update.Choices)
            {
                content.Append(choice.Delta?.Content);
                finishReason = choice.FinishReason ?? finishReason;
            }
        }
        native["id"] = id ?? $"chatcmpl-{Guid.NewGuid():N}";
        native["object"] = "chat.completion";
        native["created"] = created > 0 ? created : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        native["model"] = model ?? GetIdentifier();
        native["choices"] = new[]
        {
            new { index = 0, message = new { role = "assistant", content = content.ToString() }, finish_reason = finishReason ?? "stop" }
        };
        if (usage is not null)
            native["usage"] = usage;
        var root = JsonSerializer.SerializeToElement(native);
        var completion = root.ReadProviderChatCompletion();
        AttachMetadata(completion, root, mode, null);
        return completion;
    }

    private void AttachMetadata(ChatCompletion completion, JsonElement root, string mode, Dictionary<string, string>? headers)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["mode"] = Str(root, "mode") ?? mode };
        foreach (var name in new[] { "thread_id", "run_id", "citations" })
            if (root.TryGetProperty(name, out var value))
                metadata[name] = value.Clone();
        if (headers is { Count: > 0 })
            metadata["headers"] = headers;
        (completion.AdditionalProperties ??= [])["providerMetadata"] =
            JsonSerializer.SerializeToElement(new Dictionary<string, object> { [GetIdentifier()] = metadata });
    }
}
