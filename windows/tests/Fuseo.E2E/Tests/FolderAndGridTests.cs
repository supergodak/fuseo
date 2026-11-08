using System.Security.Cryptography;
using System.Windows.Automation;
using Fuseo.E2E.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.E2E.Tests;

/// <summary>
/// フォルダ一括処理（W9-4・受け入れは W9-5 §10.19-2）: フォルダのドロップと「フォルダを開く」（新しい作業として開く・前の作業は
/// 一覧に残る・対応外の件数・論理順）、一覧表示（セル数・マスクなし・クリックで 1 枚ずつ）、書き出しの「書類ごとに分ける」
/// （既存のファイルの横で -2・_1、既存のファイルと元のファイルは 1 バイトも変わらない、出力にメタデータと文字が無い）。
/// </summary>
public class FolderAndGridTests(ITestOutputHelper output) : E2ETest(output)
{
    /// <summary>
    /// 一括処理用のフォルダ: p10.png・p2.png・doc.pdf（2 ページ）・notes.txt（対応外）と、サブフォルダ（見ない）。
    /// 論理順（エクスプローラーと同じ）では doc.pdf → p2.png → p10.png。
    /// </summary>
    private static string MakeBatchFolder(Session s)
    {
        var dir = Path.Combine(s.In, "batch");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        Synth.WritePng(Path.Combine(dir, "p10.png"), Synth.Card());
        Synth.WritePng(Path.Combine(dir, "p2.png"), Synth.Card(800, 600));
        Synth.WritePdf(Path.Combine(dir, "doc.pdf"), [new(1, 0, 0, Synth.Marker), new(0, 0, 1)]);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not an image");
        Synth.WritePng(Path.Combine(dir, "sub", "inner.png"), Synth.Card());
        return dir;
    }

    private static readonly string[] BatchTitles =
        ["1. doc.pdf（1 ページ目）", "2. doc.pdf（2 ページ目）", "3. p2.png", "4. p10.png"];

    [Fact]
    public void FolderDrop_OpensAsNewWork_PreviousWorkStaysInList_SkipsUnsupported() => Run(s =>
    {
        var folder = MakeBatchFolder(s);
        var first = Synth.WritePng(s.InPath("first.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, first);
        app.DrawRect(new Box(0.4, 0.4, 0.2, 0.2));
        Autosave.WaitLatest(s.Data, d => d.Pages.Count == 1, "前の作業が保存される");

        FileDropper.Drop([folder], app.Main.At(0.5, 0.5));
        Wait.Until(() => app.PageTitles().SequenceEqual(BatchTitles), "フォルダの中身が論理順で開く（サブフォルダ・対応外は除く）");
        Wait.Until(() => app.Status().Contains(Ja.OpenedAsNewWork, StringComparison.Ordinal), "新しい作業として開いた表示");
        Assert.Contains(Ja.SkippedUnsupported(1), app.Status());
        Assert.Equal(Ja.NoMask, app.MaskCountOf(3)); // 前の作業の四角は持ち込まない
        Wait.Until(() => Autosave.WorkCount(s.Data) == 2, "作業が 2 つ（前の作業が残る）", TimeSpan.FromSeconds(20));

        app.NewDocument();
        app.WaitLibraryCount(2);
        Assert.Equal(1, Autosave.All(s.Data).Count(d => d.Pages.Count == 1 && d.Pages[0].Manual.Rects.Count == 1));
    });

    [Fact]
    public void OpenFolderButton_CancelDoesNothing_ThenPicksFolder() => Run(s =>
    {
        var folder = MakeBatchFolder(s);
        var app = s.Launch();
        app.Button("main.openFolder").Click();
        FileDialogs.Cancel(app, Ja.OpenFolderDialog);
        app.WaitNoDialogs();
        Assert.Equal(0, app.PageCount());

        app.Button("main.openFolder").Click();
        FileDialogs.PickFolder(app, Ja.OpenFolderDialog, folder);
        Wait.Until(() => app.PageTitles().SequenceEqual(BatchTitles), "フォルダの中身が論理順で開く");
        Wait.Until(() => app.Status().Contains(Ja.SkippedUnsupported(1), StringComparison.Ordinal), "対応外の件数の表示");
        Assert.DoesNotContain(Ja.OpenedAsNewWork, app.Status()); // 空の画面から開いたので「前の作業」は無い
    });

    [Fact]
    public void GridView_CellsNoMaskMarks_ClickOpensSinglePage() => Run(s =>
    {
        var a = Synth.WritePng(s.InPath("a.png"), Synth.Card());
        var b = Synth.WritePdf(s.InPath("b.pdf"), [new(1, 0, 0), new(0, 0.6, 0)]);
        var app = s.Launch();
        app.Import(3, a, b);
        app.PageItems()[0].Click();
        app.DrawRect(new Box(0.3, 0.3, 0.3, 0.3));

        app.Button("main.viewGrid").Click();
        var grid = Wait.For(() => app.Main.Find("main.grid") is { } g && g.IsShown() ? g : null, "一覧が出る");
        List<AutomationElement> Cells() => grid.FindAll("grid.cell");
        Wait.Until(() => Cells().Count == 3, "セルが 3 つ");
        Assert.Equal([Ja.GridTitle("a.png", 1, 1), Ja.GridTitle("b.pdf", 1, 2), Ja.GridTitle("b.pdf", 2, 2)],
                     grid.FindAll("grid.title").Select(t => t.Text()));
        Wait.Until(() => grid.FindAll("grid.noMask").Count(n => n.IsShown()) == 2, "マスクの無い 2 ページに「マスクなし」");
        Assert.Equal(Ja.NoMask, grid.FindAll("grid.noMask").First(n => n.IsShown()).Text());
        Assert.Equal(Ja.MaskCount(1), grid.FindAll("grid.maskCount").Single(n => n.IsShown()).Text());

        Cells()[2].Click();
        Wait.Until(() => !app.Main.Find("main.grid").IsShown(), "1 枚ずつに戻る");
        Assert.True(app.Button("main.viewSingle").IsSelected(), "「1 枚ずつ」が選ばれていない");
        Wait.Until(() => app.PageItems()[2].IsSelected(), "クリックしたページ（3 枚目）が選ばれる");
        Assert.True(app.Image().IsShown());
    });

    [Fact]
    public void ExportSeparate_NextToExistingFiles_AddsSuffix_LeavesExistingAndOriginalsUntouched() => Run(s =>
    {
        // 書類 X（画像 1 枚・手で塗る）と Y（PDF 2 ページ・マスクなし）。保存先は元のファイルと同じフォルダにする（最も危ない置き方）。
        var x = Synth.WriteJpeg(s.InPath("X.jpg"), Synth.Card());
        var y = Synth.WritePdf(s.InPath("Y.pdf"), [new(1, 0, 0, Synth.Marker), new(0, 0, 1, Synth.Marker)]);
        var existingPdf = s.InPath("X-masked.pdf");
        var existingJpg = s.InPath("Y-masked_1.jpg");
        File.WriteAllBytes(existingPdf, "existing file (not a real PDF) X"u8.ToArray());
        File.WriteAllBytes(existingJpg, "existing file (not a real JPEG) Y"u8.ToArray());
        var before = Directory.GetFiles(s.In).ToDictionary(f => Path.GetFileName(f), Hash);

        var app = s.Launch();
        app.Import(3, x, y);
        app.PageItems()[0].Click();
        var mask = new Box(0.3, 0.3, 0.3, 0.3);
        app.DrawRect(mask);

        void ExportSeparately(string format, int expectedSaved)
        {
            var w = app.OpenExport();
            var separate = w.Get("export.separate");
            separate.Click();
            Wait.Until(() => separate.IsSelected(), "「書類ごとに分ける」が選ばれる");
            Assert.True(w.Get("export.separateNote").IsShown(), "分けるときの説明が出ない");
            Actions.ChooseFormat(w, format);
            Assert.Contains("Y.pdf", w.Get("export.zeroMaskDocuments").Text()); // マスクの無い書類は名前で知らせる
            Assert.Equal(Ja.ExportSaveWithoutMask, w.Get("export.confirmButton").Text());
            w.Get("export.confirmButton").Click();
            FileDialogs.PickFolder(app, Ja.ExportFolderDialog, s.In);
            Wait.Until(() => w.Find("export.completedText") is { } t && t.IsShown() && t.Text() == Ja.SeparateCompleted(expectedSaved),
                       "保存の完了表示", TimeSpan.FromSeconds(30));
            w.Get("export.cancel").Click(); // 「閉じる」
            app.WaitWindowClosed(Ja.ExportWindow);
            Wait.Until(() => app.Status() == Ja.ExportedSeparate(expectedSaved, "in"), "書き出しの表示");
        }

        ExportSeparately("pdf", 2);
        ExportSeparately("jpeg", 2);

        // 既存の名前とは重ならない: X-masked.pdf がある → X-masked-2.pdf、Y-masked_1.jpg がある → Y-masked-2_1.jpg, _2.jpg。
        var expectedNew = new[] { "X-masked-2.pdf", "Y-masked.pdf", "X-masked.jpg", "Y-masked-2_1.jpg", "Y-masked-2_2.jpg" };
        var now = Directory.GetFiles(s.In).Select(f => Path.GetFileName(f)!).ToHashSet();
        Assert.Equal(before.Keys.Concat(expectedNew).Order(), now.Order());
        foreach (var (name, hash) in before)
            Assert.True(hash == Hash(s.InPath(name)), $"{name} が変わった（既存・元のファイルは書き換えない）");

        foreach (var f in expectedNew) OutputCheck.AssertClean(s.InPath(f));
        var xPdf = OutputCheck.LoadSingle(s.InPath("X-masked-2.pdf"));
        OutputCheck.AssertPainted(xPdf, mask, "X の四角（PDF）");
        OutputCheck.AssertCardIntact(xPdf, [mask], what: "X の塗っていない所（PDF）");
        OutputCheck.AssertPainted(OutputCheck.LoadSingle(s.InPath("X-masked.jpg")), mask, "X の四角（JPEG）");
        Assert.Equal(2, OutputCheck.Load(s.InPath("Y-masked.pdf")).Count);
        var y2 = OutputCheck.LoadSingle(s.InPath("Y-masked-2_2.jpg"));
        var blue = OutputCheck.Mean(y2, new Box(0.5, 0.6, 0.3, 0.3));
        Assert.True(blue.B > 200 && blue.R < 40, $"Y の 2 ページ目が青でない {blue}");
    });

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
