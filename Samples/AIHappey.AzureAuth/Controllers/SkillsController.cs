using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.Contracts;
using System.Net;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/skills")]
public class SkillsController(IAISkillProviderResolver resolver) : ControllerBase
{
    private readonly IAISkillProviderResolver _resolver = resolver;

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Get(
        [FromQuery] string? after,
        [FromQuery] int? limit,
        [FromQuery] string? order,
        CancellationToken cancellationToken)
        => Ok(await _resolver.ResolveSkills(after, limit, order, cancellationToken));

    [HttpGet("{providerId}/{skillId}/content")]
    [Authorize]
    public async Task<IActionResult> GetContent(
    string skillId,
    string? providerId,
    CancellationToken cancellationToken)
    {
        var fullId = providerId is not null
            ? $"{providerId}/{skillId}"
            : skillId;

        var bundle = await _resolver.RetrieveSkillContent(fullId, cancellationToken);

        return File(bundle, "application/zip", $"{skillId}.zip");
    }

}

