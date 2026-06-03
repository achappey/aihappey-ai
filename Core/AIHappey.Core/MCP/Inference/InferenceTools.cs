using System.ComponentModel;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Responses;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Inference;

[McpServerToolType]
public class InferenceTools
{
    [Description("Execute a non-streaming AI inference request using the unified responses endpoint.")]
    [McpServerTool(
        Title = "AI inference",
        Name = "ai_inference_execute",
        Destructive = false,
        Idempotent = false,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIInference_Execute(
        [Description("AI model identifier, including provider prefix.")] string model,
        [Description("Prompt to send to the model.")] string prompt,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        [Description("Optional instructions to guide the model response.")] string? instructions = null,
        [Description("Optional maximum number of output tokens.")] int? maxOutputTokens = null,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("'model' is required.");

            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("'prompt' is required.");

            if (maxOutputTokens is <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), "'maxOutputTokens' must be greater than zero when provided.");

            var resolver = services.GetRequiredService<IAIModelProviderResolver>();
            var provider = await resolver.Resolve(model, ct);

            var request = new ResponseRequest
            {
                Model = model.SplitModelId().Model,
                Input = new ResponseInput(prompt),
                Instructions = instructions,
                MaxOutputTokens = maxOutputTokens,
                Store = false,
                Stream = false
            };

            var result = await provider.ResponsesAsync(request, ct);

            return new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(result, ResponseJson.Default)
            };
        });
}

