using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Common.Model;
using Microsoft.AspNetCore.Authorization;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/realtime/client_secrets")]
public class RealtimeController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] RealtimeRequest requestDto, CancellationToken cancellationToken)
    {
        var provider = await resolver.Resolve(requestDto.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        requestDto.Model = requestDto.Model.SplitModelId().Model;
        try
        {
            var content = await provider.GetRealtimeToken(requestDto, cancellationToken);

            return Ok(content);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
}

