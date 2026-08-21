using System.Buffers.Binary;
using System.IO.Compression;

namespace OfflineFingerprint.Collector.Services;

public static class PngEncoder
{
    public static byte[] EncodeGrayscale(byte[] gray, int width, int height)
    {
        if (width <= 0 || height <= 0 || gray.Length != width * height) throw new ArgumentException("Invalid grayscale image.");
        using var ms = new MemoryStream();
        ms.Write([137,80,78,71,13,10,26,10]);
        WriteChunk(ms, "IHDR", BuildIhdr(width, height));
        byte[] raw = new byte[height * (width + 1)];
        for (int y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = 0;
            Buffer.BlockCopy(gray, y * width, raw, y * (width + 1) + 1, width);
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true)) z.Write(raw);
        WriteChunk(ms, "IDAT", compressed.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }
    private static byte[] BuildIhdr(int width, int height)
    {
        byte[] b = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4, 4), height);
        b[8] = 8; b[9] = 0; b[10] = 0; b[11] = 0; b[12] = 0;
        return b;
    }
    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
        Span<byte> len = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(len, data.Length); s.Write(len);
        s.Write(t); s.Write(data);
        uint crc = 0xffffffff;
        foreach (byte v in t) crc = UpdateCrc(crc, v);
        foreach (byte v in data) crc = UpdateCrc(crc, v);
        crc ^= 0xffffffff;
        Span<byte> c = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(c, crc); s.Write(c);
    }
    private static uint UpdateCrc(uint crc, byte b)
    {
        crc ^= b;
        for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        return crc;
    }
}
