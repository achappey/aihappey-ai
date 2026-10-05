using Microsoft.AspNetCore.Mvc;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/callbacks/{provider}")]
public class CallbackController : ControllerBase
{
    [HttpPost]
    public IActionResult Callback(string provider)
    {
        return Ok();
    }
}
