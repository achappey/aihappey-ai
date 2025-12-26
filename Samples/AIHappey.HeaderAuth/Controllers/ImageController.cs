using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Common.Model;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/images/generations")]
public class ImageController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ImageRequest requestDto, CancellationToken cancellationToken)
    {
        if (requestDto == null || string.IsNullOrWhiteSpace(requestDto.Model))
            return BadRequest(new { error = "'model' is a required field" });

        var provider = await _resolver.Resolve(requestDto.Model);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });
        
        requestDto.Model = requestDto.Model.SplitModelId().Model;
        var content = await provider.ImageRequest(requestDto, cancellationToken);

        return Ok(content);

    }
}

