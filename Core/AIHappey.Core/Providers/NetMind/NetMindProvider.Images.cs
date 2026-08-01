using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NetMind;

public partial class NetMindProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        ApplyAuthHeader();

        var files = request.Files?.ToList() ?? [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        using var message = BuildNetMindImageRequest(request, files, metadata);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"NetMind image request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = new List<string>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
                    images.Add(b64.GetString()!.ToDataUrl(MediaTypeNames.Image.Png));
                else if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    using var download = await _client.GetAsync(url.GetString(), cancellationToken);
                    var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (!download.IsSuccessStatusCode || bytes.Length == 0) throw new InvalidOperationException("NetMind image result download failed.");
                    images.Add(Convert.ToBase64String(bytes).ToDataUrl(download.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png));
                }
            }
        }
        if (images.Count == 0) throw new InvalidOperationException("NetMind image response contained no images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new() { Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    private static HttpRequestMessage BuildNetMindImageRequest(ImageRequest request, IReadOnlyList<ImageFile> files, JsonElement metadata)
    {
        var endpoint = files.Count switch { 0 => "images/generations", 1 => "images/variations", _ => "images/edits" };
        if (files.Count == 0)
        {
            var payload = NetMindObject(metadata);
            payload["model"] = request.Model; payload["prompt"] = request.Prompt;
            payload["n"] = request.N; payload["size"] = request.Size; payload["response_format"] = "b64_json";
            return new(HttpMethod.Post, endpoint) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json) };
        }

        var form = new MultipartFormDataContent();
        AddNetMindMetadata(form, metadata);
        Add(form, "model", request.Model); Add(form, "response_format", "b64_json"); Add(form, "n", request.N?.ToString(CultureInfo.InvariantCulture)); Add(form, "size", request.Size);
        if (files.Count > 1) Add(form, "prompt", request.Prompt);
        var selected = files.Count == 1 ? files.Take(1) : files;
        foreach (var file in selected) form.Add(NetMindFile(file), "image", "image");
        if (files.Count > 1 && request.Mask is not null) form.Add(NetMindFile(request.Mask), "mask", "mask");
        return new(HttpMethod.Post, endpoint) { Content = form };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, "images/generations", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        await foreach (var e in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(options, "images/generations", cancellationToken)) yield return e;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(options, "images/edits", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var e in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(options, "images/edits", cancellationToken)) yield return e;
    }

    private static Dictionary<string, object?> NetMindObject(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()) : [];
    private static void Add(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) form.Add(new StringContent(value), name);
    }
    private static void AddNetMindMetadata(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind == JsonValueKind.Object)
            foreach (var p in metadata.EnumerateObject()) Add(form, p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText());
    }
    private static ByteArrayContent NetMindFile(ImageFile file)
    {
        var content = new ByteArrayContent(Convert.FromBase64String(file.Data.RemoveDataUrlPrefix()));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.MediaType ?? MediaTypeNames.Application.Octet); return content;
    }
}
