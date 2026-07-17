using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using System.Text.Json;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/audio/transcriptions")]
public class OpenAITranscriptionsController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        OpenAITranscriptionRequest? requestDto = null;

        try
        {
            requestDto = (
                await Request.ReadFormAsync(cancellationToken))
                .ToAudioTranscriptionRequest();

            requestDto.ValidateOpenAITranscriptionRequest();

            HeaderAuthModelContext.SetActiveProvider(
                HttpContext,
                requestDto.Model);

            var provider = await resolver.Resolve(
                requestDto.Model,
                cancellationToken);

            if (provider == null)
            {
                return BadRequest(new
                {
                    error = $"Model '{requestDto.Model}' is not available."
                });
            }

            var responseFormat =
                requestDto.ResolveOpenAITranscriptionResponseFormat();

            requestDto.Model = requestDto.Model.SplitModelId().Model;

            if (requestDto.Stream == true)
            {
                Response.ContentType = "text/event-stream";
                Response.Headers.CacheControl = "no-cache";

                try
                {
                    await foreach (var streamEvent in
                        provider.OpenAITranscriptionStreamingAsync(
                            requestDto,
                            cancellationToken))
                    {
                        var json = JsonSerializer.Serialize(
                            streamEvent,
                            streamEvent.GetType());

                        await Response.WriteAsync(
                            $"data: {json}\n\n",
                            cancellationToken);

                        await Response.Body.FlushAsync(
                            cancellationToken);
                    }
                }
                catch (NotImplementedException) when (!Response.HasStarted)
                {
                    var transcriptionRequest =
                        await requestDto.ToTranscriptionRequest(
                            requestDto.Model,
                            provider.GetIdentifier(),
                            cancellationToken);

                    var content = await provider.TranscriptionRequest(
                        transcriptionRequest,
                        cancellationToken);

                    await Response.WriteAsync(
                        $"data: {JsonSerializer.Serialize(
                            new OpenAITranscriptionTextDelta
                            {
                                Delta = content.Text
                            })}\n\n",
                        cancellationToken);

                    await Response.WriteAsync(
                        $"data: {JsonSerializer.Serialize(
                            new OpenAITranscriptionTextDone
                            {
                                Text = content.Text
                            })}\n\n",
                        cancellationToken);

                    await Response.Body.FlushAsync(
                        cancellationToken);
                }

                await Response.WriteAsync(
                    "data: [DONE]\n\n",
                    cancellationToken);

                await Response.Body.FlushAsync(
                    cancellationToken);

                return new EmptyResult();
            }

            IOpenAITranscriptionResponse response;

            try
            {
                response =
                    await provider.OpenAITranscriptionRequestAsync(
                        requestDto,
                        cancellationToken);
            }
            catch (NotImplementedException)
            {
                var transcriptionRequest =
                    await requestDto.ToTranscriptionRequest(
                        requestDto.Model,
                        provider.GetIdentifier(),
                        cancellationToken);

                var content = await provider.TranscriptionRequest(
                    transcriptionRequest,
                    cancellationToken);

                response = content.ToOpenAITranscriptionResponse(
                    responseFormat);
            }

            return responseFormat switch
            {
                "text" => Content(
                    response.Text,
                    "text/plain"),

                "srt" => Content(
                    response.Text,
                    "application/x-subrip"),

                "vtt" => Content(
                    response.Text,
                    "text/vtt"),

                _ => Ok(response)
            };
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
                        })}\n\n",
                        CancellationToken.None);

                    await Response.Body.FlushAsync(
                        CancellationToken.None);
                }
                catch
                {
                    // Socket already gone.
                }

                return new EmptyResult();
            }

            return StatusCode(500, new
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

