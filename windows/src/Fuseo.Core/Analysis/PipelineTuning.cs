namespace Fuseo.Core.Analysis;

/// <summary>
/// 解析の調整値（Mac 版 PipelineTuning と同じ既定値＋Windows の正立判定・カード判定）。
/// 既定値のまま使うのが前提。挙動を変えたいときだけ with で差し替える（設計変更ではなく運用パラメータ）。
/// </summary>
public sealed record PipelineTuning
{
    public static readonly PipelineTuning Default = new();

    /// <summary>行グルーピングの中心 Y 許容（正規化）。WP-0 実証値。</summary>
    public double LineGroupingCenterYTolerance { get; init; } = 0.02;

    /// <summary>観測内の極大数字連続を候補にする最小桁数。これ未満はどの検出子も使わない。</summary>
    public int MinDigitRunLength { get; init; } = 6;

    /// <summary>クレジットカード（Luhn）の桁数。13 桁は製造番号等と衝突しやすいので対象外。</summary>
    public IReadOnlyList<int> CreditCardLengths { get; init; } = [14, 15, 16];

    /// <summary>保険者番号の桁数（8 桁=健保組合等 / 6 桁=市町村国保）。</summary>
    public IReadOnlyList<int> InsurerNumberLengths { get; init; } = [6, 8];

    /// <summary>保険証の記号・番号の桁数（2〜10）。</summary>
    public IReadOnlyList<int> KigoBangoLengths { get; init; } = [2, 3, 4, 5, 6, 7, 8, 9, 10];

    /// <summary>正立判定: 1 位が 2 位のこの倍率以上のときだけ回す（§10.3）。</summary>
    public double UprightMinRatio { get; init; } = 1.5;

    /// <summary>正立判定: キーワードスコアの重み（有意文字数に足す）。</summary>
    public double UprightKeywordWeight { get; init; } = 10;

    /// <summary>ID-1 カードの長辺/短辺（85.60×53.98mm）。</summary>
    public double CardAspect { get; init; } = 85.60 / 53.98;

    /// <summary>カードとみなす縦横比の許容（相対 ±5%）。</summary>
    public double CardAspectTolerance { get; init; } = 0.05;
}
