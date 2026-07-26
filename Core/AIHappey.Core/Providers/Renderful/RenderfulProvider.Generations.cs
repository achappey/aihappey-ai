using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Renderful;

public partial class RenderfulProvider
{
    private static readonly TimeSpan RenderfulPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RenderfulPollTimeout = TimeSpan.FromMinutes(10);

    private async Task<RenderfulGeneration> CreateGenerationAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"Renderful generation failed ({(int)createResponse.StatusCode})."
                : $"Renderful generation failed ({(int)createResponse.StatusCode}): {createRaw}");
        }

        using var createDocument = JsonDocument.Parse(createRaw);
        var created = createDocument.RootElement.Clone();
        var id = GetRenderfulString(created, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Renderful generation response did not include an id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: token => GetGenerationAsync(id, token),
            isTerminal: generation => IsRenderfulTerminalStatus(generation.Status),
            interval: RenderfulPollInterval,
            timeout: RenderfulPollTimeout,
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!string.Equals(completed.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var error = GetRenderfulString(completed.Root, "error", "message") ?? completed.Raw;
            throw new InvalidOperationException(
                $"Renderful generation failed with status '{completed.Status ?? "unknown"}' (id={id}): {error}");
        }

        return completed;
    }

    private async Task<RenderfulGeneration> GetGenerationAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"v1/generations/{Uri.EscapeDataString(id)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Renderful generation poll failed ({(int)response.StatusCode})."
                : $"Renderful generation poll failed ({(int)response.StatusCode}): {raw}");
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        return new RenderfulGeneration(
            root,
            raw,
            GetRenderfulString(root, "status"),
            GetRenderfulOutputUrls(root));
    }

    private static bool IsRenderfulTerminalStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);

    private static List<string> GetRenderfulOutputUrls(JsonElement root)
    {
        List<string> outputs = [];
        if (root.TryGetProperty("outputs", out var outputsElement)
            && outputsElement.ValueKind == JsonValueKind.Array)
        {
            outputs.AddRange(outputsElement.EnumerateArray()
                .Where(static output => output.ValueKind == JsonValueKind.String)
                .Select(static output => output.GetString())
                .Where(static output => !string.IsNullOrWhiteSpace(output))!);
        }

        var output = GetRenderfulString(root, "output");
        if (!string.IsNullOrWhiteSpace(output) && !outputs.Contains(output, StringComparer.Ordinal))
            outputs.Add(output);

        return outputs;
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadOutputAsync(
        string url,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Renderful output download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return (bytes, response.Content.Headers.ContentType?.MediaType ?? fallbackMediaType);
    }

    private static Dictionary<string, object?> CreateRenderfulPayload(
        Dictionary<string, JsonElement>? providerOptions,
        IDictionary<string, object?> defaults)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (providerOptions is not null
            && providerOptions.TryGetValue(nameof(Renderful).ToLowerInvariant(), out var metadata)
            && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        foreach (var (key, value) in defaults)
        {
            if (value is not null && !payload.ContainsKey(key))
                payload[key] = value;
        }

        return payload;
    }

    private static Dictionary<string, JsonElement> CreateRenderfulMetadata(JsonElement root)
        => GetIdentifierStatic().CreatePrimitiveProviderMetadata(root.Clone());

    private static string GetIdentifierStatic() => nameof(Renderful).ToLowerInvariant();

    private static string? GetRenderfulString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private sealed record RenderfulGeneration(
        JsonElement Root,
        string Raw,
        string? Status,
        IReadOnlyList<string> Outputs);
}
