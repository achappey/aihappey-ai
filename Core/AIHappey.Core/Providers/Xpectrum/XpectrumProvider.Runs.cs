using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Xpectrum;

/// <summary>
/// Workflow apps execute through POST /runs. Runs are mapped onto chat completions: inputs come from the
/// native <c>variables</c> option, the outputs become assistant content and the run object is kept as metadata.
/// Xpectrum documents no run status endpoint; asynchronous progress is available through the run stream.
/// </summary>
public sealed partial class XpectrumProvider
{
    private Dictionary<string, object?> RunPayload(ChatCompletionOptions options, bool stream)
    {
        var payload = RawPayload(options);
        if (StringValue(payload.GetValueOrDefault("user")) is null)
            throw new ArgumentException("Xpectrum workflow runs require a user identifier (user).");
        if (!payload.ContainsKey("variables"))
            payload["variables"] = new Dictionary<string, object?>();
        payload["stream"] = stream;
        return payload;
    }

    private async Task<ChatCompletion> RunWorkflowAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        using var request = JsonPost("runs", RunPayload(options, false), false, options.Headers);
        using var response = await _client.SendAsync(request, cancellationToken);
        await ThrowForStatusAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var run = document.RootElement;
        EnsureRunSucceeded(run);
        var model = NativeModel(options.Model).ToModelId(GetIdentifier());
        var native = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Str(run, "id") ?? $"run-{Guid.NewGuid():N}",
            ["object"] = "chat.completion",
            ["created"] = RunCreated(run),
            ["model"] = model,
            ["choices"] = new[]
            {
                new { index = 0, message = new { role = "assistant", content = OutputsText(run) }, finish_reason = "stop" }
            },
            ["run_id"] = Str(run, "id"),
            ["mode"] = "workflow"
        };
        if (RunUsage(run) is { } usage)
            native["usage"] = usage;
        var root = JsonSerializer.SerializeToElement(native);
        var completion = root.ReadProviderChatCompletion();
        var metadata = new Dictionary<string, object?> { ["mode"] = "workflow", ["run_id"] = Str(run, "id"), ["run"] = run.Clone() };
        var headers = response.GetHeaders();
        if (headers.Count > 0)
            metadata["headers"] = headers;
        (completion.AdditionalProperties ??= [])["providerMetadata"] =
            JsonSerializer.SerializeToElement(new Dictionary<string, object> { [GetIdentifier()] = metadata });
        return completion;
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> StreamWorkflowAsync(
        ChatCompletionOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = RunPayload(options, true);
        var user = StringValue(payload["user"])!;
        var model = NativeModel(options.Model).ToModelId(GetIdentifier());
        using var request = JsonPost("runs", payload, true, options.Headers);
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
                if (item.EventType == "error" || root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    finished = true;
                    throw StreamError(root);
                }
                var type = Str(root, "object");
                if (type is "run.started" or "run.completed")
                    runId ??= Str(root, "id");
                var completed = type == "run.completed";
                if (completed)
                {
                    finished = true;
                    EnsureRunSucceeded(root);
                }
                var chunk = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = runId ?? Str(root, "id") ?? "run",
                    ["object"] = "chat.completion.chunk",
                    ["created"] = RunCreated(root),
                    ["model"] = model,
                    ["choices"] = completed
                        ? new object[] { new { index = 0, delta = new { role = "assistant", content = OutputsText(root) }, finish_reason = "stop" } }
                        : [],
                    ["run_id"] = runId,
                    ["mode"] = "workflow",
                    ["providerMetadata"] = new Dictionary<string, object?>
                    {
                        [GetIdentifier()] = new Dictionary<string, object?> { ["mode"] = "workflow", ["run_id"] = runId, ["event"] = root.Clone() }
                    }
                };
                if (completed && RunUsage(root) is { } usage)
                    chunk["usage"] = usage;
                yield return JsonSerializer.Deserialize<ChatCompletionUpdate>(JsonSerializer.SerializeToElement(chunk))!;
            }
            cancellationToken.ThrowIfCancellationRequested();
            finished = true;
        }
        finally
        {
            if (!finished && runId is not null)
                await CancelRunAsync(runId, user);
        }
    }

    private static void EnsureRunSucceeded(JsonElement run)
    {
        var status = Str(run, "status");
        if (status is null or "succeeded")
            return;
        var detail = run.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
            ? error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText()
            : "no error details";
        throw new InvalidOperationException($"Xpectrum workflow run {Str(run, "id")} ended with status '{status}': {detail}");
    }

    private static long RunCreated(JsonElement run)
        => run.TryGetProperty("created_at", out var created) && created.TryGetInt64(out var seconds)
            ? seconds : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static object? RunUsage(JsonElement run)
        => run.TryGetProperty("tokens", out var tokens) && tokens.TryGetInt32(out var total)
            ? new { prompt_tokens = 0, completion_tokens = 0, total_tokens = total }
            : null;

    /// <summary>A single string output is returned as text; any other shape is returned as its JSON.</summary>
    private static string OutputsText(JsonElement run)
    {
        if (!run.TryGetProperty("outputs", out var outputs) || outputs.ValueKind == JsonValueKind.Null)
            return "";
        if (outputs.ValueKind == JsonValueKind.Object)
        {
            var properties = outputs.EnumerateObject().ToArray();
            if (properties.Length == 1 && properties[0].Value.ValueKind == JsonValueKind.String)
                return properties[0].Value.GetString()!;
        }
        return outputs.ValueKind == JsonValueKind.String ? outputs.GetString()! : outputs.GetRawText();
    }
}
