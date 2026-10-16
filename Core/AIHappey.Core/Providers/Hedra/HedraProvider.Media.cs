using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Hedra;

public partial class HedraProvider
{
    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private string ModelId(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }

    private static Dictionary<string, object?> CreateInput(JsonElement metadata, string field, string? value)
    {
        var input = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(value))
            input[field] = value;
        if (metadata.ValueKind != JsonValueKind.Object)
            return input;
        if (metadata.TryGetProperty("input", out var explicitInput))
        {
            if (explicitInput.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Hedra provider input must be an object.");
            metadata = explicitInput;
        }
        foreach (var property in metadata.EnumerateObject())
            input[property.Name] = property.Value.Clone();
        return input;
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object? payload,
        CancellationToken cancellationToken, bool authenticate = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (authenticate)
        {
            var key = _keyResolver.Resolve(GetIdentifier());
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("No Hedra API key.");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Key", key);
        }
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Hedra API error ({(int)response.StatusCode}): {raw}", null, response.StatusCode);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private Task<JsonElement> SubmitAsync(string model, Dictionary<string, object?> input, CancellationToken cancellationToken)
        => SendJsonAsync(HttpMethod.Post, $"v3/models/{Uri.EscapeDataString(ModelId(model))}", new { input }, cancellationToken);

    private Task<JsonElement> RetrieveAsync(string jobId, CancellationToken cancellationToken)
        => SendJsonAsync(HttpMethod.Get, $"v3/jobs/{Uri.EscapeDataString(jobId)}", null, cancellationToken);

    private async Task<JsonElement> WaitForJobAsync(JsonElement submit, CancellationToken cancellationToken)
    {
        var jobId = ReadString(submit, "job_id") ?? throw new InvalidOperationException($"Hedra response missing job_id: {submit}");
        while (true)
        {
            var result = await RetrieveAsync(jobId, cancellationToken);
            switch (ReadString(result, "status"))
            {
                case "COMPLETED":
                    return result;
                case "FAILED":
                    throw new InvalidOperationException($"Hedra job failed: {result.GetRawText()}");
                case "IN_QUEUE":
                case "IN_PROGRESS":
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown Hedra job status: {result.GetRawText()}");
            }
        }
    }

    private static List<JsonElement> Outputs(JsonElement result)
    {
        if (!result.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Hedra job returned no outputs: {result.GetRawText()}");
        var items = outputs.EnumerateArray().Select(item => item.Clone()).ToList();
        if (items.Count == 0)
            throw new InvalidOperationException($"Hedra job returned no outputs: {result.GetRawText()}");
        return items;
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadAsync(JsonElement output, CancellationToken cancellationToken)
    {
        var url = ReadString(output, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new InvalidOperationException($"Hedra output has no downloadable URL: {output.GetRawText()}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _mediaClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Hedra output download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}", null, response.StatusCode);
        return (bytes, ReadString(output, "content_type") ?? response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    private HeaderResponseData ResponseInfo(string model) => new()
    {
        ModelId = ModelId(model).ToModelId(GetIdentifier()),
        Timestamp = DateTime.UtcNow
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var input = CreateInput(metadata, "prompt", request.Prompt);
        var result = await WaitForJobAsync(await SubmitAsync(request.Model, input, cancellationToken), cancellationToken);
        var images = new List<string>();
        foreach (var output in Outputs(result))
        {
            var (bytes, mediaType) = await DownloadAsync(output, cancellationToken);
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }
        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result),
            Response = ResponseInfo(request.Model)
        };
    }

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var input = CreateInput(metadata, "text", request.Text);
        var result = await WaitForJobAsync(await SubmitAsync(request.Model, input, cancellationToken), cancellationToken);
        var (bytes, mediaType) = await DownloadAsync(Outputs(result)[0], cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mediaType,
                Format = mediaType.Split('/').Last()
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result),
            Response = new ResponseData
            {
                ModelId = ModelId(request.Model).ToModelId(GetIdentifier()),
                Timestamp = DateTime.UtcNow,
                Body = result
            }
        };
    }

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var result = await SubmitAsync(request.Model, CreateInput(metadata, "prompt", request.Prompt), cancellationToken);
        var jobId = ReadString(result, "job_id") ?? throw new InvalidOperationException($"Hedra response missing job_id: {result}");
        var token = JsonSerializer.SerializeToUtf8Bytes(new VideoOperation(jobId, ModelId(request.Model)));
        return new VideoOperationStartResult
        {
            Operation = "hdv1_" + Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result),
            Response = ResponseInfo(request.Model)
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        VideoOperation data;
        try
        {
            if (!operation.StartsWith("hdv1_", StringComparison.Ordinal))
                throw new FormatException();
            var encoded = operation[5..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            data = JsonSerializer.Deserialize<VideoOperation>(Convert.FromBase64String(encoded)) ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(data.JobId) || string.IsNullOrWhiteSpace(data.Model))
                throw new JsonException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("Invalid Hedra video operation token.", nameof(operation), ex);
        }
        var result = await RetrieveAsync(data.JobId, cancellationToken);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(result);
        var response = ResponseInfo(data.Model);
        switch (ReadString(result, "status"))
        {
            case "FAILED":
                return new VideoOperationErrorResult { Error = result.GetRawText(), ProviderMetadata = metadata, Response = response };
            case "IN_QUEUE":
            case "IN_PROGRESS":
                return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };
            case "COMPLETED":
                var videos = new List<VideoOperationVideoData>();
                foreach (var output in Outputs(result))
                {
                    var (bytes, mediaType) = await DownloadAsync(output, cancellationToken);
                    videos.Add(new VideoOperationVideoData { Type = "base64", Data = Convert.ToBase64String(bytes), MediaType = mediaType });
                }
                return new VideoOperationCompletedResult { Videos = videos, ProviderMetadata = metadata, Response = response };
            default:
                throw new InvalidOperationException($"Unknown Hedra job status: {result.GetRawText()}");
        }
    }

    private sealed record VideoOperation(string JobId, string Model);
}
