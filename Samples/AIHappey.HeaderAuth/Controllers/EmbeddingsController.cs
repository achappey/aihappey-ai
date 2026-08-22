using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Mvc;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/embeddings")]
public sealed class EmbeddingsController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] EmbeddingRequest request, CancellationToken cancellationToken)
    {
        HeaderAuthModelContext.SetActiveProvider(HttpContext, request.Model);
        var provider = await resolver.Resolve(request.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{request.Model}' is not available." });

        request.Model = request.Model.SplitModelId().Model;
        try
        {
            return Ok(await provider.EmbeddingRequestAsync(request, cancellationToken));
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
