using System.Windows;
using System.Windows.Automation;

namespace Fuseo.E2E.Driver;

/// <summary>UI オートメーションの小道具（要素を AutomationId で探す・矩形・状態）。矩形は物理ピクセル（テストプロセスは Per-Monitor V2）。</summary>
internal static class Ui
{
    public static AutomationElement? Find(this AutomationElement root, string automationId) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    public static AutomationElement Get(this AutomationElement root, string automationId, TimeSpan? timeout = null) =>
        Wait.For(() => root.Find(automationId), $"{automationId} が見つかる", timeout);

    public static List<AutomationElement> FindAll(this AutomationElement root, string automationId) =>
        root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
            .Cast<AutomationElement>().ToList();

    public static List<AutomationElement> Children(this AutomationElement root, ControlType type) =>
        root.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, type)).Cast<AutomationElement>().ToList();

    public static List<AutomationElement> Descendants(this AutomationElement root, ControlType type) =>
        root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type)).Cast<AutomationElement>().ToList();

    public static AutomationElement? FindByName(this AutomationElement root, string name, ControlType? type = null)
    {
        System.Windows.Automation.Condition c = new PropertyCondition(AutomationElement.NameProperty, name);
        if (type is not null) c = new AndCondition(c, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        return root.FindFirst(TreeScope.Descendants, c);
    }

    public static Rect Bounds(this AutomationElement e) => e.Current.BoundingRectangle;

    public static Point Center(this AutomationElement e)
    {
        var r = e.Bounds();
        if (r.IsEmpty || r.Width <= 0) throw new InvalidOperationException($"{e.Current.AutomationId} の矩形が空（画面に出ていない）");
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    /// <summary>要素の中の相対位置（0..1）の物理ピクセル座標。</summary>
    public static Point At(this AutomationElement e, double u, double v)
    {
        var r = e.Bounds();
        return new Point(r.X + r.Width * u, r.Y + r.Height * v);
    }

    public static string Text(this AutomationElement e) => e.Current.Name ?? "";

    public static bool IsShown(this AutomationElement? e)
    {
        if (e is null) return false;
        try
        {
            var r = e.Current.BoundingRectangle;
            return !e.Current.IsOffscreen && !r.IsEmpty && r.Width > 0 && r.Height > 0;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    public static bool IsEnabled(this AutomationElement e) => e.Current.IsEnabled;

    public static bool IsOn(this AutomationElement e) =>
        ((TogglePattern)e.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;

    public static bool IsSelected(this AutomationElement e) =>
        ((SelectionItemPattern)e.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;

    public static void Invoke(this AutomationElement e) =>
        ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    public static void SetValue(this AutomationElement e, string value) =>
        ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    public static string Value(this AutomationElement e) =>
        ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    /// <summary>本物のマウスで要素の中央をクリックする（押せる状態になるまで待つ）。</summary>
    public static void Click(this AutomationElement e)
    {
        Wait.Until(() => e.IsShown() && e.IsEnabled(), $"{Describe(e)} が押せる");
        Mouse.Click(e.Center());
    }

    public static string Describe(AutomationElement e)
    {
        try { return $"{e.Current.ControlType.ProgrammaticName}「{e.Current.AutomationId}／{e.Current.Name}」"; }
        catch (ElementNotAvailableException) { return "（消えた要素）"; }
    }
}
