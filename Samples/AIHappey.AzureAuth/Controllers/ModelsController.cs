using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.AI;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/models")]
public class ModelsController(AIModelProviderResolver resolver) : ControllerBase
{
    private readonly AIModelProviderResolver _resolver = resolver;

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => Ok(await _resolver.ResolveModels(cancellationToken));
}

