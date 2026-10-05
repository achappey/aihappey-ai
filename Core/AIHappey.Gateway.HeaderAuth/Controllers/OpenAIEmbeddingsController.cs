using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/embeddings")]
public sealed class OpenAIEmbeddingsController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] OpenAIEmbeddingRequest request, CancellationToken cancellationToken)
    {
        HeaderAuthModelContext.SetActiveProvider(HttpContext, request.Model);
        var provider = await resolver.Resolve(request.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = new { message = $"Model '{request.Model}' is not available.", type = "invalid_request_error" } });

        request.Model = request.Model.SplitModelId().Model;
        try
        {
            return Ok(await provider.OpenAIEmbeddingRequestAsync(request, cancellationToken));
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = new { message = ex.Message, type = "invalid_request_error" } });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = new { message = ex.Message, type = "server_error" } });
        }
    }
}
