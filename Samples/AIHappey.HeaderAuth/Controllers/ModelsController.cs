using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/models")]
public class ModelsController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => Ok(await _resolver.ResolveModels(cancellationToken));
}

