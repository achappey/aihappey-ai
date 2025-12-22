using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(AIModelProviderResolver resolver) : ControllerBase
{
    private readonly AIModelProviderResolver _resolver = resolver;

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest chatRequest, CancellationToken cancellationToken)
    {
        var provider = await _resolver.Resolve(chatRequest.Model);

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


        return new EmptyResult();
    }
}

