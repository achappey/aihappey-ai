using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/models")]
public class ModelsController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => Ok(await _resolver.ResolveModels(cancellationToken));
}

