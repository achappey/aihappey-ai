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

        if (requestDto.Stream == true)
        {
            Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(Response.Body);

            // Stream tokens or chunks, whatever your provider yields!
            await foreach (var chunk in provider.CompleteChatStreamingAsync(requestDto, cancellationToken))
            {
                // Each chunk is a string of text (token or partial content)
                var streamChunk = new
                {
                    id = Guid.NewGuid().ToString(),
                    @object = "chat.completion.chunk",
                    choices = new[] {
                        new {
                            delta = new { content = chunk },
                            index = 0,
                            finish_reason = (string?)null
                        }
                    }
                };
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(streamChunk)}\n\n");
                await writer.FlushAsync();
            }

            // Final finish chunk
            var doneChunk = new
            {
                id = Guid.NewGuid().ToString(),
                @object = "chat.completion.chunk",
                choices = new[] {
                    new {
                        delta = new { content = (string?)null },
                        index = 0,
                        finish_reason = "stop"
                    }
                }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(doneChunk)}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            return new EmptyResult();
        }
        else
        {
            // Non-streaming: collect output
            var content = await provider.CompleteChatAsync(requestDto, cancellationToken);

            /*      var text = string.Join("\n\n", content.Content
                      .Where(a => !string.IsNullOrEmpty(a.Text))
                      .Select(a => a.Text));

                  var response = new
                  {
                      id = Guid.NewGuid().ToString(),
                      @object = "chat.completion",
                      created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                      model = content.Model,
                      choices = new[] {
                          new {
                              index = 0,
                              message = new {
                                  role = "assistant",
                                  content = text
                              },
                              finish_reason = "stop"
                          }
                      },
                      usage = new
                      {
                          prompt_tokens = 0,
                          completion_tokens = 0,
                          total_tokens = 0
                      }
                  };*/

            return Ok(content);
        }
    }
}

