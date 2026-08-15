using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Inference;

internal static class InferenceMcpHelpers
{
    public static async Task<(IModelProvider Provider, string Model)> ResolveAsync(
        string model,
        string prompt,
        int? maxOutputTokens,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("'model' is required.");

        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("'prompt' is required.");

        if (maxOutputTokens is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), "'maxOutputTokens' must be greater than zero when provided.");

        var provider = await services.GetRequiredService<IAIModelProviderResolver>().Resolve(model, cancellationToken);
        return (provider, model.SplitModelId().Model);
    }

    public static async Task SendProgressAsync(
        RequestContext<CallToolRequestParams> requestContext,
        int progress,
        string? message)
    {
        var progressToken = requestContext.Params?.ProgressToken;
        if (progressToken is null || string.IsNullOrEmpty(message))
            return;

        await requestContext.Server.SendNotificationAsync(
            "notifications/progress",
            new ProgressNotificationParams
            {
                ProgressToken = progressToken.Value,
                Progress = new ProgressNotificationValue
                {
                    Progress = progress,
                    Message = message
                }
            },
            cancellationToken: CancellationToken.None);
    }

    public static string? Append(Dictionary<string, StringBuilder> buffers, string key, string? delta)
    {
        if (string.IsNullOrEmpty(delta))
            return null;

        if (!buffers.TryGetValue(key, out var buffer))
            buffers[key] = buffer = new StringBuilder();

        buffer.Append(delta);
        return buffer.ToString();
    }
}
