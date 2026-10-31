using System.Text;

namespace VitaSharp.Core.Util;

/// <summary>Minimal PNG reader for 8-bit RGB/RGBA non-interlaced images (the frame dumps).</summary>
public static class PngReader
{
    public static (byte[] Rgba, int W, int H) Read(string path)
    {
        var data = File.ReadAllBytes(path);
        int p = 8, w = 0, h = 0, colorType = 6;
        using var idat = new MemoryStream();
        while (p + 8 <= data.Length)
        {
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p));
            string type = Encoding.ASCII.GetString(data, p + 4, 4);
            var body = data.AsSpan(p + 8, len);
            if (type == "IHDR")
            {
                w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(body);
                h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                if (body[8] != 8 || body[12] != 0 || body[9] is not (2 or 6)) throw new NotSupportedException($"{path}: only 8-bit RGB/RGBA PNGs");
                colorType = body[9];
            }
            else if (type == "IDAT") idat.Write(body);
            else if (type == "IEND") break;
            p += 12 + len;
        }
        int bpp = colorType == 6 ? 4 : 3, stride = w * bpp;
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress)) z.ReadExactly(raw);
        var img = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            byte f = raw[y * (stride + 1)];
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            var dst = img.AsSpan(y * stride, stride);
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? dst[x - bpp] : 0, b = y > 0 ? img[(y - 1) * stride + x] : 0, c = x >= bpp && y > 0 ? img[(y - 1) * stride + x - bpp] : 0;
                int v = f switch
                {
                    0 => src[x],
                    1 => src[x] + a,
                    2 => src[x] + b,
                    3 => src[x] + (a + b) / 2,
                    4 => src[x] + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"{path}: bad PNG filter {f}"),
                };
                dst[x] = (byte)v;
            }
        }
        if (bpp == 4) return (img, w, h);
        var rgba = new byte[w * h * 4];
        for (int i = 0, j = 0; i < img.Length; i += 3, j += 4) { rgba[j] = img[i]; rgba[j + 1] = img[i + 1]; rgba[j + 2] = img[i + 2]; rgba[j + 3] = 255; }
        return (rgba, w, h);

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
