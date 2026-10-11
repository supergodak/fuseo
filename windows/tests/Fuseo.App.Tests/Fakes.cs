using System.IO;
using Fuseo.App.Services;
using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Imaging;

namespace Fuseo.App.Tests;

/// <summary>テスト用の取り込み（合成ビットマップを返すだけ）。</summary>
internal sealed class FakePageSource : IPageSource
{
    public Func<IReadOnlyList<string>, IReadOnlyList<SourcePage>>? Handler { get; set; }
    public Exception? Throw { get; set; }
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<IReadOnlyList<SourcePage>> LoadAsync(
        IReadOnlyList<string> paths, Func<string, Task<string?>> askPassword, CancellationToken ct)
    {
        Calls.Add(paths);
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
    public Task<string?> AskPasswordAsync(string fileName) => Task.FromResult<string?>(null);

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
