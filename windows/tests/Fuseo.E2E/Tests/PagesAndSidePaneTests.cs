using System.Windows.Automation;
using Fuseo.E2E.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.E2E.Tests;

/// <summary>ページ一覧: クリックで選ぶ・上へ・下へ・削除（書き出しの順まで確かめる）。</summary>
public class PageRailTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void ClickSelect_MoveUpDown_Remove_ChangesExportOrder() => Run(s =>
    {
        var pdf = Synth.WritePdf(s.InPath("three.pdf"),
            [new(1, 0, 0, Synth.Marker), new(0, 0.6, 0, Synth.Marker), new(0, 0, 1, Synth.Marker)]);
        var app = s.Launch();
        app.Import(3, pdf);
        var up = app.Button("pages.moveUp");
        var down = app.Button("pages.moveDown");
        Wait.Until(() => app.PageItems()[0].IsSelected(), "1 ページ目が選ばれている");
        Assert.False(up.IsEnabled(), "先頭なのに上へ動かせる");

        app.PageItems()[2].Click();
        Wait.Until(() => app.PageItems()[2].IsSelected() && !down.IsEnabled(), "3 ページ目を選ぶ（末尾は下へ動かせない）");
        up.Click();
        Wait.Until(() => app.PageTitles().SequenceEqual(["1. three.pdf（1 ページ目）", "2. three.pdf（3 ページ目）", "3. three.pdf（2 ページ目）"]), "上へ");
        down.Click();
        Wait.Until(() => app.PageTitles().SequenceEqual(["1. three.pdf（1 ページ目）", "2. three.pdf（2 ページ目）", "3. three.pdf（3 ページ目）"]), "下へ");

        app.PageItems()[0].Click();
        Wait.Until(() => app.PageItems()[0].IsSelected(), "1 ページ目を選ぶ");
        app.Button("pages.remove").Click();
        Wait.Until(() => app.PageTitles().SequenceEqual(["1. three.pdf（2 ページ目）", "2. three.pdf（3 ページ目）"]), "削除");
        app.PageItems()[1].Click();
        Wait.Until(() => app.PageItems()[1].IsSelected(), "青のページを選ぶ");
        up.Click();
        Wait.Until(() => app.PageTitles().SequenceEqual(["1. three.pdf（3 ページ目）", "2. three.pdf（2 ページ目）"]), "青を先頭へ");
        Autosave.WaitLatest(s.Data, d => d.Pages.Count == 2 && d.Pages[0].SourcePageIndex == 2, "並びが保存される");

        var outPdf = s.OutPath("order.pdf");
        app.Export("pdf", outPdf);
        OutputCheck.AssertClean(outPdf);
        var pages = OutputCheck.Load(outPdf);
        Assert.Equal(2, pages.Count);
        var p1 = OutputCheck.Mean(pages[0], new Box(0.5, 0.6, 0.3, 0.3));
        var p2 = OutputCheck.Mean(pages[1], new Box(0.5, 0.6, 0.3, 0.3));
        Assert.True(p1.B > 200 && p1.R < 40, $"1 ページ目が青でない {p1}");
        Assert.True(p2.G > 120 && p2.R < 40 && p2.B < 40, $"2 ページ目が緑でない {p2}");
    });
}

/// <summary>右ペイン: 書類の種類・注意バナー（閉じる）・チェックリストの印・自動検出候補（QR）のオンオフ。</summary>
public class SidePaneTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void DocumentType_ShowsBannerAndChecklist_BannerCloses_ExportStillWarns() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        var combo = app.Main.Get("main.documentType");
        Assert.Equal(Ja.TypeGeneric, ((SelectionPattern)combo.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection()[0].Current.Name);
        Assert.False(app.Main.Find("main.bannerClose").IsShown(), "その他の書類で注意書きが出ている");
        int Rows() => app.Main.Get("main.checklist").Children(ControlType.DataItem).Count;
        Assert.Equal(4, Rows());

        app.ChooseDocumentType(Ja.TypeMyNumberBack);
        Wait.Until(() => app.Main.Find("main.bannerClose").IsShown(), "注意書きが出る");
        Wait.Until(() => Rows() == 2, "チェックリストが種類のものになる");
        Autosave.WaitLatest(s.Data, d => d.Pages[0].DocumentType == Fuseo.Core.Model.DocumentType.MyNumberCardBack, "種類が保存される");

        app.Button("main.bannerClose").Click();
        Wait.Until(() => !app.Main.Find("main.bannerClose").IsShown(), "注意書きが閉じる");

        var export = app.OpenExport();
        Assert.True(export.Get("export.warnings").IsShown(), "書き出しシートに注意書きが再掲されない");
        export.Get("export.cancel").Click();
        app.WaitWindowClosed(Ja.ExportWindow);

        app.Key(VKey.Z, VKey.Control); // 種類の変更も元に戻せる
        Wait.Until(() => Rows() == 4, "種類が戻る");
        Autosave.WaitLatest(s.Data, d => d.Pages[0].DocumentType == Fuseo.Core.Model.DocumentType.Generic, "種類が戻って保存される");
    });

    [Fact]
    public void ChecklistCheckBox_IsSavedAndCleared() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        app.ChooseDocumentType(Ja.TypeLicenseBack);
        var check = Wait.For(() => app.Main.FindAll("checklist.check").FirstOrDefault(c => c.IsShown()), "チェックボックスが出る");
        Assert.False(check.IsOn());
        check.Click();
        Wait.Until(check.IsOn, "チェックが付く");
        Autosave.WaitLatest(s.Data, d => d.Pages[0].CheckedRuleIds.Contains("menkyoB.organDonation"), "印が保存される");
        check.Click();
        Wait.Until(() => !check.IsOn(), "チェックが外れる");
        Autosave.WaitLatest(s.Data, d => d.Pages[0].CheckedRuleIds.Count == 0, "印が消えて保存される");
    });

    [Fact]
    public void QrCandidate_ToggleOnOff_TypeDefault_AndSelectDelete() => Run(s =>
    {
        var (bmp, qr) = Synth.WithQr();
        var png = Synth.WritePng(s.InPath("qr.png"), bmp);
        var qrBox = new Box(qr.X, qr.Y, qr.W, qr.H);
        var app = s.Launch();
        app.Import(1, png);
        // その他の書類: 候補は出るが既定は OFF
        var toggle = Wait.For(() => app.Main.Find("candidate.toggle") is { } t && t.IsShown() ? t : null, "QR の候補が出る");
        Assert.False(toggle.IsOn());
        Assert.Equal(Ja.NoMask, app.MaskCountOf(0));

        toggle.Click();
        app.WaitMaskCount(1);
        var on = s.OutPath("qr-on.png");
        app.Export("png", on);
        OutputCheck.AssertPainted(OutputCheck.LoadSingle(on), qrBox, "QR コード", inset: 0);

        toggle = app.Main.Get("candidate.toggle");
        toggle.Click();
        app.WaitMaskCount(0);
        var off = s.OutPath("qr-off.png");
        var w = app.OpenExport();
        Assert.True(w.Get("export.zeroMaskWarning").IsShown(), "マスクが無いのに警告が出ない");
        Assert.Equal(Ja.ExportSaveWithoutMask, w.Get("export.confirmButton").Text());
        Actions.ChooseFormat(w, "png");
        app.SaveExport(w, off);
        var f = OutputCheck.DarkFraction(OutputCheck.LoadSingle(off), qrBox);
        Assert.InRange(f, 0.2, 0.8); // QR の模様がそのまま（塗られていない）

        // マイナンバーカード（裏）では QR は既定で ON
        app.ChooseDocumentType(Ja.TypeMyNumberBack);
        Wait.Until(() => app.Main.Find("candidate.toggle") is { } t && t.IsShown() && t.IsOn(), "候補が ON で作り直される");
        app.WaitMaskCount(1);

        // 選択の道具で候補をクリック → Delete で OFF（候補は一覧に残る）
        app.SelectTool("tool.select");
        app.ClickOnImage(qrBox.X + qrBox.W / 2, qrBox.Y + qrBox.H / 2);
        Wait.Until(() => app.Button("main.deleteSelection").IsEnabled(), "候補が選ばれる");
        app.Key(VKey.Delete);
        app.WaitMaskCount(0);
        Wait.Until(() => app.Main.Find("candidate.toggle") is { } t && t.IsShown() && !t.IsOn(), "候補が OFF になって残る");
    });
}
