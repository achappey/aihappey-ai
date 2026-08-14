using System.IO.Compression;
using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.VLMRun;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.VLMRun;

public sealed class VLMRunProviderSkillsTests
{
    [Fact]
    public async Task RetrieveSkillContent_WrapsFlatArchiveInManifestNameFolder()
    {
        var provider = CreateProvider(CreateArchive(
            ("SKILL.md", ValidManifest("bank-statements")),
            ("references/FORMAT.md", "format reference"),
            ("scripts/extract.py", "print('ok')")));

        await using var result = await provider.RetrieveSkillContent("vlmrun/skill-123");

        Assert.Equal(
            ["bank-statements/SKILL.md", "bank-statements/references/FORMAT.md", "bank-statements/scripts/extract.py"],
            ReadArchivePaths(result));
        Assert.Equal("format reference", ReadArchiveText(result, "bank-statements/references/FORMAT.md"));
    }

    [Fact]
    public async Task RetrieveSkillContent_RepairsWrongSingleRootFolder()
    {
        var provider = CreateProvider(CreateArchive(
            ("upstream-folder/SKILL.md", ValidManifest("bank-statements")),
            ("upstream-folder/assets/template.csv", "date,amount")));

        await using var result = await provider.RetrieveSkillContent("skill-123");

        Assert.Equal(
            ["bank-statements/SKILL.md", "bank-statements/assets/template.csv"],
            ReadArchivePaths(result));
    }

    [Fact]
    public async Task RetrieveSkillVersionContent_NormalizesAlreadyCompliantArchive()
    {
        var provider = CreateProvider(
            CreateArchive(("bank-statements/SKILL.md", ValidManifest("bank-statements"))),
            skillVersion: "2");

        await using var result = await provider.RetrieveSkillVersionContent("vlmrun/skill-123", "2");

        Assert.Equal(["bank-statements/SKILL.md"], ReadArchivePaths(result));
    }

    [Theory]
    [MemberData(nameof(InvalidArchives))]
    public async Task RetrieveSkillContent_RejectsInvalidOrAmbiguousArchives(byte[] archive, string expectedReason)
    {
        var provider = CreateProvider(archive);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using var _ = await provider.RetrieveSkillContent("skill-123");
        });

        Assert.Contains(expectedReason, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<byte[], string> InvalidArchives => new()
    {
        { Encoding.UTF8.GetBytes("not a zip"), "not a valid ZIP archive" },
        { CreateArchive(("README.md", "no manifest")), "exactly one file named SKILL.md" },
        { CreateArchive(("SKILL.md", "no frontmatter")), "must start with YAML frontmatter" },
        { CreateArchive(("SKILL.md", ValidManifest("Invalid_Name"))), "must be 1-64 lowercase" },
        { CreateArchive(("SKILL.md", "---\nname: valid-name\ndescription:\n---\nbody")), "non-empty description" },
        {
            CreateArchive(
                ("one/SKILL.md", ValidManifest("one")),
                ("two/SKILL.md", ValidManifest("two"))),
            "exactly one file named SKILL.md"
        },
        {
            CreateArchive(
                ("wrong-root/SKILL.md", ValidManifest("valid-name")),
                ("outside.txt", "outside")),
            "files outside the folder"
        },
        {
            CreateArchive(
                ("SKILL.md", ValidManifest("valid-name")),
                ("valid-name/SKILL.md", ValidManifest("valid-name"))),
            "exactly one file named SKILL.md"
        },
    };

    private static VLMRunProvider CreateProvider(byte[] archive, string skillVersion = "latest")
    {
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/download", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "id": "skill-123",
                      "name": "Bank statements",
                      "skill_version": "latest",
                      "download_url": "https://downloads.example/skill.zip",
                      "expires_in": 60
                    }
                    """);
            }

            if (path == "/v1/skills/skill-123")
            {
                return JsonResponse($$"""
                    {
                      "id": "skill-123",
                      "name": "Bank statements",
                      "description": "Extract bank statements.",
                      "skill_version": "{{skillVersion}}"
                    }
                    """);
            }

            if (request.RequestUri?.Host == "downloads.example")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archive)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        return new VLMRunProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(handler)));
    }

    private static string ValidManifest(string name) => $$"""
        ---
        name: {{name}}
        description: Extracts and processes bank statements. Use when handling bank statement documents.
        ---
        # Instructions
        Process the statement.
        """;

    private static byte[] CreateArchive(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Content);
            }
        }

        return stream.ToArray();
    }

    private static string[] ReadArchivePaths(Stream stream)
    {
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        return archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry.FullName)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadArchiveText(Stream stream, string path)
    {
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry(path) ?? throw new InvalidOperationException($"Missing archive entry {path}.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
