using System.Windows;

namespace Fuseo.App.Views;

/// <summary>
/// ツールバーのボタンに付けるアイコン（0.3.1）。値は Windows 標準のアイコン用フォント（Segoe Fluent Icons／Segoe MDL2 Assets）の文字。
/// 名前（Content の文字列）はそのまま残すので、読み上げと E2E の名前での検索は変わらない。
/// </summary>
public static class Icon
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Icon), new FrameworkPropertyMetadata(string.Empty));

    public static string GetGlyph(DependencyObject o) => (string)o.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject o, string value) => o.SetValue(GlyphProperty, value);

    /// <summary>アイコンを左右反転する（左回転＝右回転のアイコンの鏡像）。</summary>
    public static readonly DependencyProperty MirrorProperty = DependencyProperty.RegisterAttached(
        "Mirror", typeof(bool), typeof(Icon), new FrameworkPropertyMetadata(false));
    public static bool GetMirror(DependencyObject o) => (bool)o.GetValue(MirrorProperty);
    public static void SetMirror(DependencyObject o, bool value) => o.SetValue(MirrorProperty, value);

    /// <summary>名前を出さずアイコンだけにする（名前はツールチップと読み上げに残る）。</summary>
    public static readonly DependencyProperty HideLabelProperty = DependencyProperty.RegisterAttached(
        "HideLabel", typeof(bool), typeof(Icon), new FrameworkPropertyMetadata(false));
    public static bool GetHideLabel(DependencyObject o) => (bool)o.GetValue(HideLabelProperty);
    public static void SetHideLabel(DependencyObject o, bool value) => o.SetValue(HideLabelProperty, value);
}
