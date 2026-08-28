using System.Reflection;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Unified;

public sealed class ModelProviderUnifiedVideoSerializationTests
{
    [Fact]
    public void CompletedVideoToolResult_SerializesEmbeddedResourceBlobAsBase64Text()
    {
        byte[] expectedBytes = [0x00, 0x01, 0x02, 0x7F, 0x80, 0xFE, 0xFF];
        var expectedBase64 = Convert.ToBase64String(expectedBytes);
        var completed = new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = "video/mp4",
                    Data = expectedBase64
                }
            ]
        };

        var method = typeof(ModelProviderUnifiedVideoExtensions).GetMethod(
            "CreateCallToolResult",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(ModelProviderUnifiedVideoExtensions), "CreateCallToolResult");
        var result = Assert.IsType<CallToolResult>(method.Invoke(null, [completed, "operation-1"]));
        var json = JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web);
        var resource = json.GetProperty("content")[0].GetProperty("resource");

        Assert.Equal("video/mp4", resource.GetProperty("mimeType").GetString());
        var blob = resource.GetProperty("blob").GetString();
        Assert.Equal(expectedBase64, blob);
        Assert.Equal(expectedBytes, Convert.FromBase64String(blob!));
    }
}
