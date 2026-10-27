using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("api/speech/stream")]
public sealed class StreamingSpeechController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    [Authorize]
    public async Task Post([FromBody] StreamingSpeechRequest request, CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
            await foreach (var part in provider.SpeechStreamingAsync(request, cancellationToken))
            {
                await Response.WriteAsync($"data: {JsonSerializer.Serialize(part)}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
            await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            StreamingSpeechPart error = new SpeechErrorPart { Error = ex.Message };
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(error)}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }
}
