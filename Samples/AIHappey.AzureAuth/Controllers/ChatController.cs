using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Telemetry;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.AzureAuth.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Contracts;
using AIHappey.Core.Http;
using AIHappey.Core.Diagnostics;

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
        using var writer = new ChatSseWriter(HttpContext);
        var requestedModelId = chatRequest.Model;


        FinishUIPart? finishUIPart = null;
        IModelProvider? provider = null;
        var startedAt = DateTime.UtcNow;

        try
        {
            provider = await _resolver.Resolve(requestedModelId, cancellationToken);

            Response.ContentType = "text/event-stream";
            Response.Headers["x-vercel-ai-ui-message-stream"] = "v1";
            using var responsesDebug = ResponsesTransportDebugObserver.Begin(HttpContext);
            chatRequest.Tools = [.. chatRequest.Tools?.DistinctBy(a => a.Name) ?? []];
            chatRequest.Model = chatRequest.Model.SplitModelId().Model;
            chatRequest.Messages = chatRequest.Messages.NormalizeToolInvocations();
            chatRequest.Headers = Request.Headers
                .Select(h => new KeyValuePair<string, string?>(h.Key, h.Value.ToString()))
                .GetProviderPassthroughHeaders(provider.GetIdentifier());

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

                    await writer.WriteAsync(streamPart, cancellationToken);
                }
            }
        }
        catch (TaskCanceledException e)
        {
            await writer.WriteAsync(e.Message.ToAbortUIPart(), cancellationToken);
        }
        catch (Exception e)
        {
            await writer.WriteAsync(e.Message.ToErrorUIPart(), cancellationToken);
        }

        if (finishUIPart != null && provider != null)
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
                HttpContext.GetAgentId(),
                cancellationToken: cancellationToken);
        }

        return new EmptyResult();
    }
}

