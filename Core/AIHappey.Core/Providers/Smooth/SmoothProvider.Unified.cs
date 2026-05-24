using System.IO.Compression;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Smooth;

public partial class SmoothProvider
{
  private const int DefaultSmoothPollMaxAttempts = 600;
  private static readonly TimeSpan DefaultSmoothPollInterval = TimeSpan.FromMilliseconds(900);

  public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
      => ExecuteSmoothUnifiedAsync(request, cancellationToken);

  public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
      AIRequest request,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    ApplyAuthHeader();

    var providerId = GetIdentifier();
    var action = ResolveSmoothAction(request);
    if (action is "upload_file" or "upload_files" or "create_profile" or "profile" or "cancel" or "cancel_task")
    {
      var response = await ExecuteSmoothUnifiedAsync(request, cancellationToken);
      var eventId = TryGetMetadataString(response.Metadata, "smooth_task_id")
                    ?? TryGetMetadataString(response.Metadata, "smooth_profile_id")
                    ?? TryGetMetadataString(response.Metadata, "smooth_uploaded_file_ids")
                    ?? Guid.NewGuid().ToString("n");
      var immediateTimestamp = DateTimeOffset.UtcNow;

      yield return CreateSmoothStreamEvent(
          providerId,
          "text-start",
          eventId,
          new AITextStartEventData(),
          immediateTimestamp,
          response.Metadata);

      var text = ExtractSmoothOutputText(response.Output);
      if (!string.IsNullOrEmpty(text))
      {
        yield return CreateSmoothStreamEvent(
            providerId,
            "text-delta",
            eventId,
            new AITextDeltaEventData { Delta = text },
            immediateTimestamp,
            response.Metadata);
      }

      yield return CreateSmoothStreamEvent(
          providerId,
          "text-end",
          eventId,
          new AITextEndEventData(),
          immediateTimestamp,
          response.Metadata);

      yield return CreateSmoothStreamEvent(
          providerId,
          "finish",
          eventId,
          new AIFinishEventData
          {
            FinishReason = response.Status == "completed" ? "stop" : "error",
            Model = response.Model?.ToModelId(GetIdentifier()),
            CompletedAt = immediateTimestamp.ToUnixTimeSeconds(),
            Response = response.Metadata?.GetValueOrDefault("smooth_raw_response"),
            MessageMetadata = AIFinishMessageMetadata.Create(
                  response.Model ?? ResolveSmoothModel(request.Model),
                  immediateTimestamp,
                  usage: response.Usage,
                  temperature: request.Temperature)
          },
          immediateTimestamp,
          response.Metadata,
          response.Output);

      yield break;
    }

    var execution = await StartSmoothExecutionAsync(request, cancellationToken);
    var created = execution.Task;
    var metadata = BuildSmoothMetadata(request, created, execution.UploadedFileIds, downloadedImagesCount: 0);
    var eventT = 0L;
    var streamId = created.Id;
    var timestamp = ToDateTimeOffset(created.CreatedAt) ?? DateTimeOffset.UtcNow;
    var providerEventLog = new StringBuilder();
    var emittedText = string.Empty;
    var textStarted = false;
    SmoothTaskResponse current = created;

    yield return CreateSmoothStreamEvent(
        providerId,
        "text-start",
        streamId,
        new AITextStartEventData { ProviderMetadata = CreateFlatProviderMetadata(metadata) },
        timestamp,
        metadata);
    textStarted = true;

    while (!cancellationToken.IsCancellationRequested)
    {
      current = await GetTaskAsync(created.Id, eventT, downloads: true, cancellationToken);
      metadata = BuildSmoothMetadata(request, current, execution.UploadedFileIds, downloadedImagesCount: 0);

      var eventResult = await HandleTaskEventsAsync(created.Id, current.Events, cancellationToken);
      eventT = Math.Max(eventT, eventResult.NextEventTimestamp);

      foreach (var evt in eventResult.StreamEvents)
        yield return evt;

      foreach (var line in eventResult.LogLines)
      {
        providerEventLog.AppendLine(line);
        yield return CreateSmoothStreamEvent(
            providerId,
            "data-smooth.event",
            streamId,
            new AIDataEventData { Id = streamId, Data = line, Transient = true },
            DateTimeOffset.UtcNow,
            metadata);
      }

      var currentText = ResolveFinalOutputText(current, providerEventLog);
      if (!string.IsNullOrEmpty(currentText))
      {
        var delta = currentText.StartsWith(emittedText, StringComparison.Ordinal)
            ? currentText[emittedText.Length..]
            : currentText;

        if (!string.IsNullOrEmpty(delta))
        {
          emittedText = currentText;
          yield return CreateSmoothStreamEvent(
              providerId,
              "text-delta",
              streamId,
              new AITextDeltaEventData { Delta = delta, ProviderMetadata = CreateFlatProviderMetadata(metadata) },
              DateTimeOffset.UtcNow,
              metadata);
        }
      }

      if (IsTerminal(current.Status) || ShouldStopSessionPoll(request, action))
        break;

      await Task.Delay(ResolvePollInterval(request), cancellationToken);
    }

    var downloadedImages = await DownloadImagesAsDataUrlsAsync(current.DownloadsUrl, cancellationToken);
    metadata = BuildSmoothMetadata(request, current, execution.UploadedFileIds, downloadedImages.Count);

    foreach (var downloadedImage in downloadedImages)
    {
      yield return CreateSmoothStreamEvent(
          providerId,
          "file",
          streamId,
          new AIFileEventData
          {
            MediaType = downloadedImage.MediaType,
            Url = downloadedImage.DataUrl,
            Filename = downloadedImage.FileName,
            ProviderMetadata = CreateProviderMetadata(metadata)
          },
          DateTimeOffset.UtcNow,
          metadata);
    }

    if (textStarted)
    {
      yield return CreateSmoothStreamEvent(
          providerId,
          "text-end",
          streamId,
          new AITextEndEventData { ProviderMetadata = CreateFlatProviderMetadata(metadata) },
          DateTimeOffset.UtcNow,
          metadata);
    }

    var finalResponse = ToUnifiedResponse(current, request, providerEventLog, downloadedImages, execution.UploadedFileIds);
    var finishReason = IsFinished(current.Status) || IsSessionStatus(current.Status) ? "stop" : "error";

    yield return CreateSmoothStreamEvent(
        providerId,
        finishReason == "error" ? "error" : "finish",
        streamId,
        finishReason == "error"
            ? new AIErrorEventData { ErrorText = ResolveFinalOutputText(current, providerEventLog) }
            : new AIFinishEventData
            {
              FinishReason = finishReason,
              Model = finalResponse.Model?.ToModelId(GetIdentifier()),
              CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
              Response = finalResponse.Metadata?.GetValueOrDefault("smooth_raw_response"),
              MessageMetadata = AIFinishMessageMetadata.Create(
                    finalResponse.Model ?? ResolveSmoothModel(request.Model),
                    DateTimeOffset.UtcNow,
                    usage: finalResponse.Usage,
                    temperature: request.Temperature)
            },
        DateTimeOffset.UtcNow,
        finalResponse.Metadata,
        finalResponse.Output);
  }

  private async Task<AIResponse> ExecuteSmoothUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(request);

    ApplyAuthHeader();

    var action = ResolveSmoothAction(request);
    return action switch
    {
      "upload_file" or "upload_files" => await ExecuteSmoothUploadFilesAsync(request, cancellationToken),
      "create_profile" or "profile" => await ExecuteSmoothCreateProfileAsync(request, cancellationToken),
      "cancel" or "cancel_task" => await ExecuteSmoothCancelTaskAsync(request, cancellationToken),
      _ => await ExecuteSmoothTaskOrSessionAsync(request, action, cancellationToken)
    };
  }

  private async Task<AIResponse> ExecuteSmoothTaskOrSessionAsync(
      AIRequest request,
      string? action,
      CancellationToken cancellationToken)
  {
    var execution = await StartSmoothExecutionAsync(request, cancellationToken);
    var created = execution.Task;
    var eventT = 0L;
    var providerEventLog = new StringBuilder();
    SmoothTaskResponse current = created;

    if (ShouldStopSessionPoll(request, action))
    {
      var sessionImages = await DownloadImagesAsDataUrlsAsync(current.DownloadsUrl, cancellationToken);
      return ToUnifiedResponse(current, request, providerEventLog, sessionImages, execution.UploadedFileIds);
    }

    current = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
        async ct =>
        {
          var next = await GetTaskAsync(created.Id, eventT, downloads: true, ct);
          var eventResult = await HandleTaskEventsAsync(created.Id, next.Events, ct);
          eventT = Math.Max(eventT, eventResult.NextEventTimestamp);
          foreach (var line in eventResult.LogLines)
            providerEventLog.AppendLine(line);
          return next;
        },
        IsTerminal,
        ResolvePollInterval(request),
        ResolvePollTimeout(request),
        ResolvePollMaxAttempts(request),
        cancellationToken);

    var downloadedImages = await DownloadImagesAsDataUrlsAsync(current.DownloadsUrl, cancellationToken);
    return ToUnifiedResponse(current, request, providerEventLog, downloadedImages, execution.UploadedFileIds);
  }

  private async Task<SmoothExecutionStart> StartSmoothExecutionAsync(AIRequest request, CancellationToken cancellationToken)
  {
    var action = ResolveSmoothAction(request);
    var sessionId = ResolveStringOption(request, "session_id", "sessionId", "task_id", "taskId");

    if (!string.IsNullOrWhiteSpace(sessionId) && IsSmoothSessionAction(action))
    {
      await SendSmoothSessionEventAsync(sessionId, request, action, cancellationToken);
      var current = await GetTaskAsync(sessionId, 0, downloads: true, cancellationToken);
      return new SmoothExecutionStart(current, []);
    }

    var prompt = BuildPromptFromUnifiedRequest(request);
    var uploadedFileIds = await UploadFilesFromUnifiedRequestAsync(request, cancellationToken);
    var submit = BuildSubmitTaskRequest(request, prompt, uploadedFileIds);
    var created = await SubmitTaskAsync(submit, cancellationToken);

    if (IsSessionCreateRequest(request, action)
        && !string.IsNullOrWhiteSpace(prompt)
        && request.Metadata?.GetProviderOption<bool?>(GetIdentifier(), "run_task_after_create") != false)
    {
      await SendSmoothSessionActionAsync(created.Id, "run_task", new { task = prompt }, cancellationToken);
    }

    return new SmoothExecutionStart(created, uploadedFileIds);
  }

  private SmoothSubmitTaskRequest BuildSubmitTaskRequest(
      AIRequest request,
      string prompt,
      IReadOnlyList<string> uploadedFileIds)
  {
    var providerOptions = request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier());
    var files = new List<string>();
    files.AddRange(uploadedFileIds);
    files.AddRange(ResolveStringListOption(request, "files") ?? []);

    var task = IsSessionCreateRequest(request, ResolveSmoothAction(request))
        ? null
        : prompt;

    if (task is not null && string.IsNullOrWhiteSpace(task))
      throw new InvalidOperationException("Smooth requires non-empty unified input text unless creating a session or invoking a provider action.");

    return new SmoothSubmitTaskRequest
    {
      Task = task,
      Url = ResolveStringOption(request, "url", "start_url", "startUrl"),
      Metadata = ResolveDictionaryOption(request, "metadata"),
      Files = files.Count > 0 ? files : null,
      Agent = ResolveSmoothAgent(request.Model, request),
      MaxSteps = ResolveIntOption(request, "max_steps", "maxSteps") ?? ClampSteps(request.MaxOutputTokens),
      Device = ResolveStringOption(request, "device"),
      AllowedUrls = ResolveStringListOption(request, "allowed_urls", "allowedUrls"),
      EnableRecording = ResolveBoolOption(request, "enable_recording", "enableRecording"),
      ProfileId = ResolveStringOption(request, "profile_id", "profileId"),
      ProfileReadOnly = ResolveBoolOption(request, "profile_read_only", "profileReadOnly"),
      StealthMode = ResolveBoolOption(request, "stealth_mode", "stealthMode"),
      ProxyServer = ResolveStringOption(request, "proxy_server", "proxyServer"),
      ProxyUsername = ResolveStringOption(request, "proxy_username", "proxyUsername"),
      ProxyPassword = ResolveStringOption(request, "proxy_password", "proxyPassword"),
      Certificates = ResolveJsonElementOption(request, "certificates"),
      UseAdblock = ResolveBoolOption(request, "use_adblock", "useAdblock"),
      AdditionalTools = ResolveJsonElementOption(request, "additional_tools", "additionalTools"),
      ExperimentalFeatures = ResolveJsonElementOption(request, "experimental_features", "experimentalFeatures"),
      Extensions = ResolveStringListOption(request, "extensions"),
      ShowCursor = ResolveBoolOption(request, "show_cursor", "showCursor"),
      ResponseModel = ResolveResponseModel(request),
      CustomTools = ToSmoothToolSignatures(request.Tools)
    };
  }

  private async Task<SmoothTaskResponse> SubmitTaskAsync(SmoothSubmitTaskRequest request, CancellationToken cancellationToken)
  {
    var json = JsonSerializer.Serialize(request, SmoothJson);
    using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/task")
    {
      Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
    };

    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

    if (!resp.IsSuccessStatusCode)
      throw new HttpRequestException($"Smooth submit task failed ({(int)resp.StatusCode}): {raw}");

    var model = JsonSerializer.Deserialize<SmoothApiResponse<SmoothTaskResponse>>(raw, SmoothJson)
                ?? throw new InvalidOperationException("Smooth submit task returned empty payload.");

    model.R.RawJson = raw;
    return model.R;
  }

  private async Task<SmoothTaskResponse> GetTaskAsync(
      string taskId,
      long eventT,
      bool downloads,
      CancellationToken cancellationToken)
  {
    var route = $"api/v1/task/{Uri.EscapeDataString(taskId)}?event_t={eventT}&downloads={(downloads ? "true" : "false")}";

    using var req = new HttpRequestMessage(HttpMethod.Get, route);
    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

    if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.Accepted)
      throw new HttpRequestException($"Smooth get task failed ({(int)resp.StatusCode}): {raw}");

    var model = JsonSerializer.Deserialize<SmoothApiResponse<SmoothTaskResponse>>(raw, SmoothJson)
                ?? throw new InvalidOperationException("Smooth get task returned empty payload.");

    model.R.RawJson = raw;
    return model.R;
  }

  private async Task<AIResponse> ExecuteSmoothCancelTaskAsync(AIRequest request, CancellationToken cancellationToken)
  {
    var taskId = ResolveStringOption(request, "task_id", "taskId", "session_id", "sessionId")
                 ?? request.Id
                 ?? throw new InvalidOperationException("Smooth cancel requires provider option 'task_id' or 'session_id'.");

    using var req = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/task/{Uri.EscapeDataString(taskId)}");
    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

    if (!resp.IsSuccessStatusCode)
      throw new HttpRequestException($"Smooth cancel task failed ({(int)resp.StatusCode}): {raw}");

    var result = JsonSerializer.Deserialize<SmoothApiResponse<bool>>(raw, SmoothJson)?.R ?? true;
    var metadata = new Dictionary<string, object?>
    {
      ["smooth_task_id"] = taskId,
      ["smooth_cancelled"] = result,
      ["smooth_raw_response"] = raw
    };

    return CreateTextResponse(request, result ? "cancelled" : "failed", result ? $"Smooth task '{taskId}' cancelled." : $"Smooth task '{taskId}' was not cancelled.", metadata);
  }

  private async Task<AIResponse> ExecuteSmoothCreateProfileAsync(AIRequest request, CancellationToken cancellationToken)
  {
    var profileId = ResolveStringOption(request, "profile_id", "profileId", "id")
                    ?? request.Id
                    ?? throw new InvalidOperationException("Smooth profile creation requires provider option 'profile_id'.");

    var payload = JsonSerializer.Serialize(new { id = profileId }, SmoothJson);
    using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/profile")
    {
      Content = new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json)
    };

    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

    if (!resp.IsSuccessStatusCode)
      throw new HttpRequestException($"Smooth create profile failed ({(int)resp.StatusCode}): {raw}");

    var metadata = new Dictionary<string, object?>
    {
      ["smooth_profile_id"] = profileId,
      ["smooth_raw_response"] = raw
    };

    return CreateTextResponse(request, "completed", $"Smooth profile '{profileId}' created.", metadata);
  }

  private async Task<AIResponse> ExecuteSmoothUploadFilesAsync(AIRequest request, CancellationToken cancellationToken)
  {
    var uploaded = await UploadFilesFromUnifiedRequestAsync(request, cancellationToken);
    var metadata = new Dictionary<string, object?>
    {
      ["smooth_uploaded_file_ids"] = uploaded.ToArray()
    };

    return CreateTextResponse(request, "completed", JsonSerializer.Serialize(uploaded, SmoothJson), metadata);
  }

  private async Task<List<string>> UploadFilesFromUnifiedRequestAsync(AIRequest request, CancellationToken cancellationToken)
  {
    var uploaded = new List<string>();

    foreach (var file in EnumerateUnifiedFiles(request))
    {
      if (TryResolveExistingSmoothFileId(file, out var existingId))
      {
        uploaded.Add(existingId);
        continue;
      }

      var bytes = await ResolveFileBytesAsync(file, cancellationToken);
      if (bytes is null || bytes.Length == 0)
        continue;

      var filename = string.IsNullOrWhiteSpace(file.Filename)
          ? $"aihappey-upload{GuessFileExtension(file.MediaType)}"
          : file.Filename!;

      using var form = new MultipartFormDataContent();
      var content = new ByteArrayContent(bytes);
      content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.MediaType ?? MediaTypeNames.Application.Octet);
      form.Add(content, "file", filename);

      var purpose = TryGetMetadataString(file.Metadata, "purpose")
                    ?? request.Metadata?.GetProviderOption<string>(GetIdentifier(), "file_purpose")
                    ?? request.Metadata?.GetProviderOption<string>(GetIdentifier(), "filePurpose");
      if (!string.IsNullOrWhiteSpace(purpose))
        form.Add(new StringContent(purpose), "file_purpose");

      using var resp = await _client.PostAsync("api/v1/file", form, cancellationToken);
      var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
      if (!resp.IsSuccessStatusCode)
        throw new HttpRequestException($"Smooth upload file failed ({(int)resp.StatusCode}): {raw}");

      var result = JsonSerializer.Deserialize<SmoothApiResponse<SmoothUploadFileResponse>>(raw, SmoothJson)
                   ?? throw new InvalidOperationException("Smooth upload file returned empty payload.");
      uploaded.Add(result.R.Id);
    }

    return uploaded;
  }

  private async Task<byte[]?> ResolveFileBytesAsync(AIFileContentPart file, CancellationToken cancellationToken)
  {
    switch (file.Data)
    {
      case byte[] bytes:
        return bytes;
      case BinaryData binary:
        return binary.ToArray();
      case JsonElement json when json.ValueKind == JsonValueKind.String:
        return await ResolveFileStringBytesAsync(json.GetString(), cancellationToken);
      case string text:
        return await ResolveFileStringBytesAsync(text, cancellationToken);
      default:
        return file.Data is null ? null : JsonSerializer.SerializeToUtf8Bytes(file.Data, SmoothJson);
    }
  }

  private async Task<byte[]?> ResolveFileStringBytesAsync(string? value, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(value))
      return null;

    if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
      using var req = new HttpRequestMessage(HttpMethod.Get, value);
      using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
      if (!resp.IsSuccessStatusCode)
        throw new HttpRequestException($"Smooth file source download failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(cancellationToken)}");
      return await resp.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    var comma = value.IndexOf(',');
    if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
      value = value[(comma + 1)..];

    try
    {
      return Convert.FromBase64String(value.Trim());
    }
    catch
    {
      return Encoding.UTF8.GetBytes(value);
    }
  }

  private async Task SendSmoothSessionEventAsync(
      string sessionId,
      AIRequest request,
      string? action,
      CancellationToken cancellationToken)
  {
    var normalized = action?.Trim().ToLowerInvariant();
    var eventName = normalized is "navigate" or "extract" or "javascript" or "evaluate_js" or "browser_action"
        ? "browser_action"
        : "session_action";

    var eventPayloadName = normalized switch
    {
      "navigate" or "goto" => "navigate",
      "extract" => "extract",
      "javascript" or "evaluate_js" => "javascript",
      "close" => "close",
      "run_task" or "session_action" or "session_event" => "run_task",
      _ => "run_task"
    };

    object input = ResolveJsonElementOption(request, "input") is { } inputElement
        ? inputElement
        : eventPayloadName switch
        {
          "navigate" => new { url = ResolveStringOption(request, "url") ?? BuildPromptFromUnifiedRequest(request) },
          "extract" => new { schema = ResolveResponseModel(request), prompt = BuildPromptFromUnifiedRequest(request) },
          "javascript" => new { script = ResolveStringOption(request, "script", "javascript", "js") ?? BuildPromptFromUnifiedRequest(request) },
          "close" => new { },
          _ => new { task = BuildPromptFromUnifiedRequest(request) }
        };

    await SendSmoothSessionActionAsync(sessionId, eventPayloadName, input, cancellationToken, eventName);
  }

  private async Task SendSmoothSessionActionAsync(
      string sessionId,
      string actionName,
      object input,
      CancellationToken cancellationToken,
      string eventName = "session_action")
  {
    var responseEvent = new SmoothTaskEvent
    {
      Name = eventName,
      Id = $"evt_{Guid.NewGuid():N}",
      Payload = JsonSerializer.SerializeToElement(new
      {
        name = actionName,
        input
      }, SmoothJson)
    };

    await SendSmoothEventAsync(sessionId, responseEvent, cancellationToken);
  }

  private async Task SubmitToolCallEventAsync(
      string taskId,
      string sourceEventId,
      JsonElement payload,
      CancellationToken cancellationToken)
  {
    var responseEvent = new SmoothTaskEvent
    {
      Name = "tool_call",
      Id = $"evt_response_{sourceEventId}",
      Payload = JsonSerializer.SerializeToElement(new
      {
        code = 501,
        output = new
        {
          handled_by = "aihappey.smooth_provider",
          message = "Smooth requested a client-side custom tool, but this provider integration has no local tool executor. Returning a structured fallback.",
          source_event_id = sourceEventId,
          original_payload = payload
        }
      }, SmoothJson)
    };

    await SendSmoothEventAsync(taskId, responseEvent, cancellationToken);
  }

  private async Task SendSmoothEventAsync(string taskId, SmoothTaskEvent responseEvent, CancellationToken cancellationToken)
  {
    var json = JsonSerializer.Serialize(responseEvent, SmoothJson);
    using var req = new HttpRequestMessage(HttpMethod.Post, $"api/v1/task/{Uri.EscapeDataString(taskId)}/event")
    {
      Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
    };

    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (resp.IsSuccessStatusCode)
      return;

    var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
    throw new HttpRequestException($"Smooth send event failed ({(int)resp.StatusCode}): {raw}");
  }

  private async Task<SmoothEventHandlingResult> HandleTaskEventsAsync(
      string taskId,
      List<SmoothTaskEvent>? events,
      CancellationToken cancellationToken)
  {
    var nextEventTimestamp = 0L;
    var lines = new List<string>();
    var streamEvents = new List<AIStreamEvent>();

    foreach (var evt in events ?? [])
    {
      if (evt.Timestamp.HasValue)
        nextEventTimestamp = Math.Max(nextEventTimestamp, evt.Timestamp.Value);

      var timestamp = ToDateTimeOffset(evt.Timestamp) ?? DateTimeOffset.UtcNow;
      var eventId = evt.Id ?? Guid.NewGuid().ToString("n");
      var toolName = ExtractEventToolName(evt.Payload) ?? eventId;

      if (string.Equals(evt.Name, "tool_call", StringComparison.OrdinalIgnoreCase))
      {
        streamEvents.Add(CreateSmoothStreamEvent(
            GetIdentifier(),
            "tool-input-available",
            eventId,
            new AIToolInputAvailableEventData
            {
              ToolName = toolName,
              Title = toolName,
              Input = evt.Payload.Clone(),
              ProviderExecuted = false,
              ProviderMetadata = CreateProviderMetadata(new Dictionary<string, object?>
              {
                ["smooth_event_id"] = eventId,
                ["smooth_event_name"] = evt.Name,
                ["smooth_event_payload"] = evt.Payload.Clone()
              })
            },
            timestamp,
            null));

        lines.Add($"[tool_call] Smooth requested tool '{toolName}'. Responding with provider fallback.");
        await SubmitToolCallEventAsync(taskId, eventId, evt.Payload, cancellationToken);

        streamEvents.Add(CreateSmoothStreamEvent(
            GetIdentifier(),
            "tool-output-error",
            eventId,
            new AIToolOutputErrorEventData
            {
              ToolCallId = eventId,
              ErrorText = "Tool execution is not wired in the Smooth provider; a fallback tool response was sent to Smooth.",
              ProviderExecuted = false,
              ProviderMetadata = CreateProviderMetadata(new Dictionary<string, object?>
              {
                ["smooth_event_id"] = eventId,
                ["smooth_event_name"] = evt.Name
              })
            },
            DateTimeOffset.UtcNow,
            null));

        continue;
      }

      streamEvents.Add(CreateSmoothStreamEvent(
          GetIdentifier(),
          $"data-smooth.{evt.Name}",
          eventId,
          new AIDataEventData
          {
            Id = eventId,
            Data = evt.Payload.Clone(),
            Transient = true
          },
          timestamp,
          null));
    }

    return new SmoothEventHandlingResult(nextEventTimestamp, lines, streamEvents);
  }

  private async Task<List<SmoothDownloadedImage>> DownloadImagesAsDataUrlsAsync(
      string? downloadsUrl,
      CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(downloadsUrl))
      return [];

    try
    {
      using var req = new HttpRequestMessage(HttpMethod.Get, downloadsUrl);
      using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
      if (!resp.IsSuccessStatusCode)
        return [];

      var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
      if (bytes.Length == 0)
        return [];

      using var ms = new MemoryStream(bytes);
      using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
      var result = new List<SmoothDownloadedImage>();

      foreach (var entry in zip.Entries)
      {
        if (string.IsNullOrWhiteSpace(entry.Name))
          continue;

        var mediaType = GuessImageMediaType(entry.Name);
        if (mediaType is null)
          continue;

        await using var stream = entry.Open();
        using var entryBuffer = new MemoryStream();
        await stream.CopyToAsync(entryBuffer, cancellationToken);

        var base64 = Convert.ToBase64String(entryBuffer.ToArray());
        result.Add(new SmoothDownloadedImage(
            entry.Name,
            mediaType,
            $"data:{mediaType};base64,{base64}"));
      }

      return result;
    }
    catch
    {
      return [];
    }
  }

  private AIResponse ToUnifiedResponse(
      SmoothTaskResponse task,
      AIRequest request,
      StringBuilder providerEventLog,
      IReadOnlyList<SmoothDownloadedImage> downloadedImages,
      IReadOnlyList<string> uploadedFileIds)
  {
    var text = ResolveFinalOutputText(task, providerEventLog);
    var content = new List<AIContentPart>();
    if (!string.IsNullOrWhiteSpace(text))
    {
      content.Add(new AITextContentPart
      {
        Type = "text",
        Text = text,
        Metadata = new Dictionary<string, object?>
        {
          ["smooth_output"] = task.Output.ValueKind is JsonValueKind.Undefined ? null : task.Output.Clone()
        }
      });
    }

    foreach (var image in downloadedImages)
    {
      content.Add(new AIFileContentPart
      {
        Type = "file",
        MediaType = image.MediaType,
        Filename = image.FileName,
        Data = image.DataUrl,
        Metadata = new Dictionary<string, object?>
        {
          ["smooth.downloaded"] = true
        }
      });
    }

    var metadata = BuildSmoothMetadata(request, task, uploadedFileIds, downloadedImages.Count);
    var status = IsFinished(task.Status) || IsSessionStatus(task.Status) ? "completed" : "failed";

    return new AIResponse
    {
      ProviderId = GetIdentifier(),
      Model = ResolveSmoothModel(request.Model),
      Status = status,
      Usage = BuildSmoothUsage(task),
      Metadata = metadata,
      Output = new AIOutput
      {
        Items =
            [
                new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = content.Count > 0 ? content : [new AITextContentPart { Type = "text", Text = string.Empty }],
                        Metadata = new Dictionary<string, object?>
                        {
                            ["smooth_task_id"] = task.Id,
                            ["smooth_status"] = task.Status
                        }
                    }
            ],
        Metadata = metadata
      }
    };
  }

  private AIResponse CreateTextResponse(AIRequest request, string status, string text, Dictionary<string, object?> metadata)
      => new()
      {
        ProviderId = GetIdentifier(),
        Model = ResolveSmoothModel(request.Model),
        Status = status,
        Metadata = metadata,
        Output = new AIOutput
        {
          Items =
              [
                  new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart { Type = "text", Text = text }],
                        Metadata = metadata
                    }
              ],
          Metadata = metadata
        }
      };

  private static string BuildPromptFromUnifiedRequest(AIRequest request)
  {
    var lines = new List<string>();

    if (!string.IsNullOrWhiteSpace(request.Instructions))
      lines.Add($"system: {request.Instructions}");

    if (!string.IsNullOrWhiteSpace(request.Input?.Text))
      lines.Add(request.Input!.Text!);

    foreach (var item in request.Input?.Items ?? [])
    {
      var role = string.IsNullOrWhiteSpace(item.Role) ? item.Type : item.Role;
      var parts = new List<string>();

      foreach (var part in item.Content ?? [])
      {
        switch (part)
        {
          case AITextContentPart text when !string.IsNullOrWhiteSpace(text.Text):
            parts.Add(text.Text);
            break;
          case AIReasoningContentPart reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
            parts.Add($"[reasoning] {reasoning.Text}");
            break;
          case AIFileContentPart file:
            parts.Add($"[file: {file.Filename ?? file.MediaType ?? "attachment"}]");
            break;
          case AIToolCallContentPart tool:
            parts.Add($"[tool {tool.ToolName ?? tool.ToolCallId}: {JsonSerializer.Serialize(tool.Input ?? tool.Output ?? new { }, SmoothJson)}]");
            break;
        }
      }

      if (parts.Count > 0)
        lines.Add($"{role}: {string.Join("\n", parts)}");
    }

    return string.Join("\n\n", lines).Trim();
  }

  private static string ResolveFinalOutputText(SmoothTaskResponse task, StringBuilder? providerEventLog)
  {
    var output = GetOutputText(task.Output);
    if (!string.IsNullOrWhiteSpace(output))
      return output!;

    if (providerEventLog is not null && providerEventLog.Length > 0)
      return providerEventLog.ToString().Trim();

    return string.Empty;
  }

  private static string? GetOutputText(JsonElement output)
  {
    if (output.ValueKind == JsonValueKind.String)
      return output.GetString();

    if (output.ValueKind == JsonValueKind.Object || output.ValueKind == JsonValueKind.Array)
      return output.GetRawText();

    return null;
  }

  private static string ExtractSmoothOutputText(AIOutput? output)
      => string.Join("\n", output?.Items?
          .SelectMany(item => item.Content ?? [])
          .OfType<AITextContentPart>()
          .Select(part => part.Text)
          .Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);

  private Dictionary<string, object?> BuildSmoothMetadata(
      AIRequest request,
      SmoothTaskResponse task,
      IReadOnlyList<string> uploadedFileIds,
      int downloadedImagesCount)
  {
    var merged = request.Metadata is null
        ? []
        : new Dictionary<string, object?>(request.Metadata);

    merged["smooth_task_id"] = task.Id;
    merged["smooth_status"] = task.Status;
    merged["smooth_device"] = task.Device;
    merged["smooth_live_url"] = task.LiveUrl;
    merged["smooth_recording_url"] = task.RecordingUrl;
    merged["smooth_downloads_url"] = task.DownloadsUrl;
    merged["smooth_created_at"] = task.CreatedAt;
    merged["smooth_credits_used"] = task.CreditsUsed;
    merged["smooth_uploaded_file_ids"] = uploadedFileIds.ToArray();
    merged["smooth_downloaded_images_count"] = downloadedImagesCount;
    merged["smooth_raw_response"] = task.RawJson;
    if (task.Events is not null)
      merged["smooth_events"] = JsonSerializer.SerializeToElement(task.Events, SmoothJson);

    return merged;
  }

  private static object BuildSmoothUsage(SmoothTaskResponse task)
      => new Dictionary<string, object?>
      {
        ["credits_used"] = task.CreditsUsed,
        ["cost"] = task.CreditsUsed is null ? null : task.CreditsUsed.Value * 0.01m,
        ["prompt_tokens"] = 0,
        ["completion_tokens"] = 0,
        ["total_tokens"] = 0
      };

  private static object? ResolveResponseModel(AIRequest request)
  {
    var explicitModel = ResolveJsonElementOption(request, "response_model", "responseModel");
    if (explicitModel is { ValueKind: JsonValueKind.Object or JsonValueKind.Array })
      return explicitModel.Value.Clone();

    if (request.ResponseFormat is null)
      return null;

    var element = JsonSerializer.SerializeToElement(request.ResponseFormat, SmoothJson);
    if (element.ValueKind is JsonValueKind.Object)
    {
      if (element.TryGetProperty("schema", out var schema) && schema.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        return schema.Clone();
      if (element.TryGetProperty("json_schema", out var jsonSchema)
          && jsonSchema.ValueKind == JsonValueKind.Object
          && jsonSchema.TryGetProperty("schema", out var nestedSchema)
          && nestedSchema.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        return nestedSchema.Clone();
    }

    return element.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? element.Clone() : null;
  }

  private static List<SmoothToolSignature>? ToSmoothToolSignatures(List<AIToolDefinition>? tools)
  {
    if (tools is null || tools.Count == 0)
      return null;

    var mapped = new List<SmoothToolSignature>();
    foreach (var tool in tools)
    {
      if (string.IsNullOrWhiteSpace(tool.Name))
        continue;

      mapped.Add(new SmoothToolSignature
      {
        Name = tool.Name,
        Description = tool.Description ?? tool.Title ?? string.Empty,
        Inputs = tool.InputSchema is null
              ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }, SmoothJson)
              : JsonSerializer.SerializeToElement(tool.InputSchema, SmoothJson),
        Output = TryGetMetadataString(tool.Metadata, "output") ?? "object"
      });
    }

    return mapped.Count == 0 ? null : mapped;
  }

  private static IEnumerable<AIFileContentPart> EnumerateUnifiedFiles(AIRequest request)
      => request.Input?.Items?
          .SelectMany(item => item.Content ?? [])
          .OfType<AIFileContentPart>() ?? [];

  private static bool TryResolveExistingSmoothFileId(AIFileContentPart file, out string fileId)
  {
    fileId = TryGetMetadataString(file.Metadata, "smooth_file_id")
             ?? TryGetMetadataString(file.Metadata, "file_id")
             ?? (file.Data as string is { } text && text.StartsWith("file_", StringComparison.OrdinalIgnoreCase) ? text : string.Empty);
    return !string.IsNullOrWhiteSpace(fileId);
  }

  private static string? TryGetMetadataString(Dictionary<string, object?>? metadata, string key)
  {
    if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
      return null;

    return value switch
    {
      string text => text,
      JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
      JsonElement json when json.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => json.ToString(),
      _ => value.ToString()
    };
  }

  private static string ResolveSmoothAgent(string? model, AIRequest request)
  {
    var explicitAgent = ResolveStringOption(request, "agent");
    if (!string.IsNullOrWhiteSpace(explicitAgent))
      return explicitAgent!;

    if (string.IsNullOrWhiteSpace(model))
      return "smooth";

    return model.Contains("smooth-lite", StringComparison.OrdinalIgnoreCase) ? "smooth-lite" : "smooth";
  }

  private static string ResolveSmoothModel(string? model)
      => string.IsNullOrWhiteSpace(model) ? "smooth/smooth-agent" : model!;

  private static string? ResolveSmoothAction(AIRequest request)
      => ResolveStringOption(request, "action", "operation", "mode")?.Trim().ToLowerInvariant();

  private static bool IsSmoothSessionAction(string? action)
      => action is "session_event" or "session_action" or "browser_action" or "run_task" or "navigate" or "goto" or "extract" or "javascript" or "evaluate_js" or "close";

  private static bool IsSessionCreateRequest(AIRequest request, string? action)
      => action is "create_session" or "session" or "open_session"
         || request.Metadata?.GetProviderOption<bool?>("smooth", "session") == true
         || request.Metadata?.GetProviderOption<bool?>("smooth", "task_null") == true;

  private static bool ShouldStopSessionPoll(AIRequest request, string? action)
      => IsSessionCreateRequest(request, action)
         && request.Metadata?.GetProviderOption<bool?>("smooth", "wait_for_session_terminal") != true;

  private static bool IsTerminal(SmoothTaskResponse task)
      => IsTerminal(task.Status);

  private static bool IsTerminal(string status)
      => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
         || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
         || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

  private static bool IsFinished(string status)
      => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase);

  private static bool IsSessionStatus(string status)
      => string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
         || string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase);

  private static int ClampSteps(int? value)
      => Math.Clamp(value ?? 32, 2, 128);

  private static TimeSpan ResolvePollInterval(AIRequest request)
  {
    var milliseconds = ResolveIntOption(request, "poll_interval_ms", "pollIntervalMs");
    if (milliseconds is > 0)
      return TimeSpan.FromMilliseconds(milliseconds.Value);

    var seconds = ResolveIntOption(request, "poll_seconds", "pollSeconds");
    return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : DefaultSmoothPollInterval;
  }

  private static TimeSpan? ResolvePollTimeout(AIRequest request)
  {
    var seconds = ResolveIntOption(request, "poll_timeout_seconds", "pollTimeoutSeconds");
    if (seconds is > 0)
      return TimeSpan.FromSeconds(seconds.Value);

    var minutes = ResolveIntOption(request, "poll_timeout_minutes", "pollTimeoutMinutes");
    return minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null;
  }

  private static int? ResolvePollMaxAttempts(AIRequest request)
      => ResolveIntOption(request, "poll_max_attempts", "pollMaxAttempts") ?? DefaultSmoothPollMaxAttempts;

  private static string? ResolveStringOption(AIRequest request, params string[] names)
  {
    foreach (var name in names)
    {
      var value = request.Metadata?.GetProviderOption<string>("smooth", name);
      if (!string.IsNullOrWhiteSpace(value))
        return value;
    }

    return null;
  }

  private static int? ResolveIntOption(AIRequest request, params string[] names)
  {
    foreach (var name in names)
    {
      var value = request.Metadata?.GetProviderOption<int?>("smooth", name);
      if (value.HasValue)
        return value.Value;
    }

    return null;
  }

  private static bool? ResolveBoolOption(AIRequest request, params string[] names)
  {
    foreach (var name in names)
    {
      var value = request.Metadata?.GetProviderOption<bool?>("smooth", name);
      if (value.HasValue)
        return value.Value;
    }

    return null;
  }

  private static List<string>? ResolveStringListOption(AIRequest request, params string[] names)
  {
    foreach (var name in names)
    {
      var list = request.Metadata?.GetProviderOption<List<string>>("smooth", name)
                 ?? request.Metadata?.GetProviderOption<string[]>("smooth", name)?.ToList();
      if (list is { Count: > 0 })
        return list;

      var single = request.Metadata?.GetProviderOption<string>("smooth", name);
      if (!string.IsNullOrWhiteSpace(single))
        return [single!];
    }

    return null;
  }

  private static Dictionary<string, object?>? ResolveDictionaryOption(AIRequest request, params string[] names)
  {
    foreach (var name in names)
    {
      var dict = request.Metadata?.GetProviderOption<Dictionary<string, object?>>("smooth", name);
      if (dict is { Count: > 0 })
        return dict;
    }

    return null;
  }

  private static JsonElement? ResolveJsonElementOption(AIRequest request, params string[] names)
  {
    foreach (var name in names)
    {
      var value = request.Metadata?.GetProviderOption<JsonElement?>("smooth", name);
      if (value.HasValue && value.Value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        return value.Value.Clone();
    }

    return null;
  }

  private static string? ExtractEventToolName(JsonElement payload)
  {
    if (payload.ValueKind != JsonValueKind.Object)
      return null;

    if (payload.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
      return nameEl.GetString();

    if (payload.TryGetProperty("tool", out var toolEl) && toolEl.ValueKind == JsonValueKind.String)
      return toolEl.GetString();

    return null;
  }

  private static DateTimeOffset? ToDateTimeOffset(long? unixTimestamp)
  {
    if (!unixTimestamp.HasValue)
      return null;

    return unixTimestamp.Value > 9_999_999_999
        ? DateTimeOffset.FromUnixTimeMilliseconds(unixTimestamp.Value)
        : DateTimeOffset.FromUnixTimeSeconds(unixTimestamp.Value);
  }

  private static Dictionary<string, Dictionary<string, object>>? CreateProviderMetadata(Dictionary<string, object?>? metadata)
      => metadata is null
          ? null
          : new Dictionary<string, Dictionary<string, object>>
          {
            ["smooth"] = metadata
                  .Where(kvp => kvp.Value is not null)
                  .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!)
          };

  private static Dictionary<string, object>? CreateFlatProviderMetadata(Dictionary<string, object?>? metadata)
      => metadata?
          .Where(kvp => kvp.Value is not null)
          .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

  private static AIStreamEvent CreateSmoothStreamEvent(
      string providerId,
      string type,
      string? id,
      object data,
      DateTimeOffset timestamp,
      Dictionary<string, object?>? metadata,
      AIOutput? output = null)
      => new()
      {
        ProviderId = providerId,
        Metadata = metadata,
        Event = new AIEventEnvelope
        {
          Type = type,
          Id = id,
          Timestamp = timestamp,
          Data = data,
          Output = output
        }
      };

  private static string? GuessImageMediaType(string filename)
  {
    var ext = Path.GetExtension(filename).ToLowerInvariant();
    return ext switch
    {
      ".png" => "image/png",
      ".jpg" or ".jpeg" => "image/jpeg",
      ".webp" => "image/webp",
      ".gif" => "image/gif",
      ".bmp" => "image/bmp",
      ".svg" => "image/svg+xml",
      _ => null
    };
  }

  private static string GuessFileExtension(string? mediaType)
      => mediaType?.ToLowerInvariant() switch
      {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "application/pdf" => ".pdf",
        "text/plain" => ".txt",
        "text/csv" => ".csv",
        "application/json" => ".json",
        _ => ".bin"
      };

  private sealed record SmoothExecutionStart(SmoothTaskResponse Task, IReadOnlyList<string> UploadedFileIds);

  private sealed record SmoothEventHandlingResult(long NextEventTimestamp, List<string> LogLines, List<AIStreamEvent> StreamEvents);
}
