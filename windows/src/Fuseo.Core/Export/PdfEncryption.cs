using System.Security.Cryptography;
using System.Text;

namespace Fuseo.Core.Export;

/// <summary>
/// PDF の標準セキュリティハンドラ・AES-256（/V 5 /R 6 /AESV3・ISO 32000-2 §7.6.4）。0.4.0 の「PDF にパスワードをかける」。
/// 開くときのパスワードと権限パスワードに同じ値を使う（Mac 版と同じ。開けた人は印刷・コピーもできる）。
/// R6 は文書の鍵（32 バイト・乱数）でそのまま全オブジェクトを暗号化する（オブジェクトごとの鍵の導出は無い）。
/// Fuseo の PDF には文字列を書かない（/Info は空の辞書）ので、暗号化するのはストリームだけ。
/// </summary>
public sealed class PdfEncryption
{
    /// <summary>許可フラグ: すべて許可（R6 の規定で下位 2 ビットは 0・7〜8 と 13〜32 ビットは 1）。</summary>
    public const int Permissions = -4;

    private readonly byte[] _fileKey;
    public byte[] U { get; }
    public byte[] UE { get; }
    public byte[] O { get; }
    public byte[] OE { get; }
    public byte[] Perms { get; }
    /// <summary>trailer の /ID（暗号化した PDF では必須）。</summary>
    public byte[] Id { get; }

    private PdfEncryption(byte[] fileKey, byte[] u, byte[] ue, byte[] o, byte[] oe, byte[] perms, byte[] id)
    {
        _fileKey = fileKey; U = u; UE = ue; O = o; OE = oe; Perms = perms; Id = id;
    }

    /// <summary>パスワードから暗号化の材料を作る（乱数は暗号論的乱数）。</summary>
    public static PdfEncryption Create(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var pw = PreparePassword(password);
        var fileKey = RandomNumberGenerator.GetBytes(32);
        // 利用者（開く）: U = hash(pw, 検証用の塩) ‖ 検証用の塩 ‖ 鍵用の塩、UE = AES-256(鍵用ハッシュ, 文書の鍵)
        var uvs = RandomNumberGenerator.GetBytes(8);
        var uks = RandomNumberGenerator.GetBytes(8);
        var u = Concat(Hash(pw, uvs, []), uvs, uks);
        var ue = AesNoPadZeroIv(Hash(pw, uks, []), fileKey);
        // 所有者（権限）: 同じ計算に U を混ぜる
        var ovs = RandomNumberGenerator.GetBytes(8);
        var oks = RandomNumberGenerator.GetBytes(8);
        var o = Concat(Hash(pw, ovs, u), ovs, oks);
        var oe = AesNoPadZeroIv(Hash(pw, oks, u), fileKey);
        // Perms: P（リトルエンディアン 4 バイト）‖ FF×4 ‖ 'T'（メタデータも暗号化）‖ "adb" ‖ 乱数 4 バイト を AES-256-ECB
        var p = new byte[16];
        BitConverter.TryWriteBytes(p.AsSpan(0, 4), Permissions);
        if (!BitConverter.IsLittleEndian) Array.Reverse(p, 0, 4);
        p[4] = p[5] = p[6] = p[7] = 0xFF;
        p[8] = (byte)'T'; p[9] = (byte)'a'; p[10] = (byte)'d'; p[11] = (byte)'b';
        RandomNumberGenerator.Fill(p.AsSpan(12, 4));
        using var ecb = Aes.Create();
        ecb.Key = fileKey;
        var perms = ecb.EncryptEcb(p, PaddingMode.None);
        return new PdfEncryption(fileKey, u, ue, o, oe, perms, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>/Encrypt 辞書（この辞書自体は暗号化しない）。</summary>
    public string Dictionary() =>
        "<< /Filter /Standard /V 5 /R 6 /Length 256 " +
        "/CF << /StdCF << /AuthEvent /DocOpen /CFM /AESV3 /Length 32 >> >> /StmF /StdCF /StrF /StdCF " +
        $"/P {Permissions} /O <{Hex(O)}> /U <{Hex(U)}> /OE <{Hex(OE)}> /UE <{Hex(UE)}> /Perms <{Hex(Perms)}> " +
        "/EncryptMetadata true >>";

    /// <summary>ストリームの中身の暗号化: 乱数の IV（16 バイト）‖ AES-256-CBC（PKCS#7 詰め物）。</summary>
    public byte[] EncryptStream(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = _fileKey;
        var iv = RandomNumberGenerator.GetBytes(16);
        var body = aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
        return Concat(iv, body);
    }

    /// <summary>復号（テスト用。暗号化の往復と、鍵が UE から取り出せることの確認）。</summary>
    internal static byte[]? RecoverFileKey(string password, byte[] u, byte[] ue)
    {
        var pw = PreparePassword(password);
        if (!Hash(pw, u.AsSpan(32, 8).ToArray(), []).AsSpan().SequenceEqual(u.AsSpan(0, 32))) return null;
        using var aes = Aes.Create();
        aes.Key = Hash(pw, u.AsSpan(40, 8).ToArray(), []);
        return aes.DecryptCbc(ue, new byte[16], PaddingMode.None);
    }

    internal static byte[] DecryptStream(byte[] fileKey, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = fileKey;
        return aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.PKCS7);
    }

    /// <summary>パスワードの前処理（SASLprep の代わりに NFKC 正規化 → UTF-8・127 バイトまで）。</summary>
    internal static byte[] PreparePassword(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        return bytes.Length <= 127 ? bytes : bytes[..127];
    }

    /// <summary>ISO 32000-2 Algorithm 2.B（R6 のハッシュ）。</summary>
    internal static byte[] Hash(byte[] password, byte[] salt, byte[] userKey)
    {
        var k = SHA256.HashData(Concat(password, salt, userKey));
        var round = 0;
        while (true)
        {
            var unit = Concat(password, k, userKey);
            var k1 = new byte[unit.Length * 64];
            for (var i = 0; i < 64; i++) Buffer.BlockCopy(unit, 0, k1, i * unit.Length, unit.Length);
            byte[] e;
            using (var aes = Aes.Create())
            {
                aes.Key = k[..16];
                e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            }
            // 先頭 16 バイトを大きな整数とみた mod 3（= 各バイトの和の mod 3。256 ≡ 1 (mod 3) のため）
            var sum = 0;
            for (var i = 0; i < 16; i++) sum += e[i];
            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            round++;
            if (round >= 64 && e[^1] <= round - 32) break;
        }
        return k[..32];
    }

    private static byte[] AesNoPadZeroIv(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
    }

    internal static string Hex(byte[] b) => Convert.ToHexString(b);

    private static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        var o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }
}
