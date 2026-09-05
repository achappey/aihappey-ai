using AIHappey.Core.AI;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Epho;

public partial class EphoProvider
{
  private const string EphoChatToolName = "create_epho_chat";
  private static readonly JsonSerializerOptions EphoJson = JsonSerializerOptions.Web;
  private static readonly HashSet<string> EphoHarnesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "codex", "claude", "opencode"
    };

  public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var route = ParseEphoRoute(request.Model);
    var existingChatId = TryFindEphoChatId(request, out var recoveredChatId) ? recoveredChatId : null;
    var payload = BuildEphoPayload(request, route, existingChatId);
    var frames = await SendEphoChatAsync(payload, cancellationToken);
    var snapshot = BuildSnapshot(frames);

    if (string.IsNullOrWhiteSpace(snapshot.ChatId))
      throw new InvalidOperationException("Epho stream did not include a chat_id.");
    if (snapshot.Done is null)
      throw new InvalidOperationException("Epho stream ended without a done event.");

    var content = new List<AIContentPart>();
    if (existingChatId is null)
      content.Add(CreateChatToolPart(snapshot.ChatId!, snapshot.TurnId, snapshot.ChatFrame));

    foreach (var providerEvent in snapshot.AgentEvents)
      content.Add(CreateAgentEventToolPart(providerEvent));

    var output = GetString(snapshot.Done.Value, "output");
    if (!string.IsNullOrEmpty(output))
    {
      content.Add(new AITextContentPart
      {
        Type = "text",
        Text = output,
        Metadata = CreateRawMetadata(snapshot.Done.Value)
      });
    }

    foreach (var artifact in GetArtifacts(snapshot.Done.Value))
      content.Add(CreateArtifactPart(await DownloadArtifactAsync(artifact, cancellationToken)));

    var status = GetString(snapshot.Done.Value, "status") ?? "completed";
    var error = GetString(snapshot.Done.Value, "error");
    if (!string.IsNullOrWhiteSpace(error))
      content.Add(CreateErrorToolPart(snapshot.TurnId, error!, snapshot.Done.Value));

    var metadata = CreateResponseMetadata(snapshot, route);
    return new AIResponse
    {
      ProviderId = GetIdentifier(),
      Model = FormatEphoModelId(route),
      Status = NormalizeStatus(status, error),
      Output = new AIOutput
      {
        Items = content.Count == 0 ? null :
            [
                new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = content,
                        Metadata = metadata
                    }
            ],
        Metadata = metadata
      },
      Metadata = metadata
    };
  }

  public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
      AIRequest request,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var route = ParseEphoRoute(request.Model);
    var existingChatId = TryFindEphoChatId(request, out var recoveredChatId) ? recoveredChatId : null;
    var payload = BuildEphoPayload(request, route, existingChatId);

    using var httpRequest = CreateEphoChatRequest(payload);
    using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    await EnsureEphoSuccessAsync(response, cancellationToken);
    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var reader = new StreamReader(stream);

    string? chatId = existingChatId;
    string? turnId = null;
    var textId = $"epho-text-{Guid.NewGuid():N}";
    var emittedChatTool = existingChatId is not null;

    await foreach (var frame in ReadEphoSseAsync(reader, cancellationToken))
    {
      var frameType = GetString(frame, "type") ?? "event";
      var now = DateTimeOffset.UtcNow;

      if (string.Equals(frameType, "chat", StringComparison.OrdinalIgnoreCase))
      {
        chatId = GetString(frame, "chat_id") ?? chatId;
        turnId = GetString(frame, "turn_id") ?? turnId;
        if (!emittedChatTool && !string.IsNullOrWhiteSpace(chatId))
        {
          foreach (var evt in CreateChatToolEvents(chatId!, turnId, frame, now))
            yield return evt;
          emittedChatTool = true;
        }
        continue;
      }

      if (string.Equals(frameType, "event", StringComparison.OrdinalIgnoreCase))
      {
        var agentEvent = GetObject(frame, "event") ?? frame;
        foreach (var evt in CreateAgentStreamEvents(agentEvent, now))
          yield return evt;
        continue;
      }

      if (!string.Equals(frameType, "done", StringComparison.OrdinalIgnoreCase))
      {
        yield return CreateStreamEvent($"data-epho-{NormalizeName(frameType)}", turnId,
            new AIDataEventData { Id = turnId, Data = frame.Clone(), Transient = false }, now, null);
        continue;
      }

      var output = GetString(frame, "output");
      if (!string.IsNullOrEmpty(output))
      {
        var providerMetadata = CreateScopedMetadata(frame);
        yield return CreateStreamEvent("text-start", textId,
            new AITextStartEventData { ProviderMetadata = FlattenMetadata(frame) }, now, null);
        yield return CreateStreamEvent("text-delta", textId,
            new AITextDeltaEventData { Delta = output, ProviderMetadata = FlattenMetadata(frame) }, now, null);
        yield return CreateStreamEvent("text-end", textId,
            new AITextEndEventData { ProviderMetadata = FlattenMetadata(frame) }, now, null);
      }

      var artifactIndex = 0;
      foreach (var artifact in GetArtifacts(frame))
      {
        var downloaded = await DownloadArtifactAsync(artifact, cancellationToken);
        yield return CreateStreamEvent("file", $"epho-artifact-{turnId}-{artifactIndex}",
            new AIFileEventData
            {
              MediaType = downloaded.MediaType,
              Url = $"data:{downloaded.MediaType};base64,{downloaded.Base64}",
              Filename = downloaded.Name,
              ProviderMetadata = CreateArtifactScopedMetadata(downloaded)
            }, now, CreateArtifactMetadata(downloaded));
        artifactIndex++;
      }

      var error = GetString(frame, "error");
      if (!string.IsNullOrWhiteSpace(error))
        yield return CreateStreamEvent("error", turnId, new AIErrorEventData { ErrorText = error! }, now, null);

      var status = GetString(frame, "status") ?? "completed";
      var finishMetadata = AIFinishMessageMetadata.Create(
          FormatEphoModelId(route), now,
          additionalProperties: new Dictionary<string, object?>
          {
            ["epho.chat_id"] = chatId,
            ["epho.turn_id"] = turnId,
            ["epho.status"] = status,
            ["epho.raw"] = frame.Clone()
          });
      yield return CreateStreamEvent("finish", turnId,
          new AIFinishEventData
          {
            FinishReason = NormalizeFinishReason(status, error),
            Model = FormatEphoModelId(route),
            CompletedAt = now,
            MessageMetadata = finishMetadata,
            Response = frame.Clone()
          }, now, null);
    }
  }

  private async Task<List<JsonElement>> SendEphoChatAsync(Dictionary<string, object?> payload, CancellationToken cancellationToken)
  {
    using var request = CreateEphoChatRequest(payload);
    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    await EnsureEphoSuccessAsync(response, cancellationToken);
    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var reader = new StreamReader(stream);
    var frames = new List<JsonElement>();
    await foreach (var frame in ReadEphoSseAsync(reader, cancellationToken))
      frames.Add(frame);
    return frames;
  }

  private HttpRequestMessage CreateEphoChatRequest(Dictionary<string, object?> payload)
  {
    var request = new HttpRequestMessage(HttpMethod.Post, "chat")
    {
      Content = JsonContent.Create(payload, options: EphoJson)
    };
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Text.EventStream));
    ApplyAuthHeader(request);
    return request;
  }

  private static async Task EnsureEphoSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
  {
    if (response.IsSuccessStatusCode)
      return;
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    throw new HttpRequestException($"Epho chat API failed with status {(int)response.StatusCode} ({response.StatusCode}): {body}", null, response.StatusCode);
  }

  private Dictionary<string, object?> BuildEphoPayload(AIRequest request, EphoRoute route, string? chatId)
  {
    var prompt = ExtractLatestUserText(request) ?? request.Input?.Text;
    if (string.IsNullOrWhiteSpace(prompt))
      throw new InvalidOperationException("Epho requires a non-empty user prompt.");

    var payload = new Dictionary<string, object?> { ["prompt"] = prompt };
    if (!string.IsNullOrWhiteSpace(chatId))
      payload["chat_id"] = chatId;
    else
    {
      payload["harness"] = route.Harness;
      payload["model"] = route.Model;
      CopyOption(request, payload, "effort");
      payload["system_prompt"] = GetOption(request, "system_prompt") ?? request.Instructions;
      CopyOption(request, payload, "mcp_servers");
      CopyOption(request, payload, "instance");
      CopyOption(request, payload, "env");
      CopyOption(request, payload, "labels");
    }

    foreach (var key in new[] { "provider_api_key", "repos", "skills", "stream_files", "webhook_url", "cancel_on_disconnect" })
      CopyOption(request, payload, key);

    var inputFiles = MergeInputFiles(request);
    if (inputFiles.Count > 0)
      payload["input_files"] = inputFiles;
    return payload.Where(pair => pair.Value is not null).ToDictionary();
  }

  private static void CopyOption(AIRequest request, Dictionary<string, object?> payload, string key)
  {
    var value = GetOption(request, key);
    if (value is not null)
      payload[key] = value;
  }

  private static object? GetOption(AIRequest request, string key)
  {
    if (request.Metadata is null || !request.Metadata.TryGetValue("epho", out var raw) || raw is null)
      return null;
    var element = raw is JsonElement json ? json : JsonSerializer.SerializeToElement(raw, EphoJson);
    return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
        ? value.Clone()
        : null;
  }

  private static List<object> MergeInputFiles(AIRequest request)
  {
    var files = new List<object>();
    if (GetOption(request, "input_files") is JsonElement configured && configured.ValueKind == JsonValueKind.Array)
      files.AddRange(configured.EnumerateArray().Select(item => (object)item.Clone()));

    foreach (var file in request.Input?.Items?.SelectMany(item => item.Content ?? []).OfType<AIFileContentPart>() ?? [])
    {
      var name = string.IsNullOrWhiteSpace(file.Filename) ? $"input-{files.Count + 1}" : file.Filename!;
      var data = file.Data?.ToString();
      if (string.IsNullOrWhiteSpace(data))
        continue;
      if (Uri.TryCreate(data, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        files.Add(new { name, url = data });
      else if (TryExtractBase64(data!, out var base64))
        files.Add(new { name, content = base64 });
      else
        throw new NotSupportedException($"Epho input file '{name}' must contain an HTTPS URL, base64 string, or base64 data URI.");
    }
    return files;
  }

  private static bool TryExtractBase64(string value, out string base64)
  {
    base64 = value;
    var marker = ";base64,";
    var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && index >= 0)
      base64 = value[(index + marker.Length)..];
    try { _ = Convert.FromBase64String(base64); return true; }
    catch (FormatException) { base64 = string.Empty; return false; }
  }

  private static EphoRoute ParseEphoRoute(string? model)
  {
    if (string.IsNullOrWhiteSpace(model))
      throw new InvalidOperationException("Epho requires a model ID in the form 'epho/{harness}/{model}'.");
    var value = model.Trim();
    if (value.StartsWith("epho/", StringComparison.OrdinalIgnoreCase))
      value = value[5..];
    var slash = value.IndexOf('/');
    if (slash <= 0 || slash == value.Length - 1)
      throw new InvalidOperationException($"Invalid Epho model ID '{model}'. Expected 'epho/{{harness}}/{{model}}'.");
    var harness = value[..slash].ToLowerInvariant();
    var nativeModel = value[(slash + 1)..];
    if (!EphoHarnesses.Contains(harness))
      throw new InvalidOperationException($"Unsupported Epho harness '{harness}'. Expected codex, claude, or opencode.");
    if (harness == "opencode" && !nativeModel.Contains('/'))
      throw new InvalidOperationException("Epho OpenCode models must include their provider prefix, for example 'epho/opencode/openai/gpt-5'.");
    return new EphoRoute(harness, nativeModel);
  }

  private static string FormatEphoModelId(EphoRoute route) => $"epho/{route.Harness}/{route.Model}";

  private static string? ExtractLatestUserText(AIRequest request)
  {
    foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
    {
      if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
        continue;
      var text = string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text).Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);
      if (!string.IsNullOrWhiteSpace(text)) return text;
    }
    return null;
  }

  private static bool TryFindEphoChatId(AIRequest request, out string chatId)
  {
    chatId = request.Metadata.GetProviderOption<string>("epho", "chat_id")
             ?? request.Metadata.GetProviderOption<string>("epho", "chatId")
             ?? TryGetDictionaryString(request.Input?.Metadata, "chat_id", "chatId")
             ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(chatId)) return true;

    foreach (var item in request.Input?.Items ?? [])
    {
      if (TryExtractChatId(item.Metadata, out chatId)) return true;
      foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
      {
        if (tool.ProviderExecuted != true) continue;
        if (TryExtractChatId(tool.Output, out chatId) || TryExtractChatId(tool.Metadata, out chatId) || TryExtractChatId(tool.Input, out chatId))
          return true;
      }
    }
    chatId = string.Empty;
    return false;
  }

  private static string? TryGetDictionaryString(Dictionary<string, object?>? values, params string[] keys)
  {
    if (values is null) return null;
    foreach (var key in keys)
      if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString())) return value!.ToString();
    return null;
  }

  private static bool TryExtractChatId(object? value, out string chatId)
  {
    chatId = string.Empty;
    if (value is null) return false;
    var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, EphoJson);
    if (element.ValueKind != JsonValueKind.Object) return false;
    foreach (var nestedName in new[] { "structuredContent", "output", "epho", "chat" })
      if (element.TryGetProperty(nestedName, out var nested) && TryExtractChatId(nested, out chatId)) return true;
    chatId = GetString(element, "chat_id") ?? GetString(element, "chatId") ?? string.Empty;
    return !string.IsNullOrWhiteSpace(chatId);
  }

  private static EphoSnapshot BuildSnapshot(IEnumerable<JsonElement> frames)
  {
    var snapshot = new EphoSnapshot();
    foreach (var frame in frames)
    {
      var type = GetString(frame, "type");
      if (string.Equals(type, "chat", StringComparison.OrdinalIgnoreCase))
      {
        snapshot.ChatFrame = frame.Clone();
        snapshot.ChatId = GetString(frame, "chat_id") ?? snapshot.ChatId;
        snapshot.TurnId = GetString(frame, "turn_id") ?? snapshot.TurnId;
      }
      else if (string.Equals(type, "event", StringComparison.OrdinalIgnoreCase))
        snapshot.AgentEvents.Add((GetObject(frame, "event") ?? frame).Clone());
      else if (string.Equals(type, "done", StringComparison.OrdinalIgnoreCase))
        snapshot.Done = frame.Clone();
    }
    return snapshot;
  }

  private static AIToolCallContentPart CreateChatToolPart(string chatId, string? turnId, JsonElement raw)
      => new()
      {
        Type = "tool-call",
        ToolCallId = BuildChatToolCallId(chatId),
        ToolName = EphoChatToolName,
        Title = "Create Epho chat",
        Input = JsonSerializer.SerializeToElement(new { }, EphoJson),
        Output = CreateChatToolResult(chatId, turnId, raw),
        ProviderExecuted = true,
        State = "output-available",
        Metadata = new Dictionary<string, object?> { ["chat_id"] = chatId, ["turn_id"] = turnId, ["tool_name"] = EphoChatToolName }
      };

  private static CallToolResult CreateChatToolResult(string chatId, string? turnId, JsonElement raw)
      => new()
      {
        Content = [],
        StructuredContent = JsonSerializer.SerializeToElement(new { chat_id = chatId, chatId, turn_id = turnId, turnId, chat = raw.Clone() }, EphoJson)
      };

  private IEnumerable<AIStreamEvent> CreateChatToolEvents(string chatId, string? turnId, JsonElement raw, DateTimeOffset timestamp)
  {
    var id = BuildChatToolCallId(chatId);
    var metadata = CreateScopedMetadata(JsonSerializer.SerializeToElement(new { chat_id = chatId, turn_id = turnId, tool_name = EphoChatToolName }, EphoJson));
    yield return CreateStreamEvent("tool-input-available", id, new AIToolInputAvailableEventData
    {
      ToolName = EphoChatToolName,
      Title = "Create Epho chat",
      Input = new { },
      ProviderExecuted = true,
      ProviderMetadata = metadata
    }, timestamp, null);
    yield return CreateStreamEvent("tool-output-available", id, new AIToolOutputAvailableEventData
    {
      ToolName = EphoChatToolName,
      Output = CreateChatToolResult(chatId, turnId, raw),
      ProviderExecuted = true,
      ProviderMetadata = metadata
    }, timestamp, null);
  }

  private IEnumerable<AIStreamEvent> CreateAgentStreamEvents(JsonElement agentEvent, DateTimeOffset timestamp)
  {
    var type = GetString(agentEvent, "type") ?? "event";
    var eventId = GetString(agentEvent, "id") ?? $"epho-event-{Guid.NewGuid():N}";
    if (IsToolEvent(type, agentEvent))
    {
      var toolName = NormalizeName(GetString(agentEvent, "tool") ?? GetString(agentEvent, "command") ?? type);
      var id = $"epho-{toolName}-{eventId}";
      yield return CreateStreamEvent("tool-input-available", id, new AIToolInputAvailableEventData
      {
        ToolName = toolName,
        Title = $"Epho {type}",
        Input = agentEvent.Clone(),
        ProviderExecuted = true,
        ProviderMetadata = CreateScopedMetadata(agentEvent)
      }, timestamp, null);
      var failed = string.Equals(GetString(agentEvent, "status"), "failed", StringComparison.OrdinalIgnoreCase);
      if (failed)
        yield return CreateStreamEvent("tool-output-error", id, new AIToolOutputErrorEventData
        {
          ToolCallId = id,
          ErrorText = GetString(agentEvent, "error") ?? $"Epho {type} failed.",
          ProviderExecuted = true,
          ProviderMetadata = CreateScopedMetadata(agentEvent)
        }, timestamp, null);
      else
        yield return CreateStreamEvent("tool-output-available", id, new AIToolOutputAvailableEventData
        {
          ToolName = toolName,
          Output = new CallToolResult { Content = [], StructuredContent = agentEvent.Clone() },
          ProviderExecuted = true,
          ProviderMetadata = CreateScopedMetadata(agentEvent)
        }, timestamp, null);
    }
    else
      yield return CreateStreamEvent($"data-epho-{NormalizeName(type)}", eventId,
          new AIDataEventData { Id = eventId, Data = agentEvent.Clone(), Transient = false }, timestamp, null);
  }

  private static AIToolCallContentPart CreateAgentEventToolPart(JsonElement agentEvent)
  {
    var type = GetString(agentEvent, "type") ?? "event";
    var id = GetString(agentEvent, "id") ?? Guid.NewGuid().ToString("N");
    var toolName = NormalizeName(GetString(agentEvent, "tool") ?? GetString(agentEvent, "command") ?? type);
    var failed = string.Equals(GetString(agentEvent, "status"), "failed", StringComparison.OrdinalIgnoreCase);
    return new AIToolCallContentPart
    {
      Type = "tool-call",
      ToolCallId = $"epho-{toolName}-{id}",
      ToolName = toolName,
      Title = $"Epho {type}",
      Input = agentEvent.Clone(),
      Output = new CallToolResult { Content = [], IsError = failed, StructuredContent = agentEvent.Clone() },
      ProviderExecuted = true,
      State = failed ? "output-error" : "output-available",
      Metadata = CreateRawMetadata(agentEvent)
    };
  }

  private static AIToolCallContentPart CreateErrorToolPart(string? turnId, string error, JsonElement raw)
      => new()
      {
        Type = "tool-call",
        ToolCallId = $"epho-error-{turnId ?? Guid.NewGuid().ToString("N")}",
        ToolName = "epho_error",
        Title = "Epho error",
        Input = new { },
        Output = new CallToolResult { Content = [], IsError = true, StructuredContent = raw.Clone() },
        ProviderExecuted = true,
        State = "output-error",
        Metadata = CreateRawMetadata(raw)
      };

  private async Task<EphoArtifact> DownloadArtifactAsync(
      JsonElement artifact,
      CancellationToken cancellationToken)
  {
    var name = GetString(artifact, "name") ?? "artifact";
    var url = GetString(artifact, "url");
    if (string.IsNullOrWhiteSpace(url))
      throw new InvalidOperationException($"Epho artifact '{name}' did not include a download URL.");

    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    using var response = await _client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);
    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

    if (!response.IsSuccessStatusCode)
      throw new HttpRequestException(
          $"Epho artifact download failed for '{name}' with status {(int)response.StatusCode}.",
          null,
          response.StatusCode);
    if (bytes.Length == 0)
      throw new InvalidOperationException($"Epho artifact download returned an empty file for '{name}'.");

    var mediaType = response.Content.Headers.ContentType?.MediaType;
    if (string.IsNullOrWhiteSpace(mediaType))
      mediaType = GuessMediaType(name);

    return new EphoArtifact(name, url, mediaType, Convert.ToBase64String(bytes), artifact.Clone());
  }

  private static AIFileContentPart CreateArtifactPart(EphoArtifact artifact)
      => new()
      {
        Type = "file",
        Filename = artifact.Name,
        MediaType = artifact.MediaType,
        Data = artifact.Base64,
        Metadata = CreateArtifactMetadata(artifact)
      };

  private static Dictionary<string, object?> CreateArtifactMetadata(EphoArtifact artifact)
      => new()
      {
        ["epho.artifact"] = artifact.Raw.Clone(),
        ["epho.artifact_size"] = GetInt64(artifact.Raw, "size"),
        ["epho.artifact_url"] = artifact.Url,
        ["epho.artifact_media_type"] = artifact.MediaType
      };

  private static Dictionary<string, Dictionary<string, object>> CreateArtifactScopedMetadata(
      EphoArtifact artifact)
      => new()
      {
        ["epho"] = new Dictionary<string, object>
        {
          ["name"] = artifact.Name,
          ["url"] = artifact.Url,
          ["media_type"] = artifact.MediaType,
          ["raw"] = artifact.Raw.Clone()
        }
      };

  private AIStreamEvent CreateStreamEvent(string type, string? id, object data, DateTimeOffset timestamp, Dictionary<string, object?>? metadata)
      => new()
      {
        ProviderId = GetIdentifier(),
        Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data, Metadata = metadata },
        Metadata = metadata
      };

  private static Dictionary<string, object?> CreateResponseMetadata(EphoSnapshot snapshot, EphoRoute route)
      => new()
      {
        ["epho.chat_id"] = snapshot.ChatId,
        ["epho.turn_id"] = snapshot.TurnId,
        ["epho.harness"] = route.Harness,
        ["epho.model"] = route.Model,
        ["epho.done"] = snapshot.Done?.Clone(),
        ["epho.events"] = snapshot.AgentEvents.Select(item => item.Clone()).ToArray()
      };

  private static Dictionary<string, object?> CreateRawMetadata(JsonElement raw)
      => new() { ["epho.raw"] = raw.Clone() };

  private static Dictionary<string, Dictionary<string, object>> CreateScopedMetadata(JsonElement raw)
      => new() { ["epho"] = new Dictionary<string, object> { ["raw"] = raw.Clone() } };

  private static Dictionary<string, object> FlattenMetadata(JsonElement raw)
      => new() { ["epho.raw"] = raw.Clone() };

  private static bool IsToolEvent(string type, JsonElement value)
      => type.Contains("tool", StringComparison.OrdinalIgnoreCase)
         || type.Contains("command", StringComparison.OrdinalIgnoreCase)
         || value.TryGetProperty("tool", out _)
         || value.TryGetProperty("command", out _);

  private static IEnumerable<JsonElement> GetArtifacts(JsonElement frame)
  {
    if (!frame.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
      yield break;
    foreach (var artifact in artifacts.EnumerateArray())
      if (artifact.ValueKind == JsonValueKind.Object) yield return artifact.Clone();
  }

  private static async IAsyncEnumerable<JsonElement> ReadEphoSseAsync(
      StreamReader reader,
      [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    var data = new StringBuilder();
    while (await reader.ReadLineAsync(cancellationToken) is { } line)
    {
      if (line.Length == 0)
      {
        if (data.Length > 0 && TryParseJson(data.ToString(), out var parsed)) yield return parsed;
        data.Clear();
        continue;
      }
      if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
      {
        if (data.Length > 0) data.Append('\n');
        data.Append(line[5..].TrimStart());
      }
    }
    if (data.Length > 0 && TryParseJson(data.ToString(), out var final)) yield return final;
  }

  private static bool TryParseJson(string value, out JsonElement element)
  {
    try { element = JsonSerializer.Deserialize<JsonElement>(value, EphoJson).Clone(); return true; }
    catch (JsonException) { element = default; return false; }
  }

  private static JsonElement? GetObject(JsonElement element, string name)
      => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value.Clone() : null;

  private static string? GetString(JsonElement element, string name)
      => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

  private static long? GetInt64(JsonElement element, string name)
      => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;

  private static string BuildChatToolCallId(string chatId) => $"epho-create-chat-{NormalizeName(chatId)}";
  private static string NormalizeName(string value)
  {
    var chars = value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? char.ToLowerInvariant(character) : '-').ToArray();
    return new string(chars).Trim('-');
  }

  private static string NormalizeStatus(string status, string? error)
      => !string.IsNullOrWhiteSpace(error) || status is "failed" or "error" ? "failed" : status is "canceled" or "cancelled" ? "cancelled" : "completed";
  private static string NormalizeFinishReason(string status, string? error)
      => !string.IsNullOrWhiteSpace(error) || status is "failed" or "error" ? "error" : status is "canceled" or "cancelled" ? "cancelled" : "stop";

  private static string GuessMediaType(string? filename)
      => Path.GetExtension(filename ?? string.Empty).ToLowerInvariant() switch
      {
        ".json" => "application/json",
        ".md" => "text/markdown",
        ".txt" or ".log" => "text/plain",
        ".csv" => "text/csv",
        ".html" => "text/html",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
      };

  private sealed record EphoRoute(string Harness, string Model);
  private sealed record EphoArtifact(
      string Name,
      string Url,
      string MediaType,
      string Base64,
      JsonElement Raw);
  private sealed class EphoSnapshot
  {
    public string? ChatId { get; set; }
    public string? TurnId { get; set; }
    public JsonElement ChatFrame { get; set; }
    public JsonElement? Done { get; set; }
    public List<JsonElement> AgentEvents { get; } = [];
  }

}
