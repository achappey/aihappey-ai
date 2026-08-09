using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Lyceum;

public partial class LyceumProvider
{
    private const string LyceumVideoOperationPrefix = "lyv1_";

    private static readonly JsonSerializerOptions LyceumVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var references = request.InputReferences?.ToList() ?? [];
        if (references.Count != 1)
            throw new ArgumentException("Lyceum video edits require exactly one base64 video in inputReferences.", nameof(request));

        var inputVideo = references[0];
        ValidateLyceumVideoFile(inputVideo, "inputReferences[0]", "video/");
        if (request.Image is null)
            throw new ArgumentException("Lyceum video edits require one base64 reference image in image.", nameof(request));
        ValidateLyceumVideoFile(request.Image, "image", "image/");

        ApplyAuthHeader();
        var warnings = BuildLyceumVideoWarnings(request);
        var uploadedKeys = new List<string>(2);

        try
        {
            var videoKey = await UploadLyceumTemporaryFileAsync(inputVideo, "video", cancellationToken);
            uploadedKeys.Add(videoKey);
            var imageKey = await UploadLyceumTemporaryFileAsync(request.Image, "image", cancellationToken);
            uploadedKeys.Add(imageKey);

            var credentials = await GetLyceumStorageCredentialsAsync(cancellationToken);
            var videoUrl = CreateLyceumPresignedGetUrl(credentials, videoKey, TimeSpan.FromMinutes(10));
            var imageUrl = CreateLyceumPresignedGetUrl(credentials, imageKey, TimeSpan.FromMinutes(10));
            var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["video_url"] = videoUrl,
                ["reference_image_url"] = imageUrl,
                ["resolution"] = string.IsNullOrWhiteSpace(request.Resolution) ? "720p" : request.Resolution
            };
            MergeLyceumProviderOptions(payload, metadata);

            using var createRequest = new HttpRequestMessage(HttpMethod.Post, "videos/generations")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, LyceumVideoJsonOptions),
                    Encoding.UTF8,
                    MediaTypeNames.Application.Json)
            };
            using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
            var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!createResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"Lyceum video generation failed ({(int)createResponse.StatusCode}): {createRaw}");

            using var createDocument = JsonDocument.Parse(createRaw);
            var createRoot = createDocument.RootElement.Clone();
            var outputUrl = LyceumReadStringProperty(createRoot, "video_url");
            if (string.IsNullOrWhiteSpace(outputUrl))
                throw new InvalidOperationException("Lyceum video generation response missing 'video_url'.");

            var operation = new LyceumVideoOperationData(
                outputUrl,
                videoKey,
                imageKey,
                request.Model,
                GuessLyceumVideoMediaType(outputUrl));

            return new VideoOperationStartResult
            {
                Operation = EncodeLyceumVideoOperation(operation),
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
                {
                    status = "ready",
                    inputVideoKey = videoKey,
                    referenceImageKey = imageKey
                }),
                Response = new()
                {
                    Timestamp = DateTime.UtcNow,
                    Headers = createResponse.GetHeaders(),
                    ModelId = request.Model.ToModelId(GetIdentifier())
                }
            };
        }
        catch
        {
            foreach (var key in uploadedKeys)
                await TryDeleteLyceumTemporaryFileAsync(key, CancellationToken.None);
            throw;
        }
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeLyceumVideoOperation(operation);
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        byte[] outputBytes;
        string mediaType;
        try
        {
            using var outputResponse = await _downloadClient.GetAsync(operationData.OutputUrl, cancellationToken);
            outputBytes = await outputResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!outputResponse.IsSuccessStatusCode)
            {
                var detail = TryDecodeLyceumUtf8(outputBytes) ?? outputResponse.ReasonPhrase;
                return new VideoOperationErrorResult
                {
                    Error = $"Lyceum generated video download failed ({(int)outputResponse.StatusCode}): {detail}",
                    ProviderMetadata = CreateLyceumVideoStatusMetadata(operationData, "download_failed", []),
                    Response = responseData
                };
            }

            if (outputBytes.Length == 0)
            {
                return new VideoOperationErrorResult
                {
                    Error = "Lyceum generated video download returned an empty response.",
                    ProviderMetadata = CreateLyceumVideoStatusMetadata(operationData, "download_failed", []),
                    Response = responseData
                };
            }

            mediaType = outputResponse.Content.Headers.ContentType?.MediaType
                ?? operationData.MediaType
                ?? GuessLyceumVideoMediaType(operationData.OutputUrl)
                ?? "video/mp4";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new VideoOperationErrorResult
            {
                Error = $"Lyceum generated video download failed: {ex.Message}",
                ProviderMetadata = CreateLyceumVideoStatusMetadata(operationData, "download_failed", []),
                Response = responseData
            };
        }

        var cleanupWarnings = new List<object>();
        await DeleteWithWarningAsync(operationData.InputVideoKey, "input video", cleanupWarnings);
        await DeleteWithWarningAsync(operationData.ReferenceImageKey, "reference image", cleanupWarnings);

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(outputBytes)
                }
            ],
            Warnings = cleanupWarnings,
            ProviderMetadata = CreateLyceumVideoStatusMetadata(operationData, "completed", cleanupWarnings),
            Response = responseData
        };
    }

    private async Task DeleteWithWarningAsync(string key, string inputName, List<object> warnings)
    {
        var error = await TryDeleteLyceumTemporaryFileAsync(key, CancellationToken.None);
        if (error is not null)
            warnings.Add(new { type = "cleanup_failed", feature = inputName, key, error });
    }

    private static List<object> BuildLyceumVideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Prompt)) warnings.Add(new { type = "unsupported", feature = "prompt" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Duration is not null) warnings.Add(new { type = "unsupported", feature = "duration" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.FrameImages?.Any() == true) warnings.Add(new { type = "unsupported", feature = "frameImages" });
        if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generateAudio" });
        return warnings;
    }

    private static void ValidateLyceumVideoFile(VideoFile file, string fieldName, string requiredMediaPrefix)
    {
        if (!string.Equals(file.Type, "base64", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(file.Type, "file", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Lyceum {fieldName} must contain base64 data.");
        if (string.IsNullOrWhiteSpace(file.MediaType)
            || !file.MediaType.StartsWith(requiredMediaPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Lyceum {fieldName} must use a {requiredMediaPrefix}* media type.");
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException($"Lyceum {fieldName} base64 data is required.");

        try
        {
            _ = Convert.FromBase64String(StripLyceumDataUrl(file.Data));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"Lyceum {fieldName} contains invalid base64 data.", ex);
        }
    }

    private async Task<string> UploadLyceumTemporaryFileAsync(
        VideoFile file,
        string kind,
        CancellationToken cancellationToken)
    {
        var bytes = Convert.FromBase64String(StripLyceumDataUrl(file.Data));
        var extension = GetLyceumMediaExtension(file.MediaType);
        var requestedKey = $"aihappey/{kind}/{Guid.NewGuid():N}{extension}";
        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.MediaType);
        content.Add(fileContent, "file", Path.GetFileName(requestedKey));

        using var uploadRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"storage/upload?key={Uri.EscapeDataString(requestedKey)}")
        {
            Content = content
        };
        using var uploadResponse = await _client.SendAsync(uploadRequest, cancellationToken);
        var raw = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!uploadResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lyceum temporary {kind} upload failed ({(int)uploadResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return LyceumReadStringProperty(document.RootElement, "key")
            ?? throw new InvalidOperationException($"Lyceum temporary {kind} upload response missing 'key'.");
    }

    private async Task<LyceumStorageCredentials> GetLyceumStorageCredentialsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "storage/credentials");
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lyceum storage credentials request failed ({(int)response.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<LyceumStorageCredentials>(raw, LyceumVideoJsonOptions)
            ?? throw new InvalidOperationException("Lyceum storage credentials response was invalid.");
    }

    private async Task<string?> TryDeleteLyceumTemporaryFileAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            ApplyAuthHeader();
            using var request = new HttpRequestMessage(
                HttpMethod.Delete,
                $"storage/delete/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}");
            using var response = await _client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return null;
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            return $"HTTP {(int)response.StatusCode}: {raw}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string CreateLyceumPresignedGetUrl(
        LyceumStorageCredentials credentials,
        string key,
        TimeSpan requestedLifetime)
    {
        if (!Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("Lyceum storage credentials returned an invalid endpoint.");

        var now = DateTimeOffset.UtcNow;
        var remaining = credentials.ExpiresAt - now - TimeSpan.FromSeconds(30);
        var lifetime = remaining < requestedLifetime ? remaining : requestedLifetime;
        var expires = Math.Clamp((int)lifetime.TotalSeconds, 1, 604800);
        var region = string.IsNullOrWhiteSpace(credentials.Region) ? "us-east-1" : credentials.Region;
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var scope = $"{date}/{region}/s3/aws4_request";
        var objectPath = string.Join('/', new[] { credentials.BucketName }.Concat(key.Split('/')).Select(LyceumAwsEncode));
        var basePath = endpoint.AbsolutePath.TrimEnd('/');
        var canonicalUri = $"{basePath}/{objectPath}";
        if (!canonicalUri.StartsWith('/')) canonicalUri = "/" + canonicalUri;

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{credentials.AccessKey}/{scope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expires.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "host"
        };
        if (!string.IsNullOrWhiteSpace(credentials.SessionToken))
            query["X-Amz-Security-Token"] = credentials.SessionToken;

        var canonicalQuery = string.Join('&', query.Select(pair => $"{LyceumAwsEncode(pair.Key)}={LyceumAwsEncode(pair.Value)}"));
        var canonicalHeaders = $"host:{(endpoint.IsDefaultPort ? endpoint.Host : endpoint.Authority)}\n";
        var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\nhost\nUNSIGNED-PAYLOAD";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{LyceumSha256Hex(canonicalRequest)}";
        var signingKey = LyceumHmac(
            LyceumHmac(
                LyceumHmac(
                    LyceumHmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretKey), date),
                    region),
                "s3"),
            "aws4_request");
        var signature = Convert.ToHexString(LyceumHmac(signingKey, stringToSign)).ToLowerInvariant();
        var authority = endpoint.IsDefaultPort ? endpoint.Host : endpoint.Authority;
        return $"{endpoint.Scheme}://{authority}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}";
    }

    private static byte[] LyceumHmac(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static string LyceumSha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string LyceumAwsEncode(string value)
        => Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.OrdinalIgnoreCase);

    private static string StripLyceumDataUrl(string data)
    {
        var comma = data.IndexOf(',');
        return data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
            ? data[(comma + 1)..]
            : data;
    }

    private static string GetLyceumMediaExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".bin"
    };

    private static string? GuessLyceumVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        if (path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)) return "video/quicktime";
        return null;
    }

    private Dictionary<string, JsonElement> CreateLyceumVideoStatusMetadata(
        LyceumVideoOperationData operation,
        string status,
        List<object> cleanupWarnings)
        => GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            status,
            inputVideoKey = operation.InputVideoKey,
            referenceImageKey = operation.ReferenceImageKey,
            cleanupWarnings
        });

    private static string EncodeLyceumVideoOperation(LyceumVideoOperationData operation)
    {
        var json = JsonSerializer.Serialize(operation, LyceumVideoJsonOptions);
        return LyceumVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static LyceumVideoOperationData DecodeLyceumVideoOperation(string operation)
    {
        if (!operation.StartsWith(LyceumVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Invalid Lyceum video operation token.", nameof(operation));

        try
        {
            var encoded = operation[LyceumVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            return JsonSerializer.Deserialize<LyceumVideoOperationData>(
                       Encoding.UTF8.GetString(Convert.FromBase64String(encoded)),
                       LyceumVideoJsonOptions)
                   ?? throw new JsonException("Operation data was empty.");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("Invalid Lyceum video operation token.", nameof(operation), ex);
        }
    }

    private sealed record LyceumVideoOperationData(
        string OutputUrl,
        string InputVideoKey,
        string ReferenceImageKey,
        string Model,
        string? MediaType);

    private sealed class LyceumStorageCredentials
    {
        public string AccessKey { get; set; } = null!;
        public string SecretKey { get; set; } = null!;
        public string? SessionToken { get; set; }
        public string Endpoint { get; set; } = null!;
        public string BucketName { get; set; } = null!;
        public string Region { get; set; } = "us-east-1";
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
