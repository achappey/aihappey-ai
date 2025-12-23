using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Common.Model;
using AIHappey.Telemetry;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(IAIModelProviderResolver resolver, IChatTelemetryService chatTelemetryService) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] ChatRequest chatRequest, CancellationToken cancellationToken)
    {
        var provider = await _resolver.Resolve(chatRequest.Model, cancellationToken);
        var startedAt = DateTime.UtcNow;

        Response.ContentType = "text/event-stream";
        Response.Headers["x-vercel-ai-ui-message-stream"] = "v1";
        chatRequest.Tools = [.. chatRequest.Tools?.DistinctBy(a => a.Name) ?? []];
        chatRequest.Model = chatRequest.Model.SplitModelId().Model;

        FinishUIPart? finishUIPart = null;

        try
        {
            await foreach (var response in provider.StreamAsync(chatRequest, cancellationToken))
            {
                if (response != null)
                {
                    if (response is FinishUIPart finishUIPart1)
                    {
                        finishUIPart = finishUIPart1;
                    }

                    await Response.WriteAsync($"data: {JsonSerializer.Serialize(response, JsonSerializerOptions.Web)}\n\n", cancellationToken: cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                }
            }
        }
        catch (Exception e)
        {
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(e.Message.ToErrorUIPart(), JsonSerializerOptions.Web)}\n\n", cancellationToken: cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }

        if (finishUIPart != null)
        {
            int inputTokens = 0;

            if (finishUIPart?.MessageMetadata != null &&
                finishUIPart.MessageMetadata.TryGetValue("inputTokens", out var val) &&
                val is int i)
            {
                inputTokens = i;
            }

            int totalTokens = 0;

            if (finishUIPart?.MessageMetadata != null &&
                finishUIPart.MessageMetadata.TryGetValue("totalTokens", out var valTotal) &&
                valTotal is int iOut)
            {
                totalTokens = iOut;
            }

            var endedAt = DateTime.UtcNow;

            await chatTelemetryService.TrackChatRequestAsync(chatRequest,
                HttpContext.GetUserOid()!,
                HttpContext.GetUserUpn()!,
                inputTokens,
                totalTokens,
                provider.GetIdentifier(),
                Telemetry.Models.RequestType.Chat,
                startedAt,
                endedAt,
                cancellationToken: cancellationToken);
        }

        return new EmptyResult();
    }
}

