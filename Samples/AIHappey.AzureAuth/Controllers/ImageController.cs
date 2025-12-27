using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Common.Model;
using Microsoft.AspNetCore.Authorization;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/images/generations")]
public class ImageController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] ImageRequest requestDto, CancellationToken cancellationToken)
    {
        if (requestDto == null || string.IsNullOrWhiteSpace(requestDto.Model))
            return BadRequest(new { error = "'model' is a required field" });

        var provider = await _resolver.Resolve(requestDto.Model);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        requestDto.Model = requestDto.Model.SplitModelId().Model;
        try
        {
            var content = await provider.ImageRequest(requestDto, cancellationToken);

            return Ok(content);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
}

