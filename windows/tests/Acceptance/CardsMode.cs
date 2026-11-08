// W9-5 回帰コーパス（cards モード・docs/windows-design.md §10.19-3）。
// make_cards.py が作った合成カード・A4 を、アプリと同じ部品で取り込む:
//   PageSource → PageAnalyzer（向き・種別・ID-1 の再 OCR）→ CandidateBuilder/RuleEngine（＋ QrDetector・WindowsFaceDetector）
// （MainViewModel.ImportAsync をそのまま使う＝画面と同じ経路）。ファイルごとに種別・向き・カード 1 枚の判定・
// 検出子ごとの候補数・固定領域（位置）の候補数・番号の候補が描いた番号を覆うか・生の番号が文言に出ないかを確かめ、
// Mac の poc サマリと同じく一覧で出す。最後に ALL PASS。
//   VM: dotnet run -c Release -- cards <dir>
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Fuseo.App.ViewModels;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Fuseo.Input;
using Fuseo.Input.Detection;
using Fuseo.Input.Ocr;

static class CardsMode
{
    private static string Name(DetectorId d) => JsonNamingPolicy.CamelCase.ConvertName(d.ToString());

    private static double Coverage(NormRect target, NormRect by)
    {
        var w = Math.Min(target.Right, by.Right) - Math.Max(target.X, by.X);
        var h = Math.Min(target.Bottom, by.Bottom) - Math.Max(target.Y, by.Y);
        return w <= 0 || h <= 0 ? 0 : w * h / (target.W * target.H);
    }

    public static async Task<int> RunAsync(string dir)
    {
        var exp = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "cards-expected.json"))).RootElement;
        var secrets = exp.GetProperty("secrets").EnumerateArray().Select(s => s.GetString()!).ToList();
        var presets = PresetStore.LoadAll();
        var recognizer = WindowsOcrRecognizer.TryCreate();
        Console.WriteLine($"seed {exp.GetProperty("seed").GetInt64()}・日本語 OCR {(recognizer is null ? "なし" : "あり")}");
        if (recognizer is null) { Console.WriteLine("FAILURES: 日本語の文字認識が無い"); return 1; }

        var summary = new List<string>();
        var fails = 0;
        foreach (var file in exp.GetProperty("files").EnumerateObject())
        {
            var e = file.Value;
            var problems = new List<string>();
            void Check(bool ok, string what) { if (!ok) problems.Add(what); }

            var dialogs = new Dialogs();
            var vm = new MainViewModel(new PageSource(), new MaskRasterizer(), new Exporter(), presets, dialogs,
                                       detector: new QrDetector(), english: false, recognizer: recognizer, faceDetector: new WindowsFaceDetector());
            var sw = Stopwatch.StartNew();
            await vm.ImportAsync([Path.Combine(dir, file.Name)]);
            sw.Stop();
            Check(dialogs.Errors.Count == 0, $"取り込みのエラー {string.Join("|", dialogs.Errors)}");
            if (vm.Pages.Count != 1) { problems.Add($"ページ数 {vm.Pages.Count}"); Report(file.Name, "-", -1, [], problems, sw.ElapsedMilliseconds); continue; }

            var page = vm.Pages[0].State;
            var type = JsonNamingPolicy.CamelCase.ConvertName(page.Type.ToString());
            Check(type == e.GetProperty("type").GetString(), $"種別 {type}（期待 {e.GetProperty("type").GetString()}）");
            Check(page.Rotation == e.GetProperty("quarterTurns").GetInt32(), $"向き {page.Rotation}（期待 {e.GetProperty("quarterTurns").GetInt32()}）");
            Check(page.CardFramed == e.GetProperty("cardFramed").GetBoolean(), $"カード 1 枚の判定 {page.CardFramed}");
            Check(!page.TypeIsManual, "種別が手動になっている");

            var detected = page.Candidates.Where(c => c.Source == CandidateSource.Detector && c.Detector is not null)
                .GroupBy(c => Name(c.Detector!.Value)).ToDictionary(g => g.Key, g => g.Count());
            int Count(string d) => detected.GetValueOrDefault(d);
            foreach (var m in e.GetProperty("min").EnumerateObject())
                Check(Count(m.Name) >= m.Value.GetInt32(), $"{m.Name} {Count(m.Name)} 件（最低 {m.Value.GetInt32()}）");
            if (e.TryGetProperty("max", out var max))
                foreach (var m in max.EnumerateObject())
                    Check(Count(m.Name) <= m.Value.GetInt32(), $"{m.Name} {Count(m.Name)} 件（最大 {m.Value.GetInt32()}）");
            if (e.TryGetProperty("maxTotal", out var maxTotal))
                Check(page.Candidates.Count <= maxTotal.GetInt32(), $"候補 {page.Candidates.Count} 件（最大 {maxTotal.GetInt32()}）");
            var position = page.Candidates.Count(c => c.Source == CandidateSource.FixedRegion);
            Check(position == e.GetProperty("position").GetInt32(), $"位置の候補 {position} 件（期待 {e.GetProperty("position").GetInt32()}）");

            // 番号の候補が、描いた番号の外接矩形を覆う（正立した画像の上＝正対の正規化座標。写真は確かめない）。
            if (e.TryGetProperty("numberBox", out var nb) && e.TryGetProperty("numberDetector", out var nd))
            {
                var box = new NormRect(nb[0].GetDouble(), nb[1].GetDouble(), nb[2].GetDouble(), nb[3].GetDouble());
                var best = page.Candidates.Where(c => c.Detector is { } d && Name(d) == nd.GetString())
                    .Select(c => Coverage(box, c.Box)).DefaultIfEmpty(0).Max();
                Check(best >= 0.9, $"番号の候補が番号を覆う割合 {best:P0}（最低 90%）");
            }

            // 生の番号は、候補の文言にも画面の一覧にも出ない（伏せた形だけ）。
            vm.SelectedPage = vm.Pages[0];
            var texts = page.Candidates.Select(c => c.Label + "\n" + c.Basis)
                .Concat(vm.CandidateItems.Select(i => i.Label + "\n" + i.Basis)).ToList();
            foreach (var s in secrets)
                Check(!texts.Any(t => t.Contains(s, StringComparison.Ordinal)), "生の番号が候補の文言に出ている");

            var detail = page.Candidates.GroupBy(c => c.Source == CandidateSource.FixedRegion ? "position" : Name(c.Detector!.Value))
                .Select(g => $"{g.Key}×{g.Count()}").ToList();
            Report(file.Name, type, page.Rotation, detail, problems, sw.ElapsedMilliseconds, page.Candidates.Count);
        }

        void Report(string name, string type, int turns, List<string> detail, List<string> problems, long ms, int total = 0)
        {
            var ok = problems.Count == 0;
            if (!ok) fails++;
            var line = $"{name,-18} 種別={type,-18} 向き={turns} 候補={total}（{string.Join(" ", detail)}） {ms,5}ms  {(ok ? "OK" : "NG: " + string.Join(" / ", problems))}";
            summary.Add(line);
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine("==== サマリ ====");
        foreach (var l in summary) Console.WriteLine(l);
        Console.WriteLine(fails == 0 ? "ALL PASS" : $"FAILURES: {fails}");
        return fails == 0 ? 0 : 1;
    }
}
