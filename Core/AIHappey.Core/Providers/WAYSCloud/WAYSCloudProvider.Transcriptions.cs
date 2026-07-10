using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WAYSCloud;

public partial class WAYSCloudProvider
{
  public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
  {
    ApplyAuthHeader();

    ArgumentNullException.ThrowIfNull(request);

    if (request.Audio is null)
      throw new ArgumentException("Audio is required.", nameof(request));

    if (string.IsNullOrWhiteSpace(request.MediaType))
      throw new ArgumentException("MediaType is required.", nameof(request));

    var now = DateTime.UtcNow;
    var warnings = new List<object>();
    var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
    var audioString = ExtractWaysCloudAudioString(request.Audio);
    if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
      audioString = parsedBase64;

    var bytes = Convert.FromBase64String(audioString);
    var filename = TryGetWaysCloudString(metadata, "filename")
                   ?? "audio" + GetWaysCloudAudioExtension(request.MediaType);
    var language = TryGetWaysCloudString(metadata, "language") ?? "auto";

    var createPayload = new Dictionary<string, object?>
    {
      ["filename"] = filename,
      ["content_type"] = request.MediaType,
      ["file_size_bytes"] = bytes.LongLength,
      ["language"] = language
    };

    var createRequestBody = JsonSerializer.Serialize(createPayload, JsonSerializerOptions.Web);
    var createdJob = await CreateWaysCloudTranscriptJobAsync(createRequestBody, cancellationToken);
    var jobId = createdJob.JobId;
    JsonElement? finalJob = null;

    try
    {
      await UploadWaysCloudTranscriptBytesAsync(createdJob.UploadUrl, bytes, request.MediaType, cancellationToken);
      await ConfirmWaysCloudTranscriptUploadAsync(jobId, cancellationToken);
      finalJob = await PollWaysCloudTranscriptUntilReadyAsync(jobId, cancellationToken);

      return ConvertWaysCloudTranscriptionResponse(
          finalJob.Value,
          request.Model,
          now,
          warnings,
          createRequestBody);
    }
    finally
    {
      try
      {
        await DeleteWaysCloudTranscriptJobAsync(jobId, cancellationToken);
      }
      catch (Exception ex)
      {
        warnings.Add(new
        {
          type = "cleanup_failed",
          provider = GetIdentifier(),
          job_id = jobId,
          error = ex.Message
        });
      }
    }
  }

  private async Task<(string JobId, string UploadUrl)> CreateWaysCloudTranscriptJobAsync(
      string requestBody,
      CancellationToken cancellationToken)
  {
    using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
    using var resp = await _client.PostAsync("v1/transcript/jobs", content, cancellationToken);
    var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

    if (!resp.IsSuccessStatusCode)
      throw new InvalidOperationException($"WAYSCloud create transcription job failed ({(int)resp.StatusCode}): {raw}");

    using var doc = JsonDocument.Parse(raw);
    var root = doc.RootElement;
    var jobId = TryGetWaysCloudString(root, "job_id");
    var uploadUrl = TryGetWaysCloudString(root, "upload_url");

    if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(uploadUrl))
      throw new InvalidOperationException($"WAYSCloud create transcription response missing job_id or upload_url. Body: {raw}");

    return (jobId, uploadUrl);
  }

  private async Task UploadWaysCloudTranscriptBytesAsync(
      string uploadUrl,
      byte[] bytes,
      string mediaType,
      CancellationToken cancellationToken)
  {
    using var content = new ByteArrayContent(bytes);
    content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

    using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
    {
      Content = content
    };
    req.Headers.Remove("X-API-Key");

    using var resp = await _client.SendAsync(req, cancellationToken);
    var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

    if (!resp.IsSuccessStatusCode)
      throw new InvalidOperationException($"WAYSCloud upload transcription file failed ({(int)resp.StatusCode}): {raw}");
  }

  private async Task ConfirmWaysCloudTranscriptUploadAsync(string jobId, CancellationToken cancellationToken)
  {
    using var resp = await _client.PostAsync(
        $"v1/transcript/jobs/{Uri.EscapeDataString(jobId)}/upload-complete",
        content: null,
        cancellationToken);
    var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

    if (!resp.IsSuccessStatusCode)
      throw new InvalidOperationException($"WAYSCloud confirm transcription upload failed ({(int)resp.StatusCode}): {raw}");
  }

  private async Task<JsonElement> PollWaysCloudTranscriptUntilReadyAsync(string jobId, CancellationToken cancellationToken)
  {
    var delay = TimeSpan.FromSeconds(1);
    var maxDelay = TimeSpan.FromSeconds(5);
    var maxWait = TimeSpan.FromMinutes(15);
    var start = DateTime.UtcNow;

    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();

      using var resp = await _client.GetAsync($"v1/transcript/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);
      var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

      if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"WAYSCloud get transcription job failed ({(int)resp.StatusCode}): {raw}");

      using var doc = JsonDocument.Parse(raw);
      var root = doc.RootElement;
      var status = TryGetWaysCloudString(root, "status") ?? string.Empty;

      if (string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase))
        return root.Clone();

      if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
          || string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
      {
        var error = TryGetWaysCloudString(root, "error_message") ?? $"status '{status}'";
        throw new InvalidOperationException($"WAYSCloud transcription job failed: {error}. Body: {raw}");
      }

      if (DateTime.UtcNow - start > maxWait)
        throw new TimeoutException($"WAYSCloud transcription job '{jobId}' did not complete within {maxWait.TotalMinutes} minutes. Last status='{status}'.");

      await Task.Delay(delay, cancellationToken);
      delay = delay < maxDelay ? TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 1.5)) : maxDelay;
    }
  }

  private async Task DeleteWaysCloudTranscriptJobAsync(string jobId, CancellationToken cancellationToken)
  {
    using var req = new HttpRequestMessage(HttpMethod.Delete, $"v1/transcript/jobs/{Uri.EscapeDataString(jobId)}");
    using var resp = await _client.SendAsync(req, cancellationToken);
    var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

    if (!resp.IsSuccessStatusCode)
      throw new InvalidOperationException($"WAYSCloud delete transcription job failed ({(int)resp.StatusCode}): {raw}");
  }

  private TranscriptionResponse ConvertWaysCloudTranscriptionResponse(
      JsonElement root,
      string model,
      DateTime timestamp,
      IEnumerable<object> warnings,
      string requestBody)
  {
    var text = ExtractWaysCloudTranscriptText(root);
    var segments = ExtractWaysCloudTranscriptSegments(root).ToList();

    return new TranscriptionResponse
    {
      Text = text,
      Language = TryGetWaysCloudString(root, "language"),
      DurationInSeconds = TryGetWaysCloudFloat(root, "audio_duration_sec"),
      Segments = segments,
      Warnings = warnings,
      ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
      Response = new()
      {
        Timestamp = timestamp,
        ModelId = model.ToModelId(GetIdentifier()),
        Body = root.Clone()
      },
      Request = new()
      {
        Body = requestBody
      }
    };
  }

  private static string ExtractWaysCloudTranscriptText(JsonElement root)
  {
    if (root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
      return textEl.GetString() ?? string.Empty;

    var segments = ExtractWaysCloudTranscriptSegments(root).ToList();
    if (segments.Count > 0)
      return string.Join(" ", segments.Select(static segment => segment.Text).Where(static text => !string.IsNullOrWhiteSpace(text)));

    return string.Empty;
  }

  private static IEnumerable<TranscriptionSegment> ExtractWaysCloudTranscriptSegments(JsonElement root)
  {
    if (!root.TryGetProperty("segments", out var segmentsEl) || segmentsEl.ValueKind != JsonValueKind.Array)
      yield break;

    foreach (var segmentEl in segmentsEl.EnumerateArray())
    {
      if (segmentEl.ValueKind != JsonValueKind.Object)
        continue;

      var text = TryGetWaysCloudString(segmentEl, "text")
                 ?? TryGetWaysCloudString(segmentEl, "content")
                 ?? string.Empty;

      if (string.IsNullOrWhiteSpace(text))
        continue;

      yield return new TranscriptionSegment
      {
        Text = text,
        StartSecond = TryGetWaysCloudFloat(segmentEl, "start")
                        ?? TryGetWaysCloudFloat(segmentEl, "start_sec")
                        ?? TryGetWaysCloudFloat(segmentEl, "start_second")
                        ?? 0,
        EndSecond = TryGetWaysCloudFloat(segmentEl, "end")
                      ?? TryGetWaysCloudFloat(segmentEl, "end_sec")
                      ?? TryGetWaysCloudFloat(segmentEl, "end_second")
                      ?? 0
      };
    }
  }

  private static string ExtractWaysCloudAudioString(object audio)
      => audio switch
      {
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
        string value => value,
        _ => audio.ToString() ?? string.Empty
      };

  private static string GetWaysCloudAudioExtension(string mediaType)
      => mediaType.ToLowerInvariant() switch
      {
        "audio/mpeg" => ".mp3",
        "audio/mp3" => ".mp3",
        "audio/wav" => ".wav",
        "audio/x-wav" => ".wav",
        "audio/m4a" => ".m4a",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        "audio/webm" => ".webm",
        "video/mp4" => ".mp4",
        _ => ".bin"
      };

  private static float? TryGetWaysCloudFloat(JsonElement element, string propertyName)
      => element.ValueKind == JsonValueKind.Object
         && element.TryGetProperty(propertyName, out var value)
         && value.ValueKind == JsonValueKind.Number
          ? (float)value.GetDouble()
          : null;

}
