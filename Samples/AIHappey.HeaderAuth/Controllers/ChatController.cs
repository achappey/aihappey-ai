using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Extensions;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest chatRequest, CancellationToken cancellationToken)
    {
        var provider = await _resolver.Resolve(chatRequest.Model);

        Response.ContentType = "text/event-stream";
        Response.Headers["x-vercel-ai-ui-message-stream"] = "v1";
        chatRequest.Tools = [.. chatRequest.Tools?.DistinctBy(a => a.Name) ?? []];
        chatRequest.Model = chatRequest.Model.SplitModelId().Model;
        chatRequest.Messages = chatRequest.Messages.EnsureApprovals();

        try
        {
            await foreach (var response in provider.StreamAsync(chatRequest, cancellationToken))
            {
                if (response != null)
                {
                    await Response.WriteAsync($"data: {JsonSerializer.Serialize(response, JsonSerializerOptions.Web)}\n\n", cancellationToken: cancellationToken);

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


        return new EmptyResult();
    }
}

