using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.AI;
using AIHappey.Common.Model.Responses;
using AIHappey.Core.ModelProviders;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("responses")]
public class ResponsesController(IAIModelProviderResolver resolver) : ControllerBase
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
        requestDto.Truncation = TruncationStrategy.Auto;

        if (requestDto.Stream == true)
        {
            Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(Response.Body);

            // Stream tokens or chunks, whatever your provider yields!
            await foreach (var chunk in provider.ResponsesStreamingAsync(requestDto, cancellationToken))
            {
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                await writer.FlushAsync(cancellationToken);
            }

            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(cancellationToken);
            return new EmptyResult();
        }
        else
        {
            try
            {
                var content = await provider.ResponsesAsync(requestDto, cancellationToken);

                return Ok(content);
            }
            catch (Exception e)
            {
                return BadRequest(e.Message);
            }
        }
    }
}

