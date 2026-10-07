using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Mvc;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/decisions")]
public sealed class DecisionsController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] DecisionRequest request, CancellationToken cancellationToken)
    {
        HeaderAuthModelContext.SetActiveProvider(HttpContext, request.Model);
        var provider = await resolver.Resolve(request.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{request.Model}' is not available." });

        request.Model = request.Model.SplitModelId().Model;
        try
        {
            return Ok(await provider.DecisionRequestAsync(request, cancellationToken));
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
