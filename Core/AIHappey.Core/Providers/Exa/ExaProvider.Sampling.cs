using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Responses;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = chatRequest.GetModel();

        if (IsResearchFastModel(model))
        {
            ApplyResearchAuthHeader();
            return await ResearchSamplingAsync(chatRequest, cancellationToken);
        }

        if (IsAnswerModel(model))
            return await this.ChatCompletionsSamplingAsync(chatRequest, cancellationToken);

        if (IsResearchModel(model))
            return await this.ResponsesSamplingAsync(chatRequest, cancellationToken);

        throw new NotSupportedException($"Unsupported Exa sampling model '{chatRequest?.GetModel()}'.");
    }

    private async Task<CreateMessageResult> ResponsesSamplingAsync(
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken)
    {
        var responseRequest = new ResponseRequest
        {
            Model = chatRequest.GetModel(),
            Temperature = chatRequest.Temperature,
            Input = BuildResponseInputFromSampling(chatRequest)
        };

        var response = await ResponsesAsync(responseRequest, cancellationToken);

        var outputText = ExtractOutputText(response);
        return new CreateMessageResult
        {
            Model = response.Model,
            Role = Role.Assistant,
            StopReason = response.Status ?? "stop",
            Content = [outputText.ToTextContentBlock()],
            Meta = new JsonObject
            {
                ["inputTokens"] = GetUsageInt(response.Usage, "prompt_tokens"),
                ["totalTokens"] = GetUsageInt(response.Usage, "total_tokens")
            }
        };
    }

    private async Task<CreateMessageResult> ResearchSamplingAsync(
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken)
    {
        var model = chatRequest.GetModel() ?? throw new ArgumentException("Model missing", nameof(chatRequest));
        var input = BuildPromptFromSamplingMessages(chatRequest.Messages);

        if (string.IsNullOrWhiteSpace(input))
        {
            return new CreateMessageResult
            {
                Model = model,
                Role = Role.Assistant,
                StopReason = "error",
                Content = ["Exa research requires non-empty input derived from sampling messages.".ToTextContentBlock()]
            };
        }

        object? outputSchema = null;
        var queued = await QueueResearchTaskAsync(input, model, outputSchema, cancellationToken);
        var completed = await WaitForResearchCompletionAsync(queued.ResearchId, cancellationToken);

        var text = ToOutputText(completed.Parsed ?? completed.Content);

        return new CreateMessageResult
        {
            Model = model,
            Role = Role.Assistant,
            StopReason = "stop",
            Content = [text.ToTextContentBlock()]
        };
    }

    private static string ExtractOutputText(ResponseResult response)
    {
        if (response.Output is null)
            return string.Empty;

        foreach (var item in response.Output)
        {
            var json = JsonSerializer.SerializeToElement(item, JsonSerializerOptions.Web);
            if (json.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in contentEl.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object)
                        continue;

                    if (part.TryGetProperty("type", out var typeEl)
                        && typeEl.ValueKind == JsonValueKind.String
                        && typeEl.GetString() == "output_text"
                        && part.TryGetProperty("text", out var textEl)
                        && textEl.ValueKind == JsonValueKind.String)
                    {
                        return textEl.GetString() ?? string.Empty;
                    }
                }
            }
        }

        return string.Empty;
    }

    private static int? GetUsageInt(object? usageObj, string propertyName)
    {
        if (usageObj is null)
            return null;

        try
        {
            var el = JsonSerializer.SerializeToElement(usageObj, JsonSerializerOptions.Web);
            if (el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty(propertyName, out var prop)
                && prop.ValueKind == JsonValueKind.Number)
            {
                return prop.GetInt32();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
