using AIHappey.Interactions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private async IAsyncEnumerable<InteractionStreamEventPart> CreateGoogleAgentEnvironmentDownloadEventsAsync(
       string? environmentId,
       [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            yield break;

        List<GoogleAgentEnvironmentFileDownload> downloads;
        try
        {
            downloads = await DownloadGoogleAgentEnvironmentFilesAsync(environmentId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download Google agent environment snapshot {EnvironmentId}.", environmentId);
            yield break;
        }

        foreach (var download in downloads)
        {
            yield return new InteractionStepStartEvent
            {
                Index = download.StreamIndex,
                EventId = $"google-agent-download-input-{download.ToolCallId}",
                Step = new InteractionMcpServerToolCallContent
                {
                    Id = download.ToolCallId,
                    Name = GoogleAgentDownloadFileToolName,
                    Arguments = download.Input
                }
            };

            yield return new InteractionStepStopEvent
            {
                Index = download.StreamIndex,
                EventId = $"google-agent-download-input-stop-{download.ToolCallId}"
            };

            yield return new InteractionStepStartEvent
            {
                Index = download.StreamIndex + 1,
                EventId = $"google-agent-download-output-{download.ToolCallId}",
                Step = new InteractionMcpServerToolResultContent
                {
                    CallId = download.ToolCallId,
                    Name = GoogleAgentDownloadFileToolName,
                    Result = download.Output
                }
            };

            yield return new InteractionStepStopEvent
            {
                Index = download.StreamIndex + 1,
                EventId = $"google-agent-download-output-stop-{download.ToolCallId}"
            };
        }
    }

    private async Task<List<GoogleAgentEnvironmentFileDownload>> DownloadGoogleAgentEnvironmentFilesAsync(
        string environmentId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildGoogleAgentEnvironmentDownloadPath(environmentId));
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/x-tar");

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowGoogleAgentApiIfNotSuccess(response, cancellationToken);

        await using var tarStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ExtractGoogleAgentEnvironmentDownloadsAsync(environmentId, tarStream, cancellationToken);
    }

    private static async Task<List<GoogleAgentEnvironmentFileDownload>> ExtractGoogleAgentEnvironmentDownloadsAsync(
        string environmentId,
        Stream tarStream,
        CancellationToken cancellationToken)
    {
        var downloads = new List<GoogleAgentEnvironmentFileDownload>();
        var archiveUrl = BuildGoogleAgentEnvironmentDownloadCanonicalUrl(environmentId);

        using var reader = new TarReader(tarStream, leaveOpen: true);
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: true, cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.EntryType != TarEntryType.RegularFile
                || entry.DataStream is null
                || !TryNormalizeGoogleAgentArchiveEntryPath(entry.Name, out var entryPath))
            {
                continue;
            }

            if (ShouldSkipGoogleAgentArchiveEntryPath(entryPath))
                continue;

            await using var memory = new MemoryStream();
            await entry.DataStream.CopyToAsync(memory, cancellationToken);
            var bytes = memory.ToArray();
            var filename = Path.GetFileName(entryPath);
            if (string.IsNullOrWhiteSpace(filename))
                filename = entryPath.Replace('/', '_');

            var mediaType = ResolveGoogleAgentFileContentType(filename, entryPath);
            var dataUrl = ToGoogleAgentDataUrl(bytes, mediaType);
            var fileId = $"environment-{environmentId}:{entryPath}";
            var toolCallId = $"google-agent-download-{Guid.NewGuid():N}";
            var providerMetadata = BuildGoogleAgentDownloadProviderMetadata(
                environmentId,
                entryPath,
                filename,
                mediaType,
                fileId,
                archiveUrl,
                bytes.Length);
            var input = CreateGoogleAgentDownloadInput(environmentId, entryPath, filename, mediaType, archiveUrl);
            var output = CreateGoogleAgentDownloadToolCallResult(fileId, filename, mediaType, archiveUrl, dataUrl, bytes);

            downloads.Add(new GoogleAgentEnvironmentFileDownload
            {
                StreamIndex = 200_000 + (downloads.Count * 2),
                ToolCallId = toolCallId,
                Input = input,
                Output = output,
                ProviderMetadata = providerMetadata
            });
        }

        return downloads;
    }

    private static string? ExtractGoogleAgentEnvironmentId(InteractionStreamEventPart? evt)
        => evt switch
        {
            InteractionCreatedEvent { Interaction: not null } created => ExtractGoogleAgentEnvironmentId(created.Interaction),
            InteractionCompletedEvent { Interaction: not null } completed => ExtractGoogleAgentEnvironmentId(completed.Interaction),
            _ => null
        };

    private static string? ExtractGoogleAgentEnvironmentId(Interaction interaction)
    {
        if (interaction.AdditionalProperties is null)
            return null;

        foreach (var property in interaction.AdditionalProperties)
        {
            if (!string.Equals(property.Key, "environment_id", StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }

        return null;
    }

    private static string BuildGoogleAgentEnvironmentDownloadPath(string environmentId)
        => $"v1beta/files/environment-{Uri.EscapeDataString(environmentId)}:download?alt=media";

    private static string BuildGoogleAgentEnvironmentDownloadCanonicalUrl(string environmentId)
        => $"{GoogleAgentEnvironmentFilesUrlPrefix}{Uri.EscapeDataString(environmentId)}:download?alt=media";

    private static bool TryNormalizeGoogleAgentArchiveEntryPath(string? entryName, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(entryName))
            return false;

        var path = entryName.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains("../", StringComparison.Ordinal)
            || path.Equals("..", StringComparison.Ordinal)
            || Path.IsPathRooted(path))
        {
            return false;
        }

        normalizedPath = path;
        return true;
    }

    private static bool ShouldSkipGoogleAgentArchiveEntryPath(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return true;

        var path = normalizedPath.Replace('\\', '/').Trim();

        if (path.StartsWith("/", StringComparison.Ordinal))
            path = "." + path;
        else if (!path.StartsWith("./", StringComparison.Ordinal))
            path = "./" + path;

        return IsPathOrChild(path, "./bin")
               || IsPathOrChild(path, "./sbin")
               || IsPathOrChild(path, "./lib")
               || IsPathOrChild(path, "./lib64")
               || IsPathOrChild(path, "./usr")
               || IsPathOrChild(path, "./var")
               || IsPathOrChild(path, "./etc")
               || IsPathOrChild(path, "./boot")
               || IsPathOrChild(path, "./dev")
               || IsPathOrChild(path, "./proc")
               || IsPathOrChild(path, "./sys")
               || IsPathOrChild(path, "./run")
               || IsPathOrChild(path, "./opt");

        static bool IsPathOrChild(string path, string folder)
        {
            return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ResolveGoogleAgentFileContentType(string filename, string entryPath)
    {
        if (GoogleAgentFileContentTypeProvider.TryGetContentType(filename, out var byFilename))
            return byFilename;

        if (GoogleAgentFileContentTypeProvider.TryGetContentType(entryPath, out var byPath))
            return byPath;

        return "application/octet-stream";
    }

    private static JsonElement CreateGoogleAgentDownloadInput(
        string environmentId,
        string path,
        string filename,
        string mediaType,
        string archiveUrl)
        => JsonSerializer.SerializeToElement(new
        {
            environment_id = environmentId,
            path,
            file_name = filename,
            file_type = mediaType,
            google_environment_snapshot_url = archiveUrl
        }, GoogleAgentJsonOptions);

    private static CallToolResult CreateGoogleAgentDownloadToolCallResult(
        string fileId,
        string filename,
        string mediaType,
        string archiveUrl,
        string dataUrl,
        byte[] bytes)
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                file_id = fileId,
                filename,
                media_type = mediaType,
                url = archiveUrl,
                data_url = dataUrl
            }, GoogleAgentJsonOptions)
        };

        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            result.Content =
            [
                ImageContentBlock.FromBytes(bytes, mediaType)
            ];

            return result;
        }

        if (IsGoogleAgentTextLikeMediaType(mediaType))
        {
            result.Content =
            [
                new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents
                    {
                        Uri = archiveUrl,
                        MimeType = mediaType,
                        Text = Encoding.UTF8.GetString(bytes)
                    }
                }
            ];

            return result;
        }

        result.Content =
        [
            new EmbeddedResourceBlock
            {
                Resource = new BlobResourceContents
                {
                    Uri = archiveUrl,
                    MimeType = mediaType,
                    Blob = bytes
                }
            }
        ];

        return result;
    }

    private static Dictionary<string, Dictionary<string, object>> BuildGoogleAgentDownloadProviderMetadata(
        string environmentId,
        string path,
        string filename,
        string mediaType,
        string fileId,
        string archiveUrl,
        int bytes)
        => new()
        {
            [GoogleExtensions.Identifier()] = new Dictionary<string, object>
            {
                ["type"] = "google_agent_environment_file_download",
                ["tool_name"] = GoogleAgentDownloadFileToolName,
                ["name"] = GoogleAgentDownloadFileToolName,
                ["download_tool"] = true,
                ["download_url"] = archiveUrl,
                ["environment_id"] = environmentId,
                ["file_id"] = fileId,
                ["filename"] = filename,
                ["path"] = path,
                ["media_type"] = mediaType,
                ["bytes"] = bytes
            }
        };

    private static Dictionary<string, JsonElement> ToGoogleAgentJsonElementDictionary(Dictionary<string, object?> source)
        => source
            .Where(pair => pair.Value is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(pair.Value, GoogleAgentJsonOptions));

    private static bool IsGoogleAgentTextLikeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return false;

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("csv", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToGoogleAgentDataUrl(byte[] bytes, string? mediaType)
        => $"data:{mediaType ?? "application/octet-stream"};base64,{Convert.ToBase64String(bytes)}";

    private sealed class GoogleAgentEnvironmentFileDownload
    {
        public int StreamIndex { get; init; }

        public string ToolCallId { get; init; } = string.Empty;

        public JsonElement Input { get; init; }

        public CallToolResult Output { get; init; } = new();

        public Dictionary<string, Dictionary<string, object>> ProviderMetadata { get; init; } = [];
    }

}
