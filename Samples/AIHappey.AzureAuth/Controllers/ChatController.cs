using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Telemetry;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.AzureAuth.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Contracts;

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
        var requestedModelId = chatRequest.Model;
        var provider = await _resolver.Resolve(requestedModelId, cancellationToken);
        var startedAt = DateTime.UtcNow;

        Response.ContentType = "text/event-stream";
        Response.Headers["x-vercel-ai-ui-message-stream"] = "v1";
        chatRequest.Tools = [.. chatRequest.Tools?.DistinctBy(a => a.Name) ?? []];
        chatRequest.Model = chatRequest.Model.SplitModelId().Model;
        chatRequest.Messages = chatRequest.Messages.EnsureApprovals();

        FinishUIPart? finishUIPart = null;

        try
        {
            await foreach (var response in provider.StreamAsync(chatRequest, cancellationToken))
            {
                var streamPart = response;

                if (streamPart != null)
                {
                    if (streamPart is FinishUIPart finishUIPart1)
                    {
                        finishUIPart = finishUIPart1;
                        streamPart = finishUIPart;
                    }

                    await Response.WriteAsync($"data: {JsonSerializer.Serialize(streamPart, JsonSerializerOptions.Web)}\n\n", cancellationToken: cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                }
            }
        }
        catch (TaskCanceledException e)
        {
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(e.Message.ToAbortUIPart(), JsonSerializerOptions.Web)}\n\n", cancellationToken: cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        catch (Exception e)
        {
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(e.Message.ToErrorUIPart(), JsonSerializerOptions.Web)}\n\n", cancellationToken: cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }

        if (finishUIPart != null)
        {
            int inputTokens = finishUIPart.MessageMetadata?.Usage?.PromptTokens ?? 0;
            int totalTokens = finishUIPart.MessageMetadata?.Usage?.TotalTokens ?? 0;

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

