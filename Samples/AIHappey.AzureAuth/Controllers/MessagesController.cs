using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/messages")]
public class MessagesController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post(
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        if (!body.TryGetProperty("model", out var modelProp) ||
            !body.TryGetProperty("messages", out _) || 
            !body.TryGetProperty("max_tokens", out _))
        {
            return BadRequest(new { error = "'messages' and 'model' and 'max_tokens' are required" });
        }

        var model = modelProp.GetString();

        if (string.IsNullOrWhiteSpace(model))
            return BadRequest(new { error = "'model' is required" });

        var provider = await _resolver.Resolve(model);
        if (provider == null)
            return BadRequest(new { error = $"Model '{model}' is not available." });

        // strip provider prefix
        var cleanModel = model.SplitModelId().Model;

        // mutate JSON without static typing
        using var doc = JsonDocument.Parse(body.GetRawText());
        var root = doc.RootElement;

        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            root.GetRawText()
        )!;

        dict["model"] = cleanModel;

        var forwarded = JsonSerializer.SerializeToElement(dict);

        // streaming?
        if (body.TryGetProperty("stream", out var streamProp) &&
            streamProp.ValueKind == JsonValueKind.True)
        {
            Response.ContentType = "text/event-stream";

            await using var writer = new StreamWriter(Response.Body);

            await foreach (var chunk in provider.MessagesStreamingAsync(forwarded, cancellationToken))
            {
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                await writer.FlushAsync(cancellationToken);
            }

            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(cancellationToken);

            return new EmptyResult();
        }

        try
        {
            var result = await provider.MessagesAsync(forwarded, cancellationToken);
            return Ok(result);
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }
}