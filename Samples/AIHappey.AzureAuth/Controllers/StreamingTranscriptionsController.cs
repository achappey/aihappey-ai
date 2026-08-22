using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("api/transcriptions/stream")]
public sealed class StreamingTranscriptionsController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    [Authorize]
    public async Task Post([FromBody] StreamingTranscriptionRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.Resolve(request.Model, cancellationToken);
        if (provider == null)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { error = $"Model '{request.Model}' is not available." }, cancellationToken);
            return;
        }

        request.Model = request.Model.SplitModelId().Model;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        try
        {
            await foreach (var part in provider.TranscriptionStreamingAsync(request, cancellationToken))
            {
                await Response.WriteAsync($"data: {JsonSerializer.Serialize(part)}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
            await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StreamingTranscriptionPart error = new TranscriptionErrorPart { Error = ex.Message };
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(error)}\n\n", CancellationToken.None);
            await Response.Body.FlushAsync(CancellationToken.None);
        }
    }
}
