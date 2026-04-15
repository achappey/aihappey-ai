using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Messages;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/messages")]
public class MessagesController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;
    private static readonly JsonSerializerOptions Json = MessagesJson.Default;

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post(
        [FromBody] MessagesRequest body,
        CancellationToken cancellationToken)
    {
        var model = body.Model;

        if (string.IsNullOrWhiteSpace(model))
            return BadRequest(new { error = "'model' is required" });

        var provider = await _resolver.Resolve(model);
        if (provider == null)
            return BadRequest(new { error = $"Model '{model}' is not available." });

        // strip provider prefix
        body.Model = model.SplitModelId().Model;

        var headers = Request.Headers
                    .Where(a => a.Key.StartsWith("anthropic-"))
                    .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        // streaming?
        if (body.Stream == true)
        {
            Response.ContentType = "text/event-stream";

            await using var writer = new StreamWriter(Response.Body);

            await foreach (var chunk in provider.MessagesStreamingAsync(body, headers, cancellationToken))
            {
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk, Json)}\n\n");
                await writer.FlushAsync(cancellationToken);
            }

            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(cancellationToken);

            return new EmptyResult();
        }

        try
        {
            var result = await provider.MessagesAsync(body, headers, cancellationToken);
            return Content(JsonSerializer.Serialize(result, Json), "application/json");
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }
}
