using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    private const string BrowserUseV4RunsEndpoint = "api/v4/runs";
    private const string BrowserUseSessionToolName = "browseruse_session";
    private const int BrowserUseMaxUploadFiles = 10;
    private const int BrowserUseMaxAttachedFileIds = 20;
    private const int BrowserUseMaxUploadBytes = 50 * 1024 * 1024;
    private static readonly JsonSerializerOptions BrowserUseV4Json = JsonSerializerOptions.Web;
    private static readonly TimeSpan BrowserUsePollInterval = TimeSpan.FromSeconds(2);

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var turn = await StartBrowserUseTurnAsync(request, cancellationToken);
        try
        {
            var summary = await WaitForBrowserUseRunAsync(turn.RunId, cancellationToken);
            var downloads = await ListBrowserUseDownloadsSafeAsync(summary.SessionId, cancellationToken);
            return CreateBrowserUseUnifiedResponse(request, turn, summary, downloads);
        }
        catch (OperationCanceledException)
        {
            await CancelBrowserUseRunSafeAsync(turn.RunId, CancellationToken.None);
            throw;
        }
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var providerId = GetIdentifier();
        var timestamp = DateTimeOffset.UtcNow;
        var prompt = GetBrowserUseTurnText(request);
        var sessionId = TryFindBrowserUseSessionId(request, out var recoveredSessionId) ? recoveredSessionId : null;
        // The id of a streamed tool invocation is a protocol correlation id, not the BrowserUse
        // session id. It must remain unchanged for the complete input/output lifecycle. The
        // actual session id is persisted in the output and metadata for later continuation.
        var toolCallId = BuildBrowserUseSessionToolCallId(sessionId ?? request.Id ?? Guid.NewGuid().ToString("N"));
        var requestedFiles = GetLatestBrowserUseFileDescriptors(request);
        var input = JsonSerializer.SerializeToElement(new
        {
            task = prompt,
            sessionId,
            continuation = sessionId is not null,
            attachments = requestedFiles
        }, BrowserUseV4Json);

        yield return CreateBrowserUseStreamEvent(providerId, toolCallId, "tool-input-start", new AIToolInputStartEventData
        {
            ToolName = BrowserUseSessionToolName,
            Title = "BrowserUse session",
            ProviderExecuted = true,
            ProviderMetadata = CreateBrowserUseToolProviderMetadata(toolCallId, "tool_use")
        }, timestamp, CreateBrowserUseMetadata(null, null, null));

        yield return CreateBrowserUseStreamEvent(providerId, toolCallId, "tool-input-available", new AIToolInputAvailableEventData
        {
            ToolName = BrowserUseSessionToolName,
            Title = "BrowserUse session",
            Input = input,
            ProviderExecuted = true,
            ProviderMetadata = CreateBrowserUseToolProviderMetadata(toolCallId, "tool_use")
        }, timestamp, CreateBrowserUseMetadata(null, null, null));

        var turn = await StartBrowserUseTurnAsync(request, cancellationToken);

        var seenEventIds = new HashSet<int>();
        var after = 0;
        BrowserUseRunSummary? terminal = null;

        yield return CreateBrowserUseStreamEvent(providerId, toolCallId, "tool-output-available", new AIToolOutputAvailableEventData
        {
            ToolName = BrowserUseSessionToolName,
            Output = CreateBrowserUseSessionToolResult(turn, null),
            ProviderExecuted = true,
            Preliminary = true,
            Dynamic = true,
            ProviderMetadata = CreateBrowserUseToolProviderMetadata(toolCallId, "tool_result")
        }, DateTimeOffset.UtcNow, CreateBrowserUseMetadata(turn, null, null));

        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await GetBrowserUseRunEventsAsync(turn.RunId, after, cancellationToken);
            foreach (var item in page.Events.OrderBy(e => e.Id))
            {
                after = Math.Max(after, item.Id);
                if (!seenEventIds.Add(item.Id))
                    continue;

                // BrowserUse v4 wraps agent activity in core.event payloads. Only expose the
                // user-facing reasoning and actual tool parts; lifecycle events such as
                // state.promoted remain available in provider metadata without becoming cards.
                foreach (var streamEvent in CreateBrowserUseRunEventStreamEvents(providerId, turn, item))
                    yield return streamEvent;
            }

            var status = await GetBrowserUseRunStatusAsync(turn.RunId, cancellationToken);
            if (IsBrowserUseTerminal(status.Status))
            {
                terminal = await GetBrowserUseRunAsync(turn.RunId, cancellationToken);
                break;
            }

            await Task.Delay(BrowserUsePollInterval, cancellationToken);
        }

        if (terminal is null)
            throw new OperationCanceledException(cancellationToken);

        var downloads = await ListBrowserUseDownloadsSafeAsync(terminal.SessionId, cancellationToken);
        var metadata = CreateBrowserUseMetadata(turn, terminal, null);
        var output = CreateBrowserUseSessionToolResult(turn, terminal);
        var failed = !IsBrowserUseSuccessful(terminal);

        if (failed)
        {
            yield return CreateBrowserUseStreamEvent(providerId, toolCallId, "tool-output-error", new AIToolOutputErrorEventData
            {
                ToolCallId = toolCallId,
                ErrorText = terminal.Error ?? $"BrowserUse run ended with status '{terminal.Status}'.",
                ProviderExecuted = true,
                Dynamic = true,
                ProviderMetadata = CreateBrowserUseToolProviderMetadata(toolCallId, "tool_result")
            }, DateTimeOffset.UtcNow, metadata);
        }
        else
        {
            yield return CreateBrowserUseStreamEvent(providerId, toolCallId, "tool-output-available", new AIToolOutputAvailableEventData
            {
                ToolName = BrowserUseSessionToolName,
                Output = output,
                ProviderExecuted = true,
                Dynamic = true,
                Preliminary = false,
                ProviderMetadata = CreateBrowserUseToolProviderMetadata(toolCallId, "tool_result")
            }, DateTimeOffset.UtcNow, metadata);
        }

        var text = terminal.Result ?? terminal.Error ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return CreateBrowserUseStreamEvent(providerId, turn.RunId, "text-start", new AITextStartEventData(), DateTimeOffset.UtcNow, metadata);
            yield return CreateBrowserUseStreamEvent(providerId, turn.RunId, "text-delta", new AITextDeltaEventData { Delta = text }, DateTimeOffset.UtcNow, metadata);
            yield return CreateBrowserUseStreamEvent(providerId, turn.RunId, "text-end", new AITextEndEventData(), DateTimeOffset.UtcNow, metadata);
        }

        foreach (var download in downloads.Where(d => !string.IsNullOrWhiteSpace(d.Url)))
        {
            yield return CreateBrowserUseStreamEvent(providerId, $"{turn.RunId}_download_{download.Path}", "file", new AIFileEventData
            {
                Filename = download.Path,
                MediaType = "application/octet-stream",
                Url = download.Url!,
                ProviderMetadata = CreateBrowserUseToolProviderMetadata(toolCallId, "download")
            }, DateTimeOffset.UtcNow, metadata);
        }

        yield return CreateBrowserUseStreamEvent(providerId, turn.RunId, "finish", new AIFinishEventData
        {
            FinishReason = failed ? "error" : "stop",
            Model = terminal.Model,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            InputTokens = terminal.TotalInputTokens,
            OutputTokens = terminal.TotalOutputTokens,
            TotalTokens = terminal.TotalInputTokens + terminal.TotalOutputTokens,
            MessageMetadata = AIFinishMessageMetadata.Create(
                terminal.Model,
                DateTimeOffset.UtcNow,
                usage: CreateBrowserUseUsage(terminal),
                inputTokens: terminal.TotalInputTokens,
                outputTokens: terminal.TotalOutputTokens,
                totalTokens: terminal.TotalInputTokens + terminal.TotalOutputTokens,
                gateway: CreateBrowserUseGatewayCost(terminal))
        }, DateTimeOffset.UtcNow, metadata);
    }

    private async Task<BrowserUseTurn> StartBrowserUseTurnAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var text = GetBrowserUseTurnText(request);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("BrowserUse requires a non-empty user message, input, or instructions.");

        var options = GetBrowserUseOptions(request);
        var attachments = GetLatestBrowserUseAttachments(request);
        if (TryFindBrowserUseSessionId(request, out var sessionId))
        {
            var sessionInfo = attachments.Count > 0 ? await GetBrowserUseSessionAsync(sessionId, cancellationToken) : null;
            var workspaceId = options.WorkspaceId ?? sessionInfo?.WorkspaceId;
            if (attachments.Count > 0 && string.IsNullOrWhiteSpace(workspaceId))
                throw new InvalidOperationException("BrowserUse did not return a workspace for the continued session; attachments cannot be uploaded.");
            var uploaded = attachments.Count == 0 ? [] : await UploadBrowserUseAttachmentsAsync(workspaceId!, attachments, cancellationToken);
            var attachedFileIds = MergeBrowserUseAttachedFileIds(options.AttachedFileIds, uploaded);
            var queued = await QueueBrowserUseSessionMessageAsync(sessionId, new BrowserUseQueueMessageRequest
            {
                Text = text,
                Interrupt = options.Interrupt,
                AttachedFileIds = attachedFileIds
            }, cancellationToken);

            var runId = queued.RunId;
            while (string.IsNullOrWhiteSpace(runId))
            {
                var session = await GetBrowserUseSessionAsync(sessionId, cancellationToken);
                runId = session.LatestRunId;
                if (!string.IsNullOrWhiteSpace(runId) && !IsBrowserUseTerminal(session.Status))
                    break;

                await Task.Delay(BrowserUsePollInterval, cancellationToken);
            }

            return new BrowserUseTurn(sessionId, runId!, workspaceId, uploaded, null, queued, false);
        }

        var newWorkspaceId = options.WorkspaceId;
        if (attachments.Count > 0 && string.IsNullOrWhiteSpace(newWorkspaceId))
            newWorkspaceId = (await CreateBrowserUseWorkspaceAsync(cancellationToken)).Id;
        var newUploads = attachments.Count == 0 ? [] : await UploadBrowserUseAttachmentsAsync(newWorkspaceId!, attachments, cancellationToken);
        var newAttachedFileIds = MergeBrowserUseAttachedFileIds(options.AttachedFileIds, newUploads);
        var created = await CreateBrowserUseRunAsync(new BrowserUseCreateRunRequest
        {
            Task = text,
            Model = NormalizeBrowserUseModel(request.Model, options.Model),
            WorkspaceId = newWorkspaceId,
            AttachedFileIds = newAttachedFileIds,
            BrowserSettings = options.BrowserSettings,
            Judge = options.Judge,
            MaxCostUsd = options.MaxCostUsd
        }, cancellationToken);

        return new BrowserUseTurn(created.SessionId, created.Id, created.WorkspaceId, newUploads, created, null, true);
    }

    private static List<BrowserUseAttachment> GetLatestBrowserUseAttachments(AIRequest request)
    {
        var latestUser = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = latestUser?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count > BrowserUseMaxUploadFiles)
            throw new ArgumentException($"BrowserUse accepts at most {BrowserUseMaxUploadFiles} new files per message.", nameof(request));
        return files.Select(DecodeBrowserUseAttachment).ToList();
    }

    private static List<object> GetLatestBrowserUseFileDescriptors(AIRequest request)
    {
        var latestUser = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        return (latestUser?.Content?.OfType<AIFileContentPart>() ?? []).Select(file => (object)new
        {
            filename = file.Filename,
            mediaType = file.MediaType
        }).ToList();
    }

    private static BrowserUseAttachment DecodeBrowserUseAttachment(AIFileContentPart file, int index)
    {
        var filename = file.Filename?.Trim();
        if (string.IsNullOrWhiteSpace(filename))
            throw new ArgumentException($"BrowserUse attachment {index + 1} requires a filename.", nameof(file));
        var value = file.Data?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"BrowserUse attachment '{filename}' has no data.", nameof(file));
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new NotSupportedException($"BrowserUse attachment '{filename}' uses an HTTP(S) URL. Only raw base64 and base64 data URLs are supported.");

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Application.Octet : file.MediaType!;
        var base64 = value;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"BrowserUse attachment '{filename}' must use a base64 data URL.");
            var header = value[5..comma];
            var semicolon = header.IndexOf(';');
            if (semicolon > 0) mediaType = header[..semicolon];
            base64 = value[(comma + 1)..];
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException exception) { throw new ArgumentException($"BrowserUse attachment '{filename}' contains invalid base64 data.", nameof(file), exception); }
        if (bytes.Length == 0) throw new ArgumentException($"BrowserUse attachment '{filename}' is empty.", nameof(file));
        if (bytes.Length > BrowserUseMaxUploadBytes) throw new ArgumentException($"BrowserUse attachment '{filename}' exceeds the 50 MB limit.", nameof(file));
        return new BrowserUseAttachment(filename, mediaType, bytes, file.Metadata);
    }

    private static List<string>? MergeBrowserUseAttachedFileIds(List<string>? configured, List<BrowserUseUploadedAttachment> uploaded)
    {
        var ids = (configured ?? []).Concat(uploaded.Select(file => file.Id)).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count > BrowserUseMaxAttachedFileIds)
            throw new ArgumentException($"BrowserUse accepts at most {BrowserUseMaxAttachedFileIds} attached file IDs per run.");
        return ids.Count == 0 ? null : ids;
    }

    private async Task<BrowserUseRunSummary> WaitForBrowserUseRunAsync(string runId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = await GetBrowserUseRunStatusAsync(runId, cancellationToken);
            if (IsBrowserUseTerminal(status.Status))
                return await GetBrowserUseRunAsync(runId, cancellationToken);

            await Task.Delay(BrowserUsePollInterval, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task<BrowserUseRunCreateResponse> CreateBrowserUseRunAsync(BrowserUseCreateRunRequest body, CancellationToken cancellationToken)
        => await SendBrowserUseJsonAsync<BrowserUseRunCreateResponse>(HttpMethod.Post, BrowserUseV4RunsEndpoint, body, "create run", cancellationToken);

    private async Task<BrowserUseWorkspaceInfo> CreateBrowserUseWorkspaceAsync(CancellationToken cancellationToken)
        => await SendBrowserUseJsonAsync<BrowserUseWorkspaceInfo>(HttpMethod.Post, "api/v4/workspaces", new { }, "create workspace", cancellationToken);

    private async Task<List<BrowserUseUploadedAttachment>> UploadBrowserUseAttachmentsAsync(
        string workspaceId, List<BrowserUseAttachment> attachments, CancellationToken cancellationToken)
    {
        var initialized = await SendBrowserUseJsonAsync<BrowserUseWorkspaceFileUploadResponse>(
            HttpMethod.Post,
            $"api/v4/workspaces/{Uri.EscapeDataString(workspaceId)}/files/upload",
            new BrowserUseWorkspaceFileUploadRequest
            {
                Files = attachments.Select(file => new BrowserUseWorkspaceFileUploadItem
                {
                    Name = file.Filename,
                    ContentType = file.MediaType,
                    Size = file.Bytes.Length
                }).ToList()
            }, "initialize workspace file upload", cancellationToken);
        if (initialized.Files.Count != attachments.Count)
            throw new InvalidOperationException("BrowserUse returned an unexpected number of workspace upload URLs.");

        var uploaded = new List<BrowserUseUploadedAttachment>(attachments.Count);
        for (var index = 0; index < attachments.Count; index++)
        {
            var source = attachments[index];
            var target = initialized.Files[index];
            using var put = new HttpRequestMessage(HttpMethod.Put, target.UploadUrl)
            {
                Content = new ByteArrayContent(source.Bytes)
            };
            put.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(source.MediaType);
            put.Content.Headers.ContentLength = source.Bytes.Length;
            using var response = await _uploadClient.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"BrowserUse workspace upload failed for '{source.Filename}' ({(int)response.StatusCode}): {raw}");
            }
            uploaded.Add(new BrowserUseUploadedAttachment(target.Id, target.Name, target.StoredName, target.Path,
                source.MediaType, source.Bytes.Length, target.WillOverride, source.Metadata));
        }
        return uploaded;
    }

    private async Task<BrowserUseQueuedMessage> QueueBrowserUseSessionMessageAsync(string sessionId, BrowserUseQueueMessageRequest body, CancellationToken cancellationToken)
        => await SendBrowserUseJsonAsync<BrowserUseQueuedMessage>(HttpMethod.Post, $"api/v4/sessions/{Uri.EscapeDataString(sessionId)}/queue", body, "queue session message", cancellationToken);

    private async Task<BrowserUseRunStatus> GetBrowserUseRunStatusAsync(string runId, CancellationToken cancellationToken)
        => await SendBrowserUseJsonAsync<BrowserUseRunStatus>(HttpMethod.Get, $"{BrowserUseV4RunsEndpoint}/{Uri.EscapeDataString(runId)}/status", null, "get run status", cancellationToken);

    private async Task<BrowserUseRunSummary> GetBrowserUseRunAsync(string runId, CancellationToken cancellationToken)
        => await SendBrowserUseJsonAsync<BrowserUseRunSummary>(HttpMethod.Get, $"{BrowserUseV4RunsEndpoint}/{Uri.EscapeDataString(runId)}", null, "get run", cancellationToken);

    private async Task<BrowserUseSessionInfo> GetBrowserUseSessionAsync(string sessionId, CancellationToken cancellationToken)
        => await SendBrowserUseJsonAsync<BrowserUseSessionInfo>(HttpMethod.Get, $"api/v4/sessions/{Uri.EscapeDataString(sessionId)}", null, "get session", cancellationToken);

    private async Task<BrowserUseRunEventsResponse> GetBrowserUseRunEventsAsync(string runId, int after, CancellationToken cancellationToken)
        => await SendBrowserUseJsonAsync<BrowserUseRunEventsResponse>(HttpMethod.Get, $"{BrowserUseV4RunsEndpoint}/{Uri.EscapeDataString(runId)}/events?after={after}&limit=200", null, "get run events", cancellationToken);

    private async Task<List<BrowserUseDownload>> ListBrowserUseDownloadsSafeAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return [];

        try
        {
            var response = await SendBrowserUseJsonAsync<BrowserUseDownloadsResponse>(HttpMethod.Get, $"api/v4/browsers/{Uri.EscapeDataString(sessionId)}/downloads?includeUrls=true", null, "list downloads", cancellationToken);
            return response.Files;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task CancelBrowserUseRunSafeAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            await SendBrowserUseJsonAsync<BrowserUseRunSummary>(HttpMethod.Post, $"{BrowserUseV4RunsEndpoint}/{Uri.EscapeDataString(runId)}/cancel", null, "cancel run", cancellationToken);
        }
        catch
        {
            // Cancelling an external provider run is best effort during request cancellation.
        }
    }

    private async Task<T> SendBrowserUseJsonAsync<T>(HttpMethod method, string endpoint, object? body, string operation, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, BrowserUseV4Json), Encoding.UTF8, MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"BrowserUse v4 {operation} failed ({(int)response.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<T>(raw, BrowserUseV4Json)
               ?? throw new InvalidOperationException($"BrowserUse v4 {operation} returned an empty payload.");
    }

    private AIResponse CreateBrowserUseUnifiedResponse(AIRequest request, BrowserUseTurn turn, BrowserUseRunSummary summary, List<BrowserUseDownload> downloads)
    {
        var failed = !IsBrowserUseSuccessful(summary);
        var output = new List<AIOutputItem>
        {
            new()
            {
                Type = "tool-call",
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = BuildBrowserUseSessionToolCallId(turn.SessionId),
                        ToolName = BrowserUseSessionToolName,
                        Title = "BrowserUse session",
                        Input = JsonSerializer.SerializeToElement(new { sessionId = turn.SessionId, runId = turn.RunId }, BrowserUseV4Json),
                        Output = CreateBrowserUseSessionToolResult(turn, summary),
                        ProviderExecuted = true,
                        State = failed ? "output-error" : "output-available",
                        Metadata = CreateBrowserUseMetadata(turn, summary, null)
                    }
                ]
            }
        };

        var text = summary.Result ?? summary.Error;
        if (!string.IsNullOrWhiteSpace(text))
        {
            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = text }]
            });
        }

        foreach (var download in downloads.Where(d => !string.IsNullOrWhiteSpace(d.Url)))
        {
            output.Add(new AIOutputItem
            {
                Type = "file",
                Content = [new AIFileContentPart { Type = "file", Filename = download.Path, MediaType = "application/octet-stream", Data = download.Url }],
                Metadata = new Dictionary<string, object?> { ["browseruse.download"] = download }
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = summary.Model,
            Status = failed ? "failed" : "completed",
            Usage = CreateBrowserUseUsage(summary),
            Output = new AIOutput { Items = output },
            Metadata = CreateBrowserUseMetadata(turn, summary, null)
        };
    }

    private static bool IsBrowserUseTerminal(string? status)
        => status is "completed" or "failed" or "cancelled";

    private static bool IsBrowserUseSuccessful(BrowserUseRunSummary summary)
        => string.Equals(summary.Status, "completed", StringComparison.OrdinalIgnoreCase);

    private static string GetBrowserUseTurnText(AIRequest request)
    {
        var userText = request.Input?.Items?
            .Where(i => string.Equals(i.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(i => ExtractBrowserUseText(i.Content))
            .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));

        return userText ?? request.Input?.Text ?? request.Instructions ?? string.Empty;
    }

    private static string ExtractBrowserUseText(IEnumerable<AIContentPart>? content)
        => string.Join("\n", (content ?? []).OfType<AITextContentPart>().Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)));

    private bool TryFindBrowserUseSessionId(AIRequest request, out string sessionId)
    {
        sessionId = TryGetBrowserUseOption<string>(request.Metadata, "sessionId")
                    ?? TryGetBrowserUseOption<string>(request.Metadata, "session_id")
                    ?? TryGetDictionaryString(request.Input?.Metadata, "sessionId")
                    ?? TryGetDictionaryString(request.Input?.Metadata, "session_id")
                    ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(sessionId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (tool.ProviderExecuted != true || !string.Equals(tool.ToolName, BrowserUseSessionToolName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryExtractBrowserUseSessionId(tool.Output, out sessionId) || TryExtractBrowserUseSessionId(tool.Metadata, out sessionId) || TryExtractBrowserUseSessionId(tool.Input, out sessionId))
                    return true;
            }
        }

        sessionId = string.Empty;
        return false;
    }

    private static bool TryExtractBrowserUseSessionId(object? value, out string sessionId)
    {
        sessionId = string.Empty;
        if (value is null)
            return false;

        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, BrowserUseV4Json);
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "sessionId", StringComparison.OrdinalIgnoreCase) || string.Equals(property.Name, "session_id", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    sessionId = property.Value.GetString()!;
                    return true;
                }
            }

            if (property.Value.ValueKind == JsonValueKind.Object && TryExtractBrowserUseSessionId(property.Value, out sessionId))
                return true;
        }

        return false;
    }

    private BrowserUseRequestOptions GetBrowserUseOptions(AIRequest request)
        => TryGetBrowserUseOption<BrowserUseRequestOptions>(request.Metadata, "options")
           ?? TryGetBrowserUseOptions(request.Metadata)
           ?? new BrowserUseRequestOptions();

    private BrowserUseRequestOptions? TryGetBrowserUseOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(GetIdentifier(), out var value) || value is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<BrowserUseRequestOptions>(JsonSerializer.Serialize(value, BrowserUseV4Json), BrowserUseV4Json);
        }
        catch
        {
            return null;
        }
    }

    private T? TryGetBrowserUseOption<T>(Dictionary<string, object?>? metadata, string name) where T : class
    {
        try
        {
            return metadata?.GetProviderOption<T>(GetIdentifier(), name);
        }
        catch
        {
            return default;
        }
    }

    private static string? TryGetDictionaryString(Dictionary<string, object?>? values, string name)
        => values is not null && values.TryGetValue(name, out var value) && value is not null ? Convert.ToString(value) : null;

    private static string? NormalizeBrowserUseModel(string? requestedModel, string? configuredModel)
    {
        var model = configuredModel ?? requestedModel;
        if (string.IsNullOrWhiteSpace(model))
            return null;

        const string prefix = "browseruse/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }

    private static string BuildBrowserUseSessionToolCallId(string sessionId)
        => $"browseruse-session-{sessionId}";

    private static IEnumerable<AIStreamEvent> CreateBrowserUseRunEventStreamEvents(
        string providerId,
        BrowserUseTurn turn,
        BrowserUseRunEvent item)
    {
        if (!TryGetBrowserUseEventPart(item, out var part))
            yield break;

        var partType = GetBrowserUseJsonString(part, "type");
        var timestamp = item.Timestamp ?? DateTimeOffset.UtcNow;
        var metadata = CreateBrowserUseMetadata(turn, null, item);

        if (string.Equals(partType, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            var text = GetBrowserUseJsonString(part, "text");
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            var reasoningId = GetBrowserUseJsonString(part, "id")
                              ?? $"browseruse-run-{turn.RunId}-reasoning-{item.Id}";
            var reasoningProviderMetadata = CreateBrowserUseReasoningProviderMetadata(turn, item);

            yield return CreateBrowserUseStreamEvent(providerId, reasoningId, "reasoning-start",
                new AIReasoningStartEventData { ProviderMetadata = reasoningProviderMetadata }, timestamp, metadata);
            yield return CreateBrowserUseStreamEvent(providerId, reasoningId, "reasoning-delta",
                new AIReasoningDeltaEventData { Delta = text, ProviderMetadata = reasoningProviderMetadata }, timestamp, metadata);
            yield return CreateBrowserUseStreamEvent(providerId, reasoningId, "reasoning-end",
                new AIReasoningEndEventData { ProviderMetadata = reasoningProviderMetadata }, timestamp, metadata);
            yield break;
        }

        if (!string.Equals(partType, "tool", StringComparison.OrdinalIgnoreCase)
            || !part.TryGetProperty("state", out var state)
            || state.ValueKind != JsonValueKind.Object)
            yield break;

        var toolName = GetBrowserUseJsonString(part, "tool") ?? "browseruse_tool";
        var toolCallId = GetBrowserUseJsonString(state, "callID")
                         ?? GetBrowserUseJsonString(state, "callId")
                         ?? GetBrowserUseJsonString(part, "id")
                         ?? $"browseruse-run-{turn.RunId}-tool-{item.Id}";
        var title = GetBrowserUseJsonString(state, "title") ?? toolName;
        var status = GetBrowserUseJsonString(state, "status");
        var input = GetBrowserUseJsonValue(state, "input") ?? JsonSerializer.SerializeToElement(new { });
        var providerMetadata = CreateBrowserUseToolProviderMetadata(toolCallId, "tool_use", toolName);

        yield return CreateBrowserUseStreamEvent(providerId, toolCallId, "tool-input-available", new AIToolInputAvailableEventData
        {
            ToolName = toolName,
            Title = title,
            Input = input,
            ProviderExecuted = true,
            ProviderMetadata = providerMetadata
        }, timestamp, metadata);

        if (status is "failed" or "error")
        {
            yield return CreateBrowserUseStreamEvent(providerId, toolCallId, "tool-output-error", new AIToolOutputErrorEventData
            {
                ToolCallId = toolCallId,
                ErrorText = GetBrowserUseJsonString(state, "error")
                            ?? GetBrowserUseJsonString(state, "output")
                            ?? $"BrowserUse tool '{toolName}' failed.",
                ProviderExecuted = true,
                Dynamic = true,
                ProviderMetadata = CreateBrowserUseToolProviderMetadata(toolCallId, "tool_result", toolName)
            }, timestamp, metadata);
            yield break;
        }

        var output = GetBrowserUseJsonValue(state, "output")
                     ?? GetBrowserUseJsonValue(state, "result")
                     ?? JsonSerializer.SerializeToElement(new { status = status ?? "completed" });
        yield return CreateBrowserUseStreamEvent(providerId, toolCallId, "tool-output-available", new AIToolOutputAvailableEventData
        {
            ToolName = toolName,
            Output = output,
            ProviderExecuted = true,
            Dynamic = true,
            Preliminary = status is not null && status is not "completed",
            ProviderMetadata = CreateBrowserUseToolProviderMetadata(toolCallId, "tool_result", toolName)
        }, timestamp, metadata);
    }

    private static bool TryGetBrowserUseEventPart(BrowserUseRunEvent item, out JsonElement part)
    {
        part = default;
        return string.Equals(item.Type, "core.event", StringComparison.OrdinalIgnoreCase)
               && item.Data.ValueKind == JsonValueKind.Object
               && item.Data.TryGetProperty("part", out part)
               && part.ValueKind == JsonValueKind.Object;
    }

    private static string? GetBrowserUseJsonString(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static object? GetBrowserUseJsonValue(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out var property)
            ? property.Clone()
            : null;

    private static Dictionary<string, Dictionary<string, object>> CreateBrowserUseReasoningProviderMetadata(
        BrowserUseTurn turn,
        BrowserUseRunEvent item)
        => new()
        {
            [nameof(BrowserUse).ToLowerInvariant()] = new Dictionary<string, object>
            {
                ["type"] = "reasoning",
                ["session_id"] = turn.SessionId,
                ["run_id"] = turn.RunId,
                ["event_id"] = item.Id,
                ["api_version"] = "v4"
            }
        };

    private static string NormalizeBrowserUseEventToolName(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return "browseruse_event";

        var normalized = new string(eventType
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_')
            .ToArray());

        normalized = string.Join('_', normalized.Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "browseruse_event" : $"browseruse_{normalized}";
    }

    private static string CreateBrowserUseEventTitle(string? eventType)
        => string.IsNullOrWhiteSpace(eventType)
            ? "BrowserUse event"
            : $"BrowserUse {eventType.Replace('_', ' ')}";

    private static object CreateBrowserUseEventToolInput(BrowserUseTurn turn, BrowserUseRunEvent item)
        => new
        {
            sessionId = turn.SessionId,
            runId = turn.RunId,
            eventId = item.Id,
            eventType = item.Type
        };

    private static object CreateBrowserUseEventToolResult(BrowserUseTurn turn, BrowserUseRunEvent item)
        => new
        {
            sessionId = turn.SessionId,
            session_id = turn.SessionId,
            runId = turn.RunId,
            eventId = item.Id,
            eventType = item.Type,
            data = item.Data
        };

    private static object CreateBrowserUseSessionToolResult(BrowserUseTurn turn, BrowserUseRunSummary? summary)
        => new
        {
            type = BrowserUseSessionToolName,
            sessionId = summary?.SessionId ?? turn.SessionId,
            session_id = summary?.SessionId ?? turn.SessionId,
            workspaceId = summary?.WorkspaceId ?? turn.Created?.WorkspaceId,
            attachedFiles = turn.Attachments.Select(file => new
            {
                id = file.Id,
                name = file.Name,
                storedName = file.StoredName,
                path = file.Path,
                mediaType = file.MediaType,
                size = file.Size,
                willOverride = file.WillOverride,
                sourceMetadata = file.SourceMetadata
            }),
            runId = summary?.Id ?? turn.RunId,
            status = summary?.Status ?? turn.Created?.Status ?? turn.Queued?.Status ?? "queued",
            model = summary?.Model ?? turn.Created?.Model,
            result = summary?.Result,
            error = summary?.Error,
            raw = summary ?? (object?)turn.Created ?? turn.Queued
        };

    private static object CreateBrowserUseUsage(BrowserUseRunSummary summary)
        => new
        {
            input_tokens = summary.TotalInputTokens,
            output_tokens = summary.TotalOutputTokens,
            total_tokens = summary.TotalInputTokens + summary.TotalOutputTokens,
            cost = summary.TotalCostUsd
        };

    private static AIFinishGatewayMetadata? CreateBrowserUseGatewayCost(BrowserUseRunSummary summary)
        => decimal.TryParse(summary.TotalCostUsd, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var cost)
            ? new AIFinishGatewayMetadata { Cost = cost }
            : null;

    private static Dictionary<string, object?> CreateBrowserUseMetadata(BrowserUseTurn? turn, BrowserUseRunSummary? summary, BrowserUseRunEvent? item)
        => new Dictionary<string, object?>
        {
            ["browseruse.api_version"] = "v4",
            ["browseruse.session_id"] = summary?.SessionId ?? turn?.SessionId,
            ["browseruse.run_id"] = summary?.Id ?? turn?.RunId,
            ["browseruse.workspace_id"] = summary?.WorkspaceId ?? turn?.WorkspaceId ?? turn?.Created?.WorkspaceId,
            ["browseruse.attachments"] = turn?.Attachments.Count > 0 ? turn.Attachments.Select(file => new
            {
                id = file.Id,
                name = file.Name,
                storedName = file.StoredName,
                path = file.Path,
                mediaType = file.MediaType,
                size = file.Size,
                willOverride = file.WillOverride,
                sourceMetadata = file.SourceMetadata
            }).ToList() : null,
            ["browseruse.status"] = summary?.Status ?? turn?.Created?.Status ?? turn?.Queued?.Status,
            ["browseruse.summary"] = summary,
            ["browseruse.event"] = item
        }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);

    private static Dictionary<string, Dictionary<string, object>> CreateBrowserUseToolProviderMetadata(
        string toolCallId,
        string type,
        string toolName = BrowserUseSessionToolName)
        => new()
        {
            [nameof(BrowserUse).ToLowerInvariant()] = new Dictionary<string, object>
            {
                ["type"] = type,
                ["tool_name"] = toolName,
                ["tool_use_id"] = toolCallId,
                ["api_version"] = "v4"
            }
        };

    private static AIStreamEvent CreateBrowserUseStreamEvent(string providerId, string eventId, string type, object data, DateTimeOffset timestamp, Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = eventId, Timestamp = timestamp, Data = data }
        };

    private sealed record BrowserUseTurn(
        string SessionId,
        string RunId,
        string? WorkspaceId,
        List<BrowserUseUploadedAttachment> Attachments,
        BrowserUseRunCreateResponse? Created,
        BrowserUseQueuedMessage? Queued,
        bool NewSession);
    private sealed record BrowserUseAttachment(string Filename, string MediaType, byte[] Bytes, Dictionary<string, object?>? Metadata);
    private sealed record BrowserUseUploadedAttachment(string Id, string Name, string StoredName, string Path, string MediaType, int Size, bool WillOverride, Dictionary<string, object?>? SourceMetadata);

    private sealed class BrowserUseRequestOptions
    {
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; init; }
        [JsonPropertyName("attachedFileIds")] public List<string>? AttachedFileIds { get; init; }
        [JsonPropertyName("browserSettings")] public JsonElement? BrowserSettings { get; init; }
        [JsonPropertyName("judge")] public JsonElement? Judge { get; init; }
        [JsonPropertyName("maxCostUsd")] public decimal? MaxCostUsd { get; init; }
        [JsonPropertyName("interrupt")] public bool Interrupt { get; init; }
    }

    private sealed class BrowserUseCreateRunRequest
    {
        [JsonPropertyName("task")] public required string Task { get; init; }
        [JsonPropertyName("model")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Model { get; init; }
        [JsonPropertyName("workspaceId")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? WorkspaceId { get; init; }
        [JsonPropertyName("attachedFileIds")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? AttachedFileIds { get; init; }
        [JsonPropertyName("browserSettings")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? BrowserSettings { get; init; }
        [JsonPropertyName("judge")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? Judge { get; init; }
        [JsonPropertyName("maxCostUsd")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? MaxCostUsd { get; init; }
    }

    private sealed class BrowserUseQueueMessageRequest
    {
        [JsonPropertyName("text")] public required string Text { get; init; }
        [JsonPropertyName("interrupt")] public bool Interrupt { get; init; }
        [JsonPropertyName("attachedFileIds")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? AttachedFileIds { get; init; }
    }

    private sealed class BrowserUseRunCreateResponse
    {
        [JsonPropertyName("id")] public string Id { get; init; } = default!;
        [JsonPropertyName("status")] public string Status { get; init; } = default!;
        [JsonPropertyName("model")] public string Model { get; init; } = default!;
        [JsonPropertyName("sessionId")] public string SessionId { get; init; } = default!;
        [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; init; }
        [JsonPropertyName("eventsUrl")] public string? EventsUrl { get; init; }
    }

    private sealed class BrowserUseQueuedMessage
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("sessionId")] public string SessionId { get; init; } = default!;
        [JsonPropertyName("runId")] public string? RunId { get; init; }
        [JsonPropertyName("status")] public string Status { get; init; } = default!;
    }

    private sealed class BrowserUseRunStatus { [JsonPropertyName("status")] public string Status { get; init; } = default!; }

    private sealed class BrowserUseSessionInfo
    {
        [JsonPropertyName("sessionId")] public string SessionId { get; init; } = default!;
        [JsonPropertyName("latestRunId")] public string LatestRunId { get; init; } = default!;
        [JsonPropertyName("status")] public string Status { get; init; } = default!;
        [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; init; }
    }

    private sealed class BrowserUseWorkspaceInfo { [JsonPropertyName("id")] public string Id { get; init; } = default!; }
    private sealed class BrowserUseWorkspaceFileUploadRequest { [JsonPropertyName("files")] public required List<BrowserUseWorkspaceFileUploadItem> Files { get; init; } }
    private sealed class BrowserUseWorkspaceFileUploadItem
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("contentType")] public required string ContentType { get; init; }
        [JsonPropertyName("size")] public required int Size { get; init; }
    }
    private sealed class BrowserUseWorkspaceFileUploadResponse { [JsonPropertyName("files")] public List<BrowserUseWorkspaceFileUploadResponseItem> Files { get; init; } = []; }
    private sealed class BrowserUseWorkspaceFileUploadResponseItem
    {
        [JsonPropertyName("id")] public string Id { get; init; } = default!;
        [JsonPropertyName("name")] public string Name { get; init; } = default!;
        [JsonPropertyName("storedName")] public string StoredName { get; init; } = default!;
        [JsonPropertyName("path")] public string Path { get; init; } = default!;
        [JsonPropertyName("willOverride")] public bool WillOverride { get; init; }
        [JsonPropertyName("uploadUrl")] public string UploadUrl { get; init; } = default!;
    }

    private sealed class BrowserUseRunSummary
    {
        [JsonPropertyName("id")] public string Id { get; init; } = default!;
        [JsonPropertyName("model")] public string Model { get; init; } = default!;
        [JsonPropertyName("status")] public string Status { get; init; } = default!;
        [JsonPropertyName("result")] public string? Result { get; init; }
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("sessionId")] public string SessionId { get; init; } = default!;
        [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; init; }
        [JsonPropertyName("judgement")] public JsonElement? Judgement { get; init; }
        [JsonPropertyName("totalInputTokens")] public int TotalInputTokens { get; init; }
        [JsonPropertyName("totalOutputTokens")] public int TotalOutputTokens { get; init; }
        [JsonPropertyName("totalCostUsd")] public string? TotalCostUsd { get; init; }
    }

    private sealed class BrowserUseRunEventsResponse { [JsonPropertyName("events")] public List<BrowserUseRunEvent> Events { get; init; } = []; }

    private sealed class BrowserUseRunEvent
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("ts")] public DateTimeOffset? Timestamp { get; init; }
        [JsonPropertyName("type")] public string Type { get; init; } = default!;
        [JsonPropertyName("data")] public JsonElement Data { get; init; }
    }

    private sealed class BrowserUseDownloadsResponse { [JsonPropertyName("files")] public List<BrowserUseDownload> Files { get; init; } = []; }
    private sealed class BrowserUseDownload
    {
        [JsonPropertyName("path")] public string Path { get; init; } = default!;
        [JsonPropertyName("url")] public string? Url { get; init; }
    }
}
