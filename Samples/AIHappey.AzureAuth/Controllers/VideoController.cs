using Microsoft.AspNetCore.Mvc;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.Contracts;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("api/videos")]
public class VideoController(
    IAIModelProviderResolver resolver,
    IEnumerable<IModelProvider> providers) : ControllerBase
{
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] VideoRequest requestDto, CancellationToken cancellationToken)
    {
        try
        {
            var provider = await resolver.Resolve(requestDto.Model, cancellationToken);
            requestDto.Model = requestDto.Model.SplitModelId().Model;
            var content = await provider.StartVideoOperation(requestDto, cancellationToken);
            content.Operation = content.Operation.ToModelId(provider.GetIdentifier());

            return Ok(content);
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{providerId}/{taskId}")]
    [Authorize]
    public async Task<IActionResult> GetStatus(
        string providerId,
        string taskId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(taskId))
            return BadRequest(new { error = "A provider ID and task ID are required." });

        var provider = providers.FirstOrDefault(candidate =>
            string.Equals(candidate.GetIdentifier(), providerId, StringComparison.OrdinalIgnoreCase));

        if (provider == null)
            return NotFound(new { error = $"Provider '{providerId}' is not available." });

        try
        {
            return Ok(await provider.GetVideoOperationStatus(taskId, cancellationToken));
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

