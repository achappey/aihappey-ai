using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.QuiverAI;

public partial class QuiverAIProvider
{
    private const string SvgGenerationsEndpoint = "v1/svgs/generations";
    private const string SvgVectorizationsEndpoint = "v1/svgs/vectorizations";

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var root = await SendQuiverImageRequestAsync(
            SvgGenerationsEndpoint,
            CreateGenerationPayload(options, stream: false),
            cancellationToken);

        return ConvertQuiverResponse(root, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        await foreach (var streamEvent in StreamQuiverImagesAsync(
            SvgGenerationsEndpoint,
            CreateGenerationPayload(options, stream: true),
            isEdit: false,
            options.Size,
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var root = await SendQuiverImageRequestAsync(
            SvgVectorizationsEndpoint,
            await CreateVectorizationPayloadAsync(options, stream: false, cancellationToken),
            cancellationToken);

        return ConvertQuiverResponse(root, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        await foreach (var streamEvent in StreamQuiverImagesAsync(
            SvgVectorizationsEndpoint,
            await CreateVectorizationPayloadAsync(options, stream: true, cancellationToken),
            isEdit: true,
            options.Size,
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    private async Task<JsonElement> SendQuiverImageRequestAsync(
        string endpoint,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = CreateQuiverHttpRequest(endpoint, payload, acceptSse: false);
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"QuiverAI image request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async IAsyncEnumerable<IOpenAIImageStreamEvent> StreamQuiverImagesAsync(
        string endpoint,
        JsonObject payload,
        bool isEdit,
        string? size,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = CreateQuiverHttpRequest(endpoint, payload, acceptSse: true);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"QuiverAI streaming image request failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var eventName = string.Empty;
        var dataLines = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                foreach (var streamEvent in ConvertQuiverSseEvent(eventName, dataLines, isEdit, size))
                    yield return streamEvent;
                eventName = string.Empty;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                eventName = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].TrimStart());
        }

        foreach (var streamEvent in ConvertQuiverSseEvent(eventName, dataLines, isEdit, size))
            yield return streamEvent;
    }

    private static IEnumerable<IOpenAIImageStreamEvent> ConvertQuiverSseEvent(
        string eventName,
        List<string> dataLines,
        bool isEdit,
        string? size)
    {
        if (dataLines.Count == 0)
            yield break;

        var raw = string.Join("\n", dataLines).Trim();
        if (raw.Length == 0 || raw == "[DONE]")
            yield break;

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _) || string.Equals(eventName, "error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"QuiverAI image stream error: {raw}");

        // Quiver's generating/reasoning events carry progress text rather than an
        // image. Draft SVGs are true partial images; content SVGs are completed images.
        var svg = ReadSvg(root);
        if (string.IsNullOrWhiteSpace(svg))
            yield break;

        var created = ReadInt64(root, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var index = ReadInt32(root, "index") ?? 0;
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

        if (string.Equals(eventName, "draft", StringComparison.OrdinalIgnoreCase))
        {
            yield return isEdit
                ? new OpenAIImageEditPartialImage { B64Json = b64, CreatedAt = created, PartialImageIndex = index, Size = size, OutputFormat = "svg" }
                : new OpenAIImageGenerationPartialImage { B64Json = b64, CreatedAt = created, PartialImageIndex = index, Size = size, OutputFormat = "svg" };
        }
        else if (string.Equals(eventName, "content", StringComparison.OrdinalIgnoreCase))
        {
            var usage = ReadUsage(root);
            yield return isEdit
                ? new OpenAIImageEditCompleted { B64Json = b64, CreatedAt = created, Size = size, OutputFormat = "svg", Usage = usage }
                : new OpenAIImageGenerationCompleted { B64Json = b64, CreatedAt = created, Size = size, OutputFormat = "svg", Usage = usage };
        }
    }

    private static HttpRequestMessage CreateQuiverHttpRequest(string endpoint, JsonObject payload, bool acceptSse)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        if (acceptSse)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static JsonObject CreateGenerationPayload(OpenAIImageGenerationRequest options, bool stream)
    {
        var payload = CreateCommonPayload(options.Model, options.Size, options.AdditionalProperties, stream);
        payload["prompt"] = options.Prompt;
        payload["n"] = options.N ?? 1;
        CopyAdditional(payload, options.AdditionalProperties, "instructions", "references");
        ValidateBase64References(payload["references"]);
        return payload;
    }

    private static async Task<JsonObject> CreateVectorizationPayloadAsync(
        OpenAIImageEditRequest options,
        bool stream,
        CancellationToken cancellationToken)
    {
        var payload = CreateCommonPayload(options.Model, options.Size, options.AdditionalProperties, stream);
        payload["image"] = new JsonObject { ["base64"] = await ReadFirstBase64ImageAsync(options, cancellationToken) };
        CopyAdditional(payload, options.AdditionalProperties, "auto_crop", "target_size");
        return payload;
    }

    private static JsonObject CreateCommonPayload(
        string model,
        string? size,
        Dictionary<string, JsonElement>? additional,
        bool stream)
    {
        var payload = new JsonObject
        {
            ["model"] = NormalizeModel(model),
            ["stream"] = stream
        };

        if (TryParseSize(size, out var width, out var height))
        {
            payload["attributes"] = new JsonObject
            {
                ["viewBox"] = new JsonObject { ["minX"] = 0, ["minY"] = 0, ["width"] = width, ["height"] = height }
            };
        }

        CopyAdditional(payload, additional, "max_output_tokens", "presence_penalty", "temperature", "top_p", "attributes");
        return payload;
    }

    private static async Task<string> ReadFirstBase64ImageAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken)
    {
        var reference = options.Images?.FirstOrDefault()?.ImageUrl;
        if (!string.IsNullOrWhiteSpace(reference))
            return StripBase64DataUrl(reference);

        var file = options.ImageFiles?.FirstOrDefault();
        if (file is null)
            throw new ArgumentException("QuiverAI vectorization requires a base64 image input.", nameof(options));

        await using var input = file.OpenReadStream();
        using var output = new MemoryStream();
        await input.CopyToAsync(output, cancellationToken);
        return Convert.ToBase64String(output.ToArray());
    }

    private static string StripBase64DataUrl(string value)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("QuiverAI OpenAI-compatible image methods only support base64 image inputs.");

        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return ValidateBase64(value);

        var comma = value.IndexOf(',');
        if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The image data URL must contain base64 data.", nameof(value));
        return ValidateBase64(value[(comma + 1)..]);
    }

    private static string ValidateBase64(string value)
    {
        try { _ = Convert.FromBase64String(value); }
        catch (FormatException exception) { throw new ArgumentException("The image input is not valid base64.", nameof(value), exception); }
        return value;
    }

    private static OpenAIImagesResponse ConvertQuiverResponse(JsonElement root, string? size)
    {
        var data = new List<OpenAIImageData>();
        if (root.TryGetProperty("data", out var documents) && documents.ValueKind == JsonValueKind.Array)
        {
            foreach (var document in documents.EnumerateArray())
            {
                var svg = ReadSvg(document);
                if (!string.IsNullOrWhiteSpace(svg))
                    data.Add(new OpenAIImageData { B64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)) });
            }
        }

        if (data.Count == 0)
            throw new InvalidOperationException("QuiverAI returned no SVG documents.");

        return new OpenAIImagesResponse
        {
            Created = ReadInt64(root, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = data,
            OutputFormat = "svg",
            Size = size,
            Usage = ReadUsage(root)
        };
    }

    private static OpenAIImageUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        return new OpenAIImageUsage
        {
            InputTokens = ReadInt32(usage, "input_tokens"),
            OutputTokens = ReadInt32(usage, "output_tokens"),
            TotalTokens = ReadInt32(usage, "total_tokens")
        };
    }

    private static string? ReadSvg(JsonElement root)
    {
        if (root.TryGetProperty("svg", out var svg) && svg.ValueKind == JsonValueKind.String)
            return svg.GetString();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            return ReadSvg(data);
        return null;
    }

    private static int? ReadInt32(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static long? ReadInt64(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;

    private static void CopyAdditional(JsonObject payload, Dictionary<string, JsonElement>? additional, params string[] names)
    {
        if (additional is null)
            return;
        foreach (var name in names)
            if (additional.TryGetValue(name, out var value))
                payload[name] = JsonNode.Parse(value.GetRawText());
    }

    private static void ValidateBase64References(JsonNode? references)
    {
        if (references is not JsonArray array)
            return;
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var shorthand))
                _ = StripBase64DataUrl(shorthand);
            else if (item is JsonObject reference && reference["url"] is not null)
                throw new NotSupportedException("QuiverAI OpenAI-compatible image methods only support base64 image inputs.");
            else if (item is JsonObject base64Reference && base64Reference["base64"] is JsonValue base64Value)
                _ = ValidateBase64(base64Value.GetValue<string>());
        }
    }

    private static string NormalizeModel(string model)
    {
        const string prefix = "quiverai/";
        var value = model.Trim();
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
    }

    private static bool TryParseSize(string? size, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrWhiteSpace(size))
            return false;
        var parts = size.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height) && width > 0 && height > 0;
    }
}
