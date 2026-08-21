using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Writer;

public partial class WriterProvider
{
    private const string AgentToolName = "writer_agent_job";
    private static readonly JsonSerializerOptions WriterJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private async Task<List<WriterApplication>> GetAllWriterApplicationsAsync(CancellationToken cancellationToken)
        => await GetAllWriterPagesAsync<WriterApplication>("v1/applications?limit=100&type=generation", cancellationToken);

    private async Task<List<WriterGraph>> GetAllWriterGraphsAsync(CancellationToken cancellationToken)
        => await GetAllWriterPagesAsync<WriterGraph>("v1/graphs?limit=100", cancellationToken);

    private async Task<List<T>> GetAllWriterPagesAsync<T>(string firstUri, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        var uri = firstUri;
        while (true)
        {
            using var response = await _client.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await CreateWriterApiExceptionAsync(response, $"GET {uri}", cancellationToken);
            var page = await response.Content.ReadFromJsonAsync<WriterPage<T>>(WriterJson, cancellationToken)
                       ?? new WriterPage<T>();
            result.AddRange(page.Data);
            if (!page.HasMore || string.IsNullOrWhiteSpace(page.LastId))
                return result;
            var separator = firstUri.Contains('?') ? '&' : '?';
            uri = firstUri + separator + "after=" + Uri.EscapeDataString(page.LastId);
        }
    }

    private async Task<AIResponse> ExecuteWriterNativeAsync(AIRequest request, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        if (IsWriterAgentModel(request.Model))
            return await ExecuteWriterAgentAsync(request, cancellationToken);
        return await ExecuteWriterKnowledgeGraphAsync(request, stream: false, cancellationToken);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamWriterNativeAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        if (IsWriterAgentModel(request.Model))
        {
            await foreach (var item in StreamWriterAgentAsync(request, cancellationToken))
                yield return item;
            yield break;
        }

        await foreach (var item in StreamWriterKnowledgeGraphAsync(request, cancellationToken))
            yield return item;
    }

    private async Task<AIResponse> ExecuteWriterAgentAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var descriptor = await ResolveWriterResourceAsync(request.Model, cancellationToken)
                         ?? throw new ArgumentException($"Unknown Writer agent model '{request.Model}'.", nameof(request));
        var options = request.Metadata.GetProviderMetadata<WriterProviderMetadata>(GetIdentifier());
        var temporaryFiles = new List<string>();
        try
        {
            var inputs = await BuildWriterAgentInputsAsync(request, descriptor, options, temporaryFiles, cancellationToken);
            var created = await CreateWriterAgentJobAsync(descriptor.Id, inputs, cancellationToken);
            var completed = await PollWriterAgentJobAsync(created, options, null, cancellationToken);
            return CreateWriterTextResponse(request, completed.Data?.Suggestion ?? string.Empty,
                new Dictionary<string, object?>
                {
                    ["writer.kind"] = "agent",
                    ["writer.application_id"] = descriptor.Id,
                    ["writer.job"] = completed
                });
        }
        finally
        {
            await DeleteTemporaryWriterFilesAsync(temporaryFiles);
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamWriterAgentAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var descriptor = await ResolveWriterResourceAsync(request.Model, cancellationToken)
                         ?? throw new ArgumentException($"Unknown Writer agent model '{request.Model}'.", nameof(request));
        var options = request.Metadata.GetProviderMetadata<WriterProviderMetadata>(GetIdentifier());
        var temporaryFiles = new List<string>();
        var eventId = request.Id ?? Guid.NewGuid().ToString("N");
        var toolCallId = $"writer-agent-{eventId}";
        WriterApplicationJob? completed = null;
        try
        {
            var inputs = await BuildWriterAgentInputsAsync(request, descriptor, options, temporaryFiles, cancellationToken);
            yield return CreateWriterEvent(toolCallId, "tool-input-available", new AIToolInputAvailableEventData
            {
                ToolName = AgentToolName,
                Title = descriptor.Name,
                Input = new { application_id = descriptor.Id, inputs },
                ProviderExecuted = true,
                ProviderMetadata = WriterProviderEnvelope(new { application_id = descriptor.Id, status = "creating" })
            });

            var created = await CreateWriterAgentJobAsync(descriptor.Id, inputs, cancellationToken);
            yield return CreateWriterAgentStatusEvent(toolCallId, descriptor, created, preliminary: true);

            await foreach (var status in PollWriterAgentJobEventsAsync(created, options, cancellationToken))
            {
                completed = status;
                yield return CreateWriterAgentStatusEvent(toolCallId, descriptor, status,
                    preliminary: !IsWriterJobTerminal(status.Status));
            }

            if (completed is null)
                throw new InvalidOperationException("Writer agent polling ended without a terminal job response.");
            if (!string.Equals(completed.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                yield return CreateWriterEvent(toolCallId, "tool-output-error", new AIToolOutputErrorEventData
                {
                    ToolCallId = toolCallId,
                    ErrorText = completed.Error ?? $"Writer job failed with status '{completed.Status}'.",
                    ProviderExecuted = true,
                    ProviderMetadata = WriterProviderEnvelope(completed)
                });
                yield break;
            }

            foreach (var textEvent in CreateWriterTextEvents(eventId, request.Model, completed.Data?.Suggestion ?? string.Empty,
                         new Dictionary<string, object?> { ["writer.job"] = completed }))
                yield return textEvent;
        }
        finally
        {
            await DeleteTemporaryWriterFilesAsync(temporaryFiles);
        }
    }

    private async Task<List<object>> BuildWriterAgentInputsAsync(
        AIRequest request,
        WriterResourceDescriptor descriptor,
        WriterProviderMetadata? options,
        List<string> temporaryFiles,
        CancellationToken cancellationToken)
    {
        var application = descriptor.Application;
        if (application is null)
        {
            var all = await GetAllWriterApplicationsAsync(cancellationToken);
            application = all.FirstOrDefault(item => item.Id.Equals(descriptor.Id, StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidOperationException($"Writer application '{descriptor.Id}' was not found.");
        }

        var explicitInputs = options?.Inputs ?? new Dictionary<string, IReadOnlyList<string>>();
        var prompt = GetWriterPrompt(request);
        var files = EnumerateWriterFiles(request).ToArray();
        var preparedFiles = new List<WriterPreparedFile>();
        foreach (var file in files)
        {
            var prepared = await PrepareWriterFileAsync(file, cancellationToken);
            if (prepared is null)
                continue;
            preparedFiles.Add(prepared);
            if (prepared.Temporary)
                temporaryFiles.Add(prepared.Id);
        }

        var result = new List<object>();
        var promptAssigned = false;
        var filesAssigned = false;
        foreach (var input in application.Inputs)
        {
            IReadOnlyList<string>? values = explicitInputs.FirstOrDefault(pair =>
                pair.Key.Equals(input.Name, StringComparison.OrdinalIgnoreCase)).Value;
            if (values is null && input.InputType is "file" or "media" && !filesAssigned && preparedFiles.Count > 0)
            {
                values = preparedFiles.Select(item => item.Id).ToArray();
                filesAssigned = true;
            }
            if (values is null && input.InputType is "text" or "dropdown" && !promptAssigned && !string.IsNullOrWhiteSpace(prompt))
            {
                values = [prompt];
                promptAssigned = true;
            }
            if ((values is null || values.Count == 0) && input.Required)
                throw new ArgumentException($"Writer agent '{application.Name}' requires input '{input.Name}' ({input.InputType}). " +
                                            "Attach suitable unified content or set optional writer.inputs provider options.");
            if (values is { Count: > 0 })
                result.Add(new { id = input.Name, value = values });
        }
        return result;
    }

    private async Task<WriterApplicationJob> CreateWriterAgentJobAsync(
        string applicationId,
        List<object> inputs,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            $"v1/applications/{Uri.EscapeDataString(applicationId)}/jobs", new { inputs }, WriterJson, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateWriterApiExceptionAsync(response, "create application job", cancellationToken);
        return await response.Content.ReadFromJsonAsync<WriterApplicationJob>(WriterJson, cancellationToken)
               ?? throw new InvalidOperationException("Writer returned an empty application job.");
    }

    private async Task<WriterApplicationJob> PollWriterAgentJobAsync(
        WriterApplicationJob created,
        WriterProviderMetadata? options,
        Func<WriterApplicationJob, Task>? onStatus,
        CancellationToken cancellationToken)
    {
        WriterApplicationJob? last = null;
        await foreach (var status in PollWriterAgentJobEventsAsync(created, options, cancellationToken))
        {
            last = status;
            if (onStatus is not null)
                await onStatus(status);
        }
        return last ?? created;
    }

    private async IAsyncEnumerable<WriterApplicationJob> PollWriterAgentJobEventsAsync(
        WriterApplicationJob created,
        WriterProviderMetadata? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(options?.PollIntervalMs ?? 1000, 250, 30000));
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options?.PollTimeoutSeconds ?? 600, 5, 3600));
        var started = DateTimeOffset.UtcNow;
        var previous = created.Status;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow - started > timeout)
                throw new TimeoutException($"Writer application job '{created.Id}' did not finish within {timeout}.");
            await Task.Delay(interval, cancellationToken);
            using var response = await _client.GetAsync($"v1/applications/jobs/{Uri.EscapeDataString(created.Id)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await CreateWriterApiExceptionAsync(response, "retrieve application job", cancellationToken);
            var current = await response.Content.ReadFromJsonAsync<WriterApplicationJob>(WriterJson, cancellationToken)
                          ?? throw new InvalidOperationException("Writer returned an empty application job status.");
            if (!current.Status.Equals(previous, StringComparison.OrdinalIgnoreCase) || IsWriterJobTerminal(current.Status))
                yield return current;
            previous = current.Status;
            if (IsWriterJobTerminal(current.Status))
                yield break;
        }
    }

    private async Task<AIResponse> ExecuteWriterKnowledgeGraphAsync(
        AIRequest request,
        bool stream,
        CancellationToken cancellationToken)
    {
        var (payload, metadata) = await BuildWriterGraphRequestAsync(request, stream, cancellationToken);
        using var response = await _client.PostAsJsonAsync("v1/graphs/question", payload, WriterJson, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateWriterApiExceptionAsync(response, "question Knowledge Graph", cancellationToken);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>(WriterJson, cancellationToken);
        var text = ReadString(root, "answer") ?? string.Empty;
        metadata["writer.response"] = root.Clone();
        return CreateWriterTextResponse(request, text, metadata);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamWriterKnowledgeGraphAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (payload, metadata) = await BuildWriterGraphRequestAsync(request, stream: true, cancellationToken);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/graphs/question")
        {
            Content = JsonContent.Create(payload, options: WriterJson)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateWriterApiExceptionAsync(response, "stream Knowledge Graph question", cancellationToken);

        var eventId = request.Id ?? Guid.NewGuid().ToString("N");
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var root = await response.Content.ReadFromJsonAsync<JsonElement>(WriterJson, cancellationToken);
            metadata["writer.response"] = root.Clone();
            foreach (var item in CreateWriterTextEvents(eventId, request.Model, ReadString(root, "answer") ?? string.Empty, metadata))
                yield return item;
            yield break;
        }

        yield return CreateWriterEvent(eventId, "text-start", new AITextStartEventData
        {
            ProviderMetadata = new Dictionary<string, object> { ["writer"] = metadata }
        }, metadata);
        var assembled = new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            var json = line[5..].Trim();
            if (json.Length == 0 || json == "[DONE]")
                continue;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("data", out var nested))
                root = nested;
            var answer = ReadString(root, "answer");
            if (string.IsNullOrEmpty(answer))
                continue;
            var delta = answer.StartsWith(assembled.ToString(), StringComparison.Ordinal)
                ? answer[assembled.Length..]
                : answer;
            assembled.Append(delta);
            if (delta.Length > 0)
                yield return CreateWriterEvent(eventId, "text-delta", new AITextDeltaEventData
                {
                    Delta = delta,
                    ProviderMetadata = new Dictionary<string, object> { ["writer"] = root.Clone() }
                }, metadata);
        }
        yield return CreateWriterEvent(eventId, "text-end", new AITextEndEventData(), metadata);
        yield return CreateWriterFinishEvent(eventId, request.Model, metadata);
    }

    private async Task<(object Payload, Dictionary<string, object?> Metadata)> BuildWriterGraphRequestAsync(
        AIRequest request,
        bool stream,
        CancellationToken cancellationToken)
    {
        var options = request.Metadata.GetProviderMetadata<WriterProviderMetadata>(GetIdentifier());
        var descriptor = NormalizeWriterModel(request.Model).Equals("knowledge-graph", StringComparison.OrdinalIgnoreCase)
            ? null
            : await ResolveWriterResourceAsync(request.Model, cancellationToken);
        var graphIds = options?.GraphIds?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray()
                       ?? (descriptor is null ? [] : [descriptor.Id]);
        if (graphIds.Length == 0)
            throw new ArgumentException("Writer Knowledge Graph requests require a graph-specific model or writer.graph_ids provider options.");
        var question = GetWriterPrompt(request);
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Writer Knowledge Graph requests require unified input text.", nameof(request));
        var payload = new
        {
            graph_ids = graphIds,
            question,
            stream,
            subqueries = options?.Subqueries,
            query_config = options?.QueryConfig
        };
        return (payload, new Dictionary<string, object?>
        {
            ["writer.kind"] = "knowledge-graph",
            ["writer.graph_ids"] = graphIds,
            ["writer.question"] = question
        });
    }

    private async Task<WriterPreparedFile?> PrepareWriterFileAsync(AIFileContentPart file, CancellationToken cancellationToken)
    {
        if (TryReadWriterFileId(file, out var fileId))
            return new WriterPreparedFile(fileId, false);
        var bytes = await ReadWriterFileBytesAsync(file, cancellationToken);
        if (bytes is null || bytes.Length == 0)
            return null;
        var filename = string.IsNullOrWhiteSpace(file.Filename) ? "aihappey-upload.bin" : file.Filename;
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/files")
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(file.MediaType ?? MediaTypeNames.Application.Octet);
        request.Content.Headers.ContentLength = bytes.Length;
        request.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = filename };
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateWriterApiExceptionAsync(response, "upload file", cancellationToken);
        var uploaded = await response.Content.ReadFromJsonAsync<WriterFileResponse>(WriterJson, cancellationToken)
                       ?? throw new InvalidOperationException("Writer returned an empty file upload response.");
        return new WriterPreparedFile(uploaded.Id, true);
    }

    private async Task DeleteTemporaryWriterFilesAsync(IEnumerable<string> ids)
    {
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { using var response = await _client.DeleteAsync($"v1/files/{Uri.EscapeDataString(id)}", CancellationToken.None); }
            catch { /* Cleanup must not hide the generation result/error. */ }
        }
    }

    private async Task<byte[]?> ReadWriterFileBytesAsync(AIFileContentPart file, CancellationToken cancellationToken)
    {
        if (file.Data is byte[] bytes)
            return bytes;
        if (file.Data is BinaryData binary)
            return binary.ToArray();
        var value = file.Data switch
        {
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return await _client.GetByteArrayAsync(uri, cancellationToken);
        var comma = value.IndexOf(',');
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            value = value[(comma + 1)..];
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
    }

    private static bool TryReadWriterFileId(AIFileContentPart file, out string id)
    {
        id = string.Empty;
        if (file.Metadata is null)
            return false;
        foreach (var key in new[] { "file_id", "fileId", "writer.file_id" })
        {
            if (!file.Metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(Convert.ToString(value)))
                continue;
            id = Convert.ToString(value)!;
            return true;
        }
        return false;
    }

    private static IEnumerable<AIFileContentPart> EnumerateWriterFiles(AIRequest request)
        => request.Input?.Items?.SelectMany(item => item.Content ?? []).OfType<AIFileContentPart>() ?? [];

    private static string GetWriterPrompt(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text;
        var messages = request.Input?.Items?.Where(item => item.Role is "user" or null).ToArray() ?? [];
        return string.Join("\n", messages.SelectMany(item => item.Content ?? []).OfType<AITextContentPart>()
            .Select(item => item.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private AIResponse CreateWriterTextResponse(AIRequest request, string text, Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = "completed",
            Metadata = metadata,
            Output = new AIOutput
            {
                Items = [new AIOutputItem { Type = "message", Role = "assistant", Content = [
                    new AITextContentPart { Type = "text", Text = text }] }],
                Metadata = metadata
            }
        };

    private IEnumerable<AIStreamEvent> CreateWriterTextEvents(
        string eventId,
        string? model,
        string text,
        Dictionary<string, object?> metadata)
    {
        yield return CreateWriterEvent(eventId, "text-start", new AITextStartEventData(), metadata);
        if (text.Length > 0)
            yield return CreateWriterEvent(eventId, "text-delta", new AITextDeltaEventData { Delta = text }, metadata);
        yield return CreateWriterEvent(eventId, "text-end", new AITextEndEventData(), metadata);
        yield return CreateWriterFinishEvent(eventId, model, metadata);
    }

    private AIStreamEvent CreateWriterFinishEvent(string eventId, string? model, Dictionary<string, object?> metadata)
        => CreateWriterEvent(eventId, "finish", new AIFinishEventData
        {
            FinishReason = "stop",
            Model = model,
            MessageMetadata = AIFinishMessageMetadata.Create(model ?? "writer", DateTimeOffset.UtcNow,
                additionalProperties: new Dictionary<string, object?> { ["writer"] = metadata })
        }, metadata);

    private AIStreamEvent CreateWriterAgentStatusEvent(
        string toolCallId,
        WriterResourceDescriptor descriptor,
        WriterApplicationJob job,
        bool preliminary)
        => CreateWriterEvent(toolCallId, "tool-output-available", new AIToolOutputAvailableEventData
        {
            ToolName = AgentToolName,
            Output = new { application_id = descriptor.Id, job_id = job.Id, status = job.Status, data = job.Data, error = job.Error },
            ProviderExecuted = true,
            Preliminary = preliminary,
            ProviderMetadata = WriterProviderEnvelope(job)
        });

    private AIStreamEvent CreateWriterEvent(string id, string type, object data, Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Id = id, Type = type, Timestamp = DateTimeOffset.UtcNow, Data = data },
            Metadata = metadata
        };

    private Dictionary<string, Dictionary<string, object>> WriterProviderEnvelope(object value)
        => new() { [GetIdentifier()] = new Dictionary<string, object> { ["data"] = value } };

    private static bool IsWriterJobTerminal(string? status)
        => status is not null && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                                  || status.Equals("failed", StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<Exception> CreateWriterApiExceptionAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new HttpRequestException($"Writer {operation} failed ({(int)response.StatusCode}): {body}", null, response.StatusCode);
    }
}
