using System.IO;
using Fuseo.App.Services;
using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;

namespace Fuseo.App.Tests;

/// <summary>テスト用の取り込み（合成ビットマップを返すだけ）。</summary>
internal sealed class FakePageSource : IPageSource
{
    public Func<IReadOnlyList<string>, IReadOnlyList<SourcePage>>? Handler { get; set; }
    public Exception? Throw { get; set; }
    public List<IReadOnlyList<string>> Calls { get; } = [];
    public List<int> PagesAlreadyOpen { get; } = [];

    public Task<IReadOnlyList<SourcePage>> LoadAsync(
        IReadOnlyList<string> paths, Func<PasswordRequest, Task<string?>> askPassword, CancellationToken ct,
        int pagesAlreadyOpen = 0)
    {
        Calls.Add(paths);
        PagesAlreadyOpen.Add(pagesAlreadyOpen);
        if (Throw is not null) return Task.FromException<IReadOnlyList<SourcePage>>(Throw);
        var result = Handler?.Invoke(paths)
            ?? paths.Select((p, i) => new SourcePage(PageBitmap.Solid(40, 20, 200, 200, 200), p, 0, false)).ToList();
        return Task.FromResult(result);
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public IReadOnlyList<string>? OpenFiles { get; set; }
    public string? SavePath { get; set; }
    public bool ConfirmResult { get; set; } = true;
    public List<(string Title, string Message)> Errors { get; } = [];
    public List<string> Confirms { get; } = [];
    public List<(ExportFormat Format, string DefaultName)> SaveRequests { get; } = [];
    public Action<ExportViewModel>? OnShowExport { get; set; }

    public IReadOnlyList<string>? AskOpenFiles() => OpenFiles;
    public Task<string?> AskPasswordAsync(PasswordRequest request) => Task.FromResult<string?>(null);

    public string? AskSavePath(ExportFormat format, string defaultFileName)
    {
        SaveRequests.Add((format, defaultFileName));
        return SavePath;
    }

    public bool Confirm(string title, string message)
    {
        Confirms.Add(message);
        return ConfirmResult;
    }

    public void ShowError(string title, string message) => Errors.Add((title, message));
    public void ShowExport(ExportViewModel viewModel) => OnShowExport?.Invoke(viewModel);

    public Action<SettingsViewModel>? OnShowSettings { get; set; }
    public int AboutShown { get; private set; }
    public List<string> OpenedFolders { get; } = [];

    public void ShowSettings(SettingsViewModel viewModel) => OnShowSettings?.Invoke(viewModel);
    public void ShowAbout() => AboutShown++;
    public void OpenFolder(string path) => OpenedFolders.Add(path);

    /// <summary>フォルダ選択の答え（W9-4）。<see cref="FolderHandler"/> があればそちらを使う。</summary>
    public string? Folder { get; set; }
    public Func<string, string?>? FolderHandler { get; set; }
    public List<string> FolderRequests { get; } = [];

    public string? AskFolder(string title)
    {
        FolderRequests.Add(title);
        return FolderHandler is { } h ? h(title) : Folder;
    }

    /// <summary>「その他の色」の答え（v0.3）。</summary>
    public Fuseo.Core.Model.TextColor? ColorResult { get; set; }
    public List<Fuseo.Core.Model.TextColor> ColorRequests { get; } = [];

    public Fuseo.Core.Model.TextColor? AskColor(Fuseo.Core.Model.TextColor initial)
    {
        ColorRequests.Add(initial);
        return ColorResult;
    }

    public Quad? CropResult { get; set; }
    public List<CropRequest> CropRequests { get; } = [];

    public Quad? AskCrop(CropRequest request)
    {
        CropRequests.Add(request);
        return CropResult;
    }
}

/// <summary>受け取った焼き込み済みページを記録し、ページ番号を 1 バイト書く書き出し。</summary>
internal sealed class RecordingExporter : IExporter
{
    public IReadOnlyList<BurnedPage>? Pages { get; private set; }
    public ExportOptions? Options { get; private set; }
    public Exception? Throw { get; set; }

    public void Export(IReadOnlyList<BurnedPage> pages, ExportOptions options, Func<int, Stream> openDestination)
    {
        Pages = pages;
        Options = options;
        var count = options.Format == ExportFormat.Pdf ? 1 : pages.Count;
        for (var i = 0; i < count; i++)
        {
            var s = openDestination(i);
            s.WriteByte((byte)(i + 1));
            if (Throw is not null) throw Throw; // 書きかけで失敗する
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fuseo-app-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

/// <summary>
/// テスト用の文字認識（W9-3）。<see cref="Handler"/> が画像ごとに OCR 結果を返す（向き・寸法で変えられる）。
/// <see cref="Gate"/> を入れると、それが完了するまで結果を返さない（解析中の操作を確かめる）。
/// </summary>
internal sealed class FakeTextRecognizer : Fuseo.Core.Contracts.ITextRecognizer
{
    private int _calls;
    public Func<PageBitmap, IReadOnlyList<Fuseo.Core.Analysis.OcrItem>> Handler { get; set; } = _ => [];
    public int Calls => Volatile.Read(ref _calls);
    public bool Throw { get; set; }
    public TaskCompletionSource? Gate { get; set; }
    /// <summary>呼ばれたスレッドが UI（テスト本体）のスレッドだったか。</summary>
    public int? LastThreadId { get; private set; }

    public async Task<IReadOnlyList<Fuseo.Core.Analysis.OcrItem>> RecognizeAsync(PageBitmap page, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        LastThreadId = Environment.CurrentManagedThreadId;
        if (Gate is { } g) await g.Task.ConfigureAwait(false);
        if (Throw) throw new InvalidOperationException("文字認識に失敗");
        return Handler(page);
    }
}

/// <summary>決まった箱を返す顔の検出器（W9-3）。</summary>
internal sealed class FakeFaceDetector(params NormRect[] boxes) : IDetector
{
    private int _calls;
    public DetectorId Id => DetectorId.Face;
    public int Calls => Volatile.Read(ref _calls);
    public int? LastThreadId { get; private set; }

    public IReadOnlyList<NormRect> Detect(PageBitmap page)
    {
        Interlocked.Increment(ref _calls);
        LastThreadId = Environment.CurrentManagedThreadId;
        return boxes;
    }
}
