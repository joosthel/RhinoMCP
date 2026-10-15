using System;
using System.IO;
using System.IO.Compression;

namespace RhinoMCP.Tools.View
{
    /// <summary>
    /// Minimal managed PNG reader/writer (8-bit gray/RGB/RGBA, non-interlaced). Exists because System.Drawing on
    /// Rhino 7 Mac sits on libgdiplus, which is unavailable/fragile in-process (GDI+ failures and finalizer
    /// exceptions can take Rhino down), so no System.Drawing objects are used by the view tools.
    /// </summary>
    internal sealed class Raster
    {
        public int W, H;
        public byte[] Rgb; // W*H*3

        public Raster(int w, int h, byte fill)
        {
            W = w; H = h; Rgb = new byte[w * h * 3];
            if (fill != 0) for (int i = 0; i < Rgb.Length; i++) Rgb[i] = fill;
        }

        /// <summary>Nearest-neighbour blit of src into the rectangle (x,y,w,h) of this raster.</summary>
        public void Blit(Raster src, int x, int y, int w, int h)
        {
            for (int j = 0; j < h; j++)
            {
                int dy = y + j; if (dy < 0 || dy >= H) continue;
                int sy = j * src.H / h;
                for (int i = 0; i < w; i++)
                {
                    int dx = x + i; if (dx < 0 || dx >= W) continue;
                    int sx = i * src.W / w;
                    int s = (sy * src.W + sx) * 3, d = (dy * W + dx) * 3;
                    Rgb[d] = src.Rgb[s]; Rgb[d + 1] = src.Rgb[s + 1]; Rgb[d + 2] = src.Rgb[s + 2];
                }
            }
        }

        public void Rect(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            for (int i = 0; i < w; i++) { Px(x + i, y, r, g, b); Px(x + i, y + h - 1, r, g, b); }
            for (int j = 0; j < h; j++) { Px(x, y + j, r, g, b); Px(x + w - 1, y + j, r, g, b); }
        }

        void Px(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return;
            int d = (y * W + x) * 3; Rgb[d] = r; Rgb[d + 1] = g; Rgb[d + 2] = b;
        }
    }

    internal static class Png
    {
        static readonly byte[] Sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static bool IsPng(byte[] b) { return b != null && b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47; }
        public static bool IsJpeg(byte[] b) { return b != null && b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF; }

        static int BE(byte[] b, int o) { return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]; }

        public static Raster Decode(byte[] data)
        {
            if (!IsPng(data)) throw new InvalidDataException("not a PNG");
            int pos = 8, w = 0, h = 0, depth = 0, ctype = 0, interlace = 0;
            var idat = new MemoryStream();
            byte[] palette = null;
            while (pos + 8 <= data.Length)
            {
                int len = BE(data, pos);
                string type = "" + (char)data[pos + 4] + (char)data[pos + 5] + (char)data[pos + 6] + (char)data[pos + 7];
                int body = pos + 8;
                if (type == "IHDR") { w = BE(data, body); h = BE(data, body + 4); depth = data[body + 8]; ctype = data[body + 9]; interlace = data[body + 12]; }
                else if (type == "PLTE") { palette = new byte[len]; Array.Copy(data, body, palette, 0, len); }
                else if (type == "IDAT") idat.Write(data, body, len);
                else if (type == "IEND") break;
                pos = body + len + 4;
            }
            if (depth != 8 || interlace != 0) throw new InvalidDataException("unsupported PNG (depth " + depth + ", interlace " + interlace + ")");
            int bpp;
            switch (ctype) { case 0: bpp = 1; break; case 2: bpp = 3; break; case 3: bpp = 1; break; case 4: bpp = 2; break; case 6: bpp = 4; break; default: throw new InvalidDataException("unsupported PNG color type " + ctype); }
            if (ctype == 3 && palette == null) throw new InvalidDataException("palette PNG without PLTE");

            int stride = w * bpp;
            var raw = new byte[(stride + 1) * h];
            idat.Position = 2; // skip zlib header
            using (var ds = new DeflateStream(idat, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < raw.Length) { int n = ds.Read(raw, got, raw.Length - got); if (n <= 0) break; got += n; }
                if (got < raw.Length) throw new InvalidDataException("truncated PNG data");
            }
            var cur = new byte[stride];
            var prev = new byte[stride];
            var ras = new Raster(w, h, 0);
            for (int y = 0; y < h; y++)
            {
                int f = raw[y * (stride + 1)];
                int ro = y * (stride + 1) + 1;
                for (int i = 0; i < stride; i++)
                {
                    int x = raw[ro + i];
                    int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    switch (f)
                    {
                        case 0: break;
                        case 1: x += a; break;
                        case 2: x += b; break;
                        case 3: x += (a + b) / 2; break;
                        case 4:
                            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                            x += (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c); break;
                        default: throw new InvalidDataException("bad PNG filter " + f);
                    }
                    cur[i] = (byte)x;
                }
                for (int x = 0; x < w; x++)
                {
                    int o = x * bpp, d = (y * w + x) * 3;
                    byte r, g, bl; int alpha = 255;
                    switch (ctype)
                    {
                        case 0: r = g = bl = cur[o]; break;
                        case 4: r = g = bl = cur[o]; alpha = cur[o + 1]; break;
                        case 3: r = palette[cur[o] * 3]; g = palette[cur[o] * 3 + 1]; bl = palette[cur[o] * 3 + 2]; break;
                        case 2: r = cur[o]; g = cur[o + 1]; bl = cur[o + 2]; break;
                        default: r = cur[o]; g = cur[o + 1]; bl = cur[o + 2]; alpha = cur[o + 3]; break;
                    }
                    if (alpha != 255) { r = (byte)((r * alpha + 255 * (255 - alpha)) / 255); g = (byte)((g * alpha + 255 * (255 - alpha)) / 255); bl = (byte)((bl * alpha + 255 * (255 - alpha)) / 255); }
                    ras.Rgb[d] = r; ras.Rgb[d + 1] = g; ras.Rgb[d + 2] = bl;
                }
                var t = prev; prev = cur; cur = t;
            }
            return ras;
        }

        public static byte[] Encode(Raster r)
        {
            int stride = r.W * 3;
            var raw = new byte[(stride + 1) * r.H];
            for (int y = 0; y < r.H; y++) Array.Copy(r.Rgb, y * stride, raw, y * (stride + 1) + 1, stride);
            byte[] z;
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x01);
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(raw, 0, raw.Length);
                uint ad = Adler(raw);
                ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16)); ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
                z = ms.ToArray();
            }
            using (var o = new MemoryStream())
            {
                o.Write(Sig, 0, 8);
                var ihdr = new byte[13];
                Put(ihdr, 0, r.W); Put(ihdr, 4, r.H); ihdr[8] = 8; ihdr[9] = 2;
                Chunk(o, "IHDR", ihdr);
                Chunk(o, "IDAT", z);
                Chunk(o, "IEND", new byte[0]);
                return o.ToArray();
            }
        }

        static void Put(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        static void Chunk(Stream s, string type, byte[] body)
        {
            var hdr = new byte[8];
            Put(hdr, 0, body.Length);
            for (int i = 0; i < 4; i++) hdr[4 + i] = (byte)type[i];
            s.Write(hdr, 0, 8);
            s.Write(body, 0, body.Length);
            uint crc = Crc(hdr, 4, 4, 0xFFFFFFFF);
            crc = Crc(body, 0, body.Length, crc) ^ 0xFFFFFFFF;
            var c = new byte[4]; Put(c, 0, (int)crc);
            s.Write(c, 0, 4);
        }

        static uint[] _tab;
        static uint Crc(byte[] b, int off, int len, uint crc)
        {
            if (_tab == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; }
                _tab = t;
            }
            for (int i = 0; i < len; i++) crc = _tab[(crc ^ b[off + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        static uint Adler(byte[] b)
        {
            uint a = 1, s = 0;
            for (int i = 0; i < b.Length; i++) { a = (a + b[i]) % 65521; s = (s + a) % 65521; }
            return (s << 16) | a;
        }

        /// <summary>Reads width/height from a PNG or JPEG header (0,0 if unknown).</summary>
        public static void Size(byte[] b, out int w, out int h)
        {
            w = h = 0;
            if (IsPng(b) && b.Length >= 24) { w = BE(b, 16); h = BE(b, 20); return; }
            if (IsJpeg(b))
            {
                int i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    int m = b[i + 1];
                    if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7) || m == 0xFF) { i += (m == 0xFF) ? 1 : 2; continue; }
                    int len = (b[i + 2] << 8) | b[i + 3];
                    if ((m >= 0xC0 && m <= 0xCF) && m != 0xC4 && m != 0xC8 && m != 0xCC)
                    { h = (b[i + 5] << 8) | b[i + 6]; w = (b[i + 7] << 8) | b[i + 8]; return; }
                    i += 2 + len;
                }
            }
        }
    }
}
