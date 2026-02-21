using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Haimaker;

public partial class HaimakerProvider
{
    private static readonly JsonSerializerOptions haimakerImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var files = request.Files?.ToList() ?? [];

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio"
            });
        }

        if (request.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        HttpResponseMessage httpResponse;

        // Routing rules requested for Haimaker:
        // 1) no files => generations
        // 2) exactly one file => edits
        // 3) more than one file => variations (use first file)
        if (files.Count == 0)
        {
            if (request.Mask is not null)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "mask",
                    details = "Mask requires an input image. Ignored for generation route."
                });
            }

            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["prompt"] = request.Prompt,
                ["n"] = request.N,
                ["size"] = request.Size,
                ["response_format"] = "b64_json"
            };

            var json = JsonSerializer.Serialize(payload, haimakerImageJsonOptions);
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
            {
                Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            httpResponse = await _client.SendAsync(req, cancellationToken);
        }
        else if (files.Count == 1)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(request.Model), "model");
            form.Add(new StringContent(request.Prompt), "prompt");
            form.Add(new StringContent("b64_json"), "response_format");

            if (request.N.HasValue)
                form.Add(new StringContent(request.N.Value.ToString()), "n");

            if (!string.IsNullOrWhiteSpace(request.Size))
                form.Add(new StringContent(request.Size), "size");

            form.Add(CreateImageContent(files[0]), "image", "image");

            if (request.Mask is not null)
                form.Add(CreateImageContent(request.Mask), "mask", "mask");

            httpResponse = await _client.PostAsync("v1/images/edits", form, cancellationToken);
        }
        else
        {
            if (request.Mask is not null)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "mask",
                    details = "Mask is not supported on variation route."
                });
            }

            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = $"Variation route uses files[0] only. Received {files.Count} files."
            });

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(request.Model), "model");
            form.Add(new StringContent("b64_json"), "response_format");

            if (request.N.HasValue)
                form.Add(new StringContent(request.N.Value.ToString()), "n");

            if (!string.IsNullOrWhiteSpace(request.Size))
                form.Add(new StringContent(request.Size), "size");

            form.Add(CreateImageContent(files[0]), "image", "image");

            httpResponse = await _client.PostAsync("v1/images/variations", form, cancellationToken);
        }

        var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
            throw new Exception($"Haimaker API error: {(int)httpResponse.StatusCode} {httpResponse.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var images = await ParseImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new Exception("Haimaker image endpoint returned no images.");

        ImageUsageData? usage = null;
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            usage = new ImageUsageData
            {
                InputTokens = usageEl.TryGetProperty("prompt_tokens", out var inputEl) && inputEl.TryGetInt32(out var input)
                    ? input
                    : null,
                OutputTokens = usageEl.TryGetProperty("completion_tokens", out var outputEl) && outputEl.TryGetInt32(out var output)
                    ? output
                    : null,
                TotalTokens = usageEl.TryGetProperty("total_tokens", out var totalEl) && totalEl.TryGetInt32(out var total)
                    ? total
                    : null
            };
        }

        var timestamp = now;
        if (root.TryGetProperty("created", out var createdEl) &&
            createdEl.ValueKind == JsonValueKind.Number &&
            createdEl.TryGetInt64(out var createdUnix))
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(createdUnix).UtcDateTime;
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = usage,
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static ByteArrayContent CreateImageContent(ImageFile file)
    {
        var bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.MediaType)
                ? MediaTypeNames.Application.Octet
                : file.MediaType);

        return content;
    }

    private async Task<List<string>> ParseImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in dataEl.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));

                continue;
            }

            if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var url = urlEl.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    using var imgResp = await _client.GetAsync(url, cancellationToken);
                    var bytes = await imgResp.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (!imgResp.IsSuccessStatusCode || bytes is null || bytes.Length == 0)
                        continue;

                    var mediaType = imgResp.Content.Headers.ContentType?.MediaType;
                    if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        mediaType = MediaTypeNames.Image.Png;

                    images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
                }
            }
        }

        return images;
    }
}

