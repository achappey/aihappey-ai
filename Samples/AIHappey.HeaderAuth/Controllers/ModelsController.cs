using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/models")]
public class ModelsController(AIModelProviderResolver resolver) : ControllerBase
{
    private readonly AIModelProviderResolver _resolver = resolver;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => Ok(await _resolver.ResolveModels(cancellationToken));
}

