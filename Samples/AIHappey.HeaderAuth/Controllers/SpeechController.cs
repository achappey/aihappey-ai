using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/audio/speech")]
public class SpeechController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
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

