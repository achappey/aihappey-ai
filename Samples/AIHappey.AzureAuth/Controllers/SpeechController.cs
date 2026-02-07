using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/audio/speech")]
public class SpeechController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] SpeechRequest requestDto, CancellationToken cancellationToken)
    {
        var provider = await resolver.Resolve(requestDto.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        requestDto.Model = requestDto.Model.SplitModelId().Model;
        try
        {
            var content = await provider.SpeechRequest(requestDto, cancellationToken);

            return Ok(content);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
}

