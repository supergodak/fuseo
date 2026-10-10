using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuseo.Core.Model;

/// <summary>書類種別。JSON の documentType と同じ綴り（Mac 版 MaskingCore の DocumentType と一致させる）。</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<DocumentType>))]
public enum DocumentType
{
    Generic,
    Hokensho,
    ShikakuKakuninsho,
    JuminhyoMyNumber,
    MenkyoshoFront,
    MenkyoshoBack,
    MyNumberCardFront,
    MyNumberCardBack,
    ZairyuCardFront,
}

/// <summary>
/// 動的検出子（Mac 版 DetectorID と同じ 8 種・JSON の綴りも同じ）。
/// v0.1 の Windows 版で実装するのは <see cref="QrBarcode"/> のみ（§1.2）。他は「手で塗る案内」として扱う。
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<DetectorId>))]
public enum DetectorId
{
    MyNumber12,
    LicenseNumber12,
    CreditCardLuhn,
    InsurerNumber,
    KigoBango,
    Face,
    QrBarcode,
    ZairyuNumber,
}

[JsonConverter(typeof(CamelCaseEnumConverter<RuleKind>))]
public enum RuleKind
{
    Fixed,
    Dynamic,
}

/// <summary>
/// 列挙名を camelCase で JSON と対応させる（例: MyNumberCardFront ⇄ "myNumberCardFront"）。
/// JSON の綴りは Mac 版の rawValue と同じなので、列挙名を PascalCase にしておけば一致する。数値は受け付けない。
/// </summary>
public sealed class CamelCaseEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where T : struct, Enum;
