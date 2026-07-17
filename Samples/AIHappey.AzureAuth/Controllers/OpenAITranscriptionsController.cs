using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using System.Text.Json;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/audio/transcriptions")]
public class OpenAITranscriptionsController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    [Authorize]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        OpenAITranscriptionRequest? requestDto = null;

        try
        {
            requestDto = (await Request.ReadFormAsync(cancellationToken)).ToAudioTranscriptionRequest();
            requestDto.ValidateOpenAITranscriptionRequest();

            var provider = await resolver.Resolve(requestDto.Model, cancellationToken);
            if (provider == null)
                return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

            var responseFormat = requestDto.ResolveOpenAITranscriptionResponseFormat();
            var transcriptionRequest = await requestDto.ToTranscriptionRequest(
                requestDto.Model.SplitModelId().Model,
                provider.GetIdentifier(),
                cancellationToken);

            var content = await provider.TranscriptionRequest(transcriptionRequest, cancellationToken);

            if (requestDto.Stream == true)
            {
                Response.ContentType = "text/event-stream";

                await using var writer = new StreamWriter(Response.Body);
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(new OpenAITranscriptionTextDelta
                {
                    Delta = content.Text
                })}\n\n");

                await writer.WriteAsync($"data: {JsonSerializer.Serialize(new OpenAITranscriptionTextDone
                {
                    Text = content.Text
                })}\n\n");

                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(CancellationToken.None);

                return new EmptyResult();
            }

            if (responseFormat == "text")
                return Content(content.Text, "text/plain");

            return Ok(content.ToOpenAITranscriptionResponse(responseFormat));
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new
            {
                error = new
                {
                    message = ex.Message,
                    type = "invalid_request_error",
                    code = (string?)null
                }
            });
        }
        catch (Exception ex)
        {
            if (requestDto?.Stream == true)
            {
                try
                {
                    await Response.WriteAsync(
                        $"data: {JsonSerializer.Serialize(new
                        {
                            error = new
                            {
                                message = ex.Message,
                                type = "server_error",
                                code = (string?)null
                            }
                        })}\n\n");
                }
                catch
                {
                    // socket already gone
                }

                return new EmptyResult();
            }

            return BadRequest(new
            {
                error = new
                {
                    message = ex.Message,
                    type = "server_error",
                    code = (string?)null
                }
            });
        }
    }
}

