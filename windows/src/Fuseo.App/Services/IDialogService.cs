using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;

namespace Fuseo.App.Services;

/// <summary>
/// ダイアログの抽象（ViewModel をウィンドウ無しで単体テストできるように）。
/// 本物は <see cref="WpfDialogService"/>。
/// </summary>
public interface IDialogService
{
    /// <summary>「ファイルを開く」（複数選択・画像と PDF）。取り消しなら null。</summary>
    IReadOnlyList<string>? AskOpenFiles();

    /// <summary>
    /// パスワード入力。<see cref="IPageSource.LoadAsync"/> の askPassword にそのまま渡す。
    /// 入力値は戻り値でだけ返し、どこにも保持しない。取り消しなら null。
    /// </summary>
    Task<string?> AskPasswordAsync(PasswordRequest request);

    /// <summary>保存ダイアログ（上書き確認は標準のもの）。取り消しなら null。</summary>
    string? AskSavePath(ExportFormat format, string defaultFileName);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    /// <summary>書き出しシートをモーダルで開く（閉じるまで戻らない）。</summary>
    void ShowExport(ExportViewModel viewModel);
}
