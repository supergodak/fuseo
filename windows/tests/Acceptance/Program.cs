// W8 受け入れ検査: 実際のアプリと同じ部品（PageSource・QrDetector・MainViewModel・ExportViewModel・Exporter）で
// 合成書類を取り込み→塗る→書き出し→読み戻して、塗り漏れ・座標ずれ・メタデータ残りを画素とバイトで検査する。
// ソリューションには入れない（合成書類は Mac で作る・テスト 1 回に数十秒かかる）。リリース前に手で回す:
//   1. Mac: uv venv に pymupdf qrcode pillow piexif numpy を入れ、
//      `python -I make_corpus.py <dir>` で合成書類（すべて「見本」）と expected.json を作る
//   2. <dir> を VM へ送り、VM で `dotnet run -c Release -- <dir>` → 最後に ALL PASS
// 実物の書類（fixtures-private）は使わない・VM に持ち込まない。
using System.IO;
using System.Text;
using System.Text.Json;
using Fuseo.App.Services;
using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Fuseo.Input;
using Fuseo.Input.Detection;

var dir = args[0];
var outDir = Path.Combine(dir, "out"); Directory.CreateDirectory(outDir);
var exp = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expected.json"))).RootElement;
var fails = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) fails++; }
NormRect R(JsonElement e) => new(e[0].GetDouble(), e[1].GetDouble(), e[2].GetDouble(), e[3].GetDouble());
var secrets = exp.GetProperty("secrets").EnumerateArray().Select(s => s.GetString()!).ToList();
var presets = PresetStore.LoadAll();

async Task<PageBitmap> Reload(string path)
{
    var pages = await new PageSource().LoadAsync([path], _ => Task.FromResult<string?>(null), default);
    return pages[0].Bitmap;
}
async Task<IReadOnlyList<SourcePage>> ReloadAll(string path) =>
    await new PageSource().LoadAsync([path], _ => Task.FromResult<string?>(null), default);

// 塗った矩形の内側（縁を 3px 削る）が全部黒か。
(double blackRatio, int maxV) Inside(PageBitmap b, NormRect r)
{
    var p = CoordinateSpace.ToPixel(r, b.Width, b.Height);
    long n = 0, black = 0; int maxV = 0;
    for (var y = p.Y + 3; y < p.Bottom - 3; y++)
    for (var x = p.X + 3; x < p.Right - 3; x++)
    {
        var (bb, g, rr, _) = b.GetPixel(x, y);
        var v = Math.Max(bb, Math.Max(g, rr)); maxV = Math.Max(maxV, v);
        n++; if (v <= 24) black++;
    }
    return (n == 0 ? 0 : (double)black / n, maxV);
}
int Dark(PageBitmap b, NormRect r, Func<byte, byte, byte, bool> pred)
{
    var p = CoordinateSpace.ToPixel(r, b.Width, b.Height); var c = 0;
    for (var y = p.Y; y < p.Bottom; y++) for (var x = p.X; x < p.Right; x++) { var (bb, g, rr, _) = b.GetPixel(x, y); if (pred(bb, g, rr)) c++; }
    return c;
}
// 塗っていない所が元のまま残っているか（塗った所の周り 6px は除く）。平均差と、差が 48 を超える画素の割合。
(double mean, double bad) Outside(PageBitmap a, PageBitmap b, IReadOnlyList<NormRect> masks)
{
    var px = masks.Select(m => CoordinateSpace.ToPixel(m, a.Width, a.Height)).ToList();
    double sum = 0; long n = 0, bad = 0;
    for (var y = 0; y < a.Height; y += 2)
    for (var x = 0; x < a.Width; x += 2)
    {
        if (px.Any(p => x >= p.X - 6 && x < p.Right + 6 && y >= p.Y - 6 && y < p.Bottom + 6)) continue;
        var (b1, g1, r1, _) = a.GetPixel(x, y); var (b2, g2, r2, _) = b.GetPixel(x, y);
        var d = (Math.Abs(b1 - b2) + Math.Abs(g1 - g2) + Math.Abs(r1 - r2)) / 3.0;
        sum += d; n++; if (d > 48) bad++;
    }
    return (sum / n, (double)bad / n);
}
void Bytes(string path, bool pdf)
{
    var b = File.ReadAllBytes(path);
    var latin = Encoding.Latin1.GetString(b);
    foreach (var s in secrets)
    {
        var hit = latin.Contains(s) || IndexOf(b, Encoding.UTF8.GetBytes(s)) >= 0 || IndexOf(b, Encoding.BigEndianUnicode.GetBytes(s)) >= 0
                  || IndexOf(b, Encoding.Unicode.GetBytes(s)) >= 0;
        Check(!hit, $"{Path.GetFileName(path)}: 文字列 '{s}' が残っていない");
    }
    foreach (var s in new[] { "W8Make", "W8 Sample Camera", "Exif", "GPS", "2026:10:17", "xmpmeta", "W8 producer", "W8 creator", "W8 keyword" })
        Check(!latin.Contains(s), $"{Path.GetFileName(path)}: '{s}' が残っていない");
    if (pdf)
    {
        foreach (var s in new[] { "/Font", "BT\n", "BT ", "/Annots", "/Metadata", "/AcroForm", "/JavaScript", "/EmbeddedFile", "/Encrypt" })
            Check(!latin.Contains(s), $"{Path.GetFileName(path)}: {s.Trim()} が無い");
        var info = System.Text.RegularExpressions.Regex.Matches(latin, @"/(Author|Title|Subject|Keywords|Creator|Producer)\b").Select(m => m.Value).Distinct().ToList();
        Check(info.SequenceEqual(["/Producer"]), $"{Path.GetFileName(path)}: /Info は Producer だけ（{string.Join(",", info)}）");
    }
    else if (b[0] == 0xFF)
    {
        var markers = new List<string>();
        for (var i = 2; i + 4 < b.Length && b[i] == 0xFF;)
        {
            var m = b[i + 1]; markers.Add(m.ToString("X2"));
            if (m == 0xDA) break;
            i += 2 + (b[i + 2] << 8 | b[i + 3]);
        }
        Check(!markers.Any(m => m is "E1" or "E2" or "E3" or "E4" or "E5" or "E6" or "E7" or "E8" or "E9" or "EA" or "EB" or "EC" or "ED" or "EE" or "EF" or "FE"),
              $"{Path.GetFileName(path)}: JPEG のマーカーは APP0 だけ（{string.Join(" ", markers)}）");
    }
}
static int IndexOf(byte[] h, byte[] n)
{
    for (var i = 0; i + n.Length <= h.Length; i++) { var ok = true; for (var j = 0; j < n.Length; j++) if (h[i + j] != n[j]) { ok = false; break; } if (ok) return i; }
    return -1;
}

async Task<string> Export(MainViewModel vm, ExportFormat format, string name, Dialogs dialogs)
{
    var ex = vm.CreateExportViewModel();
    ex.Format = format;
    await ex.PrepareAsync();
    dialogs.SavePath = Path.Combine(outDir, name);
    foreach (var f in Directory.GetFiles(outDir, Path.GetFileNameWithoutExtension(name) + "*")) File.Delete(f);
    await ex.SaveAsync();
    Check(dialogs.Errors.Count == 0, $"{name}: 書き出しでエラーが出ない {string.Join("|", dialogs.Errors)}");
    return dialogs.SavePath;
}

// ================= A. 控除証明書ふう PDF =================
foreach (var (file, locked) in new[] { ("kojo-sample.pdf", false), ("kojo-locked.pdf", true) })
{
    Console.WriteLine($"=== {file}");
    var dialogs = new Dialogs { Passwords = locked ? new Queue<string?>(["wrong-pw", exp.GetProperty("password").GetString()]) : new() };
    var vm = new MainViewModel(new PageSource(), new MaskRasterizer(), new Exporter(), presets, dialogs, detector: new QrDetector());
    await vm.ImportAsync([Path.Combine(dir, file)]);
    if (locked) Check(dialogs.PasswordRequests.Select(r => r.IsRetry).SequenceEqual([false, true]), "パスワード: 1 回目は通常・誤り後は「違います」の再入力");
    Check(vm.Pages.Count == 2, $"2 ページ取り込める（{vm.Pages.Count}）");
    var pdfPages = exp.GetProperty("pdf");
    var allMasks = new List<List<NormRect>>();
    for (var i = 0; i < vm.Pages.Count; i++)
    {
        var page = vm.Pages[i]; var e = pdfPages[i];
        var size = e.GetProperty("sizePt");
        var (ew, eh) = ((int)Math.Round(size[0].GetDouble() * 300 / 72), (int)Math.Round(size[1].GetDouble() * 300 / 72));
        var b = page.State.Base;
        Check(Math.Abs(b.Width - ew) <= 1 && Math.Abs(b.Height - eh) <= 1, $"p{i + 1}: 寸法 {b.Width}x{b.Height}（期待 {ew}x{eh}・/Rotate {e.GetProperty("rotation").GetInt32()}・CropBox 反映）");
        var masks = e.GetProperty("mask");
        Check(Dark(b, R(masks.GetProperty("writer")), (bb, g, r) => r < 110 && g < 110 && bb < 110) > 200, $"p{i + 1}: フォームの記入値が描かれている（注釈を描く）");
        Check(Dark(b, R(masks.GetProperty("stamp")), (bb, g, r) => r > 150 && g < 90 && bb < 90) > 100, $"p{i + 1}: 押印ふうの注釈が描かれている");
        var qr = R(masks.GetProperty("qr"));
        var qc = page.State.Candidates.Where(c => c.Box.Intersects(qr)).ToList();
        Check(qc.Count == 1 && qc[0].Box.X <= qr.X + 0.002 && qc[0].Box.Right >= qr.Right - 0.002 && qc[0].Box.Y <= qr.Y + 0.002 && qc[0].Box.Bottom >= qr.Bottom - 0.002,
              $"p{i + 1}: QR が候補に出て QR 全体を覆う（初期 {(qc.Count > 0 && qc[0].IsOn ? "ON" : "OFF")}）（候補 {page.State.Candidates.Count} 件）");
        vm.SelectedPage = page;
        var list = new List<NormRect>();
        foreach (var m in masks.EnumerateObject())
        {
            if (m.Name == "qr") { foreach (var x in qc) vm.SetCandidateOn(x.Id, true); list.Add(qc.FirstOrDefault()?.Box ?? qr); continue; }
            var r = R(m.Value).Padded(0.004); vm.AddRect(r); list.Add(r);
        }
        allMasks.Add(list);
    }
    var before = vm.Pages.Select(p => p.State.Base).ToList();
    var outPdf = await Export(vm, ExportFormat.Pdf, Path.GetFileNameWithoutExtension(file) + "-out.pdf", dialogs);
    var back = await ReloadAll(outPdf);
    Check(back.Count == 2, "書き出した PDF は 2 ページ");
    for (var i = 0; i < back.Count; i++)
    {
        var o = back[i].Bitmap;
        Check(o.Width == before[i].Width && o.Height == before[i].Height, $"p{i + 1}: 書き出し後も寸法が同じ {o.Width}x{o.Height}");
        foreach (var (m, k) in allMasks[i].Select((m, k) => (m, k)))
        {
            var (ratio, maxV) = Inside(o, m);
            Check(ratio == 1.0, $"p{i + 1}: マスク{k + 1} の内側が全部黒（{ratio:P2}・最大値 {maxV}）");
        }
        var keep = R(pdfPages[i].GetProperty("keep"));
        Check(Dark(o, keep, (bb, g, r) => r < 110) > 50, $"p{i + 1}: 塗っていない欄（申告額）は残っている");
        var (mean, bad) = Outside(before[i], o, allMasks[i]);
        Check(mean < 3 && bad < 0.01, $"p{i + 1}: 塗っていない所は元のまま（平均差 {mean:F2}・大きな差 {bad:P3}）");
    }
    Bytes(outPdf, true);
}

// ================= B. 斜めに撮った写真 =================
{
    Console.WriteLine("=== card-photo.jpg");
    var ph = exp.GetProperty("photo");
    var dialogs = new Dialogs();
    var vm = new MainViewModel(new PageSource(), new MaskRasterizer(), new Exporter(), presets, dialogs, detector: new QrDetector());
    await vm.ImportAsync([Path.Combine(dir, "card-photo.jpg")]);
    var page = vm.Pages.Single();
    var b = page.State.Base;
    // 表示上 4032×3024 の写真は、長辺 4000 に縮小されて 4000×3000 になる（PageSource.MaxImageLongSidePx）。
    Check(b.Width == 4000 && b.Height == 3000, $"EXIF の向き（6）を反映して横長・長辺 4000 に縮小 {b.Width}x{b.Height}");
    var q = ph.GetProperty("quad");
    var quad = new Quad(new(q[0][0].GetDouble(), q[0][1].GetDouble()), new(q[1][0].GetDouble(), q[1][1].GetDouble()),
                        new(q[2][0].GetDouble(), q[2][1].GetDouble()), new(q[3][0].GetDouble(), q[3][1].GetDouble()));
    vm.SelectedPage = page;
    await vm.ApplyCropAsync(page, quad);
    var c = page.State.Base;
    var (ow, oh) = Perspective.OutputSize(quad, b.Width, b.Height);
    Check(c.Width == ow && c.Height == oh, $"切り抜き後 {c.Width}x{c.Height}（縦横比 {(double)c.Width / c.Height:F3}・カード 1.585）");
    var card = ph.GetProperty("card");
    var qrc = R(card.GetProperty("qr"));
    var qc = page.State.Candidates.Where(x => x.Box.Intersects(qrc)).ToList();
    Check(qc.Count == 1, $"切り抜いた画像で QR が候補に出る（初期 {(qc.Count > 0 && qc[0].IsOn ? "ON" : "OFF")}）");
    foreach (var x in qc) vm.SetCandidateOn(x.Id, true);
    // 回転を 1 回（右）挟み、回した画像の上で塗る（座標は QuarterTurn で回す）
    foreach (var (turns, fmt, name) in new[] { (0, ExportFormat.Jpeg, "card-out.jpg"), (1, ExportFormat.Png, "card-rot-out.png") })
    {
        if (turns == 1) { vm.RotateRightCommand.Execute(null); }
        var masks = new List<NormRect>();
        foreach (var m in card.EnumerateObject())
        {
            if (m.Name == "qr") continue;
            var r = QuarterTurn.Rotate(R(m.Value), turns); vm.AddRect(r); masks.Add(r);
        }
        masks.AddRange(page.State.Candidates.Where(x => x.IsOn).Select(x => x.Box));
        var basis = page.State.Base;
        var outFile = await Export(vm, fmt, name, dialogs);
        var o = await Reload(outFile);
        Check(o.Width == basis.Width && o.Height == basis.Height, $"{name}: 寸法 {o.Width}x{o.Height}");
        foreach (var m in card.EnumerateObject())
        {
            var (ratio, maxV) = Inside(o, QuarterTurn.Rotate(R(m.Value), turns));
            Check(ratio == 1.0, $"{name}: カードの {m.Name} の内側が全部黒（{ratio:P2}・最大値 {maxV}）");
        }
        Check(Dark(o, QuarterTurn.Rotate(R(ph.GetProperty("keep")), turns), (bb, g, r) => r < 90 && g < 90) > 300, $"{name}: 有効期限（塗らない欄）は残っている");
        var (mean, bad) = Outside(basis, o, masks);
        Check(mean < (fmt == ExportFormat.Png ? 0.01 : 3) && bad < 0.002, $"{name}: 塗っていない所は元のまま（平均差 {mean:F2}・大きな差 {bad:P3}）");
        Bytes(outFile, false);
    }
}
Console.WriteLine(fails == 0 ? "ALL PASS" : $"FAILURES: {fails}");
return fails == 0 ? 0 : 1;

sealed class Dialogs : IDialogService
{
    public Queue<string?> Passwords { get; init; } = new();
    public List<PasswordRequest> PasswordRequests { get; } = [];
    public string? SavePath { get; set; }
    public List<string> Errors { get; } = [];
    public IReadOnlyList<string>? AskOpenFiles() => null;
    public Task<string?> AskPasswordAsync(PasswordRequest request)
    {
        PasswordRequests.Add(request);
        return Task.FromResult(Passwords.Count > 0 ? Passwords.Dequeue() : null);
    }
    public string? AskSavePath(ExportFormat format, string defaultFileName) => SavePath;
    public bool Confirm(string title, string message) => true;
    public void ShowError(string title, string message) => Errors.Add(title + ": " + message);
    public void ShowExport(ExportViewModel viewModel) { }
    public void ShowSettings(SettingsViewModel viewModel) { }
    public void ShowAbout() { }
    public void OpenFolder(string path) { }
    public string? AskFolder(string title) => null;
    public Quad? AskCrop(CropRequest request) => null;
}
