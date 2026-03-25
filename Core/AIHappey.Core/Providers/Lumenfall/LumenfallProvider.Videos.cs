using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Lumenfall;

public partial class LumenfallProvider
{
    private static readonly JsonSerializerOptions LumenfallVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record LumenfallVideoFetchResult(string Status, string Raw, JsonElement Root);

    private async Task<VideoResponse> VideoRequestLumenfall(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        var normalizedN = NormalizeVideoN(request.N, warnings);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var dryRun = LumenfallTryGetBool(metadata, "dryRun") == true;
        var endpoint = dryRun ? "v1/videos?dryRun=true" : "v1/videos";

        using var createReq = BuildVideoCreateRequest(endpoint, request, metadata, normalizedN, warnings);
        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lumenfall video create failed ({(int)createResp.StatusCode}) [{endpoint}]: {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();

        if (dryRun)
        {
            return new VideoResponse
            {
                Videos = [],
                Warnings = warnings,
                ProviderMetadata = new Dictionary<string, JsonElement>
                {
                    [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                    {
                        endpoint,
                        body = createRoot
                    }, JsonSerializerOptions.Web)
                },
                Response = new()
                {
                    Timestamp = ResolveVideoTimestamp(createRoot, now),
                    ModelId = request.Model,
                    Body = createRoot
                }
            };
        }

        var videoId = ReadString(createRoot, "id");
        if (string.IsNullOrWhiteSpace(videoId))
            throw new InvalidOperationException("Lumenfall video create returned no id.");

        using var cancellationRegistration = RegisterBestEffortCancel(videoId, cancellationToken);

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => FetchVideoTaskAsync(videoId, ct),
            isTerminal: r => IsTerminalVideoStatus(r.Status),
            interval: TimeSpan.FromSeconds(3),
            timeout: TimeSpan.FromMinutes(15),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (IsFailedVideoStatus(completed.Status))
            throw new InvalidOperationException($"Lumenfall video generation failed with status '{completed.Status}' (id={videoId}): {TryGetVideoError(completed.Root)}");

        var outputUrls = TryGetVideoOutputUrls(completed.Root);
        if (outputUrls.Count == 0)
            throw new InvalidOperationException($"Lumenfall video generation completed but returned no output url (id={videoId}).");

        var videos = await DownloadVideoOutputsAsync(outputUrls, cancellationToken);
        if (videos.Count == 0)
            throw new InvalidOperationException($"Lumenfall video generation completed but no downloadable outputs were found (id={videoId}).");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    endpoint,
                    id = videoId,
                    create = createRoot,
                    final = completed.Root
                }, JsonSerializerOptions.Web)
            },
            Response = new()
            {
                Timestamp = ResolveVideoTimestamp(completed.Root, now),
                ModelId = request.Model,
                Body = completed.Root
            }
        };
    }

    private static HttpRequestMessage BuildVideoCreateRequest(
        string endpoint,
        VideoRequest request,
        JsonElement metadata,
        int? normalizedN,
        List<object> warnings)
    {
        if (request.Image is null)
        {
            var payload = BuildVideoJsonPayload(request, metadata, normalizedN);
            var json = JsonSerializer.Serialize(payload, LumenfallVideoJsonOptions);

            return new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
            };
        }

        var form = BuildVideoMultipartForm(request, metadata, normalizedN, warnings);
        return new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = form
        };
    }

    private static Dictionary<string, object?> BuildVideoJsonPayload(VideoRequest request, JsonElement metadata, int? normalizedN)
    {
        var payload = new Dictionary<string, object?>();
        MergeVideoProviderOptions(payload, metadata);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (request.Duration is not null)
            payload["seconds"] = request.Duration.Value.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (normalizedN.HasValue)
            payload["n"] = normalizedN.Value;

        return payload;
    }

    private static MultipartFormDataContent BuildVideoMultipartForm(
        VideoRequest request,
        JsonElement metadata,
        int? normalizedN,
        List<object> warnings)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(request.Model), "model" },
            { new StringContent(request.Prompt), "prompt" }
        };

        if (request.Duration is not null)
            form.Add(new StringContent(request.Duration.Value.ToString(CultureInfo.InvariantCulture)), "seconds");

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            form.Add(new StringContent(request.Resolution), "resolution");

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            form.Add(new StringContent(request.AspectRatio), "aspect_ratio");

        if (normalizedN.HasValue)
            form.Add(new StringContent(normalizedN.Value.ToString(CultureInfo.InvariantCulture)), "n");

        AddMultipartProviderOptions(form, metadata, warnings);
        if (request.Image != null)
            AddRequestImageAsInputReference(form, request.Image);

        return form;
    }

    private static void MergeVideoProviderOptions(Dictionary<string, object?> payload, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in metadata.EnumerateObject())
        {
            if (string.Equals(prop.Name, "dryRun", StringComparison.OrdinalIgnoreCase))
                continue;

            payload[prop.Name] = prop.Value.Clone();
        }
    }

    private static void AddMultipartProviderOptions(MultipartFormDataContent form, JsonElement metadata, List<object> warnings)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in metadata.EnumerateObject())
        {
            if (string.Equals(prop.Name, "dryRun", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(prop.Name, "input_reference", StringComparison.OrdinalIgnoreCase))
            {
                AddInputReferenceFields(form, prop.Value, warnings);
                continue;
            }

            if (string.Equals(prop.Name, "model", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, "prompt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, "seconds", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, "duration", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, "resolution", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, "aspect_ratio", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, "n", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddJsonElementField(form, prop.Name, prop.Value);
        }
    }

    private static void AddInputReferenceFields(MultipartFormDataContent form, JsonElement inputReference, List<object> warnings)
    {
        if (inputReference.ValueKind == JsonValueKind.String)
        {
            var value = inputReference.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                form.Add(new StringContent(value), "input_reference");

            return;
        }

        if (inputReference.ValueKind == JsonValueKind.Object)
        {
            if (inputReference.TryGetProperty("image_url", out var imageUrlEl) && imageUrlEl.ValueKind == JsonValueKind.String)
            {
                var value = imageUrlEl.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    form.Add(new StringContent(value), "input_reference");

                return;
            }

            warnings.Add(new
            {
                type = "unsupported",
                feature = "providerOptions.input_reference",
                details = "Object value was provided without image_url and was ignored for multipart form payload."
            });
            return;
        }

        if (inputReference.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in inputReference.EnumerateArray())
                AddInputReferenceFields(form, item, warnings);

            return;
        }

        warnings.Add(new
        {
            type = "unsupported",
            feature = "providerOptions.input_reference",
            details = "Field type is not supported in multipart payload and was ignored."
        });
    }

    private static void AddJsonElementField(MultipartFormDataContent form, string field, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var str = value.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                        form.Add(new StringContent(str), field);
                    break;
                }
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                form.Add(new StringContent(value.GetRawText()), field);
                break;
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                form.Add(new StringContent(value.GetRawText(), Encoding.UTF8, MediaTypeNames.Application.Json), field);
                break;
        }
    }

    private static void AddRequestImageAsInputReference(MultipartFormDataContent form, VideoFile image)
    {
        if (image is null)
            return;

        if (string.IsNullOrWhiteSpace(image.Data))
            throw new ArgumentException("Image data is required.", nameof(image));

        if (image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(image.Data), "input_reference");
            return;
        }

        var imageContent = CreateVideoImageContent(image);
        form.Add(imageContent, "input_reference", $"input_reference{GetImageExtension(image.MediaType)}");
    }

    private static ByteArrayContent CreateVideoImageContent(VideoFile image)
    {
        var bytes = Convert.FromBase64String(image.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(image.MediaType)
                ? MediaTypeNames.Application.Octet
                : image.MediaType);

        return content;
    }

    private IDisposable RegisterBestEffortCancel(string videoId, CancellationToken cancellationToken)
    {
        return cancellationToken.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await CancelVideoBestEffortAsync(videoId, CancellationToken.None);
                }
                catch
                {
                    // Best effort cancellation; intentionally ignored.
                }
            });
        });
    }

    private async Task CancelVideoBestEffortAsync(string videoId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return;

        using var cancelReq = new HttpRequestMessage(HttpMethod.Delete, $"v1/videos/{Uri.EscapeDataString(videoId)}");
        using var _ = await _client.SendAsync(cancelReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<LumenfallVideoFetchResult> FetchVideoTaskAsync(string videoId, CancellationToken cancellationToken)
    {
        using var fetchReq = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{Uri.EscapeDataString(videoId)}");
        using var fetchResp = await _client.SendAsync(fetchReq, cancellationToken);
        var fetchRaw = await fetchResp.Content.ReadAsStringAsync(cancellationToken);
        if (!fetchResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lumenfall video poll failed ({(int)fetchResp.StatusCode}): {fetchRaw}");

        using var fetchDoc = JsonDocument.Parse(fetchRaw);
        var root = fetchDoc.RootElement.Clone();
        var status = ReadString(root, "status") ?? "queued";
        return new LumenfallVideoFetchResult(status, fetchRaw, root);
    }

    private async Task<List<VideoResponseFile>> DownloadVideoOutputsAsync(IReadOnlyList<string> outputUrls, CancellationToken cancellationToken)
    {
        List<VideoResponseFile> videos = [];

        foreach (var outputUrl in outputUrls)
        {
            using var outputResp = await _client.GetAsync(outputUrl, cancellationToken);
            var bytes = await outputResp.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!outputResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Lumenfall video download failed ({(int)outputResp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

            if (bytes.Length == 0)
                continue;

            var mediaType = outputResp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                mediaType = GuessVideoMediaType(outputUrl) ?? "video/mp4";

            videos.Add(new VideoResponseFile
            {
                MediaType = mediaType,
                Data = Convert.ToBase64String(bytes)
            });
        }

        return videos;
    }

    private static bool IsTerminalVideoStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFailedVideoStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return true;

        return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryGetVideoError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errorEl))
        {
            if (errorEl.ValueKind == JsonValueKind.String)
                return errorEl.GetString() ?? "Unknown error";

            if (errorEl.ValueKind == JsonValueKind.Object)
            {
                var code = ReadString(errorEl, "code");
                var message = ReadString(errorEl, "message") ?? errorEl.GetRawText();
                return string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}";
            }
        }

        var messageText = ReadString(root, "message");
        return string.IsNullOrWhiteSpace(messageText) ? "Unknown error" : messageText;
    }

    private static List<string> TryGetVideoOutputUrls(JsonElement root)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddOutputUrl(ReadString(root, "url"));

        if (root.TryGetProperty("output", out var outputEl))
        {
            if (outputEl.ValueKind == JsonValueKind.Object)
            {
                AddOutputUrl(ReadString(outputEl, "url"));
            }
            else if (outputEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        AddOutputUrl(item.GetString());
                        continue;
                    }

                    if (item.ValueKind == JsonValueKind.Object)
                        AddOutputUrl(ReadString(item, "url"));
                }
            }
        }

        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    AddOutputUrl(ReadString(item, "url"));
            }
        }

        return urls;

        void AddOutputUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (seen.Add(value))
                urls.Add(value);
        }
    }

    private static DateTime ResolveVideoTimestamp(JsonElement root, DateTime fallbackUtc)
    {
        if (root.TryGetProperty("created_at", out var createdAtEl) && createdAtEl.ValueKind == JsonValueKind.Number && createdAtEl.TryGetInt64(out var unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }
            catch
            {
                return fallbackUtc;
            }
        }

        if (root.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number && createdEl.TryGetInt64(out unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }
            catch
            {
                return fallbackUtc;
            }
        }

        return fallbackUtc;
    }

    private static int? NormalizeVideoN(int? value, List<object> warnings)
    {
        if (!value.HasValue)
            return null;

        var clamped = Math.Clamp(value.Value, 1, 4);
        if (clamped != value.Value)
        {
            warnings.Add(new
            {
                type = "clamped",
                feature = "n",
                details = "Lumenfall videos n is documented as 1..4. Value was clamped."
            });
        }

        return clamped;
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.Contains(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.Contains(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (url.Contains(".mkv", StringComparison.OrdinalIgnoreCase))
            return "video/x-matroska";
        if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty(propertyName, out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
