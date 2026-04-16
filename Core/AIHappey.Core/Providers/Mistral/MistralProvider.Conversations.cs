using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Abstractions.Http;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    private const string ConversationsEndpoint = "/v1/conversations";

    private static readonly JsonSerializerOptions MistralJsonSerializerOptions = JsonSerializerOptions.Web;

    private ConversationTarget ResolveConversationTarget(string? model)
    {
        var normalized = NormalizeMistralModelId(model);
        if (string.IsNullOrWhiteSpace(normalized))
            return new ConversationTarget(null, null);

        if (normalized.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var agentId = normalized[AgentModelPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(agentId))
                return new ConversationTarget(null, agentId);
        }

        return new ConversationTarget(normalized, null);
    }


    private MistralConversationRequest CreateConversationRequest(
        ConversationTarget target,
        JsonNode inputs,
        string? instructions,
        MistralConversationCompletionArgs? completionArgs,
        JsonNode? tools,
        bool stream)
        => new()
        {
            AgentId = target.AgentId,
            Model = target.Model,
            Instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions,
            Inputs = inputs,
            CompletionArgs = IsEmpty(completionArgs) ? null : completionArgs,
            Tools = IsEmpty(tools) ? null : tools,
            Stream = stream,
            Store = false
        };

    private string NormalizeReportedModel(string? upstreamModel, ConversationTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.AgentId))
            return target.ExposedModelId;

        var normalized = NormalizeMistralModelId(upstreamModel);
        return string.IsNullOrWhiteSpace(normalized)
            ? target.ExposedModelId
            : normalized;
    }

    private string NormalizeMistralModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var trimmed = model.Trim();
        var providerPrefix = GetIdentifier() + "/";

        if (trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed.SplitModelId().Model;

        return trimmed;
    }

    private async Task<MistralConversationResponse> StartConversationAsync(
        MistralConversationRequest request,
        CancellationToken cancellationToken,
        ProviderBackendCaptureRequest? capture = null)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Post, ConversationsEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, MistralJsonSerializerOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw CreateConversationException(resp, body);

        await ProviderBackendCapture.CaptureJsonAsync("conversations", resp, body, capture, cancellationToken);

        return JsonSerializer.Deserialize<MistralConversationResponse>(body, MistralJsonSerializerOptions)
            ?? throw new MistralConversationException(
                resp.StatusCode,
                "Mistral returned an empty conversation response.",
                body);
    }

    private async IAsyncEnumerable<MistralConversationStreamEvent> StartConversationStreamAsync(
        MistralConversationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        ProviderBackendCaptureRequest? capture = null)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Post, ConversationsEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, MistralJsonSerializerOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Text.EventStream));

        using var resp = await _client.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw CreateConversationException(resp, body);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        await using var captureSink = ProviderBackendCapture.BeginStreamCapture("conversations", resp, capture);

        string? sseEvent = null;
        var dataBuilder = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (captureSink is not null)
                await captureSink.WriteLineAsync(line, cancellationToken);

            if (line.Length == 0)
            {
                if (dataBuilder.Length > 0)
                {
                    yield return ParseConversationStreamEvent(sseEvent, dataBuilder.ToString());
                    dataBuilder.Clear();
                }

                sseEvent = null;
                continue;
            }

            if (line.StartsWith(':'))
                continue;

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                sseEvent = line["event: ".Length..].Trim();
                continue;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            if (dataBuilder.Length > 0)
                dataBuilder.AppendLine();

            dataBuilder.Append(line["data: ".Length..].Trim());
        }

        if (dataBuilder.Length > 0)
            yield return ParseConversationStreamEvent(sseEvent, dataBuilder.ToString());
    }

    private static MistralConversationStreamEvent ParseConversationStreamEvent(string? sseEvent, string data)
    {
        var parsed = MistralExtensions.ParseConversationStreamEventEnvelope(sseEvent, data);
        return new MistralConversationStreamEvent(parsed.Type, parsed.Payload);
    }

    private static JsonNode? GetPrimaryMessageOutput(MistralConversationResponse response)
        => response.Outputs?
               .FirstOrDefault(o => string.Equals(GetString(o, "type"), "message.output", StringComparison.Ordinal))
           ?? response.Outputs?.FirstOrDefault();

    private static IEnumerable<MistralContentPart> EnumerateContentParts(JsonNode? content)
    {
        foreach (var part in MistralExtensions.EnumerateConversationContentParts(content))
        {
            yield return new MistralContentPart(
                part.Type,
                part.Text,
                part.FileId,
                part.FileName,
                part.FileType,
                part.Url,
                part.Title,
                part.Raw);
        }
    }

    private static MistralContentPart? ParseContentPart(JsonNode? node)
        => MistralExtensions.EnumerateConversationContentParts(node)
            .Select(part => new MistralContentPart(
                part.Type,
                part.Text,
                part.FileId,
                part.FileName,
                part.FileType,
                part.Url,
                part.Title,
                part.Raw))
            .FirstOrDefault();

    private static MistralConversationUsage ExtractUsage(JsonNode? usage)
    {
        var promptTokens = GetInt32(usage, "prompt_tokens") ?? 0;
        var completionTokens = GetInt32(usage, "completion_tokens") ?? 0;
        var totalTokens = GetInt32(usage, "total_tokens") ?? (promptTokens + completionTokens);

        return new MistralConversationUsage(promptTokens, completionTokens, totalTokens);
    }

    private async Task<MistralDownloadedFileResult> TryDownloadConversationFileAsync(
        string? fileId,
        string? fallbackMimeType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            return MistralDownloadedFileResult.Failure("Missing Mistral file id.");

        ApplyAuthHeader();

        using var metaReq = new HttpRequestMessage(HttpMethod.Get, $"/v1/files/{fileId}");
        using var metaResp = await _client.SendAsync(metaReq, cancellationToken);
        if (!metaResp.IsSuccessStatusCode)
            return MistralDownloadedFileResult.Failure($"Error retrieving file metadata: {fileId}");

        var metaBody = await metaResp.Content.ReadAsStringAsync(cancellationToken);
        var metaJson = JsonNode.Parse(metaBody);
        if (GetBoolean(metaJson, "deleted") == true)
            return MistralDownloadedFileResult.Failure($"File deleted before retrieval: {fileId}");

        var mimeType = GetString(metaJson, "mimetype")
            ?? fallbackMimeType
            ?? MediaTypeNames.Application.Octet;

        using var fileReq = new HttpRequestMessage(HttpMethod.Get, $"/v1/files/{fileId}/content");
        using var fileResp = await _client.SendAsync(fileReq, cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
            return MistralDownloadedFileResult.Failure($"Error downloading file content: {fileId}");

        var bytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);

        return MistralDownloadedFileResult.Success(
            new MistralDownloadedFile(fileId, mimeType, bytes, GetString(metaJson, "filename")));
    }

    private static string ReadNodeAsString(JsonNode? node)
        => MistralExtensions.ReadNodeAsString(node);

    private static string? GetString(JsonNode? node, string propertyName)
        => MistralExtensions.GetString(node, propertyName);

    private static int? GetInt32(JsonNode? node, string propertyName)
        => MistralExtensions.GetInt32(node, propertyName);

    private static bool? GetBoolean(JsonNode? node, string propertyName)
        => MistralExtensions.GetBoolean(node, propertyName);

    private static bool IsEmpty(JsonNode? node)
        => node is null || node is JsonArray array && array.Count == 0;

    private static bool IsEmpty(MistralConversationCompletionArgs? args)
        => args is null
           || args.Temperature is null
           && args.MaxTokens is null
           && args.TopP is null
           && string.IsNullOrWhiteSpace(args.ToolChoice)
           && args.ResponseFormat is null;

    private static MistralConversationException CreateConversationException(HttpResponseMessage response, string responseBody)
        => new(
            response.StatusCode,
            string.IsNullOrWhiteSpace(responseBody) ? response.ReasonPhrase ?? "Mistral conversations request failed." : responseBody,
            responseBody);



    private sealed record ConversationTarget(string? Model, string? AgentId)
    {
        public string ExposedModelId => !string.IsNullOrWhiteSpace(AgentId)
            ? $"{MistralProvider.AgentModelPrefix}{AgentId}"
            : Model ?? "mistral";
    }

    private sealed class MistralConversationRequest
    {
        [JsonPropertyName("agent_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AgentId { get; init; }

        [JsonPropertyName("model")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Model { get; init; }

        [JsonPropertyName("instructions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Instructions { get; init; }

        [JsonPropertyName("inputs")]
        public JsonNode Inputs { get; init; } = new JsonArray();

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonNode? Tools { get; init; }

        [JsonPropertyName("completion_args")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MistralConversationCompletionArgs? CompletionArgs { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("store")]
        public bool Store { get; init; }
    }

    private sealed class MistralConversationCompletionArgs
    {
        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Temperature { get; init; }

        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; init; }

        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? TopP { get; init; }

        [JsonPropertyName("tool_choice")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolChoice { get; init; }

        [JsonPropertyName("reasoning_effort")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReasoningEffort { get; init; }

        [JsonPropertyName("random_seed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RandomSeed { get; init; }

        [JsonPropertyName("frequency_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? FrequencyPenalty { get; init; }

        [JsonPropertyName("presence_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? PresencePenalty { get; init; }

        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonNode? ResponseFormat { get; init; }
    }

    private sealed class MistralConversationResponse
    {
        [JsonPropertyName("conversation_id")]
        public string? ConversationId { get; init; }

        [JsonPropertyName("outputs")]
        public JsonArray? Outputs { get; init; }

        [JsonPropertyName("usage")]
        public JsonNode? Usage { get; init; }
    }

    private sealed record MistralConversationStreamEvent(string Type, JsonNode Payload)
    {
        public string? GetString(string propertyName) => MistralProvider.GetString(Payload, propertyName);

        public JsonNode? GetNode(string propertyName) => Payload[propertyName];
    }

    private sealed record MistralContentPart(
        string Type,
        string? Text = null,
        string? FileId = null,
        string? FileName = null,
        string? FileType = null,
        string? Url = null,
        string? Title = null,
        JsonNode? Raw = null);

    private readonly record struct MistralConversationUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

    private sealed record MistralDownloadedFile(string FileId, string MimeType, byte[] Bytes, string? FileName);

    private sealed record MistralDownloadedFileResult(MistralDownloadedFile? File, string? Error)
    {
        public static MistralDownloadedFileResult Success(MistralDownloadedFile file) => new(file, null);

        public static MistralDownloadedFileResult Failure(string error) => new(null, error);
    }

    private sealed class MistralConversationException(HttpStatusCode statusCode, string message, string? responseBody = null) : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;

        public string? ResponseBody { get; } = responseBody;
    }

}
