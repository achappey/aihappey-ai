using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using System.Text.Json;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/audio/speech")]
public class OpenAISpeechController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] AudioSpeechRequest requestDto, CancellationToken cancellationToken)
    {
        if (requestDto == null ||
            string.IsNullOrWhiteSpace(requestDto.Input) ||
            string.IsNullOrWhiteSpace(requestDto.Model) ||
            string.IsNullOrWhiteSpace(requestDto.Voice))
        {
            return BadRequest(new { error = "'input', 'model' and 'voice' are required fields" });
        }

        var provider = await resolver.Resolve(requestDto.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        requestDto.Model = requestDto.Model.SplitModelId().Model;

        try
        {
            if (requestDto.StreamFormat == "sse")
            {
                Response.ContentType = "text/event-stream";
                Response.Headers.CacheControl = "no-cache";

                try
                {
                    await foreach (var streamEvent in
                        provider.OpenAISpeechStreamingAsync(
                            requestDto,
                            cancellationToken))
                    {
                        var json = JsonSerializer.Serialize(
                            streamEvent,
                            streamEvent.GetType());

                        await Response.WriteAsync(
                            $"data: {json}\n\n",
                            cancellationToken);

                        await Response.Body.FlushAsync(cancellationToken);
                    }
                }
                catch (NotImplementedException) when (!Response.HasStarted)
                {
                    var speechRequest = requestDto.ToSpeechRequest();

                    var content = await provider.SpeechRequest(
                        speechRequest,
                        cancellationToken);

                    await Response.WriteAsync(
                        $"data: {JsonSerializer.Serialize(new AudioSpeechStreamDelta
                        {
                            Audio = content.Audio.Base64
                        })}\n\n",
                        cancellationToken);

                    await Response.WriteAsync(
                        $"data: {JsonSerializer.Serialize(new AudioSpeechStreamDone())}\n\n",
                        cancellationToken);

                    await Response.Body.FlushAsync(cancellationToken);
                }

                await Response.WriteAsync(
                    "data: [DONE]\n\n",
                    cancellationToken);

                await Response.Body.FlushAsync(cancellationToken);

                return new EmptyResult();
            }

            try
            {
                var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(
                    requestDto,
                    cancellationToken);

                return File(audio, mimeType);
            }
            catch (NotImplementedException)
            {
                var speechRequest = requestDto.ToSpeechRequest();

                var content = await provider.SpeechRequest(
                    speechRequest,
                    cancellationToken);

                var audioBytes = Convert.FromBase64String(
                    content.Audio.Base64);

                return File(
                    audioBytes,
                    content.Audio.MimeType);
            }
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            if (requestDto.StreamFormat == "sse")
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

                    await Response.Body.FlushAsync(CancellationToken.None);
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

