using System.Buffers.Binary;
using System.Text.Json;
using ASCOM.Common.Alpaca;

namespace SigmaFp.Alpaca;

/// <summary>
/// Writes ImageArray responses straight from row-major pixels, without building a 240 MB int[,].
/// Alpaca arrays are [x, y] serialised with the rightmost index fastest, i.e. column by column.
/// </summary>
public static class ImageArrayWriter
{
    public const string ImageBytesMime = "application/imagebytes";
    const int HeaderSize = 44;

    /// <summary>Alpaca ImageBytes, metadata version 1, Int32 image transmitted as UInt16.</summary>
    public static byte[] ToImageBytes(ushort[] pixels, int width, int height, uint clientTx, uint serverTx)
    {
        var b = new byte[HeaderSize + 2L * width * height];
        var h = b.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h, 1);                                        // MetadataVersion
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], 0);                                   // ErrorNumber
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], clientTx);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], serverTx);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], HeaderSize);                         // DataStart
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], (int)ImageArrayElementTypes.Int32);  // ImageElementType
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], (int)ImageArrayElementTypes.UInt16); // TransmissionElementType
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], 2);                                  // Rank
        BinaryPrimitives.WriteInt32LittleEndian(h[32..], width);
        BinaryPrimitives.WriteInt32LittleEndian(h[36..], height);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], 0);

        Parallel.For(0, width, x =>
        {
            var col = b.AsSpan(HeaderSize + 2 * x * height, 2 * height);
            for (int y = 0; y < height; y++) BinaryPrimitives.WriteUInt16LittleEndian(col[(2 * y)..], pixels[y * width + x]);
        });
        return b;
    }

    /// <summary>JSON ImageArray (Type 2 = Int32, Rank 2), streamed.</summary>
    public static async Task WriteJsonAsync(Stream body, ushort[] pixels, int width, int height, uint clientTx, uint serverTx)
    {
        await using var w = new Utf8JsonWriter(body, new JsonWriterOptions { SkipValidation = true });
        w.WriteStartObject();
        w.WriteNumber("Type", 2);
        w.WriteNumber("Rank", 2);
        w.WriteStartArray("Value");
        for (int x = 0; x < width; x++)
        {
            w.WriteStartArray();
            for (int y = 0; y < height; y++) w.WriteNumberValue(pixels[y * width + x]);
            w.WriteEndArray();
            if (w.BytesPending > 1 << 20) await w.FlushAsync();
        }
        w.WriteEndArray();
        w.WriteNumber("ClientTransactionID", clientTx);
        w.WriteNumber("ServerTransactionID", serverTx);
        w.WriteNumber("ErrorNumber", 0);
        w.WriteString("ErrorMessage", "");
        w.WriteEndObject();
        await w.FlushAsync();
    }
}
