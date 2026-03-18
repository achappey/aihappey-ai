using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.Contracts;
using System.Net;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/skills")]
public class SkillsController(IAISkillProviderResolver resolver) : ControllerBase
{
    private readonly IAISkillProviderResolver _resolver = resolver;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? after,
        [FromQuery] int? limit,
        [FromQuery] string? order,
        CancellationToken cancellationToken)
        => Ok(await _resolver.ResolveSkills(after, limit, order, cancellationToken));

    [HttpGet("{skillId}/content")]
    public async Task<IActionResult> GetContent(string skillId, CancellationToken cancellationToken)
    {
        var bundle = await _resolver.RetrieveSkillContent(WebUtility.UrlDecode(skillId), cancellationToken);
        return File(bundle, "application/zip", $"{skillId}.zip");
    }
}

