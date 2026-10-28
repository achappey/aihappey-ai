using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vultr;

public partial class VultrProvider
{
    private const string VultrVideoOperationPrefix = "vultrvideo_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var warnings = new List<object>();
        if (request.Duration is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Video duration must be positive.");

        AddUnsupportedVideoWarning(request.Seed, "seed", warnings);
        AddUnsupportedVideoWarning(request.Fps, "fps", warnings);
        AddUnsupportedVideoWarning(request.N, "n", warnings);
        AddUnsupportedVideoWarning(request.AspectRatio, "aspectRatio", warnings);
        AddUnsupportedVideoWarning(request.FrameImages?.Any() == true, "frameImages", warnings);
        AddUnsupportedVideoWarning(request.GenerateAudio, "generateAudio", warnings);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };
        if (request.Duration is not null)
            payload["seconds"] = request.Duration.Value;
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["size"] = request.Resolution;

        var inputReference = GetVultrVideoInputReference(request, warnings);
        if (inputReference is not null)
            payload["input_reference"] = inputReference;

        ApplyAuthHeader();
        using var response = await _client.PostAsync(
            "v1/videos",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(raw)
                    ? $"Vultr video creation failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Vultr video creation failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var id = ReadVultrVideoString(root, "id");
            if (string.IsNullOrWhiteSpace(id))
                throw new JsonException("Vultr video creation response did not contain an id.");

            return new VideoOperationStartResult
            {
                Operation = EncodeVultrVideoOperation(id, request.Model),
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
                Response = new HeaderResponseData
                {
                    Timestamp = DateTime.UtcNow,
                    ModelId = request.Model.ToModelId(GetIdentifier()),
                    Headers = response.GetHeaders()
                }
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Vultr returned an invalid video creation response.", exception);
        }
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var operationData = DecodeVultrVideoOperation(operation);

        ApplyAuthHeader();
        using var statusResponse = await _client.GetAsync(
            $"v1/videos/{Uri.EscapeDataString(operationData.Id)}",
            cancellationToken);
        var statusRaw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(statusRaw)
                    ? $"Vultr video status request failed ({(int)statusResponse.StatusCode} {statusResponse.ReasonPhrase})."
                    : $"Vultr video status request failed ({(int)statusResponse.StatusCode} {statusResponse.ReasonPhrase}): {statusRaw}",
                null,
                statusResponse.StatusCode);

        JsonElement statusRoot;
        string status;
        try
        {
            using var statusDocument = JsonDocument.Parse(statusRaw);
            statusRoot = statusDocument.RootElement.Clone();
            status = ReadVultrVideoString(statusRoot, "status")
                ?? throw new JsonException("Vultr video status response did not contain a status.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Vultr returned an invalid video status response.", exception);
        }

        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = operationData.Model.ToModelId(GetIdentifier()),
            Headers = statusResponse.GetHeaders()
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(statusRoot);

        if (status.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || status.Equals("in_progress", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationPendingResult
            {
                ProviderMetadata = metadata,
                Response = responseData
            };
        }

        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
        {
            var error = ReadVultrVideoString(statusRoot, "error")
                ?? ReadVultrVideoString(statusRoot, "failure_reason")
                ?? $"Vultr video operation '{operationData.Id}' {status}.";
            return new VideoOperationErrorResult
            {
                Error = error,
                ProviderMetadata = metadata,
                Response = responseData
            };
        }

        if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Vultr returned unsupported video status '{status}'.");

        using var contentResponse = await _client.GetAsync(
            $"v1/videos/{Uri.EscapeDataString(operationData.Id)}/content",
            cancellationToken);
        var bytes = await contentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResponse.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Vultr video download failed ({(int)contentResponse.StatusCode} {contentResponse.ReasonPhrase}): {Encoding.UTF8.GetString(bytes)}",
                null,
                contentResponse.StatusCode);
        if (bytes.Length == 0)
            throw new InvalidOperationException("Vultr returned an empty video.");

        responseData.Headers = contentResponse.GetHeaders();
        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = contentResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4",
                    Data = Convert.ToBase64String(bytes)
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private static void AddUnsupportedVideoWarning(object? value, string feature, ICollection<object> warnings)
    {
        if (value is not null)
            warnings.Add(new { type = "unsupported", feature });
    }

    private static void AddUnsupportedVideoWarning(bool value, string feature, ICollection<object> warnings)
    {
        if (value)
            warnings.Add(new { type = "unsupported", feature });
    }

    private static object? GetVultrVideoInputReference(VideoRequest request, ICollection<object> warnings)
    {
        var references = request.InputReferences?.ToArray() ?? [];
        if (request.Image is not null && references.Length > 0)
            throw new ArgumentException("Vultr accepts one video input_reference; provide either image or inputReferences, not both.", nameof(request));

        if (request.Image is not null)
        {
            if (TryGetVultrVideoHttpsUrl(request.Image, out var imageUrl))
                return new { image_url = imageUrl };

            warnings.Add(new { type = "unsupported", feature = "image", reason = "Only HTTPS image URLs are mapped to Vultr input_reference." });
            return null;
        }

        if (references.Length == 0)
            return null;
        if (references.Length != 1)
        {
            warnings.Add(new { type = "unsupported", feature = "inputReferences", reason = "Vultr accepts one input_reference." });
            return null;
        }
        if (TryGetVultrVideoHttpsUrl(references[0], out var referenceUrl))
            return referenceUrl;

        warnings.Add(new { type = "unsupported", feature = "inputReferences", reason = "Only one HTTPS URL is mapped to Vultr input_reference." });
        return null;
    }

    private static bool TryGetVultrVideoHttpsUrl(VideoFile file, out string url)
    {
        url = string.Empty;
        if (file is not VideoFileUrl urlFile
            || !Uri.TryCreate(urlFile.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        url = uri.AbsoluteUri;
        return true;
    }

    private static string EncodeVultrVideoOperation(string id, string model)
    {
        var value = JsonSerializer.SerializeToUtf8Bytes(new VultrVideoOperationData(id, model));
        return VultrVideoOperationPrefix
            + Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private VultrVideoOperationData DecodeVultrVideoOperation(string operation)
    {
        if (!operation.StartsWith(VultrVideoOperationPrefix, StringComparison.Ordinal))
            return new VultrVideoOperationData(Uri.UnescapeDataString(operation), GetIdentifier());

        var encoded = operation[VultrVideoOperationPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        try
        {
            return JsonSerializer.Deserialize<VultrVideoOperationData>(Convert.FromBase64String(encoded))
                is { Id.Length: > 0, Model.Length: > 0 } data
                    ? data
                    : throw new ArgumentException("The Vultr video operation token is invalid.", nameof(operation));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The Vultr video operation token is invalid.", nameof(operation), exception);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The Vultr video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? ReadVultrVideoString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private sealed record VultrVideoOperationData(string Id, string Model);
}
