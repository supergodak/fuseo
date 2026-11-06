using System.Text;

namespace Fuseo.Core.Analysis;

/// <summary>
/// OCR 文字列の整形（Windows 固有・docs/windows-design.md §10.9／§10.12）。
/// 用途で使い分ける: キーワード照合は <see cref="ForKeywords"/>（NFKC）、数字・在留カードは <see cref="ForDigits"/>（全角英数→半角だけ）。
/// 数字側に NFKC を使わない理由: 「℃」が「°C」の 2 文字になり文字数が変わる（§10.10 の実物で発生）。
/// </summary>
public static class OcrText
{
    /// <summary>
    /// 単語を 1 行にする。隣り合う単語の**両側が ASCII のときだけ半角空白**、それ以外は詰める。
    /// Windows OCR は日本語を 1 文字ずつ単語にするので、空白を残すとキーワードが一致せず、
    /// 全部詰めると数字の群の区切り（逆順の保険・電話番号の分断）が消える。空の単語は無視する。
    /// </summary>
    public static string JoinWords(IEnumerable<string> words)
    {
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            if (w.Length == 0) continue;
            if (sb.Length > 0 && IsAscii(sb[^1]) && IsAscii(w[0])) sb.Append(' ');
            sb.Append(w);
        }
        return sb.ToString();
    }

    /// <summary>
    /// キーワード照合用: NFKC → 空白（全種）を除去 → ハイフン・ダッシュ類を長音「ー」に揃える。分類・キーワード近接の照合だけに使う。
    /// 長音を揃えるのは Windows OCR が「在留カード」の「ー」を ASCII の「-」と読むため（W9-2 の合成カードで毎回発生・メイン決定）。
    /// キーワード側も同じ関数を通すので、プリセットの表記はそのままでよい。
    /// </summary>
    public static string ForKeywords(string text)
    {
        var n = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(n.Length);
        foreach (var ch in n)
        {
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(IsDashLike(ch) ? 'ー' : ch);
        }
        return sb.ToString();
    }

    /// <summary>長音と取り違えやすい横棒（- ‐ ‑ ‒ – — ― −）。NFKC 後なので全角の「－」は「-」になっている。</summary>
    private static bool IsDashLike(char c) => c is '-' or '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212';

    /// <summary>
    /// 数字処理用: 全角英数（U+FF10–FF19・FF21–FF3A・FF41–FF5A）を半角に、全角空白を半角に。
    /// **それ以外は変えない**（文字数を保つ。「℃」は 1 文字のまま）。
    /// </summary>
    public static string ForDigits(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if ((c >= '０' && c <= '９') || (c >= 'Ａ' && c <= 'Ｚ') || (c >= 'ａ' && c <= 'ｚ'))
                chars[i] = (char)(c - 0xFEE0);
            else if (c == '　')
                chars[i] = ' ';
        }
        return new string(chars);
    }

    /// <summary>
    /// 数字の読み違い（O→0・I/l/|→1）を直した行（Windows 固有・§10.9）。行に ASCII 数字が 1 つ以上あるときだけ、
    /// 半角空白区切りの各単語のうち <c>[0-9OoIl|]+</c> だけでできたものを置き換える（それ以外の単語・空白はそのまま）。
    /// 元の行は呼び出し側で残す（追加の候補列として使う）。チェックデジットのある検出子だけが使う。
    /// </summary>
    public static string FixDigitConfusables(string line)
    {
        if (!line.Any(IsAsciiDigit)) return line;
        var words = line.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length > 0 && w.All(IsConfusableOrDigit)) words[i] = FixConfusables(w);
        }
        return string.Join(' ', words);
    }

    internal static bool IsAscii(char c) => c < 0x80;
    internal static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
    private static bool IsConfusableOrDigit(char c) => IsAsciiDigit(c) || c is 'O' or 'o' or 'I' or 'l' or '|';

    /// <summary>O/o→0、I/l/|→1（他の文字はそのまま）。</summary>
    internal static string FixConfusables(string s)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = chars[i] switch { 'O' or 'o' => '0', 'I' or 'l' or '|' => '1', var c => c };
        return new string(chars);
    }
}
