using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NetMind;

public partial class NetMindProvider
{
    private const string NetMindVideoOperationTokenPrefix = "nmv1_";
    private static readonly Uri NetMindGenerationUri = new("https://api.netmind.ai/v1/generation");
    private static readonly JsonSerializerOptions NetMindVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record NetMindVideoOperationData(string GenerationId, string? Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var config = NetMindObject(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        config["prompt"] = request.Prompt;
        config["duration"] = request.Duration;
        config["resolution"] = request.Resolution;
        config["aspect_ratio"] = request.AspectRatio;
        config["fps"] = request.Fps;
        config["seed"] = request.Seed;
        config["n"] = request.N;
        if (request.Image is not null)
            config["image"] = request.Image.Data;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["config"] = config
        };
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, NetMindGenerationUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, NetMindVideoJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"NetMind video create failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDocument = JsonDocument.Parse(createRaw);
        var create = createDocument.RootElement.Clone();
        var generationId = FindNetMindString(create, "id", "generation_id", "task_id", "job_id")
            ?? throw new InvalidOperationException("NetMind video create response contained no generation id.");
        var status = FindNetMindString(create, "status", "state") ?? "pending";

        return new VideoOperationStartResult
        {
            Operation = EncodeNetMindVideoOperation(generationId, request.Model),
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                generationId,
                status,
                generation = create
            }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeNetMindVideoOperation(operation);
        ApplyAuthHeader();
        var generationUri = new Uri($"{NetMindGenerationUri}/{Uri.EscapeDataString(operationData.GenerationId)}");
        using var pollResponse = await _client.GetAsync(generationUri, cancellationToken);
        var pollRaw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"NetMind video poll failed ({(int)pollResponse.StatusCode}): {pollRaw}");

        using var pollDocument = JsonDocument.Parse(pollRaw);
        var generation = pollDocument.RootElement.Clone();
        var status = FindNetMindString(generation, "status", "state") ?? "unknown";
        var providerModel = FindNetMindString(generation, "model", "model_id");
        var model = string.IsNullOrWhiteSpace(providerModel) ? operationData.Model : providerModel;
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            generationId = operationData.GenerationId,
            status,
            generation
        });
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = pollResponse.GetHeaders(),
            ModelId = string.IsNullOrWhiteSpace(model)
                ? GetIdentifier()
                : model.ToModelId(GetIdentifier())
        };

        if (!NetMindTerminal(status))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        if (!NetMindSuccess(status))
        {
            return new VideoOperationErrorResult
            {
                Error = $"NetMind video generation '{operationData.GenerationId}' failed with status '{status}': {pollRaw}",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var urls = FindNetMindResultUrls(generation).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = $"NetMind video generation '{operationData.GenerationId}' completed without result URLs.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var videos = new List<VideoOperationVideoData>();
        foreach (var url in urls)
        {
            using var download = await _client.GetAsync(url, cancellationToken);
            var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!download.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"NetMind video result download failed ({(int)download.StatusCode}).");

            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                Data = Convert.ToBase64String(bytes),
                MediaType = download.Content.Headers.ContentType?.MediaType ?? "video/mp4"
            });
        }

        using var deleteResponse = await _client.DeleteAsync(generationUri, cancellationToken);
        if (!deleteResponse.IsSuccessStatusCode)
        {
            var deleteRaw = await deleteResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"NetMind video job cleanup failed ({(int)deleteResponse.StatusCode}): {deleteRaw}");
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static string EncodeNetMindVideoOperation(string generationId, string model)
    {
        var json = JsonSerializer.Serialize(new NetMindVideoOperationData(generationId, model), NetMindVideoJson);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return NetMindVideoOperationTokenPrefix + base64Url;
    }

    private static NetMindVideoOperationData DecodeNetMindVideoOperation(string operation)
    {
        if (!operation.StartsWith(NetMindVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new NetMindVideoOperationData(Uri.UnescapeDataString(operation), null);

        var base64Url = operation[NetMindVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<NetMindVideoOperationData>(json, NetMindVideoJson);
            if (data is null || string.IsNullOrWhiteSpace(data.GenerationId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The NetMind video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The NetMind video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static bool NetMindTerminal(string? status)
        => NetMindSuccess(status)
            || status?.ToLowerInvariant() is "failed" or "error" or "cancelled" or "canceled";

    private static bool NetMindSuccess(string? status)
        => status?.ToLowerInvariant() is "success" or "succeeded" or "completed" or "done";

    private static string? FindNetMindString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return value.ToString();
        }

        foreach (var property in element.EnumerateObject())
        {
            var found = FindNetMindString(property.Value, names);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static IEnumerable<string> FindNetMindResultUrls(JsonElement generation)
    {
        if (!generation.TryGetProperty("result", out var result)
            || !result.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var absoluteUrl))
                yield return absoluteUrl.ToString();
        }
    }
}
