using System.ComponentModel.DataAnnotations;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Authorize]
[Route("api/realtime")]
public sealed class GenericRealtimeController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost("token/{**model}")]
    [ProducesResponseType(typeof(RealtimeTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> Token(
        [FromRoute, Required] string model, [FromBody] RealtimeTokenRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.Resolve(model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{model}' is not available." });

        try
        {
            return Ok(await provider.CreateRealtimeTokenAsync(model.SplitModelId().Model, request, cancellationToken));
        }
        catch (NotSupportedException ex)
        {
            return Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Not Implemented", detail: ex.Message);
        }
    }

    [HttpPost("session/{**model}")]
    [ProducesResponseType(typeof(RealtimeSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> Session(
        [FromRoute, Required] string model, [FromBody] RealtimeSessionRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.Resolve(model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{model}' is not available." });

        try
        {
            return Ok(await provider.CreateRealtimeSessionAsync(model.SplitModelId().Model, request, cancellationToken));
        }
        catch (NotSupportedException ex)
        {
            return Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Not Implemented", detail: ex.Message);
        }
    }
}
