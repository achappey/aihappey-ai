using AIHappey.Core.AI;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using System.Text.Json;
using System.Text;
using System.Globalization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.StabilityAI;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.StabilityAI;

public partial class StabilityAIProvider : IModelProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = imageRequest.GetImageProviderMetadata<StabilityAIImageProviderMetadata>(GetIdentifier());
        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;

        var modelSuffix = NormalizeModelSuffix(imageRequest.Model);
        var (path, sd3Model) = ResolveStabilityEndpoint(modelSuffix);

        var warnings = new List<object>();

        if (imageRequest.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        var aspectRatio = SizeToAspectRatio(imageRequest.Size) ?? "1:1";

        using var form = new MultipartFormDataContent();

        form.Add(NamedField("prompt", imageRequest.Prompt));
        form.Add(NamedField("output_format", "png"));
        form.Add(NamedField("aspect_ratio", aspectRatio));

        // sd3 only
        form.Add(NamedField("mode", "text-to-image"));
        form.Add(NamedField("model", sd3Model ?? "sd3.5-flash"));

        // optional seed
        if (imageRequest.Seed is not null)
            form.Add(NamedField("seed", imageRequest.Seed.Value.ToString(CultureInfo.InvariantCulture)));

        if (!string.IsNullOrEmpty(metadata?.NegativePrompt))
            form.Add(NamedField("negative_prompt", metadata.NegativePrompt));

        if (!string.IsNullOrEmpty(metadata?.StylePreset))
            form.Add(NamedField("style_preset", metadata.StylePreset));

        // sanity check (copy from MCP)
        foreach (var part in form)
        {
            var cd = part.Headers.ContentDisposition;
            if (cd?.Name is null)
                throw new InvalidOperationException($"Form part missing name. Headers: {part.Headers}");
        }

        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

        using var resp = await _client.PostAsync(path, form, cancellationToken);
        var bytesOut = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(bytesOut);
            throw new Exception($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {TryExtractErrorMessage(text)}");
        }

        var mime = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytesOut)}";

        return new ImageResponse
        {
            Images = [dataUrl],
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };
    }


    private static StringContent NamedField(string name, string value)
    {
        var c = new StringContent(value ?? "", Encoding.UTF8);
        c.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{name}\""
        };
        return c;
    }

    private static (string path, string? sd3Model) ResolveStabilityEndpoint(string modelSuffix) =>
        modelSuffix switch
        {
            "stable-image-ultra" => ("stable-image/generate/ultra", null),
            "stable-image-core" => ("stable-image/generate/core", null),

            // sd3 / sd3.5 variants all go through /sd3 + "model" field
            var m when m.StartsWith("sd3", StringComparison.OrdinalIgnoreCase)
                => ("stable-image/generate/sd3", m),

            _ => ("stable-image/generate/core", null)
        };

    private static string NormalizeModelSuffix(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "stable-image-core";

        var s = model.Trim();

        // handle ids like "stabilityai/sd3.5-large" or "stabilityai:sd3.5-large"
        var cut = s.LastIndexOfAny(['/', ':', '|', '\\']);
        if (cut >= 0 && cut < s.Length - 1)
            s = s[(cut + 1)..];

        return s.ToLowerInvariant();
    }

    private static string? SizeToAspectRatio(string? size)
    {
        // expected: "1024x1024" etc
        if (string.IsNullOrWhiteSpace(size))
            return "1:1";

        var parts = size.ToLowerInvariant().Split('x');
        if (parts.Length != 2)
            return "1:1";

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ||
            w <= 0 || h <= 0)
            return "1:1";

        var target = (double)w / h;

        // supported ratios in Stability "stable-image" endpoints
        var options = new Dictionary<string, double>
        {
            ["1:1"] = 1.0,
            ["16:9"] = 16d / 9d,
            ["21:9"] = 21d / 9d,
            ["2:3"] = 2d / 3d,
            ["3:2"] = 3d / 2d,
            ["4:5"] = 4d / 5d,
            ["5:4"] = 5d / 4d,
            ["9:16"] = 9d / 16d,
            ["9:21"] = 9d / 21d
        };

        string best = "1:1";
        double bestDiff = double.MaxValue;

        foreach (var kv in options)
        {
            var diff = Math.Abs(kv.Value - target);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = kv.Key;
            }
        }

        return best;
    }

    private static string TryExtractErrorMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown error";

        raw = raw.Trim();

        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    return msg.GetString() ?? raw;

                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? raw;
                    if (err.ValueKind == JsonValueKind.Object &&
                        err.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                        return em.GetString() ?? raw;
                }

                if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                    errors.ValueKind == JsonValueKind.Array &&
                    errors.GetArrayLength() > 0)
                {
                    var first = errors[0];
                    if (first.ValueKind == JsonValueKind.Object &&
                        first.TryGetProperty("message", out var fm) && fm.ValueKind == JsonValueKind.String)
                        return fm.GetString() ?? raw;
                }
            }
        }
        catch
        {
            // not JSON, fall through
        }

        return raw;
    }

    public Task<string> GetToken(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<string> GetToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<string> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
