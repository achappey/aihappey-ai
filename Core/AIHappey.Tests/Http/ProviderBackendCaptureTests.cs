using System.Net;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AIHappey.Tests.TestInfrastructure;

namespace AIHappey.Tests.Http;

[Collection(BackendCaptureCollection.Name)]
public sealed class ProviderBackendCaptureTests
{
    [Theory]
    [InlineData("stream")]
    [InlineData("array")]
    [InlineData("json")]
    public async Task Concurrent_captures_generate_unique_paths_and_preserve_every_body(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "aihappey-backend-capture-tests", Guid.NewGuid().ToString("N"));
        var previous = ProviderBackendCapture.Current;
        try
        {
            ProviderBackendCapture.Configure(new ProviderBackendCaptureOptions
            {
                Enabled = true,
                DevelopmentOnly = false,
                RootDirectory = root
            });

            const int count = 128;
            var captures = await Task.WhenAll(Enumerable.Range(0, count)
                .Select(index => Task.Run(() => CaptureAsync(kind, index))));

            Assert.Equal(count, captures.Select(capture => capture.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(count, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
            foreach (var (path, index) in captures)
            {
                Assert.Equal(Path.Combine(root, "interactions", "raw"), Path.GetDirectoryName(path));
                Assert.StartsWith($"interactions-{(kind == "stream" ? "stream" : "response")}-unknown-host-", Path.GetFileName(path));
                Assert.Equal(kind == "stream" ? ".jsonl" : ".json", Path.GetExtension(path));
                var name = Path.GetFileNameWithoutExtension(path);
                Assert.True(Guid.TryParseExact(name[(name.LastIndexOf('-') + 1)..], "N", out _));

                // Exclusive access also verifies that capture writers have been disposed.
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                using var document = await JsonDocument.ParseAsync(file);
                var body = kind == "array" ? Assert.Single(document.RootElement.EnumerateArray()) : document.RootElement;
                Assert.Equal(index, body.GetProperty("index").GetInt32());
            }
        }
        finally
        {
            ProviderBackendCapture.Configure(previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("stream", "explicit", "explicit.jsonl")]
    [InlineData("array", "explicit", "explicit.json")]
    [InlineData("json", "explicit", "explicit.json")]
    [InlineData("stream", "explicit.custom", "explicit.custom")]
    [InlineData("array", "explicit.custom", "explicit.custom")]
    [InlineData("json", "explicit.custom", "explicit.custom")]
    public async Task Explicit_filename_overrides_keep_their_name_and_extension(string kind, string name, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "aihappey-backend-capture-tests", Guid.NewGuid().ToString("N"));
        var previous = ProviderBackendCapture.Current;
        try
        {
            ProviderBackendCapture.Configure(new ProviderBackendCaptureOptions
            {
                Enabled = false,
                DevelopmentOnly = false,
                RootDirectory = root
            });

            var capture = await CaptureAsync(kind, 42, new ProviderBackendCaptureRequest
            {
                Enabled = true,
                RelativeDirectory = "overrides",
                FileName = name
            });

            Assert.Equal(Path.Combine(root, "overrides", expected), capture.Path);
            Assert.Equal(capture.Path, Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories)));
        }
        finally
        {
            ProviderBackendCapture.Configure(previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(string Path, int Index)> CaptureAsync(string kind, int index, ProviderBackendCaptureRequest? request = null)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var body = JsonSerializer.Serialize(new { index });
        if (kind == "stream")
        {
            await using var sink = ProviderBackendCapture.BeginStreamCapture("interactions", response, request);
            Assert.NotNull(sink);
            await sink.WriteLineAsync(body);
            return (sink.FilePath, index);
        }
        if (kind == "array")
        {
            await using var sink = ProviderBackendCapture.BeginJsonArrayCapture("interactions", response, request);
            Assert.NotNull(sink);
            await sink.WriteRawJsonEntryAsync(body);
            return (sink.FilePath, index);
        }

        var path = await ProviderBackendCapture.CaptureJsonAsync("interactions", response, body, request);
        Assert.NotNull(path);
        return (path, index);
    }
}
