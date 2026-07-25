using System.Net.Mime;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BytePlus;

public partial class BytePlusProvider
{

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        var result = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var request = options.ToImageRequest(options.Model, GetIdentifier());

        if (IsSeedreamStreamingModel(request.Model))
        {
            await foreach (var streamEvent in StreamBytePlusImagesAsync(request, isEdit: false, cancellationToken))
                yield return streamEvent;
            yield break;
        }

        var result = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in result.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        if (IsSeedreamStreamingModel(request.Model))
        {
            await foreach (var streamEvent in StreamBytePlusImagesAsync(request, isEdit: true, cancellationToken))
                yield return streamEvent;
            yield break;
        }

        var result = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in result.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private async IAsyncEnumerable<IOpenAIImageStreamEvent> StreamBytePlusImagesAsync(
        ImageRequest request,
        bool isEdit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v3/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(CreateStreamingImagePayload(request), ImageJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"BytePlus streaming image request failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                foreach (var streamEvent in ConvertBytePlusSseEvent(dataLines, isEdit))
                    yield return streamEvent;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].Trim());
        }

        foreach (var streamEvent in ConvertBytePlusSseEvent(dataLines, isEdit))
            yield return streamEvent;
    }

    private static Dictionary<string, object?> CreateStreamingImagePayload(ImageRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? null : request.Size,
            ["response_format"] = "b64_json",
            ["stream"] = true
        };

        var images = request.Files?.Select(ToDataUrl).ToList() ?? [];
        if (images.Count > 0 && !IsSeedream30Model(request.Model))
            payload["image"] = images.Count == 1 ? images[0] : images;

        return payload;
    }

    private static IEnumerable<IOpenAIImageStreamEvent> ConvertBytePlusSseEvent(List<string> dataLines, bool isEdit)
    {
        if (dataLines.Count == 0)
            yield break;

        var data = string.Join("\n", dataLines).Trim();
        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            yield break;

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var type = ReadString(root, "type");

        if (string.Equals(type, "image_generation.partial_failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "error", StringComparison.OrdinalIgnoreCase)
            || root.TryGetProperty("error", out _))
        {
            throw new InvalidOperationException($"BytePlus image stream error: {ReadBytePlusError(root)}");
        }

        var created = ReadInt64(root, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (string.Equals(type, "image_generation.partial_succeeded", StringComparison.OrdinalIgnoreCase))
        {
            var b64 = ReadString(root, "b64_json");
            if (string.IsNullOrWhiteSpace(b64))
                throw new InvalidOperationException("BytePlus streamed an image URL where base64 image data was required.");

            if (isEdit)
            {
                yield return new OpenAIImageEditCompleted { B64Json = b64, CreatedAt = created, Size = ReadString(root, "size") };
            }
            else
            {
                yield return new OpenAIImageGenerationCompleted { B64Json = b64, CreatedAt = created, Size = ReadString(root, "size") };
            }

            yield break;
        }

        if (string.Equals(type, "image_generation.completed", StringComparison.OrdinalIgnoreCase))
        {
            // The established contract has no usage-only image stream event. The
            // BytePlus terminal usage is therefore consumed without an emission.
            _ = ReadBytePlusUsage(root);
        }
    }

    private static OpenAIImageUsage? ReadBytePlusUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        return new OpenAIImageUsage
        {
            OutputTokens = ReadInt32(usage, "output_tokens"),
            TotalTokens = ReadInt32(usage, "total_tokens")
        };
    }

    private static string ReadBytePlusError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = ReadString(error, "code");
            var message = ReadString(error, "message");
            return string.Join(": ", new[] { code, message }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return root.GetRawText();
    }

}
