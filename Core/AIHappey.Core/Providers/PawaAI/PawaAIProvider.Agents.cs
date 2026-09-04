using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.PawaAI;

public partial class PawaAIProvider
{
    private bool TryParsePawaAgentModel(string? model, out string agentReferenceId, out string languageModel)
    {
        agentReferenceId = string.Empty;
        languageModel = string.Empty;
        var local = NormalizePawaModelId(model);
        if (!local.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = local[AgentModelPrefix.Length..];
        var separator = remainder.IndexOf('/');
        if (separator <= 0 || separator == remainder.Length - 1)
            return false;
        agentReferenceId = remainder[..separator];
        languageModel = remainder[(separator + 1)..];
        return true;
    }

    private async Task<AIResponse> ExecutePawaAgentAsync(AIRequest request, CancellationToken cancellationToken)
    {
        if (!TryParsePawaAgentModel(request.Model, out var referenceId, out var languageModel))
            throw new ArgumentException("PawaAI agent model must use agent/{agentReferenceId}/{languageModelId}.", nameof(request));

        var agent = (await ListPawaAgentsAsync(cancellationToken)).FirstOrDefault(item =>
            string.Equals(item.AgentReferenceId, referenceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown or inactive PawaAI agent '{referenceId}'.", nameof(request));

        var messages = BuildPawaAgentMessages(request);
        var latestUserIndex = messages.FindLastIndex(message =>
            string.Equals(message["role"]?.GetValue<string>(), "user", StringComparison.OrdinalIgnoreCase));
        if (latestUserIndex < 0)
            throw new ArgumentException("PawaAI agent requests require a user message.", nameof(request));

        var payload = CopyPawaOptions(GetPawaOptions(request.Metadata));
        payload["name"] = agent.Name;
        payload["description"] = agent.Description;
        payload["instruction"] = string.IsNullOrWhiteSpace(request.Instructions) ? agent.Instruction : request.Instructions;
        payload["intents"] = new JsonArray(agent.Intents.Select(value => JsonValue.Create(value)).ToArray<JsonNode?>());
        payload["model"] = languageModel;
        payload["message"] = messages[latestUserIndex].DeepClone();
        payload["memoryChat"] = new JsonArray(messages.Take(latestUserIndex).Select(item => item.DeepClone()).ToArray());
        payload["temperature"] = request.Temperature ?? ReadPawaNumber(payload, "temperature") ?? 0.1;
        payload["top_p"] = request.TopP ?? ReadPawaNumber(payload, "top_p") ?? 0.95;
        payload["max_tokens"] = request.MaxOutputTokens ?? ReadPawaNumber(payload, "max_tokens") ?? 4096;
        payload["frequency_penalty"] ??= 0.3;
        payload["presence_penalty"] ??= 0.3;
        payload["seed"] ??= 2024;
        payload["stream"] = false;
        if (request.ResponseFormat is not null)
            payload["response_format"] = JsonSerializer.SerializeToNode(request.ResponseFormat, PawaJson);

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/agents/chat/request")
        {
            Content = new StringContent(payload.ToJsonString(PawaJson), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsurePawaSuccess(response, raw, "agent request");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var text = ExtractPawaAgentText(root);
        var model = $"{AgentModelPrefix}{referenceId}/{languageModel}".ToModelId(GetIdentifier());
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = text,
                                Metadata = new() { ["pawaai.raw"] = root }
                            }
                        ],
                        Metadata = new() { ["pawaai.raw"] = root }
                    }
                ],
                Metadata = new() { ["pawaai.raw"] = root }
            },
            Metadata = new()
            {
                ["pawaai.agent.reference_id"] = referenceId,
                ["pawaai.agent.id"] = agent.Id,
                ["pawaai.agent.raw"] = root,
                ["pawaai.agent.request"] = JsonSerializer.SerializeToElement(payload, PawaJson)
            }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamPawaAgentAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecutePawaAgentAsync(request, cancellationToken);
        var text = response.Output?.Items?.SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>().Select(item => item.Text).FirstOrDefault() ?? string.Empty;
        await foreach (var item in StreamPawaBufferedResponse(request, response, text, cancellationToken))
            yield return item;
    }

    private static List<JsonObject> BuildPawaAgentMessages(AIRequest request)
    {
        var result = new List<JsonObject>();
        foreach (var item in request.Input?.Items ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Role))
                continue;
            var content = new JsonArray();
            foreach (var part in item.Content ?? [])
            {
                if (part is AITextContentPart text && !string.IsNullOrEmpty(text.Text))
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                else if (part is AIFileContentPart file && file.Data is not null)
                    content.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = PawaFileDataAsString(file) }
                    });
            }
            if (content.Count > 0)
                result.Add(new JsonObject { ["role"] = item.Role, ["content"] = content });
        }
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(request.Input?.Text))
            result.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.Input.Text })
            });
        return result;
    }

    private static string ExtractPawaAgentText(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("request", out var requests)
            || requests.ValueKind != JsonValueKind.Array)
            return string.Empty;
        return string.Join("\n", requests.EnumerateArray().Select(item =>
            item.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
                ? content.GetString()
                : null).Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static double? ReadPawaNumber(JsonObject payload, string name)
        => payload[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
