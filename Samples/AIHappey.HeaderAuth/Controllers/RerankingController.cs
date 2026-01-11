using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Common.Model;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/rerank")]
public class RerankingController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] RerankingRequest requestDto, CancellationToken cancellationToken)
    {
        var provider = await resolver.Resolve(requestDto.Model, cancellationToken);

        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        requestDto.Model = requestDto.Model.SplitModelId().Model;

        try
        {
            var content = await provider.RerankingRequest(requestDto, cancellationToken);

            return Ok(content);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
}

