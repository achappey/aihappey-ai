using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Http;
using AIHappey.Core.Diagnostics;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest chatRequest, CancellationToken cancellationToken)
    {
        HeaderAuthModelContext.SetActiveProvider(HttpContext, chatRequest.Model);
        var provider = await _resolver.Resolve(chatRequest.Model);

        Response.ContentType = "text/event-stream";
        Response.Headers["x-vercel-ai-ui-message-stream"] = "v1";
        // Debug delivery can start the response immediately. Subscribe only after
        // resolution and header setup, keeping discovery failures non-streaming.
        using var writer = new ChatSseWriter(HttpContext);
        using var responsesDebug = ResponsesTransportDebugObserver.Begin(HttpContext);
        chatRequest.Tools = [.. chatRequest.Tools?.DistinctBy(a => a.Name) ?? []];
        chatRequest.Model = chatRequest.Model.SplitModelId().Model;
        chatRequest.Messages = chatRequest.Messages.NormalizeToolInvocations();
        chatRequest.Headers = Request.Headers
            .Select(h => new KeyValuePair<string, string?>(h.Key, h.Value.ToString()))
            .GetProviderPassthroughHeaders(provider.GetIdentifier());

        try
        {
            await foreach (var response in provider.StreamAsync(chatRequest, cancellationToken))
            {
                if (response != null)
                {
                    await writer.WriteAsync(response, cancellationToken);
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


        return new EmptyResult();
    }
}

