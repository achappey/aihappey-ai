using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("chat/completions")]
public class ChatCompletionsController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatCompletionOptions requestDto, CancellationToken cancellationToken)
    {
        if (requestDto == null || requestDto.Messages == null || string.IsNullOrWhiteSpace(requestDto.Model))
            return BadRequest(new { error = "'messages' array and 'model' are required fields" });

        var provider = await _resolver.Resolve(requestDto.Model);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        requestDto.Model = requestDto.Model.SplitModelId().Model;

        if (requestDto.Stream == true)
        {
            Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(Response.Body);

            // Stream tokens or chunks, whatever your provider yields!
            await foreach (var chunk in provider.CompleteChatStreamingAsync(requestDto, cancellationToken))
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
                var content = await provider.CompleteChatAsync(requestDto, cancellationToken);

                return Ok(content);
            }
            catch (Exception e)
            {
                return BadRequest(e.Message);
            }
        }
    }
}

