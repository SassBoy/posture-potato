using System.IO.Compression;

namespace PosturePotato;

/// <summary>
/// Renders the potato into icon bytes (ICO for Windows, PNG for Linux), so no image files
/// ship with the app. Pure managed code: no UI toolkit needed.
/// </summary>
internal static class IconFactory
{
    public static readonly int[] TraySizes = { 16, 20, 24, 32, 40, 48, 64 };
    public static readonly int[] AppSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static byte[] BuildIco(int[] sizes)
    {
        var entries = sizes.Select(s => s >= 256 ? RenderPng(s) : RenderDib(s)).ToArray();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(entries[i].Length); w.Write(offset);
            offset += entries[i].Length;
        }
        foreach (var e in entries) w.Write(e);
        return ms.ToArray();
    }

    /// <summary>The potato at size x size, straight-alpha ARGB (0xAARRGGBB), row-major.
    /// Whole multiples stay crisp; small sizes are box-averaged so the outline survives.</summary>
    public static uint[] Render(int size)
    {
        var pose = new PotatoPose { Eyes = EyeState.Open, Mouth = MouthState.Smile };
        var src = PotatoArt.RenderIconSprite(pose); // 32 x 32
        var dst = new uint[size * size];

        if (size >= 32)
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    dst[y * size + x] = src[(y * 32 / size) * 32 + x * 32 / size];
            return dst;
        }

        // Area average in premultiplied space.
        double scale = 32.0 / size;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double a = 0, r = 0, g = 0, b = 0, area = 0;
            double y0 = y * scale, y1 = y0 + scale, x0 = x * scale, x1 = x0 + scale;
            for (int sy = (int)y0; sy < Math.Ceiling(y1); sy++)
            for (int sx = (int)x0; sx < Math.Ceiling(x1); sx++)
            {
                double wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                double wx = Math.Min(x1, sx + 1) - Math.Max(x0, sx);
                double wgt = wx * wy;
                uint c = src[sy * 32 + sx];
                double ca = (c >> 24) / 255.0;
                a += ca * wgt;
                r += ((c >> 16) & 0xFF) * ca * wgt;
                g += ((c >> 8) & 0xFF) * ca * wgt;
                b += (c & 0xFF) * ca * wgt;
                area += wgt;
            }
            a /= area;
            if (a <= 0) continue;
            uint A = (uint)Math.Round(a * 255);
            uint R = (uint)Math.Clamp(Math.Round(r / area / a), 0, 255);
            uint G = (uint)Math.Clamp(Math.Round(g / area / a), 0, 255);
            uint B = (uint)Math.Clamp(Math.Round(b / area / a), 0, 255);
            dst[y * size + x] = (A << 24) | (R << 16) | (G << 8) | B;
        }
        return dst;
    }

    /// <summary>Classic 32-bit icon entry (BITMAPINFOHEADER + BGRA bottom-up + empty AND mask);
    /// Windows' small-icon loaders don't read PNG entries below 256 px.</summary>
    private static byte[] RenderDib(int size)
    {
        var px = Render(size);
        int maskStride = ((size + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40); w.Write(size); w.Write(size * 2); w.Write((short)1); w.Write((short)32);
        w.Write(0); w.Write(size * size * 4 + maskStride * size); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = size - 1; y >= 0; y--)
            for (int x = 0; x < size; x++)
                w.Write(px[y * size + x]); // little-endian 0xAARRGGBB = B, G, R, A
        w.Write(new byte[maskStride * size]);
        return ms.ToArray();
    }

    /// <summary>An RGBA PNG of the potato.</summary>
    public static byte[] RenderPng(int size)
    {
        var px = Render(size);
        var raw = new byte[size * (size * 4 + 1)];
        int i = 0;
        for (int y = 0; y < size; y++)
        {
            raw[i++] = 0; // filter: none
            for (int x = 0; x < size; x++)
            {
                uint c = px[y * size + x];
                raw[i++] = (byte)(c >> 16); raw[i++] = (byte)(c >> 8); raw[i++] = (byte)c; raw[i++] = (byte)(c >> 24);
            }
        }

        byte[] idat;
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true)) zs.Write(raw);
            idat = z.ToArray();
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)size); WriteBE(ihdr, 4, (uint)size);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", idat);
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        WriteBE(head, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        uint crc = Crc32(head.AsSpan(4, 4), data);
        var tail = new byte[4];
        WriteBE(tail, 0, crc);
        s.Write(tail);
    }

    private static void WriteBE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
