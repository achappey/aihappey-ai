using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.VLMRun;

public partial class VLMRunProvider
{
    private const string VLMRunAgentModelPrefix = "agent/";
    private const string VLMRunAgentListEndpoint = "v1/agent";
    private const string VLMRunAgentExecuteEndpoint = "v1/agent/execute";
    private const string VLMRunAgentExecutionsEndpoint = "v1/agent/executions";
    private const string VLMRunFilesEndpoint = "v1/files";
    private static readonly TimeSpan VLMRunAgentPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan VLMRunAgentPollTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions VLMRunAgentJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private sealed record VLMRunAgentUploadedFile(string FileId, string? Filename, string? PublicUrl);

    private sealed record VLMRunAgentUploadResult(JsonObject Input, VLMRunAgentUploadedFile UploadedFile);

    private sealed record VLMRunAgentFileBytes(byte[] Bytes, string MediaType, string Filename);

    private static bool IsVLMRunAgentModel(string? model)
    {
        var local = NormalizeVLMRunAgentModel(model);
        return local.StartsWith(VLMRunAgentModelPrefix, StringComparison.OrdinalIgnoreCase)
               && local.Length > VLMRunAgentModelPrefix.Length;
    }

    private static string NormalizeVLMRunAgentModel(string? model)
    {
        var local = model?.Trim() ?? string.Empty;
        const string providerPrefix = "vlmrun/";

        if (local.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            local = local[providerPrefix.Length..];

        return local.Trim('/');
    }

    private static string ResolveVLMRunAgentName(AIRequest request, JsonObject payload)
    {
        if (payload.TryGetPropertyValue("name", out var nameNode)
            && nameNode is JsonValue nameValue
            && nameValue.TryGetValue<string>(out var providerName)
            && !string.IsNullOrWhiteSpace(providerName))
        {
            return providerName;
        }

        var local = NormalizeVLMRunAgentModel(request.Model);
        if (!local.StartsWith(VLMRunAgentModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("VLMRun agent target could not be resolved from the model id.");

        var agentName = local[VLMRunAgentModelPrefix.Length..].Trim('/');
        if (string.IsNullOrWhiteSpace(agentName))
            throw new InvalidOperationException("VLMRun agent model id must be in the form 'agent/{agentName}'.");

        return agentName;
    }

    private async Task<AIResponse> ExecuteAgentUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var payload = ExtractVLMRunProviderOptions(request.Metadata) is { } providerOptions
            ? JsonElementObjectToJsonObject(providerOptions)
            : [];

        var agentName = ResolveVLMRunAgentName(request, payload);
        var uploadedFiles = new List<VLMRunAgentUploadedFile>();
        var deletedFileIds = new List<string>();
        JsonElement finalRoot;

        try
        {
            payload["name"] = agentName;

            if (!payload.ContainsKey("batch"))
                payload["batch"] = true;

            var inputs = ExtractVLMRunAgentInputsPayload(payload);
            payload["inputs"] = await BuildVLMRunAgentInputsAsync(request, inputs, uploadedFiles, cancellationToken);

            if (request.Id is { Length: > 0 } requestId && !payload.ContainsKey("id"))
                payload["id"] = requestId;

            if (request.MaxOutputTokens is not null && !payload.ContainsKey("max_output_tokens"))
                payload["max_output_tokens"] = request.MaxOutputTokens.Value;

            using var httpRequest = CreateVLMRunAgentJsonRequest(HttpMethod.Post, VLMRunAgentExecuteEndpoint, payload);
            using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var root = await ReadVLMRunAgentResponseAsync(response, "VLMRun agent execute", cancellationToken);
            finalRoot = await WaitForVLMRunAgentCompletionAsync(root, cancellationToken);
        }
        finally
        {
            foreach (var uploadedFile in uploadedFiles)
            {
                if (string.IsNullOrWhiteSpace(uploadedFile.FileId))
                    continue;

                try
                {
                    await DeleteVLMRunUploadedFileAsync(uploadedFile.FileId, cancellationToken);
                    deletedFileIds.Add(uploadedFile.FileId);
                }
                catch
                {
                    // Best-effort cleanup: the response metadata records deletions that completed before return.
                }
            }
        }

        return CreateVLMRunAgentUnifiedResponse(request, agentName, payload, finalRoot, uploadedFiles, deletedFileIds);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamAgentUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var eventId = request.Id ?? $"vlmrun_agent_{Guid.NewGuid():N}";
        AIResponse response;
        Exception? error = null;

        try
        {
            response = await ExecuteAgentUnifiedAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex;
            response = null!;
        }

        if (error is not null)
        {
            yield return CreateVLMRunAgentStreamEvent(
                eventId,
                "error",
                new AIErrorEventData { ErrorText = error.Message },
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?> { ["vlmrun.agent.error"] = error.Message });

            yield return CreateVLMRunAgentStreamEvent(
                eventId,
                "finish",
                new AIFinishEventData
                {
                    FinishReason = "error",
                    Model = request.Model,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        request.Model ?? "vlmrun",
                        DateTimeOffset.UtcNow,
                        additionalProperties: new Dictionary<string, object?> { ["vlmrun"] = error.Message })
                },
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?> { ["vlmrun.agent.error"] = error.Message });

            yield break;
        }

        var metadata = response.Metadata ?? [];
        var text = ExtractVLMRunAgentResponseText(response);
        var providerMetadata = CreateVLMRunAgentLooseProviderMetadata(response);

        if (!string.IsNullOrEmpty(text))
        {
            yield return CreateVLMRunAgentStreamEvent(
                eventId,
                "text-start",
                new AITextStartEventData { ProviderMetadata = providerMetadata },
                timestamp,
                metadata);

            yield return CreateVLMRunAgentStreamEvent(
                eventId,
                "text-delta",
                new AITextDeltaEventData { Delta = text, ProviderMetadata = providerMetadata },
                DateTimeOffset.UtcNow,
                metadata);

            yield return CreateVLMRunAgentStreamEvent(
                eventId,
                "text-end",
                new AITextEndEventData { ProviderMetadata = providerMetadata },
                DateTimeOffset.UtcNow,
                metadata);
        }

        foreach (var dataEvent in CreateVLMRunAgentArtifactDataEvents(eventId, response, metadata))
            yield return dataEvent;

        yield return CreateVLMRunAgentFinishEvent(eventId, request, response, DateTimeOffset.UtcNow, metadata);
    }

    private async Task<JsonObject> BuildVLMRunAgentInputsAsync(
        AIRequest request,
        JsonObject inputs,
        List<VLMRunAgentUploadedFile> uploadedFiles,
        CancellationToken cancellationToken)
    {
        var instructionText = BuildVLMRunAgentInstructionText(request);
        if (!string.IsNullOrWhiteSpace(instructionText) && !inputs.ContainsKey("instruction"))
        {
            inputs["instruction"] = new JsonObject
            {
                ["type"] = "text",
                ["text"] = instructionText
            };
        }

        var fileIndex = 0;
        foreach (var file in EnumerateVLMRunAgentUserFiles(request))
        {
            var upload = await UploadVLMRunAgentFileAsync(file, cancellationToken);
            uploadedFiles.Add(upload.UploadedFile);
            AddVLMRunAgentInput(inputs, fileIndex == 0 ? "file" : $"file_{fileIndex + 1}", upload.Input);
            fileIndex++;
        }

        if (inputs.Count == 0)
            throw new ArgumentException("VLMRun agent requests require input text or at least one attachment.", nameof(request));

        return inputs;
    }

    private static JsonObject ExtractVLMRunAgentInputsPayload(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("inputs", out var inputsNode) || inputsNode is null)
            return [];

        if (inputsNode is JsonObject inputsObject)
        {
            payload.Remove("inputs");
            return inputsObject;
        }

        return JsonNode.Parse(inputsNode.ToJsonString(VLMRunAgentJson)) as JsonObject ?? [];
    }

    private static void AddVLMRunAgentInput(JsonObject inputs, string preferredKey, JsonObject value)
    {
        var key = preferredKey;
        var suffix = 2;
        while (inputs.ContainsKey(key))
        {
            key = $"{preferredKey}_{suffix.ToString(CultureInfo.InvariantCulture)}";
            suffix++;
        }

        inputs[key] = value;
    }

    private async Task<VLMRunAgentUploadResult> UploadVLMRunAgentFileAsync(VLMRunAgentFileBytes file, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(file.Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(file.MediaType);
        form.Add(content, "file", file.Filename);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{VLMRunFilesEndpoint}?purpose=assistants&generate_public_url=true")
        {
            Content = form
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var root = await ReadVLMRunAgentResponseAsync(response, "VLMRun file upload", cancellationToken);

        var fileId = TryGetVLMRunAgentString(root, "id")
                     ?? throw new InvalidOperationException("VLMRun file upload response did not include an id.");

        return new VLMRunAgentUploadResult(
            new JsonObject
            {
                ["type"] = "input_file",
                ["file_id"] = fileId
            },
            new VLMRunAgentUploadedFile(
                fileId,
                TryGetVLMRunAgentString(root, "filename") ?? file.Filename,
                TryGetVLMRunAgentString(root, "public_url")));
    }

    private async Task DeleteVLMRunUploadedFileAsync(string fileId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{VLMRunFilesEndpoint}/{Uri.EscapeDataString(fileId)}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"VLMRun file delete failed ({(int)response.StatusCode}): {body}");
        }
    }

    private HttpRequestMessage CreateVLMRunAgentJsonRequest(HttpMethod method, string url, JsonObject payload)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(payload.ToJsonString(VLMRunAgentJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        return request;
    }

    private async Task<JsonElement> ReadVLMRunAgentResponseAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body)
                ? $"{operation} failed ({(int)response.StatusCode})."
                : $"{operation} failed ({(int)response.StatusCode}): {body}");

        if (string.IsNullOrWhiteSpace(body))
            return JsonSerializer.SerializeToElement(new { }, VLMRunAgentJson).Clone();

        return JsonSerializer.Deserialize<JsonElement>(body, VLMRunAgentJson).Clone();
    }

    private async Task<JsonElement> WaitForVLMRunAgentCompletionAsync(JsonElement initialRoot, CancellationToken cancellationToken)
    {
        var current = initialRoot.Clone();
        var started = DateTimeOffset.UtcNow;

        while (IsVLMRunAgentRunningStatus(TryGetVLMRunAgentString(current, "status")))
        {
            var executionId = TryGetVLMRunAgentString(current, "id");
            if (string.IsNullOrWhiteSpace(executionId))
                break;

            if (DateTimeOffset.UtcNow - started > VLMRunAgentPollTimeout)
                throw new TimeoutException($"VLMRun agent execution '{executionId}' did not complete within {VLMRunAgentPollTimeout}.");

            await Task.Delay(VLMRunAgentPollInterval, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{VLMRunAgentExecutionsEndpoint}/{Uri.EscapeDataString(executionId)}");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            current = await ReadVLMRunAgentResponseAsync(response, "VLMRun agent execution poll", cancellationToken);
        }

        return current;
    }

    private AIResponse CreateVLMRunAgentUnifiedResponse(
        AIRequest request,
        string agentName,
        JsonObject payload,
        JsonElement root,
        List<VLMRunAgentUploadedFile> uploadedFiles,
        List<string> deletedFileIds)
    {
        var status = TryGetVLMRunAgentString(root, "status") ?? "completed";
        var responseValue = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var responseElement)
            ? responseElement.Clone()
            : root.Clone();
        var text = ExtractVLMRunAgentResponseText(responseValue);
        var usage = CloneProperty(root, "usage");
        var metadata = CreateVLMRunAgentMetadata(request, agentName, payload, root, uploadedFiles, deletedFileIds);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model?.StartsWith(GetIdentifier() + "/", StringComparison.OrdinalIgnoreCase) == true
                ? request.Model
                : $"{VLMRunAgentModelPrefix}{agentName}".ToModelId(GetIdentifier()),
            Status = NormalizeVLMRunAgentResponseStatus(status),
            Usage = usage,
            Metadata = metadata,
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = text,
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["vlmrun.agent.raw_response"] = responseValue.Clone()
                                }
                            }
                        ],
                        Metadata = new Dictionary<string, object?>
                        {
                            ["vlmrun.agent.raw_response"] = responseValue.Clone()
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["vlmrun.agent.raw"] = root.Clone(),
                    ["vlmrun.agent.response"] = responseValue.Clone()
                }
            }
        };
    }

    private Dictionary<string, object?> CreateVLMRunAgentMetadata(
        AIRequest request,
        string agentName,
        JsonObject payload,
        JsonElement root,
        List<VLMRunAgentUploadedFile> uploadedFiles,
        List<string> deletedFileIds)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["vlmrun.agent"] = true,
            ["vlmrun.agent.name"] = agentName,
            ["vlmrun.agent.model"] = request.Model ?? $"{VLMRunAgentModelPrefix}{agentName}",
            ["vlmrun.agent.execution_id"] = TryGetVLMRunAgentString(root, "id"),
            ["vlmrun.agent.status"] = TryGetVLMRunAgentString(root, "status"),
            ["vlmrun.agent.request.payload"] = JsonSerializer.SerializeToElement(payload, VLMRunAgentJson),
            ["vlmrun.agent.raw"] = root.Clone(),
            ["vlmrun.agent.response"] = CloneProperty(root, "response"),
            ["vlmrun.agent.usage"] = CloneProperty(root, "usage"),
            ["vlmrun.agent.uploaded_files"] = uploadedFiles.Select(file => new { file.FileId, file.Filename, file.PublicUrl }).ToList(),
            ["vlmrun.agent.deleted_file_ids"] = deletedFileIds.ToList()
        };

        if (ExtractVLMRunAgentArtifacts(root) is { Count: > 0 } artifacts)
            metadata["vlmrun.agent.artifacts"] = artifacts;

        return metadata;
    }

    private static IEnumerable<VLMRunAgentFileBytes> EnumerateVLMRunAgentUserFiles(AIRequest request)
    {
        foreach (var item in request.Input?.Items ?? [])
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var file in item.Content?.OfType<AIFileContentPart>() ?? [])
            {
                if (TryCreateVLMRunAgentFileBytes(file, out var bytes))
                    yield return bytes;
            }
        }
    }

    private static bool TryCreateVLMRunAgentFileBytes(AIFileContentPart file, out VLMRunAgentFileBytes result)
    {
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Application.Octet : file.MediaType!;
        var filename = string.IsNullOrWhiteSpace(file.Filename) ? $"attachment{GuessVLMRunAgentFileExtension(mediaType)}" : file.Filename!;
        var data = file.Data;

        switch (data)
        {
            case byte[] bytes:
                result = new VLMRunAgentFileBytes(bytes, mediaType, filename);
                return true;
            case BinaryData binaryData:
                result = new VLMRunAgentFileBytes(binaryData.ToArray(), mediaType, filename);
                return true;
            case JsonElement json:
                return TryCreateVLMRunAgentFileBytesFromString(json.ValueKind == JsonValueKind.String ? json.GetString() : json.GetRawText(), mediaType, filename, out result);
            case string text:
                return TryCreateVLMRunAgentFileBytesFromString(text, mediaType, filename, out result);
            case not null:
                result = new VLMRunAgentFileBytes(JsonSerializer.SerializeToUtf8Bytes(data, VLMRunAgentJson), MediaTypeNames.Application.Json, filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? filename : filename + ".json");
                return true;
            default:
                result = default!;
                return false;
        }
    }

    private static bool TryCreateVLMRunAgentFileBytesFromString(string? value, string mediaType, string filename, out VLMRunAgentFileBytes result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default!;
            return false;
        }

        var trimmed = value.Trim();
        if (TryDecodeVLMRunDataUrl(trimmed, out var dataUrlBytes, out var dataUrlMediaType))
        {
            result = new VLMRunAgentFileBytes(dataUrlBytes, dataUrlMediaType ?? mediaType, filename);
            return true;
        }

        var base64 = StripVLMRunAgentBase64Prefix(trimmed);
        if (TryDecodeVLMRunBase64(base64, out var bytes))
        {
            result = new VLMRunAgentFileBytes(bytes, mediaType, filename);
            return true;
        }

        // The app should provide attachments, not URL references. Persist URL-looking values as a small text file instead of forwarding URLs.
        result = new VLMRunAgentFileBytes(Encoding.UTF8.GetBytes(trimmed), MediaTypeNames.Text.Plain, filename.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? filename : filename + ".txt");
        return true;
    }

    private static string BuildVLMRunAgentInstructionText(AIRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            parts.Add(request.Instructions!);

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            parts.Add(request.Input!.Text!);

        foreach (var item in request.Input?.Items ?? [])
        {
            var text = ExtractVLMRunAgentText(item.Content);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var role = string.IsNullOrWhiteSpace(item.Role) ? "user" : item.Role;
                parts.Add($"{role}: {text}");
            }
        }

        return string.Join("\n\n", parts.Distinct(StringComparer.Ordinal));
    }

    private static string ExtractVLMRunAgentText(IEnumerable<AIContentPart>? parts)
        => string.Join("\n", (parts ?? [])
            .Select(part => part switch
            {
                AITextContentPart text => text.Text,
                AIReasoningContentPart reasoning => reasoning.Text,
                _ => null
            })
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static JsonElement? ExtractVLMRunProviderOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("vlmrun", out var raw) || raw is null)
            return null;

        var element = raw switch
        {
            JsonElement json => json.Clone(),
            JsonObject jsonObject => JsonSerializer.SerializeToElement(jsonObject, VLMRunAgentJson),
            Dictionary<string, object?> dictionary => JsonSerializer.SerializeToElement(dictionary, VLMRunAgentJson),
            _ => JsonSerializer.SerializeToElement(raw, VLMRunAgentJson)
        };

        return element.ValueKind == JsonValueKind.Object ? element : null;
    }

    private static JsonObject JsonElementObjectToJsonObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        return JsonNode.Parse(element.GetRawText()) as JsonObject ?? [];
    }

    private static AIStreamEvent CreateVLMRunAgentStreamEvent(string id, string type, object data, DateTimeOffset timestamp, Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = "vlmrun",
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateVLMRunAgentFinishEvent(string eventId, AIRequest request, AIResponse response, DateTimeOffset timestamp, Dictionary<string, object?> metadata)
    {
        var totalTokens = ExtractVLMRunAgentTotalTokens(response.Usage);
        var model = response.Model ?? request.Model ?? "vlmrun";
        var failed = string.Equals(response.Status, "failed", StringComparison.OrdinalIgnoreCase);

        return CreateVLMRunAgentStreamEvent(
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = failed ? "error" : "stop",
                Model = model,
                TotalTokens = totalTokens,
                MessageMetadata = AIFinishMessageMetadata.Create(
                    model,
                    timestamp,
                    response.Usage,
                    totalTokens: totalTokens,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        ["vlmrun"] = metadata.TryGetValue("vlmrun.agent.raw", out var raw) ? raw : null
                    })
            },
            timestamp,
            metadata);
    }

    private static IEnumerable<AIStreamEvent> CreateVLMRunAgentArtifactDataEvents(string eventId, AIResponse response, Dictionary<string, object?> metadata)
    {
        if (response.Metadata?.TryGetValue("vlmrun.agent.artifacts", out var artifacts) != true || artifacts is not List<Dictionary<string, object?>> list)
            yield break;

        for (var i = 0; i < list.Count; i++)
        {
            yield return CreateVLMRunAgentStreamEvent(
                $"{eventId}:artifact:{i}",
                "data-vlmrun.artifact",
                new AIDataEventData
                {
                    Id = $"{eventId}:artifact:{i}",
                    Data = list[i]
                },
                DateTimeOffset.UtcNow,
                metadata);
        }
    }

    private static Dictionary<string, object>? CreateVLMRunAgentLooseProviderMetadata(AIResponse response)
    {
        var metadata = new Dictionary<string, object>();

        if (response.Metadata?.TryGetValue("vlmrun.agent.raw", out var raw) == true && raw is not null)
            metadata["raw"] = raw;

        if (response.Metadata?.TryGetValue("vlmrun.agent.execution_id", out var executionId) == true && executionId is not null)
            metadata["execution_id"] = executionId;

        if (response.Usage is not null)
            metadata["usage"] = response.Usage;

        return metadata.Count == 0 ? null : metadata;
    }

    private static string ExtractVLMRunAgentResponseText(AIResponse response)
        => string.Join("\n", (response.Output?.Items ?? [])
            .Where(item => string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static string ExtractVLMRunAgentResponseText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            JsonValueKind.Object => TryGetVLMRunAgentString(element, "text")
                                    ?? TryGetVLMRunAgentString(element, "message")
                                    ?? TryGetVLMRunAgentString(element, "output")
                                    ?? TryGetVLMRunAgentString(element, "answer")
                                    ?? element.GetRawText(),
            JsonValueKind.Array => element.GetRawText(),
            _ => string.Empty
        };
    }

    private static List<Dictionary<string, object?>> ExtractVLMRunAgentArtifacts(JsonElement root)
    {
        var artifacts = new List<Dictionary<string, object?>>();
        CollectVLMRunAgentArtifactRefs(root, artifacts);
        return artifacts;
    }

    private static void CollectVLMRunAgentArtifactRefs(JsonElement element, List<Dictionary<string, object?>> artifacts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetVLMRunAgentString(element, "object_id") is { Length: > 0 } objectId
                    || TryGetVLMRunArtifactLikeString(element, out objectId))
                {
                    artifacts.Add(new Dictionary<string, object?>
                    {
                        ["object_id"] = objectId,
                        ["execution_id"] = TryGetVLMRunAgentString(element, "execution_id"),
                        ["raw"] = element.Clone()
                    });
                }

                foreach (var property in element.EnumerateObject())
                    CollectVLMRunAgentArtifactRefs(property.Value, artifacts);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectVLMRunAgentArtifactRefs(item, artifacts);
                break;
        }
    }

    private static bool TryGetVLMRunArtifactLikeString(JsonElement element, out string objectId)
    {
        objectId = string.Empty;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var value = property.Value.GetString();
            if (IsVLMRunArtifactObjectId(value))
            {
                objectId = value!;
                return true;
            }
        }

        return false;
    }

    private static bool IsVLMRunArtifactObjectId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.StartsWith("img_", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("vid_", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("aud_", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("doc_", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("recon_", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("url_", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("arr_", StringComparison.OrdinalIgnoreCase));

    private static bool IsVLMRunAgentRunningStatus(string? status)
        => string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "enqueued", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVLMRunAgentResponseStatus(string? status)
        => string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "failed" : "completed";

    private static string? TryGetVLMRunAgentString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }

    private static JsonElement? CloneProperty(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var property)
            ? property.Clone()
            : null;

    private static int? ExtractVLMRunAgentTotalTokens(object? usage)
    {
        if (usage is not JsonElement json || json.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "total_tokens", "totalTokens", "credits_used", "steps" })
        {
            if (json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
                return intValue;
        }

        return null;
    }

    private static bool TryDecodeVLMRunDataUrl(string value, out byte[] bytes, out string? mediaType)
    {
        bytes = [];
        mediaType = null;

        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var commaIndex = value.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
            return false;

        var header = value[5..commaIndex];
        mediaType = header.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return TryDecodeVLMRunBase64(value[(commaIndex + 1)..], out bytes);
    }

    private static string StripVLMRunAgentBase64Prefix(string value)
    {
        var commaIndex = value.IndexOf(',', StringComparison.Ordinal);
        return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0
            ? value[(commaIndex + 1)..]
            : value;
    }

    private static bool TryDecodeVLMRunBase64(string value, out byte[] bytes)
    {
        bytes = [];
        try
        {
            bytes = Convert.FromBase64String(value);
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GuessVLMRunAgentFileExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "video/mp4" => ".mp4",
            "text/plain" => ".txt",
            "application/json" => ".json",
            _ => ".bin"
        };
}
