using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Core.Contracts;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/responses")]
public class ResponsesController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ResponseRequest requestDto, CancellationToken cancellationToken)
    {
        if (requestDto == null || requestDto.Input == null || string.IsNullOrWhiteSpace(requestDto.Model))
            return BadRequest(new { error = "'input' array and 'model' are required fields" });

        HeaderAuthModelContext.SetActiveProvider(HttpContext, requestDto.Model);

        var provider = await _resolver.Resolve(requestDto.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        requestDto.Model = requestDto.Model.SplitModelId().Model;
        requestDto.Store = false;

        if (requestDto.Stream == true)
        {
            Response.ContentType = "text/event-stream";

            try
            {
                await using var writer = new StreamWriter(Response.Body);

                await foreach (var chunk in provider.ResponsesStreamingAsync(requestDto, cancellationToken))
                {
                    await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                    await writer.FlushAsync(CancellationToken.None); // important
                }

                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(CancellationToken.None);

                return new EmptyResult();
            }
            catch (OperationCanceledException)
            {
                // client disconnected → silent exit
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
                    // socket already gone
                }

                return new EmptyResult();
            }
        }
        else
        {
            try
            {
                var content = await provider.ResponsesAsync(requestDto, cancellationToken);
                return Ok(content);
            }
            catch (OperationCanceledException)
            {
                return new EmptyResult();
            }
            catch (Exception ex)
            {
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
}

