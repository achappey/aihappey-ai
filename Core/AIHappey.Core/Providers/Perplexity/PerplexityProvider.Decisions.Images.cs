using System.Buffers.Binary;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    private static void ValidateImage(string? url)
    {
        var comma = url?.IndexOf(',') ?? -1;
        if (comma < 0 || url![..comma].ToLowerInvariant() is not ("data:image/png;base64" or "data:image/jpeg;base64" or "data:image/webp;base64"))
            throw new ArgumentException("Perplexity decision images require PNG, JPEG, or WebP base64 data URLs; remote URLs and GIF are unsupported.");
        if (url.Length > MaxBodyBytes) throw new ArgumentException("Decision image exceeds the 32 MiB request limit.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(url[(comma + 1)..]); }
        catch (FormatException ex) { throw new ArgumentException("Invalid base64 decision image.", ex); }
        var (width, height, mime) = ImageDimensions(bytes);
        if (!url[..comma].Equals($"data:image/{mime};base64", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Image MIME type does not match its bytes.");
        if (width <= 0 || height <= 0 || ((long)width + 31) / 32 * (((long)height + 31) / 32) > 2048)
            throw new ArgumentException("Perplexity decision images must fit within 2,048 tiles of 32 × 32 pixels; resize the image before sending.");
    }

    // Inspect dimensions without decompressing potentially huge images or adding a native image dependency.
    private static (int Width, int Height, string Mime) ImageDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            return (BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4)), "png");
        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216)
        {
            var offset = 2;
            while (offset + 4 <= bytes.Length)
            {
                if (bytes[offset++] != 255) break;
                while (offset < bytes.Length && bytes[offset] == 255) offset++;
                if (offset >= bytes.Length) break;
                var marker = bytes[offset++];
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
                if (marker is 0xD9 or 0xDA || offset + 2 > bytes.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
                if (length < 2 || offset + length > bytes.Length) break;
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC) && length >= 7)
                    return (BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2)), BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2)), "jpeg");
                offset += length;
            }
        }
        if (bytes.Length >= 30 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            if (bytes.Slice(12, 4).SequenceEqual("VP8X"u8)) return (1 + Read24(bytes.Slice(24, 3)), 1 + Read24(bytes.Slice(27, 3)), "webp");
            if (bytes.Slice(12, 4).SequenceEqual("VP8 "u8) && bytes.Slice(23, 3).SequenceEqual(new byte[] { 157, 1, 42 }))
                return (BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(26, 2)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(28, 2)) & 0x3FFF, "webp");
            if (bytes.Slice(12, 4).SequenceEqual("VP8L"u8) && bytes[20] == 0x2F)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(21, 4));
                return (1 + (int)(bits & 0x3FFF), 1 + (int)((bits >> 14) & 0x3FFF), "webp");
            }
        }
        throw new ArgumentException("Cannot read PNG, JPEG, or WebP decision image dimensions.");
    }
    private static int Read24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
}
