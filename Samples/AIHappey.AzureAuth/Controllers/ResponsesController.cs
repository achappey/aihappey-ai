using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Core.Contracts;
using AIHappey.Responses.Streaming;
using AIHappey.AzureAuth.Extensions;
using AIHappey.Telemetry;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/responses")]
public class ResponsesController(IAIModelProviderResolver resolver, IChatTelemetryService chatTelemetryService) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] ResponseRequest requestDto, CancellationToken cancellationToken)
    {
        if (requestDto == null || requestDto.Input == null || string.IsNullOrWhiteSpace(requestDto.Model))
            return BadRequest(new { error = "'input' array and 'model' are required fields" });

        var provider = await _resolver.Resolve(requestDto.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        requestDto.Model = requestDto.Model.SplitModelId().Model;
        requestDto.Store = false;
        var startedAt = DateTime.UtcNow;

        if (requestDto.Stream == true)
        {
            Response.ContentType = "text/event-stream";
            ResponseCompleted? completed = null;

            try
            {
                await using var writer = new StreamWriter(Response.Body);

                await foreach (var chunk in provider.ResponsesStreamingAsync(requestDto, cancellationToken))
                {

                    if (chunk is ResponseCompleted responseCompleted)
                    {
                        completed = responseCompleted;
                    }

                    await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                    await writer.FlushAsync(CancellationToken.None);
                }

                // if we get here → it completed naturally
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(CancellationToken.None);

                if (completed != null)
                {
                    await TrackTelemetryAsync(
                        completed.Response?.Usage,
                        completed.Response?.Model?.SplitModelId().Model,
                        requestDto.Temperature ?? 1,
                        provider.GetIdentifier(),
                        startedAt,
                        cancellationToken);
                }

                return new EmptyResult();
            }
            catch (OperationCanceledException)
            {
                return new EmptyResult();
            }
            catch (Exception ex)
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
                    // ignore if socket already closed
                }

                return new EmptyResult();
            }

        }
        else
        {
            try
            {
                var content = await provider.ResponsesAsync(requestDto, cancellationToken);

                await TrackTelemetryAsync(
                    content?.Usage,
                    content?.Model?.SplitModelId().Model,
                    requestDto.Temperature ?? 1,
                    provider.GetIdentifier(),
                    startedAt,
                    cancellationToken);

                return Ok(content);
            }
            catch (OperationCanceledException)
            {
                return new EmptyResult();
            }
            catch (Exception e)
            {
                return StatusCode(500, new
                {
                    error = new
                    {
                        message = e.Message,
                        type = "server_error"
                    }
                });
            }
        }
    }

    private async Task TrackTelemetryAsync(
    object? usageObj,
    string? model,
    float temperature,
    string providerId,
    DateTime startedAt,
    CancellationToken cancellationToken)
    {
        int inputTokens = 0;
        int totalTokens = 0;

        if (usageObj is JsonElement usage)
        {
            if (usage.TryGetProperty("input_tokens", out var v))
                inputTokens = v.GetInt32();

            if (usage.TryGetProperty("total_tokens", out var v2))
                totalTokens = v2.GetInt32();
        }

        var endedAt = DateTime.UtcNow;

        await chatTelemetryService.TrackChatRequestAsync(
            new Vercel.Models.ChatRequest()
            {
                Model = model!,
                Temperature = temperature,
            },
            HttpContext.GetUserOid()!,
            HttpContext.GetUserUpn()!,
            inputTokens,
            totalTokens,
            providerId,
            Telemetry.Models.RequestType.Responses,
            startedAt,
            endedAt,
            cancellationToken: cancellationToken);
    }
}

