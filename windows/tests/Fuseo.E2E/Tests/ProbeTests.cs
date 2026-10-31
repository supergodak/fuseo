using System.Text;
using System.Windows.Automation;
using Fuseo.E2E.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.E2E.Tests;

/// <summary>開発用: UIA の木を書き出す（FUSEO_E2E_PROBE=1 のときだけ動く）。</summary>
public class ProbeTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void DumpTree()
    {
        if (Environment.GetEnvironmentVariable("FUSEO_E2E_PROBE") != "1") return;
        Run(s =>
        {
            Output.WriteLine($"virtual screen {Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN)}x{Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN)}");
            var app = s.Launch();
            var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
            app.Main.Get("main.openFiles").Click();
            // ファイルのダイアログの木は書き出さない（初期フォルダ＝Mac の共有の中身が映るため）。
            FileDialogs.Open(app, "画像か PDF を開く", [png]);
            Wait.Until(() => app.Main.FindAll("page.maskCount").Count == 1, "1 ページ");
            var img = app.Main.Get("main.pageImage");
            Output.WriteLine($"image {img.Bounds()} main {app.Main.Bounds()}");
            Mouse.Drag(img.At(0.3, 0.3), img.At(0.5, 0.6));
            Wait.Until(() => app.Main.Find("page.maskCount")?.Text() == "マスク 1", "マスク 1");
            File.WriteAllText(Path.Combine(s.Dir, "main.txt"), Dump(app.Main));
        });
    }

    internal static string Dump(AutomationElement root)
    {
        var sb = new StringBuilder();
        void Walk(AutomationElement e, int depth)
        {
            if (depth > 40) return;
            try
            {
                var c = e.Current;
                sb.Append(' ', depth * 2).Append($"{c.ControlType.ProgrammaticName} id='{c.AutomationId}' name='{c.Name}' class='{c.ClassName}' rect={c.BoundingRectangle} off={c.IsOffscreen} en={c.IsEnabled}\n");
            }
            catch (ElementNotAvailableException) { return; }
            var child = TreeWalker.RawViewWalker.GetFirstChild(e);
            while (child is not null)
            {
                Walk(child, depth + 1);
                child = TreeWalker.RawViewWalker.GetNextSibling(child);
            }
        }
        Walk(root, 0);
        return sb.ToString();
    }
}
