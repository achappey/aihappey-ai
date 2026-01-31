using Microsoft.AspNetCore.Mvc;
using AIHappey.Common.Model;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("api/generate")]
public class UIController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] UIRequest requestDto, CancellationToken cancellationToken)
    {
        var provider = await resolver.Resolve(requestDto.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        ChatRequest chatRequest = requestDto.ToChatRequest();
        Response.ContentType = "text/plain; charset=utf-8";

        await foreach (var part in provider.StreamAsync(chatRequest, cancellationToken))
        {
            if (part is TextDeltaUIMessageStreamPart deltaUIMessageStreamPart)
            {
                await Response.WriteAsync(
                            deltaUIMessageStreamPart.Delta,
                            cancellationToken
                        );

                await Response.Body.FlushAsync(cancellationToken);
            }
        }

        return new EmptyResult();
    }
}

