using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Core.ModelProviders;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/audio/transcriptions")]
public class TranscriptionsController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] TranscriptionRequest requestDto, CancellationToken cancellationToken)
    {
        var provider = await resolver.Resolve(requestDto.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        requestDto.Model = requestDto.Model.SplitModelId().Model;
        try
        {
            var content = await provider.TranscriptionRequest(requestDto, cancellationToken);

            return Ok(content);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
}

