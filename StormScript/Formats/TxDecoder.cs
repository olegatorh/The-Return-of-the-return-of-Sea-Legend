using System.Buffers.Binary;

namespace StormScript.Formats;

/// <summary>
/// Decoder for Storm Engine .tx textures: u32 flags, width, height, mip count, format (FourCC or D3DFORMAT), level-0 size,
/// then the mip chain. Only the top level is decoded, to RGBA8.
/// </summary>
public static class TxDecoder
{
    public sealed record Image(int Width, int Height, byte[] Rgba);

    public static Image Decode(byte[] b)
    {
        if (b.Length < 24) throw new InvalidDataException("tx too short");
        int w = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        int h = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        uint fmt = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
        var body = b.AsSpan(24);
        string fourcc = System.Text.Encoding.ASCII.GetString(b, 16, 4);
        byte[] rgba = fourcc switch
        {
            "DXT1" => Dxt(body, w, h, 1),
            "DXT3" => Dxt(body, w, h, 3),
            "DXT5" => Dxt(body, w, h, 5),
            _ => Raw(body, w, h, (int)fmt),
        };
        return new Image(w, h, rgba);
    }

    static void C565(int c, out byte r, out byte g, out byte b)
    {
        int rr = (c >> 11) & 31, gg = (c >> 5) & 63, bb = c & 31;
        r = (byte)((rr << 3) | (rr >> 2));
        g = (byte)((gg << 2) | (gg >> 4));
        b = (byte)((bb << 3) | (bb >> 2));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]       // hot loops: no tier-0 code for the first big textures
    static byte[] Dxt(ReadOnlySpan<byte> d, int w, int h, int kind)
    {
        var o = new byte[w * h * 4];
        int bs = kind == 1 ? 8 : 16, bw = (w + 3) / 4, bh = (h + 3) / 4, pos = 0;
        Span<byte> col = stackalloc byte[16];
        Span<byte> al = stackalloc byte[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                if (pos + bs > d.Length) return o;
                var blk = d.Slice(pos, bs);
                pos += bs;
                var cb = blk.Slice(bs - 8);
                int c0 = cb[0] | (cb[1] << 8), c1 = cb[2] | (cb[3] << 8);
                uint idx = BinaryPrimitives.ReadUInt32LittleEndian(cb.Slice(4));
                C565(c0, out byte r0, out byte g0, out byte b0);
                C565(c1, out byte r1, out byte g1, out byte b1);
                col[0] = r0; col[1] = g0; col[2] = b0; col[3] = 255;
                col[4] = r1; col[5] = g1; col[6] = b1; col[7] = 255;
                if (c0 > c1 || kind != 1)
                {
                    col[8] = (byte)((2 * r0 + r1) / 3); col[9] = (byte)((2 * g0 + g1) / 3); col[10] = (byte)((2 * b0 + b1) / 3); col[11] = 255;
                    col[12] = (byte)((r0 + 2 * r1) / 3); col[13] = (byte)((g0 + 2 * g1) / 3); col[14] = (byte)((b0 + 2 * b1) / 3); col[15] = 255;
                }
                else
                {
                    col[8] = (byte)((r0 + r1) / 2); col[9] = (byte)((g0 + g1) / 2); col[10] = (byte)((b0 + b1) / 2); col[11] = 255;
                    col[12] = 0; col[13] = 0; col[14] = 0; col[15] = 0;
                }
                if (kind == 3)
                {
                    ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(blk);
                    for (int i = 0; i < 16; i++) al[i] = (byte)(((bits >> (4 * i)) & 15) * 17);
                }
                else if (kind == 5)
                {
                    int a0 = blk[0], a1 = blk[1];
                    Span<int> tab = stackalloc int[8];
                    tab[0] = a0; tab[1] = a1;
                    if (a0 > a1) for (int i = 1; i < 7; i++) tab[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                    else { for (int i = 1; i < 5; i++) tab[i + 1] = ((5 - i) * a0 + i * a1) / 5; tab[6] = 0; tab[7] = 255; }
                    ulong bits = 0;
                    for (int i = 0; i < 6; i++) bits |= (ulong)blk[2 + i] << (8 * i);
                    for (int i = 0; i < 16; i++) al[i] = (byte)tab[(int)((bits >> (3 * i)) & 7)];
                }
                for (int i = 0; i < 16; i++)
                {
                    int px = bx * 4 + (i & 3), py = by * 4 + (i >> 2);
                    if (px >= w || py >= h) continue;
                    int ci = (int)((idx >> (2 * i)) & 3) * 4;
                    int oo = (py * w + px) * 4;
                    o[oo] = col[ci]; o[oo + 1] = col[ci + 1]; o[oo + 2] = col[ci + 2];
                    o[oo + 3] = kind == 1 ? col[ci + 3] : al[i];
                }
            }
        return o;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static byte[] Raw(ReadOnlySpan<byte> d, int w, int h, int fmt)
    {
        var o = new byte[w * h * 4];
        int n = w * h;
        if (fmt is 21 or 22)
        {
            for (int i = 0; i < n && i * 4 + 3 < d.Length; i++)
            {
                o[i * 4] = d[i * 4 + 2]; o[i * 4 + 1] = d[i * 4 + 1]; o[i * 4 + 2] = d[i * 4];
                o[i * 4 + 3] = fmt == 21 ? d[i * 4 + 3] : (byte)255;
            }
            return o;
        }
        if (fmt is not (23 or 25 or 26)) throw new InvalidDataException("unsupported tx format " + fmt);
        for (int i = 0; i < n && i * 2 + 1 < d.Length; i++)
        {
            int v = d[i * 2] | (d[i * 2 + 1] << 8);
            byte r, g, b, a = 255;
            if (fmt == 23) C565(v, out r, out g, out b);
            else if (fmt == 25) { r = (byte)(((v >> 10) & 31) * 255 / 31); g = (byte)(((v >> 5) & 31) * 255 / 31); b = (byte)((v & 31) * 255 / 31); a = (byte)((v & 0x8000) != 0 ? 255 : 0); }
            else { a = (byte)(((v >> 12) & 15) * 17); r = (byte)(((v >> 8) & 15) * 17); g = (byte)(((v >> 4) & 15) * 17); b = (byte)((v & 15) * 17); }
            o[i * 4] = r; o[i * 4 + 1] = g; o[i * 4 + 2] = b; o[i * 4 + 3] = a;
        }
        return o;
    }
}
