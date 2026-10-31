using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

[assembly: InternalsVisibleTo("AIHappey.Tests")]

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
{
    // Instance-scoped seam: tests provide a WebSocket, not a replacement public provider contract.
    internal Func<Uri, string, CancellationToken, Task<WebSocket>> TranscriptionSocketFactory { get; set; }
        = ConnectTranscriptionSocket;

    private static async Task<WebSocket> ConnectTranscriptionSocket(Uri uri, string key, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Token " + key);
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.InputAudioFormat?.Type))
            throw new ArgumentException("inputAudioFormat.type is required.");
        var options = ListenOptions.Create(request.Model, request.ProviderOptions, streaming: true);
        options.ValidateAudio(request.InputAudioFormat.Type,
            ProviderNumericConversions.ToInt32Exact(request.InputAudioFormat.Rate, "inputAudioFormat.rate"));
        var audio = DecodeTranscriptionAudio(request.Audio);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var socket = await TranscriptionSocketFactory(new Uri("wss://api.deepgram.com/v1/listen?" + options.Query),
            ResolveTranscriptionKey(), linked.Token);
        var upload = UploadTranscriptionAudio(socket, audio, linked);
        var finals = new List<ListenSpan>();
        var committed = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<JsonElement>();
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double? duration = null;
        var receivedMetadata = false;
        var cleanClose = false;
        try
        {
            yield return new TranscriptionStreamStartPart { Warnings = options.Warnings };
            while (true)
            {
                var message = await ReceiveTranscriptionMessage(socket, linked.Token);
                if (message == null)
                {
                    cleanClose = socket.CloseStatus == WebSocketCloseStatus.NormalClosure;
                    break;
                }
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                var type = ListenString(root, "type");
                if (request.IncludeRawChunks == true)
                    yield return new TranscriptionRawPart { RawValue = root.Clone() };
                if (type == "Results")
                {
                    if (!root.TryGetProperty("channel", out var channel)
                        || !channel.TryGetProperty("alternatives", out var alternatives) || alternatives.GetArrayLength() == 0) continue;
                    var alternative = alternatives[0];
                    var text = ListenString(alternative, "transcript") ?? "";
                    var start = ListenNumber(root, "start") ?? 0;
                    var length = ListenNumber(root, "duration") ?? 0;
                    var channelIndex = root.TryGetProperty("channel_index", out var indices) && indices.ValueKind == JsonValueKind.Array && indices.GetArrayLength() > 0
                        ? indices[0].GetInt32() : 0;
                    var id = FormattableString.Invariant($"{channelIndex}:{start:R}");
                    if (ListenString(channel, "detected_language") is { } language) languages.Add(language);
                    if (alternative.TryGetProperty("languages", out var langs) && langs.ValueKind == JsonValueKind.Array)
                        foreach (var lang in langs.EnumerateArray()) if (lang.ValueKind == JsonValueKind.String) languages.Add(lang.GetString()!);
                    var isFinal = root.TryGetProperty("is_final", out var final) && final.ValueKind == JsonValueKind.True;
                    if (isFinal)
                    {
                        // speech_final is endpointing, NOT request completion. Repeated committed results are ignored.
                        if (string.IsNullOrWhiteSpace(text) || !committed.Add(id)) continue;
                        finals.Add(new(text, start, start + length, channelIndex, null));
                        events.Add(root.Clone());
                        yield return new TranscriptionFinalPart
                        {
                            Id = id, Text = text, StartSecond = start, EndSecond = start + length,
                            ChannelIndex = channelIndex, ProviderMetadata = ListenMetadata(root)
                        };
                    }
                    else if (!committed.Contains(id))
                        yield return new TranscriptionPartialPart
                        {
                            Id = id, Text = text, StartSecond = start, DurationInSeconds = length,
                            ChannelIndex = channelIndex, ProviderMetadata = ListenMetadata(root)
                        };
                }
                else if (type is "Error" or "Warning")
                {
                    var nonfatal = type == "Warning" || root.TryGetProperty("fatal", out var fatal) && fatal.ValueKind == JsonValueKind.False;
                    yield return new TranscriptionErrorPart { Error = root.Clone() };
                    if (!nonfatal) throw new InvalidOperationException("Deepgram streaming failed: " + root);
                    events.Add(root.Clone());
                }
                else
                {
                    events.Add(root.Clone());
                    if (type == "Metadata")
                    {
                        receivedMetadata = true;
                        duration = ListenNumber(root, "duration") ?? duration;
                    }
                    // Includes SpeechStarted, UtteranceEnd, Configure confirmations and future metadata events.
                    yield return new TranscriptionResponseMetadataPart
                    {
                        Timestamp = DateTime.UtcNow, ModelId = request.Model, Body = root.Clone()
                    };
                }
            }
            // Never report success for early/error close or failed upload, even if a final transcript was received.
            if (!cleanClose || !receivedMetadata)
                throw new InvalidOperationException("Deepgram stream closed before upload, final metadata and clean completion were confirmed.");
            await upload;
            linked.Token.ThrowIfCancellationRequested();
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
            var ordered = SortListenSpans(finals);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TranscriptionFinishPart
            {
                Text = string.Join(" ", ordered.Select(s => s.Text)), Segments = ordered.Select(ToSegment).ToArray(),
                Language = languages.Count > 1 ? "multi" : languages.FirstOrDefault(),
                DurationInSeconds = duration,
                ProviderMetadata = ListenMetadata(JsonSerializer.SerializeToElement(new { events }))
            };
        }
        finally
        {
            linked.Cancel();
            socket.Abort(); // Unblock a pending send/receive on cancellation or early iterator disposal.
            try { await upload; } catch { /* Observe the upload task; do not hide the receive/cancellation failure. */ }
        }
    }

    private static async Task UploadTranscriptionAudio(WebSocket socket, byte[] audio, CancellationTokenSource linked)
    {
        try
        {
            // Only this task sends until it has completed. Fixed-size awaited sends bound in-flight audio.
            const int chunkSize = 32 * 1024;
            for (var offset = 0; offset < audio.Length; offset += chunkSize)
                await socket.SendAsync(new ArraySegment<byte>(audio, offset, Math.Min(chunkSize, audio.Length - offset)),
                    WebSocketMessageType.Binary, true, linked.Token);
            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"type\":\"CloseStream\"}")),
                WebSocketMessageType.Text, true, linked.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(60)); // Bound a server that never flushes/closes.
        }
        catch
        {
            linked.Cancel();
            throw;
        }
    }

    private static async Task<string?> ReceiveTranscriptionMessage(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (message.Length != 0) throw new InvalidOperationException("Deepgram closed within a fragmented message.");
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Unexpected Deepgram binary response.");
            message.Write(buffer, 0, result.Count);
            if (message.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Deepgram message exceeded the size limit.");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
        }
    }
}
