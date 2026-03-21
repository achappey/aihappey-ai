using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.Contracts;

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

    [HttpGet("{providerId}/{skillId}/versions")]
    [Authorize]
    public async Task<IActionResult> GetVersions(
        string providerId,
        string skillId,
        [FromQuery] string? after,
        [FromQuery] int? limit,
        [FromQuery] string? order,
        CancellationToken cancellationToken)
        => Ok(await _resolver.ResolveSkillVersions($"{providerId}/{skillId}", after, limit, order, cancellationToken));

    [HttpGet("{providerId}/{skillId}/content")]
    [Authorize]
    public async Task<IActionResult> GetContent(
        string providerId,
        string skillId,
        CancellationToken cancellationToken)
    {
        var bundle = await _resolver.RetrieveSkillContent($"{providerId}/{skillId}", cancellationToken);

        return File(bundle, "application/zip", $"{skillId}.zip");
    }

    [HttpGet("{providerId}/{skillId}/versions/{version}/content")]
    [Authorize]
    public async Task<IActionResult> GetVersionContent(
        string providerId,
        string skillId,
        string version,
        CancellationToken cancellationToken)
    {
        var bundle = await _resolver.RetrieveSkillVersionContent($"{providerId}/{skillId}", version, cancellationToken);

        return File(bundle, "application/zip", $"{skillId}-{version}.zip");
    }

}

