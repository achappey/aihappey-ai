using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenAI;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TranscriptionController(IOptions<AIServiceConfig> config) : ControllerBase
{
    [HttpPost("transcribe")]
    [Authorize]
    public async Task<IActionResult> Transcribe([FromForm] IFormFile audio)
    {
        if (audio == null || audio.Length == 0)
            return BadRequest("No audio file uploaded.");

        using var content = new MultipartFormDataContent();
        using var stream = audio.OpenReadStream();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
        content.Add(fileContent, "audio", audio.FileName);
        var openClient = new OpenAIClient(
                  config.Value.OpenAI?.ApiKey);
        var audioClient = openClient.GetAudioClient("gpt-4o-transcribe");
        var result = await audioClient.TranscribeAudioAsync(stream, audio.FileName, new OpenAI.Audio.AudioTranscriptionOptions());

        return new JsonResult(result.Value);
    }
}
