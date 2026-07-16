using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/images/edits")]
public class OpenAIImageEditsController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        OpenAIImageEditRequest? requestDto = null;

        try
        {
            requestDto = Request.HasFormContentType
                ? (await Request.ReadFormAsync(cancellationToken)).ToOpenAIImageEditRequest()
                : await Request.ReadFromJsonAsync<OpenAIImageEditRequest>(cancellationToken) ?? new OpenAIImageEditRequest();

            requestDto.Model = requestDto.ResolveOpenAIImageEditModel();
            requestDto.ValidateOpenAIImageEditRequest();

            HeaderAuthModelContext.SetActiveProvider(HttpContext, requestDto.Model);
            var provider = await resolver.Resolve(requestDto.Model, cancellationToken);
            if (provider == null)
                return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

            requestDto.Model = requestDto.Model.SplitModelId().Model;

            if (requestDto.Stream == true)
                return await Stream(requestDto, provider, cancellationToken);

            try
            {
                var content = await provider.OpenAIImageEditRequestAsync(requestDto, cancellationToken);
                return Ok(content);
            }
            catch (NotImplementedException)
            {
                var imageRequest = await requestDto.ToImageRequest(requestDto.Model, provider.GetIdentifier(), cancellationToken);
                var content = await provider.ImageRequest(imageRequest, cancellationToken);
                return Ok(content.ToOpenAIImagesResponse(requestDto));
            }
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            if (requestDto?.Stream == true)
            {
                await WriteStreamError(ex.Message);
                return new EmptyResult();
            }

            return StatusCode(500, CreateError(ex.Message));
        }
    }

    private async Task<IActionResult> Stream(OpenAIImageEditRequest requestDto, IModelProvider provider, CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        try
        {
            try
            {
                await foreach (var streamEvent in provider.OpenAIImageEditStreamingAsync(requestDto, cancellationToken))
                    await WriteStreamEvent(streamEvent, cancellationToken);
            }
            catch (NotImplementedException) when (!Response.HasStarted)
            {
                var imageRequest = await requestDto.ToImageRequest(requestDto.Model!, provider.GetIdentifier(), cancellationToken);
                var content = await provider.ImageRequest(imageRequest, cancellationToken);

                foreach (var streamEvent in content.ToOpenAIImageEditCompletedEvents(requestDto))
                    await WriteStreamEvent(streamEvent, cancellationToken);
            }

            return new EmptyResult();
        }
        catch (Exception ex)
        {
            await WriteStreamError(ex.Message);
            return new EmptyResult();
        }
    }

    private async Task WriteStreamEvent(IOpenAIImageStreamEvent streamEvent, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(streamEvent, streamEvent.GetType());
        await Response.WriteAsync($"event: {streamEvent.Type}\n", cancellationToken);
        await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private async Task WriteStreamError(string message)
    {
        try
        {
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(CreateError(message))}\n\n", CancellationToken.None);
            await Response.Body.FlushAsync(CancellationToken.None);
        }
        catch
        {
            // Socket already gone.
        }
    }

    private static object CreateError(string message) => new
    {
        error = new
        {
            message,
            type = "server_error",
            code = (string?)null
        }
    };
}
