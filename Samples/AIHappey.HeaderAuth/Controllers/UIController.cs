using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Common.Model;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using System.Text.Json;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/generate")]
public class UIController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] UIRequest requestDto, CancellationToken cancellationToken)
    {
        var provider = await resolver.Resolve(requestDto.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        ChatRequest chatRequest = new()
        {
            ProviderMetadata = requestDto.ProviderMetadata,
            Model = requestDto.Model.SplitModelId().Model,
            Messages =
                   [
                       new()
                {
                    Role = Role.system,
                    Parts =
                    [
                        new TextUIPart()
                        {
                            Text = JsonSerializer.Serialize(
                               requestDto.Context ?? new {},
                             JsonSerializerOptions.Web)
                        },
                        new TextUIPart()
                        {
                            Text = requestDto.CatalogPrompt
                        }
                    ]
                },
                new()
                {
                    Role = Role.user,
                    Parts =
                    [
                        new TextUIPart()
                        {
                            Text = requestDto.Prompt
                        }
                    ]
                }
                   ]
        };

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

