using Fuseo.Core.Analysis;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.Core.Tests.Analysis;

/// <summary>
/// 解析テストの合成 OCR とテスト用の番号。**実在しうる番号は書かない**: チェックデジットの通る番号は
/// 見るからに合成の本体（0123… 等）から実行時に作る（Mac のテストと同じ方針）。
/// Mac のフィクスチャ（左下原点の中心 Y）を移植するときは y_top = 1 − y − h（中心なら 1 − midY）に読み替える。
/// </summary>
internal static class AnalysisFixtures
{
    /// <summary>中心 Y（左上原点）= midY・高さ h・x 開始 x・幅 w の 1 単語 1 行の観測。</summary>
    public static OcrItem Item(string text, double x, double midY, double w = 0.08, double h = 0.03)
    {
        var box = new NormRect(x, midY - h / 2, w, h);
        return new OcrItem(text, box, [new OcrWord(text, box)]);
    }

    /// <summary>単語の並びから 1 行を作る（x から幅 w ずつ横に並べる・Text は JoinWords）。</summary>
    public static OcrItem Line(double x, double midY, double w, params string[] words)
    {
        var list = words.Select((t, i) => new OcrWord(t, new NormRect(x + i * w, midY - 0.015, w, 0.03))).ToList();
        return OcrItem.FromWords(list);
    }

    /// <summary>末尾（または指定位置）の 1 桁を 0..9 で試し、検証が通る番号を返す。</summary>
    public static string MakeValid(Func<char, string> build, Func<string, bool> validate)
    {
        for (var c = '0'; c <= '9'; c++)
        {
            var s = build(c);
            if (validate(s)) return s;
        }
        throw new InvalidOperationException("チェックデジットが見つからない本体（テストの作りの誤り）");
    }

    /// <summary>合成の個人番号（本体 11 桁＋検査用数字）。</summary>
    public static string MyNumber(string body11 = "01234567890") => MakeValid(c => body11 + c, Checkdigits.IsValidMyNumber);

    /// <summary>合成の免許証番号（本体 10 桁＋チェック＋再交付回数）。</summary>
    public static string License(string body10 = "1234567890", char last = '0') =>
        MakeValid(c => body10 + c + last, Checkdigits.IsValidLicenseNumber);

    /// <summary>合成のカード番号（Luhn）。</summary>
    public static string Luhn(string body = "123456789012345") => MakeValid(c => body + c, Checkdigits.IsValidLuhn);

    public static DocumentPreset Preset(DocumentType type, (string Text, int Weight)[] keywords, params MaskRule[] rules) => new()
    {
        SchemaVersion = 1, DocumentType = type, DisplayName = type.ToString(),
        Classification = new ClassificationSpec { Keywords = keywords.Select(k => new WeightedKeyword(k.Text, k.Weight)).ToList() },
        Rules = rules,
    };

    /// <summary>キーワードを含まない本文っぽい観測を n 個。</summary>
    public static List<OcrItem> Filler(int n) =>
        Enumerable.Range(0, n).Select(i => Item($"明細{i}", 0.05, 0.05 + i * 0.001, w: 0.1)).ToList();
}
