using System.Buffers.Binary;

namespace Fuseo.Core.Export;

/// <summary>
/// 自前のベースライン JPEG エンコーダ（JFIF・YCbCr 4:4:4・ITU-T T.81 Annex K の標準テーブル）。
/// 書くマーカーは SOI・APP0(JFIF)・DQT・SOF0・DHT・SOS・EOI **だけ**＝EXIF・XMP・サムネイル・ICC・コメントを
/// 構造的に持ち得ない（docs/windows-design.md §4.2）。4:4:4 にするのは書類の細い文字の色にじみを避けるため。
/// 入力は不透明 RGB（<see cref="RgbImage"/>）。
/// </summary>
public static class JpegEncoder
{
    public static byte[] Encode(RgbImage img, double quality)
    {
        if (!(quality > 0 && quality <= 1)) throw new ArgumentOutOfRangeException(nameof(quality), "品質は 0 より大きく 1 以下");
        if (img.Width > 65535 || img.Height > 65535) throw new ArgumentException("JPEG の寸法上限（65535px）を超えています");

        var qLuma = ScaleTable(StdLumaQuant, quality);
        var qChroma = ScaleTable(StdChromaQuant, quality);
        var coeffs = Transform(img, qLuma, qChroma);

        using var ms = new MemoryStream();
        WriteHeaders(ms, img, qLuma, qChroma);
        WriteScan(ms, coeffs, BlocksOf(img));
        ms.WriteByte(0xFF); ms.WriteByte(0xD9); // EOI
        return ms.ToArray();
    }

    // ---- テーブル（自然順＝行優先。行=縦方向の周波数） ----

    internal static readonly byte[] StdLumaQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    internal static readonly byte[] StdChromaQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    /// <summary>ジグザグ順の k 番目が自然順のどこか。</summary>
    internal static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    internal static readonly byte[] DcLumaBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    internal static readonly byte[] DcChromaBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    internal static readonly byte[] DcVals = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    internal static readonly byte[] AcLumaBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    internal static readonly byte[] AcLumaVals =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    internal static readonly byte[] AcChromaBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    internal static readonly byte[] AcChromaVals =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    /// <summary>libjpeg と同じ品質スケーリング（quality 0..1 → 1..100）。</summary>
    internal static byte[] ScaleTable(byte[] std, double quality)
    {
        var q = Math.Clamp((int)Math.Round(quality * 100), 1, 100);
        var scale = q < 50 ? 5000 / q : 200 - 2 * q;
        var t = new byte[64];
        for (var i = 0; i < 64; i++) t[i] = (byte)Math.Clamp((std[i] * scale + 50) / 100, 1, 255);
        return t;
    }

    // ---- 変換（色空間→DCT→量子化）。ブロック行ごとに並列 ----

    private static (int Bw, int Bh) BlocksOf(RgbImage img) => ((img.Width + 7) / 8, (img.Height + 7) / 8);

    private static readonly double[] Cos = BuildCos();

    private static double[] BuildCos()
    {
        var c = new double[64]; // c[u*8+x] = C(u)/2 * cos((2x+1)uπ/16)
        for (var u = 0; u < 8; u++)
        for (var x = 0; x < 8; x++)
            c[u * 8 + x] = (u == 0 ? 1 / Math.Sqrt(2) : 1) / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16);
        return c;
    }

    /// <summary>係数をブロック×成分×64（ジグザグ順）の short 配列で返す。</summary>
    private static short[] Transform(RgbImage img, byte[] qLuma, byte[] qChroma)
    {
        var (bw, bh) = BlocksOf(img);
        var coeffs = new short[checked(bw * bh * 3 * 64)];
        Parallel.For(0, bh, by =>
        {
            var ycc = new double[3][] { new double[64], new double[64], new double[64] };
            var tmp = new double[64];
            for (var bx = 0; bx < bw; bx++)
            {
                for (var y = 0; y < 8; y++)
                {
                    var sy = Math.Min(by * 8 + y, img.Height - 1); // 端は複製で埋める
                    for (var x = 0; x < 8; x++)
                    {
                        var sx = Math.Min(bx * 8 + x, img.Width - 1);
                        var i = (sy * img.Width + sx) * 3;
                        double r = img.Rgb[i], g = img.Rgb[i + 1], b = img.Rgb[i + 2];
                        ycc[0][y * 8 + x] = 0.299 * r + 0.587 * g + 0.114 * b - 128;
                        ycc[1][y * 8 + x] = -0.168736 * r - 0.331264 * g + 0.5 * b;
                        ycc[2][y * 8 + x] = 0.5 * r - 0.418688 * g - 0.081312 * b;
                    }
                }
                var baseIdx = (by * bw + bx) * 3 * 64;
                for (var c = 0; c < 3; c++)
                    Fdct(ycc[c], tmp, c == 0 ? qLuma : qChroma, coeffs.AsSpan(baseIdx + c * 64, 64));
            }
        });
        return coeffs;
    }

    private static void Fdct(double[] f, double[] tmp, byte[] q, Span<short> outZz)
    {
        // 行方向: tmp[y*8+u] = Σx c[u,x] f[y,x]
        for (var y = 0; y < 8; y++)
        for (var u = 0; u < 8; u++)
        {
            double s = 0;
            for (var x = 0; x < 8; x++) s += Cos[u * 8 + x] * f[y * 8 + x];
            tmp[y * 8 + u] = s;
        }
        // 列方向: F[v*8+u] = Σy c[v,y] tmp[y,u]
        for (var k = 0; k < 64; k++)
        {
            var n = ZigZag[k];
            int v = n / 8, u = n % 8;
            double s = 0;
            for (var y = 0; y < 8; y++) s += Cos[v * 8 + y] * tmp[y * 8 + u];
            outZz[k] = (short)Math.Round(s / q[n], MidpointRounding.AwayFromZero);
        }
    }

    // ---- ヘッダ ----

    private static void WriteHeaders(Stream s, RgbImage img, byte[] qLuma, byte[] qChroma)
    {
        s.Write([0xFF, 0xD8]); // SOI
        // APP0 JFIF 1.01・密度は 300dpi 固定（端末や撮影の情報は持たない）・サムネイル無し
        s.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x01,
                 0x01, 0x2C, 0x01, 0x2C, 0x00, 0x00]);
        WriteDqt(s, 0, qLuma);
        WriteDqt(s, 1, qChroma);
        // SOF0: 8bit・3 成分・全成分 1x1（4:4:4）
        Span<byte> sof = stackalloc byte[19];
        sof[0] = 0xFF; sof[1] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(sof[2..], 17);
        sof[4] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(sof[5..], (ushort)img.Height);
        BinaryPrimitives.WriteUInt16BigEndian(sof[7..], (ushort)img.Width);
        sof[9] = 3;
        sof[10] = 1; sof[11] = 0x11; sof[12] = 0;
        sof[13] = 2; sof[14] = 0x11; sof[15] = 1;
        sof[16] = 3; sof[17] = 0x11; sof[18] = 1;
        s.Write(sof);
        WriteDht(s, 0x00, DcLumaBits, DcVals);
        WriteDht(s, 0x10, AcLumaBits, AcLumaVals);
        WriteDht(s, 0x01, DcChromaBits, DcVals);
        WriteDht(s, 0x11, AcChromaBits, AcChromaVals);
        // SOS
        s.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
    }

    private static void WriteDqt(Stream s, byte id, byte[] table)
    {
        s.Write([0xFF, 0xDB, 0x00, 0x43, id]);
        for (var k = 0; k < 64; k++) s.WriteByte(table[ZigZag[k]]);
    }

    private static void WriteDht(Stream s, byte cls, byte[] bits, byte[] vals)
    {
        var len = 2 + 1 + 16 + vals.Length;
        s.Write([0xFF, 0xC4, (byte)(len >> 8), (byte)len, cls]);
        s.Write(bits);
        s.Write(vals);
    }

    // ---- エントロピー符号化 ----

    private readonly record struct Huff(ushort[] Code, byte[] Size);

    internal static (ushort[] Code, byte[] Size) BuildHuffman(byte[] bits, byte[] vals)
    {
        var code = new ushort[256];
        var size = new byte[256];
        int c = 0, k = 0;
        for (var len = 1; len <= 16; len++)
        {
            for (var i = 0; i < bits[len - 1]; i++)
            {
                code[vals[k]] = (ushort)c;
                size[vals[k]] = (byte)len;
                k++; c++;
            }
            c <<= 1;
        }
        return (code, size);
    }

    private static Huff Make(byte[] bits, byte[] vals)
    {
        var (code, size) = BuildHuffman(bits, vals);
        return new Huff(code, size);
    }

    private static void WriteScan(Stream s, short[] coeffs, (int Bw, int Bh) blocks)
    {
        var dc = new[] { Make(DcLumaBits, DcVals), Make(DcChromaBits, DcVals), Make(DcChromaBits, DcVals) };
        var ac = new[] { Make(AcLumaBits, AcLumaVals), Make(AcChromaBits, AcChromaVals), Make(AcChromaBits, AcChromaVals) };
        var w = new BitWriter(s);
        var prev = new int[3];
        var n = blocks.Bw * blocks.Bh;
        for (var b = 0; b < n; b++)
        for (var c = 0; c < 3; c++)
        {
            var off = (b * 3 + c) * 64;
            int diff = coeffs[off] - prev[c];
            prev[c] = coeffs[off];
            var cat = Category(diff);
            w.Write(dc[c].Code[cat], dc[c].Size[cat]);
            if (cat > 0) w.Write(Magnitude(diff, cat), cat);

            var run = 0;
            for (var k = 1; k < 64; k++)
            {
                int v = coeffs[off + k];
                if (v == 0) { run++; continue; }
                while (run > 15) { w.Write(ac[c].Code[0xF0], ac[c].Size[0xF0]); run -= 16; }
                var vc = Category(v);
                var sym = (run << 4) | vc;
                w.Write(ac[c].Code[sym], ac[c].Size[sym]);
                w.Write(Magnitude(v, vc), vc);
                run = 0;
            }
            if (run > 0) w.Write(ac[c].Code[0x00], ac[c].Size[0x00]); // EOB
        }
        w.Flush();
    }

    internal static int Category(int v)
    {
        var a = Math.Abs(v);
        var n = 0;
        while (a > 0) { n++; a >>= 1; }
        return n;
    }

    private static int Magnitude(int v, int cat) => v >= 0 ? v : v + (1 << cat) - 1;

    /// <summary>MSB から詰めるビット書き出し。0xFF の後に 0x00 を挿入（バイトスタッフィング）。</summary>
    private sealed class BitWriter(Stream s)
    {
        private uint _acc;
        private int _n;

        public void Write(int bits, int count)
        {
            if (count == 0) return;
            if (count > 16) throw new InvalidOperationException("符号長が 16 を超えた");
            _acc = (_acc << count) | ((uint)bits & ((1u << count) - 1));
            _n += count;
            while (_n >= 8)
            {
                var b = (byte)(_acc >> (_n - 8));
                s.WriteByte(b);
                if (b == 0xFF) s.WriteByte(0x00);
                _n -= 8;
            }
            _acc &= (1u << _n) - 1;
        }

        public void Flush()
        {
            if (_n > 0) Write((1 << (8 - _n)) - 1, 8 - _n); // 残りは 1 で埋める
        }
    }
}
