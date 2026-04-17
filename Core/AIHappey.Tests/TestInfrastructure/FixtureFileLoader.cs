using System.Text;
using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.TestInfrastructure;

internal static class FixtureFileConventions
{
    public const string TypedFixtureExtension = ".json";
    public const string RawStreamCaptureExtension = ".jsonl";
}

internal static class FixtureFileLoader
{
    public static IReadOnlyList<MessageStreamPart> LoadMessageTypedFixture(string relativePath)
        => DeserializeList<MessageStreamPart>(relativePath, MessagesJson.Default);

    public static IReadOnlyList<MessageStreamPart> LoadMessageRawFixture(string relativePath)
        => LoadJsonPayloads(relativePath)
            .Select(payload => JsonSerializer.Deserialize<MessageStreamPart>(payload, MessagesJson.Default)
                ?? throw new InvalidOperationException($"Could not deserialize message stream payload from [{relativePath}](Core/AIHappey.Tests/{relativePath})."))
            .ToList();

    public static IReadOnlyList<ResponseStreamPart> LoadResponseTypedFixture(string relativePath)
        => DeserializeList<ResponseStreamPart>(relativePath, ResponseJson.Default);

    public static IReadOnlyList<ResponseStreamPart> LoadResponseRawFixture(string relativePath)
        => LoadJsonPayloads(relativePath)
            .Select(payload => JsonSerializer.Deserialize<ResponseStreamPart>(payload, ResponseJson.Default)
                ?? throw new InvalidOperationException($"Could not deserialize response stream payload from [{relativePath}](Core/AIHappey.Tests/{relativePath})."))
            .ToList();

    public static IReadOnlyList<InteractionStreamEventPart> LoadInteractionRawFixture(string relativePath)
        => LoadJsonPayloads(relativePath)
            .Select(payload => JsonSerializer.Deserialize<InteractionStreamEventPart>(payload, InteractionJson.Default)
                ?? throw new InvalidOperationException($"Could not deserialize interaction stream payload from [{relativePath}](Core/AIHappey.Tests/{relativePath})."))
            .ToList();

    public static IReadOnlyList<UIMessagePart> LoadUiTypedFixture(string relativePath)
        => DeserializeList<UIMessagePart>(relativePath, JsonSerializerOptions.Web);

    public static IReadOnlyList<UIMessagePart> LoadUiRawFixture(string relativePath)
        => LoadJsonPayloads(relativePath)
            .Select(payload => JsonSerializer.Deserialize<UIMessagePart>(payload, JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException($"Could not deserialize UI stream payload from [{relativePath}](Core/AIHappey.Tests/{relativePath})."))
            .ToList();

    internal static string ResolveFixturePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(AppContext.BaseDirectory, normalized);
    }

    private static IReadOnlyList<T> DeserializeList<T>(string relativePath, JsonSerializerOptions options)
    {
        var fullPath = ResolveFixturePath(relativePath);
        var json = File.ReadAllText(fullPath);

        return JsonSerializer.Deserialize<List<T>>(json, options)
            ?? throw new InvalidOperationException($"Could not deserialize fixture array from [{relativePath}](Core/AIHappey.Tests/{relativePath}).");
    }

    private static IReadOnlyList<string> LoadJsonPayloads(string relativePath)
    {
        var fullPath = ResolveFixturePath(relativePath);
        var payloads = new List<string>();
        var sseBuffer = new StringBuilder();
        var insideSseEvent = false;

        foreach (var rawLine in File.ReadLines(fullPath))
        {
            var trimmed = rawLine.Trim();

            if (trimmed.Length == 0)
            {
                FlushBufferedSsePayload(payloads, sseBuffer, ref insideSseEvent);
                continue;
            }

            if (trimmed.StartsWith(':'))
                continue;

            if (trimmed.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("retry:", StringComparison.OrdinalIgnoreCase))
            {
                insideSseEvent = true;
                continue;
            }

            if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                insideSseEvent = true;

                var separatorIndex = rawLine.IndexOf(':');
                var data = separatorIndex >= 0
                    ? rawLine[(separatorIndex + 1)..].TrimStart()
                    : trimmed["data:".Length..].TrimStart();

                if (sseBuffer.Length > 0)
                    sseBuffer.AppendLine();

                sseBuffer.Append(data);
                continue;
            }

            FlushBufferedSsePayload(payloads, sseBuffer, ref insideSseEvent);
            payloads.Add(trimmed);
        }

        FlushBufferedSsePayload(payloads, sseBuffer, ref insideSseEvent);
        return payloads;
    }

    private static void FlushBufferedSsePayload(List<string> payloads, StringBuilder sseBuffer, ref bool insideSseEvent)
    {
        if (!insideSseEvent)
            return;

        insideSseEvent = false;

        var payload = sseBuffer.ToString().Trim();
        sseBuffer.Clear();

        if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            return;

        payloads.Add(payload);
    }
}
