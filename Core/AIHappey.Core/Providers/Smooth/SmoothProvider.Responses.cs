using System.IO.Compression;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.ChatCompletions.Models;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Smooth;

public partial class SmoothProvider
{
    private static readonly JsonSerializerOptions SmoothJson = JsonSerializerOptions.Web;

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()),
           cancellationToken);

        return result.ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            yield return part.ToResponseStreamPart();
        }

        yield break;
    }
    
    private sealed class SmoothApiResponse<T>
    {
        [JsonPropertyName("r")]
        public T R { get; init; } = default!;
    }

    private sealed class SmoothSubmitTaskRequest
    {
        [JsonPropertyName("task")]
        public string? Task { get; init; }

        [JsonPropertyName("response_model")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? ResponseModel { get; init; }

        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Url { get; init; }

        [JsonPropertyName("metadata")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object?>? Metadata { get; init; }

        [JsonPropertyName("files")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Files { get; init; }

        [JsonPropertyName("agent")]
        public string Agent { get; init; } = "smooth";

        [JsonPropertyName("max_steps")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxSteps { get; init; }

        [JsonPropertyName("device")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Device { get; init; }

        [JsonPropertyName("allowed_urls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? AllowedUrls { get; init; }

        [JsonPropertyName("enable_recording")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? EnableRecording { get; init; }

        [JsonPropertyName("profile_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProfileId { get; init; }

        [JsonPropertyName("profile_read_only")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ProfileReadOnly { get; init; }

        [JsonPropertyName("stealth_mode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? StealthMode { get; init; }

        [JsonPropertyName("proxy_server")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProxyServer { get; init; }

        [JsonPropertyName("proxy_username")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProxyUsername { get; init; }

        [JsonPropertyName("proxy_password")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProxyPassword { get; init; }

        [JsonPropertyName("certificates")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Certificates { get; init; }

        [JsonPropertyName("use_adblock")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? UseAdblock { get; init; }

        [JsonPropertyName("additional_tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? AdditionalTools { get; init; }

        [JsonPropertyName("custom_tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SmoothToolSignature>? CustomTools { get; init; }

        [JsonPropertyName("experimental_features")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? ExperimentalFeatures { get; init; }

        [JsonPropertyName("extensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Extensions { get; init; }

        [JsonPropertyName("show_cursor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ShowCursor { get; init; }
    }

    private sealed class SmoothToolSignature
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = default!;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("inputs")]
        public JsonElement Inputs { get; init; }

        [JsonPropertyName("output")]
        public string Output { get; init; } = "object";
    }

    private sealed class SmoothTaskResponse
    {
        [JsonIgnore]
        public string? RawJson { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; init; } = default!;

        [JsonPropertyName("status")]
        public string Status { get; init; } = default!;

        [JsonPropertyName("output")]
        public JsonElement Output { get; init; }

        [JsonPropertyName("credits_used")]
        public int? CreditsUsed { get; init; }

        [JsonPropertyName("device")]
        public string? Device { get; init; }

        [JsonPropertyName("live_url")]
        public string? LiveUrl { get; init; }

        [JsonPropertyName("recording_url")]
        public string? RecordingUrl { get; init; }

        [JsonPropertyName("downloads_url")]
        public string? DownloadsUrl { get; init; }

        [JsonPropertyName("created_at")]
        public long? CreatedAt { get; init; }

        [JsonPropertyName("events")]
        public List<SmoothTaskEvent>? Events { get; init; }
    }

    private sealed class SmoothTaskEvent
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = default!;

        [JsonPropertyName("payload")]
        public JsonElement Payload { get; init; }

        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; init; }

        [JsonPropertyName("timestamp")]
        public long? Timestamp { get; init; }
    }

    private sealed record SmoothDownloadedImage(string FileName, string MediaType, string DataUrl);

    private sealed class SmoothUploadFileResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = default!;
    }

}

