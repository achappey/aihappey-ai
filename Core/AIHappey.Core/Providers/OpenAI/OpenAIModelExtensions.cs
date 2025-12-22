using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Mime;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using Microsoft.AspNetCore.StaticFiles;
using OpenAI.Containers;
using OpenAI.Models;

namespace AIHappey.Core.Providers.OpenAI;

public static class OpenAIModelExtensions
{
    public static Dictionary<string, object> ToProviderMetadata(this Dictionary<string, object> metadata)
         => new()
         { { Constants.OpenAI, metadata } };

    public static Model ToModel(this OpenAIModel source) => new()
    {
        Id = source.Id.ToModelId(Constants.OpenAI),
        Name = source.Id,
        Created = source.CreatedAt.ToUnixTimeSeconds(),
        // Publisher = nameof(OpenAI),
        OwnedBy = nameof(OpenAI),
    };

    public static IEnumerable<Model> ToModels(this IEnumerable<OpenAIModel> source)
        => source.Select(a => a.ToModel());

    // ---- Public API ---------------------------------------------------------

    public static Task<ClientResult> UploadDataUriAsync(
        this ContainerClient containerClient,
        string containerId,
        string dataUri,
        string? explicitMimeType = null,
        string partName = "file",
        RequestOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(containerId)) throw new ArgumentNullException(nameof(containerId));
        if (string.IsNullOrWhiteSpace(dataUri)) throw new ArgumentNullException(nameof(dataUri));

        // Split header/payload
        int comma = dataUri.IndexOf(',');
        if (comma < 0) throw new FormatException("Invalid data URI (no comma).");

        string header = dataUri[..comma];     // e.g. "data:application/pdf;base64"
        string payload = dataUri[(comma + 1)..];

        // Detect mime
        string mimeType = explicitMimeType ?? "application/octet-stream";
        const string prefix = "data:";
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            int semi = header.IndexOf(';');
            if (semi > prefix.Length)
                mimeType = header.Substring(prefix.Length, semi - prefix.Length);
        }

        // Decode
        byte[] bytes = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(payload)
            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));

        return UploadBytesMultipartAsync(containerClient, containerId, bytes, mimeType, partName, options);
    }

    public static async Task<ClientResult> UploadBytesMultipartAsync(
        dynamic containerClient,
        string containerId,
        byte[] data,
        string mimeType,
        string partName = "file",
        RequestOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(containerId)) throw new ArgumentNullException(nameof(containerId));
        if (data is null || data.Length == 0) throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrWhiteSpace(mimeType)) mimeType = "application/octet-stream";

        // Filename: yyMMdd_HHmmss + extension resolved from MIME (built-in provider, no NuGet)
        string extension = ResolveExtensionFromMime(mimeType);
        string filename = $"{DateTime.UtcNow:yyMMdd_HHmmss}{extension}";

        // Build multipart body (single file part)
        string boundary = "----aihappey_" + Guid.NewGuid().ToString("N");
        byte[] body = BuildSingleFileMultipartBody(boundary, partName, filename, mimeType, data);

        // Wrap in BinaryContent and set the correct Content-Type with boundary
        var content = BinaryContent.Create(BinaryData.FromBytes(body));
        string contentType = $"multipart/form-data; boundary={boundary}";

        // You don’t need Content-Disposition on the request headers; multipart part has it.
        var req = options ?? new RequestOptions();

        return await containerClient.CreateContainerFileAsync(
            containerId,
            content,
            contentType,
            req
        );
    }

    // ---- Helpers ------------------------------------------------------------

    private static byte[] BuildSingleFileMultipartBody(
        string boundary,
        string partName,
        string filename,
        string fileMime,
        byte[] fileBytes)
    {
        var nl = "\r\n";
        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.UTF8.GetBytes(s));
        void WriteBytes(byte[] b) => ms.Write(b, 0, b.Length);

        Write($"--{boundary}{nl}");
        Write($"Content-Disposition: form-data; name=\"{partName}\"; filename=\"{filename}\"{nl}");
        Write($"Content-Type: {fileMime}{nl}");
        Write(nl);
        WriteBytes(fileBytes);
        Write(nl);
        Write($"--{boundary}--{nl}");

        return ms.ToArray();
    }

    /// <summary>
    /// Reverse lookup (MIME -> extension) using built-in provider. Fallback .bin if unknown.
    /// </summary>
    private static string ResolveExtensionFromMime(string mimeType)
    {
        var provider = new FileExtensionContentTypeProvider(); // extension -> mime
        foreach (var kvp in provider.Mappings)
        {
            if (string.Equals(kvp.Value, mimeType, StringComparison.OrdinalIgnoreCase))
                return kvp.Key; // e.g. ".xlsx"
        }
        return ".bin";
    }

    public static readonly HashSet<string> CodeInterpreterMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/x-c",
        "text/x-csharp",
        "text/x-c++",
        MediaTypeNames.Text.Csv,
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        MediaTypeNames.Text.Html,
        "text/x-java",
        MediaTypeNames.Application.Json,
        MediaTypeNames.Text.Markdown,
        MediaTypeNames.Application.Pdf,
        "text/x-php",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "text/x-python",
        "text/x-script.python",
        "text/x-ruby",
        "text/x-tex",
        MediaTypeNames.Text.Plain,
        MediaTypeNames.Text.Css,
        MediaTypeNames.Text.JavaScript,
        "application/x-sh",
        "application/typescript",
        "application/csv",
        MediaTypeNames.Image.Jpeg,
        MediaTypeNames.Image.Gif,
        MediaTypeNames.Application.Octet,
        MediaTypeNames.Image.Png,
        "application/x-tar",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        MediaTypeNames.Application.Xml,
        MediaTypeNames.Text.Xml,
        MediaTypeNames.Application.Zip
    };
}